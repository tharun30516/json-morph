using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JsonMorph.Api;
using JsonMorph.Api.Mcp;
using JsonMorph.Api.Migrations;
using JsonMorph.Core;
using Microsoft.Extensions.Options;

var stdioMode = args.Contains("--stdio");
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--stdio").ToArray());

if (stdioMode)
{
    // stdout is reserved for MCP messages; send all logs to stderr and pick a free port for the HTTP side.
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.WebHost.UseUrls("http://127.0.0.1:0");
}

builder.Services.Configure<JsonMorphOptions>(builder.Configuration.GetSection("JsonMorph"));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonDefaults.Options.DefaultIgnoreCondition;
    o.SerializerOptions.Encoder = JsonDefaults.Options.Encoder;
    foreach (var c in JsonDefaults.Options.Converters) o.SerializerOptions.Converters.Add(c);
});
builder.Services.AddHttpClient("migration", c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton(_ => new MappingEngine());
builder.Services.AddSingleton<DataPaths>();
builder.Services.AddSingleton<MappingStore>();
builder.Services.AddSingleton<MigrationIo>();
builder.Services.AddSingleton<MigrationService>();
builder.Services.AddSingleton<Operations>();
builder.Services.AddSingleton<McpServer>();
builder.Services.AddHostedService<MigrationWorker>();
if (stdioMode) builder.Services.AddHostedService<McpStdioHost>();

var app = builder.Build();
var options = app.Services.GetRequiredService<IOptions<JsonMorphOptions>>().Value;

// Map domain exceptions to HTTP status codes.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex) when (ex is ValidationException or MappingException or JsonException or BadHttpRequestException)
    {
        var errors = ex is ValidationException ve ? ve.Errors : new[] { ex.Message };
        await Results.BadRequest(new { errors }).ExecuteAsync(ctx);
    }
    catch (NotFoundException ex)
    {
        await Results.NotFound(new { error = ex.Message }).ExecuteAsync(ctx);
    }
});

// Optional API key for everything except /health.
app.Use(async (ctx, next) =>
{
    if (options.ApiKey is { Length: > 0 } key && !ctx.Request.Path.StartsWithSegments("/health"))
    {
        var supplied = ctx.Request.Headers["X-Api-Key"].FirstOrDefault()
                       ?? ctx.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);
        if (supplied is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(key)))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Missing or invalid API key." });
            return;
        }
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "json-morph" }));

// ------------------------------------------------------------------ transform & mappings
var api = app.MapGroup("/api");

api.MapPost("/transform", (TransformRequest req, Operations ops) =>
    Results.Ok(ops.Transform(req.Input, req.MappingName, req.MappingVersion, req.Mapping)));

api.MapPost("/mappings", (MappingDefinition mapping, MappingStore store) =>
{
    var saved = store.Save(mapping);
    return Results.Created($"/api/mappings/{saved.Name}?version={saved.Version}", saved);
});
api.MapGet("/mappings", (MappingStore store) => Results.Ok(store.List()));
api.MapGet("/mappings/{name}", (string name, int? version, MappingStore store) => Results.Ok(store.Get(name, version)));
api.MapDelete("/mappings/{name}", (string name, MappingStore store) =>
    store.Delete(name) ? Results.NoContent() : Results.NotFound(new { error = $"Mapping '{name}' not found." }));
api.MapPost("/mappings/validate", (MappingDefinition mapping, Operations ops) => Results.Ok(ops.Validate(mapping)));
api.MapPost("/mappings/infer", (InferRequest req, Operations ops) =>
    Results.Ok(ops.Infer(req.Source, req.Target, req.Name, req.Save)));

// ------------------------------------------------------------------ migrations
api.MapPost("/migrations", (MigrationRequest req, MigrationService svc) =>
{
    var job = svc.Start(req);
    return Results.Accepted($"/api/migrations/{job.Id}", job.Snapshot());
});
api.MapGet("/migrations", (MigrationService svc) => Results.Ok(svc.List()));
api.MapGet("/migrations/{id:guid}", (Guid id, MigrationService svc) => Results.Ok(svc.Get(id).Snapshot()));
api.MapGet("/migrations/{id:guid}/output", (Guid id, int? skip, int? take, MigrationService svc) =>
    Results.Ok(svc.Get(id).GetOutput(Math.Max(0, skip ?? 0), Math.Clamp(take ?? 100, 1, 1000))));
api.MapGet("/migrations/{id:guid}/dead-letters", (Guid id, int? skip, int? take, MigrationService svc) =>
    Results.Ok(svc.Get(id).GetDeadLetters(Math.Max(0, skip ?? 0), Math.Clamp(take ?? 100, 1, 1000))));
api.MapPost("/migrations/{id:guid}/cancel", (Guid id, MigrationService svc) => Results.Ok(svc.Cancel(id)));

// ------------------------------------------------------------------ MCP (Streamable HTTP, JSON responses)
app.MapPost("/mcp", async (HttpContext ctx, McpServer mcp) =>
{
    if (!OriginAllowed(ctx.Request.Headers.Origin.FirstOrDefault()))
        return Results.Json(new { error = "Origin not allowed." }, statusCode: StatusCodes.Status403Forbidden);

    JsonNode? message;
    try { message = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
    catch (JsonException ex)
    {
        return Results.Json(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = null,
            ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error: " + ex.Message },
        }, statusCode: StatusCodes.Status400BadRequest);
    }

    var response = await mcp.HandleAsync(message, ctx.RequestAborted);
    return response is null ? Results.Accepted() : Results.Text(response.ToJsonString(JsonDefaults.Options), "application/json");
});
// This server doesn't push server-initiated messages, so there is no SSE stream to open.
app.MapGet("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
app.MapDelete("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));

app.Run();

bool OriginAllowed(string? origin)
{
    // Guards against DNS-rebinding from browsers; non-browser MCP clients send no Origin.
    if (string.IsNullOrEmpty(origin)) return true;
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
    return uri.IsLoopback || options.AllowedHttpHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
}

public sealed record TransformRequest(JsonNode? Input, string? MappingName, int? MappingVersion, MappingDefinition? Mapping);
public sealed record InferRequest(JsonNode? Source, JsonNode? Target, string? Name, bool Save);

public partial class Program;
