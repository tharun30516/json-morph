using System.Collections.Concurrent;
using System.Text.Json;
using JsonMorph.Core;
using Microsoft.Extensions.Options;

namespace JsonMorph.Api;

public sealed class JsonMorphOptions
{
    /// <summary>Root folder for stored mappings and file sources/sinks. File paths can't escape it.</summary>
    public string DataRoot { get; set; } = "data";

    /// <summary>If set, every /api and /mcp request needs header X-Api-Key (or Authorization: Bearer).</summary>
    public string? ApiKey { get; set; }

    public int MaxConcurrentJobs { get; set; } = 2;
    public int MaxBatchSize { get; set; } = 5_000;

    /// <summary>Cap for records kept in memory per job (memory sink, dry-run preview, dead letters).</summary>
    public int MaxMemoryRecords { get; set; } = 10_000;

    /// <summary>Hosts allowed for http sources/sinks. "*" allows any host. Empty disables HTTP I/O.</summary>
    public List<string> AllowedHttpHosts { get; set; } = new();
}

public sealed class ValidationException(IReadOnlyList<string> errors) : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
    public ValidationException(string error) : this(new[] { error }) { }
}

public sealed class NotFoundException(string message) : Exception(message);

public sealed class DataPaths(IOptions<JsonMorphOptions> options)
{
    public string Root { get; } = EnsureDir(Path.GetFullPath(options.Value.DataRoot));

    public string MappingsDir => EnsureDir(Path.Combine(Root, "mappings"));

    /// <summary>Resolves a user-supplied relative path, refusing anything outside the data root.</summary>
    public string Resolve(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new ValidationException("A file path is required.");
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ValidationException($"Path '{relative}' must stay inside the data folder.");
        return full;
    }

    private static string EnsureDir(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public sealed record MappingSummary(string Name, int LatestVersion, IReadOnlyList<int> Versions, string? Description);

/// <summary>Versioned mapping store. Every save creates a new immutable version, persisted as JSON files.</summary>
public sealed class MappingStore
{
    private readonly ConcurrentDictionary<string, SortedDictionary<int, MappingDefinition>> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly DataPaths _paths;
    private readonly object _writeLock = new();

    public MappingStore(DataPaths paths, ILogger<MappingStore> log)
    {
        _paths = paths;
        foreach (var file in Directory.EnumerateFiles(paths.MappingsDir, "*.json"))
        {
            try
            {
                var def = JsonSerializer.Deserialize<MappingDefinition>(File.ReadAllText(file), JsonDefaults.Options);
                if (def?.Name is null || def.Version <= 0) continue;
                _items.GetOrAdd(def.Name, _ => new())[def.Version] = def;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                log.LogWarning(ex, "Skipping unreadable mapping file {File}", file);
            }
        }
    }

    public MappingDefinition Save(MappingDefinition mapping)
    {
        var errors = MappingValidator.Validate(mapping, requireName: true);
        if (errors.Count > 0) throw new ValidationException(errors);

        lock (_writeLock)
        {
            var versions = _items.GetOrAdd(mapping.Name!, _ => new());
            mapping.Version = versions.Count == 0 ? 1 : versions.Keys.Max() + 1;
            var file = Path.Combine(_paths.MappingsDir, $"{mapping.Name}.v{mapping.Version}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(mapping, JsonDefaults.Indented));
            versions[mapping.Version] = mapping;
            return mapping;
        }
    }

    public MappingDefinition Get(string name, int? version = null)
    {
        if (!_items.TryGetValue(name, out var versions) || versions.Count == 0)
            throw new NotFoundException($"Mapping '{name}' not found.");
        lock (_writeLock)
        {
            if (version is null) return versions[versions.Keys.Max()];
            return versions.TryGetValue(version.Value, out var def)
                ? def
                : throw new NotFoundException($"Mapping '{name}' has no version {version}.");
        }
    }

    public IReadOnlyList<MappingSummary> List()
    {
        lock (_writeLock)
        {
            return _items.Where(kv => kv.Value.Count > 0)
                .Select(kv => new MappingSummary(kv.Key, kv.Value.Keys.Max(), kv.Value.Keys.ToList(), kv.Value[kv.Value.Keys.Max()].Description))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public bool Delete(string name)
    {
        lock (_writeLock)
        {
            if (!_items.TryRemove(name, out var versions)) return false;
            foreach (var v in versions.Keys)
            {
                var file = Path.Combine(_paths.MappingsDir, $"{name}.v{v}.json");
                if (File.Exists(file)) File.Delete(file);
            }
            return true;
        }
    }
}
