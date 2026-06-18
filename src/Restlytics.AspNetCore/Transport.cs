using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
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

/// <summary>
/// Default transport: gzip the JSON body and POST it with <see cref="HttpClient"/>.
///
/// Design constraints (all in service of "telemetry must never hurt the host app"):
///  - Send is kicked off via <c>Task.Run</c> AFTER the response is flushed, so its
///    latency is invisible to the end user and never blocks the request thread.
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
internal sealed class HttpTransport : ITransport
{
    private readonly HttpClient _client;
    private readonly string _url;
    private readonly string _key;
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _onError;

    public HttpTransport(
        string ingestUrl,
        string key,
        int timeoutMs = 2000,
        HttpClient? client = null,
        Action<string>? onError = null)
    {
        _url = ingestUrl.TrimEnd('/') + "/v1/traces";
        _key = key;
        _timeout = TimeSpan.FromMilliseconds(timeoutMs);
        _onError = onError;

        // A dedicated client (not the host's) so we don't pick up the app's
        // DelegatingHandlers (which would re-instrument our own egress).
        _client = client ?? new HttpClient
        {
            Timeout = _timeout,
        };
    }

    public void Send(ExportTraceServiceRequest payload)
    {
        // Defensive: without the basics there's nothing useful to do — and we must
        // not throw, so just bail quietly.
        if (string.IsNullOrEmpty(_key))
        {
            return;
        }

        byte[] body;
        try
        {
            byte[] json = Payload.Serialize(payload);
            body = Gzip(json);
        }
        catch (Exception ex)
        {
            // Encoding/gzip failure: drop the batch rather than send a mislabeled body.
            Report("restlytics: failed to encode payload: " + ex.Message);
            return;
        }

        // Fire-and-forget: do not await. The continuation swallows everything.
        _ = Task.Run(() => PostAsync(body));
    }

    private async Task PostAsync(byte[] body)
    {
        try
        {
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
        }
        catch (Exception ex)
        {
            // Degrade silently on timeout/503/connection error — drop the batch,
            // never retry into the request path.
            Report("restlytics: send failed: " + ex.Message);
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
internal sealed class NullTransport : ITransport
{
    public ExportTraceServiceRequest? LastPayload { get; private set; }

    public void Send(ExportTraceServiceRequest payload)
    {
        LastPayload = payload;
    }
}
