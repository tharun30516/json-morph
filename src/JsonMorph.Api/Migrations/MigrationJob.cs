using System.Text.Json.Nodes;
using JsonMorph.Core;

namespace JsonMorph.Api.Migrations;

public enum SourceType { Inline, File, Http }
public enum SinkType { Memory, File, Http }
public enum OutputFormat { Ndjson, Json }
public enum MigrationStatus { Queued, Running, Completed, CompletedWithErrors, Failed, Cancelled }

public sealed class SourceSpec
{
    public SourceType Type { get; set; } = SourceType.Inline;
    /// <summary>Inline records (type=inline).</summary>
    public JsonArray? Records { get; set; }
    /// <summary>Relative to the data folder. .ndjson/.jsonl = one record per line; .json = array (streamed) or document.</summary>
    public string? Path { get; set; }
    public string? Url { get; set; }
    /// <summary>Where the records live inside a JSON document, e.g. "$.data.items".</summary>
    public string? RecordsPath { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
}

public sealed class SinkSpec
{
    public SinkType Type { get; set; } = SinkType.Memory;
    public string? Path { get; set; }
    public string? Url { get; set; }
    public OutputFormat Format { get; set; } = OutputFormat.Ndjson;
    public Dictionary<string, string>? Headers { get; set; }
}

public sealed class MigrationRequest
{
    public string? MappingName { get; set; }
    public int? MappingVersion { get; set; }
    /// <summary>Inline mapping (alternative to mappingName).</summary>
    public MappingDefinition? Mapping { get; set; }
    public SourceSpec Source { get; set; } = new();
    public SinkSpec Sink { get; set; } = new();
    public int BatchSize { get; set; } = 500;
    /// <summary>Map everything but write nothing; output kept in memory as a preview.</summary>
    public bool DryRun { get; set; }
    /// <summary>Abort (and discard file output) once failed records exceed this.</summary>
    public long? MaxErrors { get; set; }
    /// <summary>Write records that had mapping errors anyway (they still go to dead-letter).</summary>
    public bool WritePartialRecords { get; set; }
    /// <summary>Optional .ndjson file (in the data folder) for failed records.</summary>
    public string? DeadLetterPath { get; set; }
}

public sealed record RecordError(long Record, string Target, string Message);
public sealed record DeadLetter(long Record, JsonNode? Input, IReadOnlyList<MappingError> Errors);

public sealed record MigrationSnapshot(
    Guid Id,
    MigrationStatus Status,
    string? Mapping,
    int? MappingVersion,
    SourceType Source,
    SinkType Sink,
    bool DryRun,
    long Read,
    long Succeeded,
    long Failed,
    long Written,
    int Batches,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    double? DurationMs,
    double? RecordsPerSecond,
    string? FailureReason,
    bool OutputTruncated,
    IReadOnlyList<RecordError> Errors);

public sealed class MigrationJob(MigrationRequest request, MappingDefinition mapping, int memoryCap)
{
    private readonly object _lock = new();
    private readonly List<JsonNode> _output = new();
    private readonly List<DeadLetter> _deadLetters = new();
    private readonly List<RecordError> _errors = new();
    private long _read, _succeeded, _failed, _written;
    private int _batches;
    private bool _outputTruncated;

    public Guid Id { get; } = Guid.NewGuid();
    public MigrationRequest Request { get; } = request;
    public MappingDefinition Mapping { get; } = mapping;
    public CancellationTokenSource Cancellation { get; } = new();
    public MigrationStatus Status { get; private set; } = MigrationStatus.Queued;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? FailureReason { get; private set; }
    public long FailedCount => Interlocked.Read(ref _failed);

    public bool IsFinished => Status is MigrationStatus.Completed or MigrationStatus.CompletedWithErrors
        or MigrationStatus.Failed or MigrationStatus.Cancelled;

    public void MarkRunning() { lock (_lock) { Status = MigrationStatus.Running; StartedAt = DateTimeOffset.UtcNow; } }

    public void MarkCompleted() => Finish(FailedCount > 0 ? MigrationStatus.CompletedWithErrors : MigrationStatus.Completed, null);
    public void MarkFailed(string reason) => Finish(MigrationStatus.Failed, reason);
    public void MarkCancelled() => Finish(MigrationStatus.Cancelled, "Cancelled by request.");

    private void Finish(MigrationStatus status, string? reason)
    {
        lock (_lock)
        {
            if (IsFinished) return;
            Status = status;
            FailureReason = reason;
            FinishedAt = DateTimeOffset.UtcNow;
        }
    }

    public void RecordRead() => Interlocked.Increment(ref _read);
    public void RecordSuccess() => Interlocked.Increment(ref _succeeded);
    public void RecordWritten(int count) { Interlocked.Add(ref _written, count); Interlocked.Increment(ref _batches); }

    public void RecordFailure(long index, JsonNode? input, IReadOnlyList<MappingError> errors)
    {
        Interlocked.Increment(ref _failed);
        lock (_lock)
        {
            if (_deadLetters.Count < memoryCap) _deadLetters.Add(new DeadLetter(index, input, errors));
            foreach (var e in errors)
                if (_errors.Count < 200) _errors.Add(new RecordError(index, e.Target, e.Message));
        }
    }

    public void AddOutput(IEnumerable<JsonNode> records)
    {
        lock (_lock)
        {
            foreach (var r in records)
            {
                if (_output.Count >= memoryCap) { _outputTruncated = true; return; }
                _output.Add(r);
            }
        }
    }

    public IReadOnlyList<JsonNode> GetOutput(int skip, int take)
    {
        lock (_lock) return _output.Skip(skip).Take(take).Select(n => n.DeepClone()).ToList();
    }

    public IReadOnlyList<DeadLetter> GetDeadLetters(int skip, int take)
    {
        lock (_lock) return _deadLetters.Skip(skip).Take(take).ToList();
    }

    public MigrationSnapshot Snapshot()
    {
        lock (_lock)
        {
            var end = FinishedAt ?? DateTimeOffset.UtcNow;
            double? ms = StartedAt is { } s ? (end - s).TotalMilliseconds : null;
            var read = Interlocked.Read(ref _read);
            return new MigrationSnapshot(
                Id, Status, Mapping.Name, Mapping.Version == 0 ? null : Mapping.Version,
                Request.Source.Type, Request.DryRun ? SinkType.Memory : Request.Sink.Type, Request.DryRun,
                read, Interlocked.Read(ref _succeeded), FailedCount, Interlocked.Read(ref _written), _batches,
                CreatedAt, StartedAt, FinishedAt, ms,
                ms is > 0 ? Math.Round(read / (ms.Value / 1000d), 1) : null,
                FailureReason, _outputTruncated, _errors.Take(50).ToList());
        }
    }
}
