using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Restlytics.AspNetCore;

/// <summary>
/// Ships a fully-built OTLP/JSON ExportTraceServiceRequest to the ingestion service.
///
/// Implementations MUST be fire-and-forget and MUST NOT throw — telemetry must
/// never be able to fail (or slow) the host application's request. Any transport
/// error is swallowed, never surfaced.
/// </summary>
internal interface ITransport
{
    /// <summary>Send the serialized OTLP body. Returns immediately; delivery is async.</summary>
    void Send(ExportTraceServiceRequest payload);
}

/// <summary>A payload-free snapshot of process-local transport health.</summary>
public readonly record struct RestlyticsTransportDiagnostics(
    long AcceptedBatches,
    long DeliveredBatches,
    long DroppedBatches,
    long FailedBatches,
    int QueuedBatches,
    int InFlightBatches,
    int QueueCapacity,
    bool Closed);

/// <summary>Public shutdown and delivery diagnostics exposed through dependency injection.</summary>
public interface IRestlyticsDiagnostics
{
    RestlyticsTransportDiagnostics Snapshot { get; }

    ValueTask<bool> FlushAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default transport: gzip the JSON body and POST it with <see cref="HttpClient"/>.
///
/// Design constraints (all in service of "telemetry must never hurt the host app"):
///  - Send performs a non-blocking write to a bounded channel AFTER the response
///    is flushed; one worker owns gzip and network I/O.
///  - A hard short timeout (default 2s) bounds a slow/unreachable ingest endpoint.
///  - Every error path is swallowed. We never throw into the host application.
///
/// Wire format (must match the ingestion contract exactly):
///   POST {ingestUrl}/v1/traces
///   X-Restlytics-Key: {key}
///   Content-Type: application/json
///   Content-Encoding: gzip
///   body = gzip(json)
/// </summary>
internal sealed class HttpTransport : ITransport, IRestlyticsDiagnostics, IDisposable, IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly string _url;
    private readonly string _key;
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _onError;
    private readonly Channel<ExportTraceServiceRequest> _queue;
    private readonly Task _worker;
    private readonly int _queueCapacity;
    private long _pending;
    private long _accepted;
    private long _delivered;
    private long _dropped;
    private long _failed;
    private int _inFlight;
    private int _closed;

    public HttpTransport(
        string ingestUrl,
        string key,
        int timeoutMs = 2000,
        HttpClient? client = null,
        Action<string>? onError = null,
        int queueCapacity = 64)
    {
        _url = ingestUrl.TrimEnd('/') + "/v1/traces";
        _key = key;
        _timeout = TimeSpan.FromMilliseconds(timeoutMs);
        _onError = onError;
        _queueCapacity = Math.Max(1, queueCapacity);
        _queue = Channel.CreateBounded<ExportTraceServiceRequest>(new BoundedChannelOptions(_queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // A dedicated client (not the host's) so we don't pick up the app's
        // DelegatingHandlers (which would re-instrument our own egress).
        _client = client ?? new HttpClient
        {
            Timeout = _timeout,
        };
        _worker = RunAsync();
    }

    public void Send(ExportTraceServiceRequest payload)
    {
        // Defensive: without the basics there's nothing useful to do — and we must
        // not throw, so just bail quietly.
        if (Volatile.Read(ref _closed) != 0 || string.IsNullOrEmpty(_key))
        {
            RecordDrop("restlytics: batch dropped because transport is closed or unconfigured");
            return;
        }
        Interlocked.Increment(ref _pending);
        if (_queue.Writer.TryWrite(payload))
        {
            Interlocked.Increment(ref _accepted);
            return;
        }
        Interlocked.Decrement(ref _pending);
        RecordDrop("restlytics: batch dropped because transport queue is full");
    }

    public RestlyticsTransportDiagnostics Snapshot => new(
        AcceptedBatches: Interlocked.Read(ref _accepted),
        DeliveredBatches: Interlocked.Read(ref _delivered),
        DroppedBatches: Interlocked.Read(ref _dropped),
        FailedBatches: Interlocked.Read(ref _failed),
        QueuedBatches: _queue.Reader.Count,
        InFlightBatches: Volatile.Read(ref _inFlight),
        QueueCapacity: _queueCapacity,
        Closed: Volatile.Read(ref _closed) != 0);

    public async ValueTask<bool> FlushAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan wait = timeout ?? TimeSpan.FromSeconds(2);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(wait < TimeSpan.Zero ? TimeSpan.Zero : wait);
        try
        {
            while (Interlocked.Read(ref _pending) > 0)
            {
                await Task.Delay(5, cts.Token).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunAsync()
    {
        await foreach (ExportTraceServiceRequest payload in _queue.Reader.ReadAllAsync())
        {
            Volatile.Write(ref _inFlight, 1);
            try
            {
                if (await PostAsync(payload).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref _delivered);
                }
                else
                {
                    Interlocked.Increment(ref _failed);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failed);
                Report("restlytics: transport worker failed: " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref _inFlight, 0);
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    private async Task<bool> PostAsync(ExportTraceServiceRequest payload)
    {
        try
        {
            byte[] body = Gzip(Payload.Serialize(payload));
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");

            using var request = new HttpRequestMessage(HttpMethod.Post, _url)
            {
                Content = content,
            };
            request.Headers.TryAddWithoutValidation("X-Restlytics-Key", _key);

            // Bound the send independently of the client default, so a custom
            // injected client can't accidentally remove the cap.
            using var cts = new CancellationTokenSource(_timeout);

            // Response is always 200 with a partialSuccess envelope — we don't read
            // the body. Treat any/no response as success and move on.
            using HttpResponseMessage response =
                await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Degrade silently on timeout/503/connection error — drop the batch,
            // never retry into the request path.
            Report("restlytics: send failed: " + ex.Message);
            return false;
        }
    }

    private void RecordDrop(string message)
    {
        Interlocked.Increment(ref _dropped);
        Report(message);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _queue.Writer.TryComplete();
        }
        await FlushAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        try
        {
            await _worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Bounded best-effort shutdown; the worker owns no foreground thread.
        }
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private void Report(string message)
    {
        if (_onError is null)
        {
            return;
        }

        try
        {
            _onError(message);
        }
        catch
        {
            // Even logging must not throw.
        }
    }
}

/// <summary>
/// No-op transport. Useful in tests, local dev, and CI where you don't want to
/// (or can't) reach the ingestion service. Records the last payload so tests can
/// assert on the built OTLP body without any network. Select with
/// <c>RESTLYTICS_TRANSPORT=null</c>.
/// </summary>
internal sealed class NullTransport : ITransport, IRestlyticsDiagnostics
{
    public ExportTraceServiceRequest? LastPayload { get; private set; }

    public void Send(ExportTraceServiceRequest payload)
    {
        LastPayload = payload;
    }

    public RestlyticsTransportDiagnostics Snapshot => new(0, 0, 0, 0, 0, 0, 0, false);

    public ValueTask<bool> FlushAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(true);
}
