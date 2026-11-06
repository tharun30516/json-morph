using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JsonMorph.Core;

/// <summary>
/// Value transforms. Scalar ops applied to an array run per element; "whole value" ops see the array itself.
/// </summary>
public static class Transforms
{
    private static readonly HashSet<string> WholeValueOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "join", "first", "last", "count", "distinct", "compact", "stringify",
    };

    private static readonly HashSet<string> ScalarOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "trim", "lower", "upper", "toString", "toNumber", "toInt", "toBool", "nullIfEmpty",
        "date", "map", "split", "replace", "regex", "round", "multiply", "add",
        "prefix", "suffix", "hash", "mask", "json",
    };

    public static IReadOnlyCollection<string> KnownOps { get; } =
        WholeValueOps.Concat(ScalarOps).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public static (string Op, JsonObject? Args) ParseSpec(JsonNode spec)
    {
        if (spec is JsonValue v && v.TryGetValue<string>(out var name)) return (name, null);
        if (spec is JsonObject o && o["op"] is JsonValue opv && opv.TryGetValue<string>(out var op)) return (op, o);
        throw new MappingException($"Invalid transform spec: {spec.ToJsonString()}. Use \"op\" or {{\"op\": \"...\"}}.");
    }

    public static string? Validate(JsonNode spec)
    {
        try
        {
            var (op, args) = ParseSpec(spec);
            if (!WholeValueOps.Contains(op) && !ScalarOps.Contains(op)) return $"Unknown transform '{op}'.";
            string? need = op.ToLowerInvariant() switch
            {
                "map" when args?["values"] is not JsonObject => "Transform 'map' needs a \"values\" object.",
                "replace" when args?["find"] is null => "Transform 'replace' needs \"find\".",
                "regex" when args?["pattern"] is null => "Transform 'regex' needs \"pattern\".",
                "multiply" when args?["by"] is null => "Transform 'multiply' needs \"by\".",
                "add" when args?["value"] is null => "Transform 'add' needs \"value\".",
                "prefix" or "suffix" when args?["value"] is null => $"Transform '{op}' needs \"value\".",
                _ => null,
            };
            if (need is null && op.Equals("regex", StringComparison.OrdinalIgnoreCase))
            {
                try { _ = new Regex(Str(args!["pattern"])!); }
                catch (ArgumentException ex) { need = $"Invalid regex: {ex.Message}"; }
            }
            return need;
        }
        catch (MappingException ex)
        {
            return ex.Message;
        }
    }

    public static JsonNode? Apply(JsonNode? value, JsonNode spec)
    {
        var (op, args) = ParseSpec(spec);
        if (WholeValueOps.Contains(op)) return ApplyWhole(op.ToLowerInvariant(), value, args);
        if (!ScalarOps.Contains(op)) throw new MappingException($"Unknown transform '{op}'.");

        var lop = op.ToLowerInvariant();
        if (value is JsonArray arr)
        {
            var result = new JsonArray();
            foreach (var item in arr) result.Add(ApplyScalar(lop, item?.DeepClone(), args));
            return result;
        }
        return ApplyScalar(lop, value, args);
    }

    private static JsonNode? ApplyWhole(string op, JsonNode? value, JsonObject? args)
    {
        if (op == "stringify") return value is null ? null : JsonValue.Create(value.ToJsonString());
        if (value is null) return op == "count" ? JsonValue.Create(0) : null;
        var items = value is JsonArray a ? a.ToList() : new List<JsonNode?> { value };

        switch (op)
        {
            case "join":
                var sep = Str(args?["sep"]) ?? ",";
                return JsonValue.Create(string.Join(sep, items.Where(x => x is not null).Select(x => Str(x))));
            case "first": return items.FirstOrDefault()?.DeepClone();
            case "last": return items.LastOrDefault()?.DeepClone();
            case "count": return JsonValue.Create(items.Count);
            case "compact": return new JsonArray(items.Where(x => x is not null).Select(x => x!.DeepClone()).ToArray());
            case "distinct":
                var seen = new HashSet<string>();
                var outArr = new JsonArray();
                foreach (var x in items)
                    if (seen.Add(x?.ToJsonString() ?? "null")) outArr.Add(x?.DeepClone());
                return outArr;
            default: throw new MappingException($"Unknown transform '{op}'.");
        }
    }

    private static JsonNode? ApplyScalar(string op, JsonNode? value, JsonObject? args)
    {
        if (value is null) return null;
        var s = Str(value);

        switch (op)
        {
            case "trim": return JsonValue.Create(s!.Trim());
            case "lower": return JsonValue.Create(s!.ToLowerInvariant());
            case "upper": return JsonValue.Create(s!.ToUpperInvariant());
            case "tostring": return JsonValue.Create(s);
            case "nullifempty": return string.IsNullOrWhiteSpace(s) ? null : value;
            case "tonumber": return Num(ToDouble(value, op));
            case "toint": return JsonValue.Create((long)Math.Truncate(ToDouble(value, op)));
            case "tobool": return JsonValue.Create(ToBool(value));
            case "date": return ConvertDate(value, args);
            case "map":
            {
                var values = args?["values"] as JsonObject ?? throw new MappingException("Transform 'map' needs \"values\".");
                if (s is not null && values.TryGetPropertyValue(s, out var mapped)) return mapped?.DeepClone();
                if (args!.TryGetPropertyValue("fallback", out var fb)) return fb?.DeepClone();
                return value;
            }
            case "split":
            {
                var sep = Str(args?["sep"]) ?? ",";
                var trim = args?["trim"] is not JsonValue tv || !tv.TryGetValue<bool>(out var t) || t;
                var parts = s!.Split(sep, trim ? StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries : StringSplitOptions.None);
                return new JsonArray(parts.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
            }
            case "replace":
            {
                var find = Str(args?["find"]) ?? "";
                var with = Str(args?["with"]) ?? "";
                var isRegex = args?["regex"] is JsonValue rv && rv.TryGetValue<bool>(out var r) && r;
                return JsonValue.Create(isRegex ? Regex.Replace(s!, find, with, RegexOptions.None, TimeSpan.FromSeconds(1)) : s!.Replace(find, with));
            }
            case "regex":
            {
                var pattern = Str(args?["pattern"])!;
                var group = args?["group"] is JsonValue gv && gv.TryGetValue<int>(out var g) ? g : 1;
                var m = Regex.Match(s!, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
                if (!m.Success) return null;
                if (group >= m.Groups.Count) group = 0;
                return JsonValue.Create(m.Groups[group].Value);
            }
            case "round":
            {
                var digits = args?["digits"] is JsonValue dv && dv.TryGetValue<int>(out var d) ? d : 0;
                return Num(Math.Round(ToDouble(value, op), digits, MidpointRounding.AwayFromZero));
            }
            case "multiply": return Num(ToDouble(value, op) * ToDouble(args!["by"]!, op));
            case "add": return Num(ToDouble(value, op) + ToDouble(args!["value"]!, op));
            case "prefix": return JsonValue.Create(Str(args!["value"]) + s);
            case "suffix": return JsonValue.Create(s + Str(args!["value"]));
            case "hash":
            {
                var salt = Str(args?["salt"]) ?? "";
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(salt + s));
                return JsonValue.Create(Convert.ToHexString(bytes).ToLowerInvariant());
            }
            case "mask":
            {
                var keep = args?["keepLast"] is JsonValue kv && kv.TryGetValue<int>(out var k) ? k : 4;
                var ch = (Str(args?["char"]) ?? "*") is { Length: > 0 } c ? c[0] : '*';
                keep = Math.Clamp(keep, 0, s!.Length);
                return JsonValue.Create(new string(ch, s.Length - keep) + s[^keep..]);
            }
            case "json":
                try { return JsonNode.Parse(s!); }
                catch (JsonException ex) { throw new MappingException($"Value is not valid JSON: {ex.Message}"); }
            default: throw new MappingException($"Unknown transform '{op}'.");
        }
    }

    private static JsonNode? ConvertDate(JsonNode value, JsonObject? args)
    {
        var to = Str(args?["to"]) ?? "o";
        var fromNode = args?["from"];
        DateTimeOffset dto;
        var fromStr = Str(fromNode);

        if (fromStr is "unix" or "unixms")
        {
            var n = (long)ToDouble(value, "date");
            dto = fromStr == "unix" ? DateTimeOffset.FromUnixTimeSeconds(n) : DateTimeOffset.FromUnixTimeMilliseconds(n);
        }
        else
        {
            var s = Str(value)!;
            string[]? formats = fromNode switch
            {
                JsonArray fa => fa.Select(x => Str(x)!).ToArray(),
                JsonValue => new[] { fromStr! },
                _ => null,
            };
            const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces;
            var ok = formats is null
                ? DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, styles, out dto)
                : DateTimeOffset.TryParseExact(s, formats, CultureInfo.InvariantCulture, styles, out dto);
            if (!ok) throw new MappingException($"Cannot parse date '{s}'" + (formats is null ? "." : $" with format(s) {string.Join(", ", formats)}."));
        }

        dto = dto.ToUniversalTime();
        return to switch
        {
            "unix" => JsonValue.Create(dto.ToUnixTimeSeconds()),
            "unixms" => JsonValue.Create(dto.ToUnixTimeMilliseconds()),
            _ => JsonValue.Create(dto.ToString(to, CultureInfo.InvariantCulture)),
        };
    }

    public static string? Str(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return node.ToJsonString();
    }

    private static double ToDouble(JsonNode node, string op)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
            if (v.TryGetValue<string>(out var s) &&
                double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }
        throw new MappingException($"Transform '{op}': '{Str(node)}' is not a number.");
    }

    private static JsonNode Num(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 9e15 ? JsonValue.Create((long)d) : JsonValue.Create(d);

    private static bool ToBool(JsonNode node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<double>(out var d)) return d != 0;
            if (v.TryGetValue<string>(out var s))
            {
                switch (s.Trim().ToLowerInvariant())
                {
                    case "true" or "yes" or "y" or "1" or "on": return true;
                    case "false" or "no" or "n" or "0" or "off" or "": return false;
                }
            }
        }
        throw new MappingException($"Transform 'toBool': '{Str(node)}' is not a boolean.");
    }

    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0 && !s.Equals("false", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };
}
