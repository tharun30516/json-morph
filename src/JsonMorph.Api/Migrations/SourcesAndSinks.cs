using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JsonMorph.Core;
using Microsoft.Extensions.Options;

namespace JsonMorph.Api.Migrations;

public interface IRecordSink : IAsyncDisposable
{
    Task WriteBatchAsync(IReadOnlyList<JsonNode> batch, CancellationToken ct);
    /// <summary>Commits the output. A sink disposed without completing discards what it wrote where it can.</summary>
    Task CompleteAsync(CancellationToken ct);
}

/// <summary>Builds record readers and writers, enforcing the data-folder and HTTP host allowlist.</summary>
public sealed class MigrationIo(DataPaths paths, IHttpClientFactory http, IOptions<JsonMorphOptions> options)
{
    private readonly JsonMorphOptions _options = options.Value;

    public void ValidateSource(SourceSpec spec)
    {
        switch (spec.Type)
        {
            case SourceType.Inline when spec.Records is null:
                throw new ValidationException("source.records is required for inline sources.");
            case SourceType.File:
                var f = paths.Resolve(spec.Path);
                if (!File.Exists(f)) throw new ValidationException($"Source file '{spec.Path}' does not exist.");
                break;
            case SourceType.Http:
                CheckUrl(spec.Url);
                break;
        }
        if (spec.RecordsPath is { } rp && !JsonPath.TryParse(rp, out _, out var err))
            throw new ValidationException($"source.recordsPath: {err}");
    }

    public void ValidateSink(SinkSpec spec, string? deadLetterPath)
    {
        if (spec.Type == SinkType.File) paths.Resolve(spec.Path);
        if (spec.Type == SinkType.Http) CheckUrl(spec.Url);
        if (deadLetterPath is not null) paths.Resolve(deadLetterPath);
    }

    private Uri CheckUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ValidationException($"'{url}' is not a valid http(s) URL.");
        var allowed = _options.AllowedHttpHosts;
        if (!allowed.Contains("*") && !allowed.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new ValidationException($"Host '{uri.Host}' is not in JsonMorph:AllowedHttpHosts.");
        return uri;
    }

    public async IAsyncEnumerable<JsonNode?> ReadAsync(SourceSpec spec, [EnumeratorCancellation] CancellationToken ct)
    {
        switch (spec.Type)
        {
            case SourceType.Inline:
                foreach (var r in Extract(spec.Records, spec.RecordsPath)) yield return r;
                break;

            case SourceType.File:
            {
                var file = paths.Resolve(spec.Path);
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".ndjson" or ".jsonl")
                {
                    using var reader = new StreamReader(file, Encoding.UTF8);
                    var lineNo = 0;
                    while (await reader.ReadLineAsync(ct) is { } line)
                    {
                        lineNo++;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        JsonNode? node;
                        try { node = JsonNode.Parse(line); }
                        catch (JsonException ex) { throw new InvalidDataException($"Line {lineNo} is not valid JSON: {ex.Message}"); }
                        yield return node;
                    }
                    yield break;
                }

                await using var stream = File.OpenRead(file);
                if (spec.RecordsPath is null && await FirstNonWhitespaceAsync(stream, ct) == '[')
                {
                    stream.Position = 0;
                    // Streams huge top-level arrays without loading the whole file.
                    await foreach (var node in JsonSerializer.DeserializeAsyncEnumerable<JsonNode?>(stream, JsonDefaults.Options, ct))
                        yield return node;
                    yield break;
                }
                stream.Position = 0;
                var doc = await JsonNode.ParseAsync(stream, cancellationToken: ct);
                foreach (var r in Extract(doc, spec.RecordsPath)) yield return r;
                break;
            }

            case SourceType.Http:
            {
                var client = http.CreateClient("migration");
                using var req = new HttpRequestMessage(HttpMethod.Get, CheckUrl(spec.Url));
                AddHeaders(req, spec.Headers);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var body = await resp.Content.ReadAsStreamAsync(ct);
                var doc = await JsonNode.ParseAsync(body, cancellationToken: ct);
                foreach (var r in Extract(doc, spec.RecordsPath)) yield return r;
                break;
            }
        }
    }

    private static IEnumerable<JsonNode?> Extract(JsonNode? doc, string? recordsPath)
    {
        if (recordsPath is not null)
        {
            var jp = JsonPath.Parse(recordsPath);
            var hits = jp.Select(doc).ToList();
            if (!jp.IsMulti && hits.Count == 1 && hits[0] is JsonArray inner) return inner.Select(x => x?.DeepClone());
            return hits.Select(x => x?.DeepClone());
        }
        return doc is JsonArray arr ? arr.Select(x => x?.DeepClone()) : new[] { doc };
    }

    private static async Task<int> FirstNonWhitespaceAsync(Stream s, CancellationToken ct)
    {
        var buf = new byte[1];
        while (await s.ReadAsync(buf, ct) == 1)
        {
            var b = buf[0];
            if (b == 0xEF || b == 0xBB || b == 0xBF) continue; // UTF-8 BOM
            if (!char.IsWhiteSpace((char)b)) return b;
        }
        return -1;
    }

    public IRecordSink CreateSink(MigrationJob job)
    {
        var req = job.Request;
        if (req.DryRun) return new MemorySink(job);
        return req.Sink.Type switch
        {
            SinkType.Memory => new MemorySink(job),
            SinkType.File => new FileSink(paths.Resolve(req.Sink.Path), req.Sink.Format),
            SinkType.Http => new HttpSink(http.CreateClient("migration"), CheckUrl(req.Sink.Url), req.Sink.Headers),
            _ => throw new ValidationException($"Unknown sink type {req.Sink.Type}."),
        };
    }

    public StreamWriter? OpenDeadLetter(MigrationJob job) =>
        job.Request.DeadLetterPath is { } p ? new StreamWriter(EnsureParent(paths.Resolve(p)), append: false, Encoding.UTF8) : null;

    internal static string EnsureParent(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return file;
    }

    internal static void AddHeaders(HttpRequestMessage req, Dictionary<string, string>? headers)
    {
        if (headers is null) return;
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
    }
}

internal sealed class MemorySink(MigrationJob job) : IRecordSink
{
    public Task WriteBatchAsync(IReadOnlyList<JsonNode> batch, CancellationToken ct)
    {
        job.AddOutput(batch);
        return Task.CompletedTask;
    }

    public Task CompleteAsync(CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Writes to a temp file and atomically moves it into place on completion (all-or-nothing).</summary>
internal sealed class FileSink : IRecordSink
{
    private readonly string _final;
    private readonly string _temp;
    private readonly OutputFormat _format;
    private readonly StreamWriter _writer;
    private bool _first = true, _completed;

    public FileSink(string path, OutputFormat format)
    {
        _final = MigrationIo.EnsureParent(path);
        _temp = path + $".{Guid.NewGuid():N}.tmp";
        _format = format;
        _writer = new StreamWriter(_temp, append: false, new UTF8Encoding(false));
        if (format == OutputFormat.Json) _writer.Write('[');
    }

    public async Task WriteBatchAsync(IReadOnlyList<JsonNode> batch, CancellationToken ct)
    {
        foreach (var node in batch)
        {
            if (_format == OutputFormat.Json)
            {
                if (!_first) await _writer.WriteAsync(',');
                await _writer.WriteAsync(node.ToJsonString(JsonDefaults.Options));
            }
            else
            {
                await _writer.WriteLineAsync(node.ToJsonString(JsonDefaults.Options));
            }
            _first = false;
        }
        await _writer.FlushAsync(ct);
    }

    public async Task CompleteAsync(CancellationToken ct)
    {
        if (_format == OutputFormat.Json) await _writer.WriteAsync(']');
        await _writer.FlushAsync(ct);
        await _writer.DisposeAsync();
        File.Move(_temp, _final, overwrite: true);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        if (!_completed && File.Exists(_temp)) File.Delete(_temp);
    }
}

/// <summary>POSTs each batch as a JSON array, retrying 5xx/429/network errors with exponential backoff.</summary>
internal sealed class HttpSink(HttpClient client, Uri url, Dictionary<string, string>? headers) : IRecordSink
{
    private const int MaxAttempts = 4;

    public async Task WriteBatchAsync(IReadOnlyList<JsonNode> batch, CancellationToken ct)
    {
        var payload = new JsonArray(batch.Select(n => (JsonNode?)n.DeepClone()).ToArray()).ToJsonString(JsonDefaults.Options);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                MigrationIo.AddHeaders(req, headers);
                using var resp = await client.SendAsync(req, ct);
                var code = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode) return;
                // Non-retryable (4xx) or out of attempts: InvalidOperationException escapes the retry filters below.
                if (code < 500 && code != 429 || attempt >= MaxAttempts)
                    throw new InvalidOperationException($"Sink returned {code} {resp.ReasonPhrase} after {attempt} attempt(s).");
            }
            catch (HttpRequestException) when (attempt < MaxAttempts) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts) { }
            await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1)), ct);
        }
    }

    public Task CompleteAsync(CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
