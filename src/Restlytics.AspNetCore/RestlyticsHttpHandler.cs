using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Restlytics.AspNetCore;

/// <summary>
/// Outbound HTTP instrumentation: a <see cref="DelegatingHandler"/> attached to
/// <see cref="HttpClient"/> pipelines. Each outgoing call becomes a CLIENT span,
/// best-effort, parented to whatever SERVER span is active when the call starts.
///
/// Because <see cref="AsyncLocal{T}"/> request state flows into the awaited send,
/// the ambient tracer state is the originating request's — so the span lands in the
/// right trace even across the await.
///
/// Redaction: <c>url.full</c> has its query string scrubbed of sensitive keys; no
/// request/response bodies or headers are captured.
/// </summary>
public sealed class RestlyticsHttpHandler : DelegatingHandler
{
    private readonly Tracer _tracer;
    private readonly RestlyticsOptions _options;

    internal RestlyticsHttpHandler(Tracer tracer, RestlyticsOptions options)
    {
        _tracer = tracer;
        _options = options;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Capture ambient state up front; if untraced, do nothing but forward.
        RequestState? state = _tracer.Current;
        bool trace = _options.InstrumentHttp
            && state is { Sampled: true, RootSpan: not null };

        if (!trace)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        long startNs = state!.NowNs();
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response;
        }
        finally
        {
            try
            {
                long endNs = state.NowNs();
                Uri? uri = request.RequestUri;
                string host = uri?.Host ?? string.Empty;

                SpanBuilder? span = state.AddChild($"http {host}", startNs, endNs);
                if (span is not null)
                {
                    span.SetString("http.request.method", request.Method.Method);
                    if (uri is not null)
                    {
                        span.SetString("url.full", Redaction.Url(uri, _options.RedactQueryKeys));
                    }

                    span.SetString("server.address", host);
                    if (response is not null)
                    {
                        span.SetInt("http.response.status_code", (int)response.StatusCode);
                    }

                    span.SetString("restlytics.category", "http");
                }
            }
            catch
            {
                // Outbound HTTP instrumentation never breaks the call.
            }
        }
    }

}
