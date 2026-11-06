using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JsonMorph.Core;

/// <summary>Applies a <see cref="MappingDefinition"/> to an input record. Thread-safe and stateless.</summary>
public sealed partial class MappingEngine
{
    private readonly Func<DateTimeOffset> _clock;

    public MappingEngine(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public MappingResult Map(JsonNode? input, MappingDefinition mapping)
    {
        var output = new JsonObject();
        var errors = new List<MappingError>();
        MapInto(input, mapping, output, errors, prefix: "");
        return new MappingResult(output, errors);
    }

    private void MapInto(JsonNode? input, MappingDefinition def, JsonObject output, List<MappingError> errors, string prefix)
    {
        foreach (var rule in def.Rules)
        {
            var target = prefix + rule.Target;
            try
            {
                if (rule.When is { } when)
                {
                    var negate = when.StartsWith('!');
                    var truthy = Transforms.Truthy(Resolve(input, negate ? when[1..] : when));
                    if (truthy == negate) continue;
                }

                var value = Evaluate(input, rule);

                if (rule.Transform is { } pipeline)
                    foreach (var t in pipeline) value = Transforms.Apply(value, t);

                if (rule.Each is { } nested) value = MapEach(value, nested, errors, target);

                if (value is null && rule.Default is not null) value = rule.Default.DeepClone();

                if (value is null)
                {
                    if (rule.Required) errors.Add(new MappingError(target, "Required value is missing."));
                    if (!def.IncludeNulls) continue;
                }

                JsonPath.Parse(rule.Target).SetValue(output, value);
            }
            catch (MappingException ex)
            {
                errors.Add(new MappingError(target, ex.Message));
            }
        }
    }

    private JsonNode? MapEach(JsonNode? value, MappingDefinition nested, List<MappingError> errors, string target)
    {
        switch (value)
        {
            case null: return null;
            case JsonArray arr:
                var result = new JsonArray();
                for (var i = 0; i < arr.Count; i++)
                {
                    var sub = new JsonObject();
                    MapInto(arr[i], nested, sub, errors, $"{target}[{i}].");
                    result.Add(sub);
                }
                return result;
            default:
                var single = new JsonObject();
                MapInto(value, nested, single, errors, target + ".");
                return single;
        }
    }

    private JsonNode? Evaluate(JsonNode? input, MappingRule rule)
    {
        if (rule.Const is not null) return rule.Const.DeepClone();
        if (rule.Template is not null) return JsonValue.Create(RenderTemplate(input, rule.Template));
        if (rule.Sources is { Count: > 0 })
        {
            foreach (var p in rule.Sources)
                if (Resolve(input, p) is { } v) return v;
            return null;
        }
        return rule.Source is null ? null : Resolve(input, rule.Source);
    }

    /// <summary>Resolves a source path to a detached (cloned) node.</summary>
    public JsonNode? Resolve(JsonNode? input, string path)
    {
        if (path == "$now") return JsonValue.Create(_clock().UtcDateTime.ToString("o"));
        if (path == "$") return input?.DeepClone();
        var jp = JsonPath.Parse(path);
        if (jp.IsMulti) return new JsonArray(jp.Select(input).Select(n => n?.DeepClone()).ToArray());
        return jp.Select(input).FirstOrDefault()?.DeepClone();
    }

    private string RenderTemplate(JsonNode? input, string template) =>
        TemplateToken().Replace(template, m => Transforms.Str(Resolve(input, m.Groups[1].Value.Trim())) ?? "");

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex TemplateToken();
}
