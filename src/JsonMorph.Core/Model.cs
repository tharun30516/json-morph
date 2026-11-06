using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JsonMorph.Core;

public sealed class MappingException(string message) : Exception(message);

/// <summary>A declarative description of how to turn one JSON shape (A) into another (B).</summary>
public sealed class MappingDefinition
{
    public string? Name { get; set; }
    public int Version { get; set; }
    public string? Description { get; set; }

    /// <summary>When true, rules that resolve to null still write an explicit null.</summary>
    public bool IncludeNulls { get; set; }

    public List<MappingRule> Rules { get; set; } = new();
}

public sealed class MappingRule
{
    /// <summary>Where the value goes in the output, e.g. "profile.email" or "tags[0]".</summary>
    public string Target { get; set; } = "";

    /// <summary>JSONPath into the input. "$" = whole record, "$now" = current UTC timestamp.</summary>
    public string? Source { get; set; }

    /// <summary>Coalesce: first non-null of these paths wins.</summary>
    public List<string>? Sources { get; set; }

    /// <summary>String template, e.g. "{$.first} {$.last}".</summary>
    public string? Template { get; set; }

    /// <summary>Constant value.</summary>
    public JsonNode? Const { get; set; }

    /// <summary>Fallback when the resolved value is null.</summary>
    public JsonNode? Default { get; set; }

    /// <summary>Record fails if this resolves to null.</summary>
    public bool Required { get; set; }

    /// <summary>Only apply when this path is truthy. Prefix with "!" to negate.</summary>
    public string? When { get; set; }

    /// <summary>Pipeline of transforms: "trim" or { "op": "date", "to": "yyyy-MM-dd" }.</summary>
    public List<JsonNode>? Transform { get; set; }

    /// <summary>Nested mapping applied to each element of an array (or to a single object).</summary>
    public MappingDefinition? Each { get; set; }
}

public sealed record MappingError(string Target, string Message);

public sealed record MappingResult(JsonObject Output, IReadOnlyList<MappingError> Errors)
{
    public bool Success => Errors.Count == 0;
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    public static JsonSerializerOptions Create(bool indented) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = indented,
        // Keep output readable (no \u002B for "+", accents stay as-is). Output is data, not HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
