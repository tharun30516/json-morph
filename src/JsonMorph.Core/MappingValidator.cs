using System.Text.RegularExpressions;

namespace JsonMorph.Core;

public static partial class MappingValidator
{
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name) && !name.Contains("..");

    /// <param name="requireName">True when the mapping is being stored (name is used as a file name).</param>
    public static List<string> Validate(MappingDefinition? mapping, bool requireName = false)
    {
        var errors = new List<string>();
        if (mapping is null)
        {
            errors.Add("Mapping is required.");
            return errors;
        }
        if (requireName && !IsValidName(mapping.Name))
            errors.Add("Name must be 1-100 chars of letters, digits, '_', '-' or '.'.");
        ValidateRules(mapping, "rules", errors, depth: 0);
        return errors;
    }

    private static void ValidateRules(MappingDefinition def, string at, List<string> errors, int depth)
    {
        if (depth > 16) { errors.Add($"{at}: nesting too deep."); return; }
        if (def.Rules is not { Count: > 0 }) { errors.Add($"{at}: at least one rule is required."); return; }

        for (var i = 0; i < def.Rules.Count; i++)
        {
            var r = def.Rules[i];
            var here = $"{at}[{i}]({r.Target})";

            if (string.IsNullOrWhiteSpace(r.Target)) errors.Add($"{here}: target is required.");
            else if (!JsonPath.TryParse(r.Target, out var tp, out var err)) errors.Add($"{here}: bad target: {err}");
            else if (tp!.IsMulti) errors.Add($"{here}: target cannot contain wildcards.");
            else if (tp.Segments.Any(s => s.Kind == SegmentKind.Index && s.Index < 0)) errors.Add($"{here}: target cannot use negative indexes.");

            var valueSources = (r.Const is not null ? 1 : 0) + (r.Template is not null ? 1 : 0)
                             + (r.Sources is { Count: > 0 } ? 1 : 0) + (r.Source is not null ? 1 : 0);
            if (valueSources != 1)
                errors.Add($"{here}: set exactly one of source, sources, template or const (found {valueSources}).");

            foreach (var p in (r.Sources ?? new()).Append(r.Source).Where(p => p is not null and not "$" and not "$now"))
                if (!JsonPath.TryParse(p!, out _, out var perr)) errors.Add($"{here}: bad source path: {perr}");

            if (r.When is { } w && !JsonPath.TryParse(w.TrimStart('!'), out _, out var werr))
                errors.Add($"{here}: bad when path: {werr}");

            if (r.Transform is { } ts)
                foreach (var t in ts)
                    if (Transforms.Validate(t) is { } terr) errors.Add($"{here}: {terr}");

            if (r.Each is { } nested) ValidateRules(nested, $"{here}.each.rules", errors, depth + 1);
        }
    }
}
