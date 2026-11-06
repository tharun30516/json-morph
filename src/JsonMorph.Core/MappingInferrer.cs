using System.Globalization;
using System.Text.Json.Nodes;

namespace JsonMorph.Core;

public sealed record InferenceResult(MappingDefinition Mapping, IReadOnlyList<string> Unmatched, IReadOnlyList<string> Notes);

/// <summary>
/// Infers a draft mapping from one example input (A) and the output you want (B) by matching values.
/// Handles renames, nesting changes, trim/lower/upper, string-number-bool casts, "first last" templates,
/// and arrays of objects (via nested "each" mappings). Review the draft before using it.
/// </summary>
public static class MappingInferrer
{
    private sealed record Candidate(string Path, List<string> Transforms, int Score);

    public static InferenceResult Infer(JsonNode? source, JsonNode? target, string? name = null)
    {
        if (target is not JsonObject tgt) throw new MappingException("Target sample must be a JSON object.");
        var unmatched = new List<string>();
        var notes = new List<string>();
        var rules = InferRules(source, tgt, "", unmatched, notes, 0);
        var mapping = new MappingDefinition { Name = name, Description = "Inferred from sample A/B pair", Rules = rules };
        return new InferenceResult(mapping, unmatched, notes);
    }

    private static List<MappingRule> InferRules(JsonNode? src, JsonObject tgt, string prefix,
        List<string> unmatched, List<string> notes, int depth)
    {
        var rules = new List<MappingRule>();
        if (depth > 8) return rules;
        var leaves = new List<(string Path, JsonValue Value)>();
        CollectLeaves(src, "$", leaves);

        foreach (var (name, tval) in tgt)
        {
            var seg = JsonPath.FormatProperty(name);
            var tpath = prefix.Length == 0 && seg.StartsWith('.') ? seg[1..] : prefix + seg;

            switch (tval)
            {
                case JsonObject o:
                    rules.AddRange(InferRules(src, o, tpath, unmatched, notes, depth + 1));
                    break;

                case JsonArray { Count: > 0 } a when a[0] is JsonObject firstObj:
                {
                    List<MappingRule>? best = null;
                    string? bestPath = null;
                    List<string>? bestMissing = null;
                    foreach (var (spath, sarr) in ArraysOfObjects(src, "$"))
                    {
                        var missing = new List<string>();
                        var sub = InferRules(sarr[0], firstObj, "", missing, notes, depth + 1);
                        if (sub.Count > (best?.Count ?? 0)) { best = sub; bestPath = spath; bestMissing = missing; }
                    }
                    if (best is null) { unmatched.Add(tpath); break; }
                    rules.Add(new MappingRule { Target = tpath, Source = bestPath, Each = new MappingDefinition { Rules = best } });
                    unmatched.AddRange(bestMissing!.Select(m => $"{tpath}[*].{m}"));
                    break;
                }

                case JsonArray a:
                {
                    var rule = MatchWholeArray(src, a, tpath, leaves);
                    if (rule is null) unmatched.Add(tpath); else rules.Add(rule);
                    break;
                }

                case JsonValue v:
                {
                    var rule = MatchScalar(v, name, tpath, leaves, notes);
                    if (rule is null) unmatched.Add(tpath); else rules.Add(rule);
                    break;
                }

                default:
                    unmatched.Add(tpath);
                    break;
            }
        }
        return rules;
    }

    private static MappingRule? MatchScalar(JsonValue t, string targetName, string tpath,
        List<(string Path, JsonValue Value)> leaves, List<string> notes)
    {
        var tText = t.ToJsonString();
        var tStr = Transforms.Str(t)!;
        if (tStr.Length == 0) return null;

        Candidate? best = null;
        foreach (var (path, s) in leaves)
        {
            var c = Score(t, tText, tStr, s, path);
            if (c is null) continue;
            if (LastName(path).Equals(targetName, StringComparison.OrdinalIgnoreCase)) c = c with { Score = c.Score + 10 };
            if (best is null || c.Score > best.Score || (c.Score == best.Score && c.Path.Length < best.Path.Length)) best = c;
        }

        if (best is not null)
        {
            var weak = t.TryGetValue<bool>(out _) || (t.TryGetValue<double>(out var d) && Math.Abs(d) < 10);
            if (weak && !LastName(best.Path).Equals(targetName, StringComparison.OrdinalIgnoreCase))
                notes.Add($"{tpath}: low-confidence match on '{best.Path}' (small/boolean value). Double-check it.");
            return new MappingRule
            {
                Target = tpath,
                Source = best.Path,
                Transform = best.Transforms.Count == 0 ? null : best.Transforms.Select(x => (JsonNode)JsonValue.Create(x)).ToList(),
            };
        }

        // "Jane Doe" built from two source fields -> template.
        if (t.TryGetValue<string>(out _) && tStr.Contains(' '))
        {
            var parts = tStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var used = new HashSet<string>();
            var paths = new List<string>();
            foreach (var part in parts)
            {
                var hit = leaves.FirstOrDefault(l => !used.Contains(l.Path) && l.Value.TryGetValue<string>(out var ls) && ls == part);
                if (hit.Path is null) return null;
                used.Add(hit.Path);
                paths.Add(hit.Path);
            }
            if (paths.Count >= 2)
                return new MappingRule { Target = tpath, Template = string.Join(" ", paths.Select(p => "{" + p + "}")) };
        }
        return null;
    }

    private static Candidate? Score(JsonValue t, string tText, string tStr, JsonValue s, string path)
    {
        if (s.ToJsonString() == tText) return new Candidate(path, new(), 100);
        var tIsString = t.TryGetValue<string>(out _);
        var sIsString = s.TryGetValue<string>(out var sStr);

        if (tIsString && sIsString)
        {
            if (sStr!.Trim() == tStr) return new Candidate(path, new() { "trim" }, 90);
            if (sStr.ToLowerInvariant() == tStr) return new Candidate(path, new() { "lower" }, 85);
            if (sStr.ToUpperInvariant() == tStr) return new Candidate(path, new() { "upper" }, 85);
            if (sStr.Trim().ToLowerInvariant() == tStr) return new Candidate(path, new() { "trim", "lower" }, 80);
            if (sStr.Trim().ToUpperInvariant() == tStr) return new Candidate(path, new() { "trim", "upper" }, 80);
            return null;
        }
        if (t.TryGetValue<double>(out var tNum) && sIsString &&
            double.TryParse(sStr!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sNum) && sNum == tNum)
            return new Candidate(path, new() { "toNumber" }, 80);
        if (tIsString && !sIsString && Transforms.Str(s) == tStr)
            return new Candidate(path, new() { "toString" }, 80);
        if (t.TryGetValue<bool>(out var tb) && !s.TryGetValue<bool>(out _))
        {
            try
            {
                if (Transforms.Apply(s, JsonValue.Create("toBool")!) is JsonValue bv && bv.GetValue<bool>() == tb)
                    return new Candidate(path, new() { "toBool" }, 60);
            }
            catch (MappingException) { }
        }
        return null;
    }

    private static MappingRule? MatchWholeArray(JsonNode? src, JsonArray target, string tpath, List<(string Path, JsonValue Value)> leaves)
    {
        var text = target.ToJsonString();
        foreach (var (path, node) in AllNodes(src, "$"))
            if (node is JsonArray && node.ToJsonString() == text) return new MappingRule { Target = tpath, Source = path };

        if (target.Count > 0 && target.All(x => x is JsonValue v && v.TryGetValue<string>(out _)))
        {
            foreach (var sep in new[] { ",", ";", "|", " " })
                foreach (var (path, v) in leaves)
                {
                    if (!v.TryGetValue<string>(out var s)) continue;
                    var parts = s.Split(sep, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == target.Count && parts.SequenceEqual(target.Select(x => x!.GetValue<string>())))
                        return new MappingRule
                        {
                            Target = tpath, Source = path,
                            Transform = new() { new JsonObject { ["op"] = "split", ["sep"] = sep } },
                        };
                }
        }
        return null;
    }

    private static void CollectLeaves(JsonNode? node, string path, List<(string, JsonValue)> into)
    {
        foreach (var (p, n) in AllNodes(node, path))
            if (n is JsonValue v) into.Add((p, v));
    }

    private static IEnumerable<(string Path, JsonNode? Node)> AllNodes(JsonNode? node, string path)
    {
        yield return (path, node);
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                    foreach (var x in AllNodes(v, path + JsonPath.FormatProperty(k))) yield return x;
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    foreach (var x in AllNodes(a[i], $"{path}[{i}]")) yield return x;
                break;
        }
    }

    private static IEnumerable<(string Path, JsonArray Array)> ArraysOfObjects(JsonNode? node, string path)
    {
        if (node is JsonArray { Count: > 0 } a && a[0] is JsonObject) yield return (path + "[*]", a);
        if (node is JsonObject o)
            foreach (var (k, v) in o)
                foreach (var x in ArraysOfObjects(v, path + JsonPath.FormatProperty(k))) yield return x;
    }

    private static string LastName(string path)
    {
        var jp = JsonPath.Parse(path);
        for (var i = jp.Segments.Count - 1; i >= 0; i--)
            if (jp.Segments[i].Kind == SegmentKind.Property) return jp.Segments[i].Name!;
        return "";
    }
}
