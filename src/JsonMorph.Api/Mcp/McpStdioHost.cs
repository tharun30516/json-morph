using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JsonMorph.Core;

namespace JsonMorph.Api.Mcp;

/// <summary>
/// MCP stdio transport: newline-delimited JSON-RPC on stdin/stdout (for Claude Desktop / Claude Code).
/// Logging goes to stderr in this mode so stdout carries protocol messages only.
/// </summary>
public sealed class McpStdioHost(McpServer server, IHostApplicationLifetime lifetime, ILogger<McpStdioHost> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        log.LogInformation("MCP stdio transport ready");

        try
        {
            while (!stoppingToken.IsCancellationRequested && await stdin.ReadLineAsync(stoppingToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? response;
                try
                {
                    response = await server.HandleAsync(JsonNode.Parse(line), stoppingToken);
                }
                catch (JsonException ex)
                {
                    response = new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = null,
                        ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error: " + ex.Message },
                    };
                }
                if (response is not null) await stdout.WriteLineAsync(response.ToJsonString(JsonDefaults.Options));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }

        log.LogInformation("stdin closed; shutting down");
        lifetime.StopApplication();
    }
}
