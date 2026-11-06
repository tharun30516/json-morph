using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace JsonMorph.Core;

public enum SegmentKind { Property, Index, Wildcard }

public readonly record struct PathSegment(SegmentKind Kind, string? Name = null, int Index = 0);

/// <summary>
/// Small JSONPath subset: $.a.b, a.b, ['odd key'], [0], [-1] (from end), [*] and .* wildcards.
/// </summary>
public sealed class JsonPath
{
    private static readonly ConcurrentDictionary<string, JsonPath> Cache = new();

    public string Text { get; }
    public IReadOnlyList<PathSegment> Segments { get; }
    public bool IsMulti { get; }

    private JsonPath(string text, List<PathSegment> segments)
    {
        Text = text;
        Segments = segments;
        IsMulti = segments.Any(s => s.Kind == SegmentKind.Wildcard);
    }

    public static JsonPath Parse(string path) => Cache.GetOrAdd(path, p => new JsonPath(p, ParseSegments(p)));

    public static bool TryParse(string path, out JsonPath? result, out string? error)
    {
        try
        {
            result = Parse(path);
            error = null;
            return true;
        }
        catch (MappingException ex)
        {
            result = null;
            error = ex.Message;
            return false;
        }
    }

    private static List<PathSegment> ParseSegments(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new MappingException("Path is empty.");
        var s = path.Trim();
        var segs = new List<PathSegment>();
        var i = 0;
        if (s[0] == '$') i = 1;
        else if (s[0] != '.' && s[0] != '[') s = "." + s; // bare "a.b" form

        while (i < s.Length)
        {
            var c = s[i];
            if (c == '.')
            {
                i++;
                var start = i;
                while (i < s.Length && s[i] != '.' && s[i] != '[') i++;
                var name = s[start..i];
                if (name.Length == 0) throw new MappingException($"Empty property name in path '{path}'.");
                segs.Add(name == "*" ? new PathSegment(SegmentKind.Wildcard) : new PathSegment(SegmentKind.Property, name));
            }
            else if (c == '[')
            {
                var close = s.IndexOf(']', i);
                if (close < 0) throw new MappingException($"Unclosed '[' in path '{path}'.");
                var inner = s[(i + 1)..close].Trim();
                if (inner == "*")
                {
                    segs.Add(new PathSegment(SegmentKind.Wildcard));
                }
                else if (inner.Length >= 2 && (inner[0] == '\'' || inner[0] == '"') && inner[^1] == inner[0])
                {
                    segs.Add(new PathSegment(SegmentKind.Property, inner[1..^1]));
                }
                else if (int.TryParse(inner, out var idx))
                {
                    segs.Add(new PathSegment(SegmentKind.Index, Index: idx));
                }
                else
                {
                    throw new MappingException($"Invalid bracket expression '[{inner}]' in path '{path}'.");
                }
                i = close + 1;
            }
            else
            {
                throw new MappingException($"Unexpected character '{c}' at position {i} in path '{path}'.");
            }
        }
        return segs;
    }

    public IEnumerable<JsonNode?> Select(JsonNode? root)
    {
        IEnumerable<JsonNode?> current = new[] { root };
        foreach (var seg in Segments)
        {
            var next = new List<JsonNode?>();
            foreach (var node in current)
            {
                switch (seg.Kind)
                {
                    case SegmentKind.Property:
                        if (node is JsonObject o && o.TryGetPropertyValue(seg.Name!, out var v)) next.Add(v);
                        break;
                    case SegmentKind.Index:
                        if (node is JsonArray a)
                        {
                            var idx = seg.Index < 0 ? a.Count + seg.Index : seg.Index;
                            if (idx >= 0 && idx < a.Count) next.Add(a[idx]);
                        }
                        break;
                    case SegmentKind.Wildcard:
                        if (node is JsonArray arr) next.AddRange(arr);
                        else if (node is JsonObject obj) next.AddRange(obj.Select(kv => kv.Value));
                        break;
                }
            }
            current = next;
        }
        return current;
    }

    /// <summary>Writes a value at this path, creating intermediate objects/arrays.</summary>
    public void SetValue(JsonObject root, JsonNode? value)
    {
        if (IsMulti) throw new MappingException($"Target path '{Text}' cannot contain wildcards.");
        if (Segments.Count == 0) throw new MappingException("Target path cannot be the root.");

        JsonNode current = root;
        for (var i = 0; i < Segments.Count; i++)
        {
            var seg = Segments[i];
            var last = i == Segments.Count - 1;
            JsonNode NewContainer() => Segments[i + 1].Kind == SegmentKind.Index ? new JsonArray() : new JsonObject();

            if (seg.Kind == SegmentKind.Property)
            {
                if (current is not JsonObject obj)
                    throw new MappingException($"Target path '{Text}' conflicts with an existing non-object value.");
                if (last) { obj[seg.Name!] = value; return; }
                if (!obj.TryGetPropertyValue(seg.Name!, out var child) || child is null)
                {
                    child = NewContainer();
                    obj[seg.Name!] = child;
                }
                current = child;
            }
            else
            {
                if (seg.Index < 0) throw new MappingException($"Target path '{Text}' cannot use negative indexes.");
                if (current is not JsonArray arr)
                    throw new MappingException($"Target path '{Text}' conflicts with an existing non-array value.");
                while (arr.Count <= seg.Index) arr.Add(null);
                if (last) { arr[seg.Index] = value; return; }
                var child = arr[seg.Index];
                if (child is null)
                {
                    child = NewContainer();
                    arr[seg.Index] = child;
                }
                current = child;
            }
        }
    }

    /// <summary>Formats a property name as a path segment (".name" or "['odd name']").</summary>
    public static string FormatProperty(string name)
    {
        var safe = name.Length > 0 && name.All(ch => ch != '.' && ch != '[' && ch != ']' && ch != '\'' && ch != '"' && !char.IsWhiteSpace(ch)) && name != "*";
        if (safe) return "." + name;
        var sb = new StringBuilder("['");
        sb.Append(name.Replace("'", "\\'"));
        sb.Append("']");
        return sb.ToString();
    }

    public override string ToString() => Text;
}
