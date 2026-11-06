using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using JsonMorph.Api.Migrations;
using JsonMorph.Core;

namespace JsonMorph.Api.Mcp;

/// <summary>
/// Model Context Protocol server (JSON-RPC 2.0). Transport-agnostic: used by POST /mcp (Streamable HTTP)
/// and by the stdio host. Exposes tools so an AI agent can map JSON shape A to shape B and run migrations.
/// </summary>
public sealed class McpServer
{
    private static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };
    private static readonly string ServerVersion =
        typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    private const string Instructions =
        "JsonMorph converts JSON from shape A to shape B. Typical flow: (1) infer_mapping with one example A record " +
        "and the B record you want; check reproducesTarget/unmatched and fix rules if needed. (2) transform_json to try " +
        "it on more records. (3) save_mapping to version it. (4) start_migration with dryRun=true, inspect, then run for real. " +
        "File paths are relative to the service's data folder.";

    private sealed record Tool(string Name, string Description, JsonObject Schema, Func<JsonObject, CancellationToken, Task<JsonNode?>> Run);

    private readonly Dictionary<string, Tool> _tools;
    private readonly ILogger<McpServer> _log;

    public McpServer(Operations ops, MappingStore store, MigrationService migrations, ILogger<McpServer> log)
    {
        _log = log;
        _tools = BuildTools(ops, store, migrations).ToDictionary(t => t.Name);
    }

    /// <summary>Handles one JSON-RPC message (or batch). Returns null for notifications/responses.</summary>
    public async Task<JsonNode?> HandleAsync(JsonNode? message, CancellationToken ct)
    {
        if (message is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var m in batch)
                if (await HandleOneAsync(m, ct) is { } r) responses.Add(r);
            return responses.Count == 0 ? null : responses;
        }
        return await HandleOneAsync(message, ct);
    }

    private async Task<JsonNode?> HandleOneAsync(JsonNode? message, CancellationToken ct)
    {
        if (message is not JsonObject msg || msg["jsonrpc"]?.GetValue<string>() != "2.0")
            return Error(null, -32600, "Invalid Request: expected a JSON-RPC 2.0 object.");

        var id = msg["id"]?.DeepClone();
        if (msg["method"] is not JsonValue mv || !mv.TryGetValue<string>(out var method))
            return null; // a response from the client; nothing to do
        var isNotification = !msg.ContainsKey("id");
        var p = msg["params"] as JsonObject ?? new JsonObject();

        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(p),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray(_tools.Values.Select(t => (JsonNode?)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = t.Schema.DeepClone(),
                    }).ToArray()),
                },
                "tools/call" => await CallToolAsync(p, ct),
                _ when method.StartsWith("notifications/", StringComparison.Ordinal) => null,
                _ => throw new RpcException(-32601, $"Method not found: {method}"),
            };
            if (isNotification) return null;
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
        }
        catch (RpcException ex)
        {
            return isNotification ? null : Error(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MCP method {Method} failed", method);
            return isNotification ? null : Error(id, -32603, "Internal error: " + ex.Message);
        }
    }

    private static JsonObject Initialize(JsonObject p)
    {
        var requested = p["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = requested is not null && SupportedVersions.Contains(requested) ? requested : SupportedVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "json-morph", ["title"] = "JsonMorph JSON migration service", ["version"] = ServerVersion },
            ["instructions"] = Instructions,
        };
    }

    private async Task<JsonNode> CallToolAsync(JsonObject p, CancellationToken ct)
    {
        var name = p["name"]?.GetValue<string>() ?? throw new RpcException(-32602, "params.name is required.");
        if (!_tools.TryGetValue(name, out var tool)) throw new RpcException(-32602, $"Unknown tool: {name}");
        var args = p["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            var result = await tool.Run(args, ct);
            var structured = result as JsonObject ?? new JsonObject { ["result"] = result?.DeepClone() };
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = structured.ToJsonString(JsonDefaults.Indented),
                }),
                ["structuredContent"] = structured,
                ["isError"] = false,
            };
        }
        catch (Exception ex) when (ex is ValidationException or NotFoundException or MappingException or JsonException or FormatException)
        {
            // Tool-level errors go back to the model so it can correct itself.
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"Error: {ex.Message}" }),
                ["isError"] = true,
            };
        }
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private sealed class RpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    // ---------------------------------------------------------------- tools

    private static T Arg<T>(JsonObject args, string name) =>
        args[name] is { } n ? n.Deserialize<T>(JsonDefaults.Options)! : default!;

    private static string RequiredString(JsonObject args, string name) =>
        args[name]?.GetValue<string>() is { Length: > 0 } s ? s : throw new ValidationException($"'{name}' is required.");

    private static Guid RequiredId(JsonObject args) =>
        Guid.TryParse(RequiredString(args, "id"), out var g) ? g : throw new ValidationException("'id' must be a migration id (GUID).");

    private static IEnumerable<Tool> BuildTools(Operations ops, MappingStore store, MigrationService migrations)
    {
        yield return new Tool("transform_json",
            "Transform JSON from shape A to shape B using a mapping (inline or saved by name). Input may be one record or an array of records.",
            Schema(new()
            {
                ["input"] = new JsonObject { ["description"] = "Record (object) or array of records to transform." },
                ["mapping"] = MappingSchemaRef("Inline mapping definition."),
                ["mappingName"] = Str("Name of a saved mapping (alternative to 'mapping')."),
                ["mappingVersion"] = Int("Saved mapping version (default: latest)."),
            }, "input"),
            (a, _) => Task.FromResult<JsonNode?>(ops.Transform(a["input"]?.DeepClone(),
                a["mappingName"]?.GetValue<string>(), Arg<int?>(a, "mappingVersion"), Arg<MappingDefinition?>(a, "mapping"))));

        yield return new Tool("infer_mapping",
            "Infer a mapping from one example source record (A) and the desired target record (B). Returns the draft mapping, whether it reproduces B exactly, and any target fields it could not match.",
            Schema(new()
            {
                ["source"] = new JsonObject { ["description"] = "Example input record (shape A)." },
                ["target"] = new JsonObject { ["type"] = "object", ["description"] = "The same record in the desired shape B." },
                ["name"] = Str("Name for the mapping (needed when save=true)."),
                ["save"] = Bool("Save the inferred mapping as a new version."),
            }, "source", "target"),
            (a, _) => Task.FromResult<JsonNode?>(ops.Infer(a["source"]?.DeepClone(), a["target"]?.DeepClone(),
                a["name"]?.GetValue<string>(), Arg<bool>(a, "save"))));

        yield return new Tool("validate_mapping", "Check a mapping definition for errors without running it.",
            Schema(new() { ["mapping"] = MappingSchemaRef("Mapping definition to validate.") }, "mapping"),
            (a, _) => Task.FromResult<JsonNode?>(ops.Validate(Arg<MappingDefinition?>(a, "mapping"))));

        yield return new Tool("save_mapping", "Save a mapping. Each save creates a new immutable version.",
            Schema(new() { ["mapping"] = MappingSchemaRef("Mapping definition; 'name' is required.") }, "mapping"),
            (a, _) =>
            {
                var saved = store.Save(Arg<MappingDefinition?>(a, "mapping") ?? throw new ValidationException("'mapping' is required."));
                return Task.FromResult<JsonNode?>(new JsonObject { ["name"] = saved.Name, ["version"] = saved.Version });
            });

        yield return new Tool("list_mappings", "List saved mappings and their versions.", Schema(new()),
            (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["mappings"] = Operations.ToNode(store.List()) }));

        yield return new Tool("get_mapping", "Get a saved mapping definition.",
            Schema(new() { ["name"] = Str("Mapping name."), ["version"] = Int("Version (default: latest).") }, "name"),
            (a, _) => Task.FromResult(Operations.ToNode(store.Get(RequiredString(a, "name"), Arg<int?>(a, "version")))));

        yield return new Tool("start_migration",
            "Run a batch migration: read records from a source (inline records, a file in the data folder, or an HTTP URL), map each one, write to a sink (memory, file, or HTTP POST). Failed records go to dead-letter. Use dryRun=true first. Set waitSeconds to block until it finishes.",
            Schema(new()
            {
                ["mappingName"] = Str("Saved mapping to use."),
                ["mappingVersion"] = Int("Saved mapping version (default: latest)."),
                ["mapping"] = MappingSchemaRef("Inline mapping (alternative to mappingName)."),
                ["source"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Where records come from.",
                    ["properties"] = new JsonObject
                    {
                        ["type"] = Enum("inline", "file", "http"),
                        ["records"] = new JsonObject { ["type"] = "array", ["description"] = "Records for type=inline." },
                        ["path"] = Str("File in the data folder (.json array/document, or .ndjson/.jsonl)."),
                        ["url"] = Str("URL for type=http (GET)."),
                        ["recordsPath"] = Str("JSONPath to the records inside the document, e.g. $.data.items."),
                        ["headers"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
                    },
                    ["required"] = new JsonArray("type"),
                },
                ["sink"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Where mapped records go (default: memory).",
                    ["properties"] = new JsonObject
                    {
                        ["type"] = Enum("memory", "file", "http"),
                        ["path"] = Str("Output file in the data folder for type=file."),
                        ["format"] = Enum("ndjson", "json"),
                        ["url"] = Str("URL for type=http; each batch is POSTed as a JSON array."),
                        ["headers"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
                    },
                },
                ["batchSize"] = Int("Records per write (default 500)."),
                ["dryRun"] = Bool("Map everything but write nothing; keeps a preview in memory."),
                ["maxErrors"] = Int("Abort and discard file output once failures exceed this."),
                ["writePartialRecords"] = Bool("Also write records that had mapping errors."),
                ["deadLetterPath"] = Str("Optional .ndjson file in the data folder for failed records."),
                ["waitSeconds"] = Int("Wait up to this many seconds (max 120) for the migration to finish."),
            }, "source"),
            async (a, ct) =>
            {
                var wait = Math.Clamp(Arg<int?>(a, "waitSeconds") ?? 0, 0, 120);
                var req = a.Deserialize<MigrationRequest>(JsonDefaults.Options) ?? throw new ValidationException("Invalid migration request.");
                var job = migrations.Start(req);
                var deadline = DateTime.UtcNow.AddSeconds(wait);
                while (!job.IsFinished && DateTime.UtcNow < deadline) await Task.Delay(100, ct);
                return Operations.ToNode(job.Snapshot());
            });

        yield return new Tool("get_migration",
            "Get a migration's status and counters, optionally with some output records and dead letters.",
            Schema(new()
            {
                ["id"] = Str("Migration id."),
                ["outputSample"] = Int("Include up to N output records (memory sink / dry run)."),
                ["deadLetterSample"] = Int("Include up to N failed records with their errors."),
            }, "id"),
            (a, _) =>
            {
                var job = migrations.Get(RequiredId(a));
                var result = (JsonObject)Operations.ToNode(job.Snapshot())!;
                if (Arg<int?>(a, "outputSample") is > 0 and var n)
                    result["outputSample"] = Operations.ToNode(job.GetOutput(0, Math.Min(n, 100)));
                if (Arg<int?>(a, "deadLetterSample") is > 0 and var d)
                    result["deadLetterSample"] = Operations.ToNode(job.GetDeadLetters(0, Math.Min(d, 100)));
                return Task.FromResult<JsonNode?>(result);
            });

        yield return new Tool("cancel_migration", "Cancel a queued or running migration.",
            Schema(new() { ["id"] = Str("Migration id.") }, "id"),
            (a, _) => Task.FromResult(Operations.ToNode(migrations.Cancel(RequiredId(a)))));

        yield return new Tool("list_migrations", "List migrations started since the service came up.", Schema(new()),
            (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["migrations"] = Operations.ToNode(migrations.List()) }));
    }

    private static JsonObject Schema(Dictionary<string, JsonNode> props, params string[] required)
    {
        var properties = new JsonObject();
        foreach (var (k, v) in props) properties[k] = v;
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        return schema;
    }

    private static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
    private static JsonObject Int(string d) => new() { ["type"] = "integer", ["description"] = d };
    private static JsonObject Bool(string d) => new() { ["type"] = "boolean", ["description"] = d };
    private static JsonObject Enum(params string[] values) =>
        new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };

    private static JsonObject MappingSchemaRef(string description) => new()
    {
        ["type"] = "object",
        ["description"] = description + " Shape: { name?, description?, includeNulls?, rules: [ { target, source | sources | template | const, " +
                          "default?, required?, when?, transform?: [\"trim\" | {op,...}], each?: {rules:[...]} } ] }. " +
                          "Paths are JSONPath ($.a.b, $.items[*], $.list[0]); '$now' = current time. Transforms: " +
                          string.Join(", ", Transforms.KnownOps) + ".",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject { ["type"] = "string" },
            ["description"] = new JsonObject { ["type"] = "string" },
            ["includeNulls"] = new JsonObject { ["type"] = "boolean" },
            ["rules"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } },
        },
        ["required"] = new JsonArray("rules"),
    };
}
