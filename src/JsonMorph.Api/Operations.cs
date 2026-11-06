using System.Text.Json.Nodes;
using JsonMorph.Api.Migrations;
using JsonMorph.Core;

namespace JsonMorph.Api;

/// <summary>The service's use cases, shared by the REST endpoints and the MCP tools.</summary>
public sealed class Operations(MappingEngine engine, MappingStore store, MigrationService migrations)
{
    public JsonObject Transform(JsonNode? input, string? mappingName, int? mappingVersion, MappingDefinition? mapping)
    {
        var def = migrations.ResolveMapping(mappingName, mappingVersion, mapping);
        if (input is JsonArray arr)
        {
            var results = new JsonArray();
            var failed = 0;
            for (var i = 0; i < arr.Count; i++)
            {
                var r = engine.Map(arr[i], def);
                if (!r.Success) failed++;
                results.Add(new JsonObject
                {
                    ["index"] = i,
                    ["output"] = r.Output,
                    ["errors"] = ErrorsJson(r.Errors),
                });
            }
            return new JsonObject { ["success"] = failed == 0, ["total"] = arr.Count, ["failed"] = failed, ["results"] = results };
        }

        var single = engine.Map(input, def);
        return new JsonObject { ["success"] = single.Success, ["output"] = single.Output, ["errors"] = ErrorsJson(single.Errors) };
    }

    /// <summary>Infers a mapping from an A/B example, then proves it by running it on A and comparing to B.</summary>
    public JsonObject Infer(JsonNode? source, JsonNode? target, string? name, bool save)
    {
        var inferred = MappingInferrer.Infer(source, target, name);
        var check = engine.Map(source, inferred.Mapping);
        var reproduces = JsonNode.DeepEquals(check.Output, target);

        MappingDefinition mapping = inferred.Mapping;
        if (save)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ValidationException("A name is required to save the inferred mapping.");
            mapping = store.Save(inferred.Mapping);
        }

        return new JsonObject
        {
            ["mapping"] = ToNode(mapping),
            ["reproducesTarget"] = reproduces,
            ["preview"] = check.Output,
            ["unmatched"] = new JsonArray(inferred.Unmatched.Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()),
            ["notes"] = new JsonArray(inferred.Notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            ["saved"] = save,
        };
    }

    public JsonObject Validate(MappingDefinition? mapping)
    {
        var errors = MappingValidator.Validate(mapping);
        return new JsonObject
        {
            ["valid"] = errors.Count == 0,
            ["errors"] = new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
        };
    }

    public static JsonNode? ToNode<T>(T value) => System.Text.Json.JsonSerializer.SerializeToNode(value, JsonDefaults.Options);

    private static JsonArray ErrorsJson(IEnumerable<MappingError> errors) =>
        new(errors.Select(e => (JsonNode?)new JsonObject { ["target"] = e.Target, ["message"] = e.Message }).ToArray());
}
