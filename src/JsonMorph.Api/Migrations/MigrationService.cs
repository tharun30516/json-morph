using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using JsonMorph.Core;
using Microsoft.Extensions.Options;

namespace JsonMorph.Api.Migrations;

/// <summary>Accepts migration jobs, tracks them, and lets callers inspect/cancel them.</summary>
public sealed class MigrationService(MappingStore store, MigrationIo io, IOptions<JsonMorphOptions> options)
{
    private readonly ConcurrentDictionary<Guid, MigrationJob> _jobs = new();
    private readonly Channel<MigrationJob> _queue = Channel.CreateUnbounded<MigrationJob>();

    internal ChannelReader<MigrationJob> Queue => _queue.Reader;

    public MigrationJob Start(MigrationRequest request)
    {
        var mapping = ResolveMapping(request.MappingName, request.MappingVersion, request.Mapping);
        var max = options.Value.MaxBatchSize;
        if (request.BatchSize < 1 || request.BatchSize > max)
            throw new ValidationException($"batchSize must be between 1 and {max}.");
        if (request.MaxErrors is < 0) throw new ValidationException("maxErrors cannot be negative.");
        io.ValidateSource(request.Source);
        if (!request.DryRun) io.ValidateSink(request.Sink, request.DeadLetterPath);

        var job = new MigrationJob(request, mapping, options.Value.MaxMemoryRecords);
        _jobs[job.Id] = job;
        _queue.Writer.TryWrite(job);
        return job;
    }

    public MappingDefinition ResolveMapping(string? name, int? version, MappingDefinition? inline)
    {
        if (inline is not null && name is not null) throw new ValidationException("Use either mappingName or mapping, not both.");
        var mapping = inline ?? (name is not null ? store.Get(name, version) : throw new ValidationException("mappingName or mapping is required."));
        var errors = MappingValidator.Validate(mapping);
        if (errors.Count > 0) throw new ValidationException(errors);
        return mapping;
    }

    public MigrationJob Get(Guid id) =>
        _jobs.TryGetValue(id, out var job) ? job : throw new NotFoundException($"Migration {id} not found.");

    public IReadOnlyList<MigrationSnapshot> List() =>
        _jobs.Values.OrderByDescending(j => j.CreatedAt).Select(j => j.Snapshot()).ToList();

    public MigrationSnapshot Cancel(Guid id)
    {
        var job = Get(id);
        if (!job.IsFinished)
        {
            job.Cancellation.Cancel();
            if (job.Status == MigrationStatus.Queued) job.MarkCancelled();
        }
        return job.Snapshot();
    }
}

/// <summary>Background worker: streams source records, maps them, writes batches, dead-letters failures.</summary>
public sealed class MigrationWorker(
    MigrationService service, MigrationIo io, MappingEngine engine,
    IOptions<JsonMorphOptions> options, ILogger<MigrationWorker> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, Math.Max(1, options.Value.MaxConcurrentJobs))
            .Select(_ => Task.Run(() => Loop(stoppingToken), stoppingToken));
        return Task.WhenAll(workers);
    }

    private async Task Loop(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in service.Queue.ReadAllAsync(stoppingToken))
            {
                if (job.IsFinished) continue; // cancelled while queued
                await RunAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task RunAsync(MigrationJob job, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancellation.Token);
        var ct = linked.Token;
        var req = job.Request;
        job.MarkRunning();
        log.LogInformation("Migration {Id} started (mapping {Mapping})", job.Id, job.Mapping.Name ?? "inline");

        try
        {
            await using var sink = io.CreateSink(job);
            await using var deadLetter = req.DryRun ? null : io.OpenDeadLetter(job);
            var batch = new List<JsonNode>(req.BatchSize);
            long index = 0;

            await foreach (var record in io.ReadAsync(req.Source, ct))
            {
                var i = index++;
                job.RecordRead();
                var result = engine.Map(record, job.Mapping);

                if (result.Success) job.RecordSuccess();
                else
                {
                    job.RecordFailure(i, record, result.Errors);
                    if (deadLetter is not null)
                    {
                        var entry = new JsonObject
                        {
                            ["record"] = i,
                            ["input"] = record?.DeepClone(),
                            ["errors"] = new JsonArray(result.Errors.Select(e => (JsonNode?)new JsonObject { ["target"] = e.Target, ["message"] = e.Message }).ToArray()),
                        };
                        await deadLetter.WriteLineAsync(entry.ToJsonString(JsonDefaults.Options));
                    }
                    if (req.MaxErrors is { } maxErr && job.FailedCount > maxErr)
                        throw new MigrationAbortedException($"Aborted: {job.FailedCount} failed records exceeded maxErrors={maxErr}. Output was not committed.");
                }

                if (result.Success || req.WritePartialRecords) batch.Add(result.Output);
                if (batch.Count >= req.BatchSize)
                {
                    await sink.WriteBatchAsync(batch, ct);
                    job.RecordWritten(batch.Count);
                    batch = new List<JsonNode>(req.BatchSize);
                }
            }

            if (batch.Count > 0)
            {
                await sink.WriteBatchAsync(batch, ct);
                job.RecordWritten(batch.Count);
            }
            await sink.CompleteAsync(ct);
            job.MarkCompleted();
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            job.MarkCancelled();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            job.MarkFailed("Service shut down while the migration was running.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Migration {Id} failed", job.Id);
            job.MarkFailed(ex.Message);
        }
        log.LogInformation("Migration {Id} finished: {Status}", job.Id, job.Status);
    }
}

public sealed class MigrationAbortedException(string message) : Exception(message);
