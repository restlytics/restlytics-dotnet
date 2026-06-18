using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;

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
                        span.SetString("url.full", RedactUrl(uri, _options.RedactQueryKeys));
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

    /// <summary>
    /// Strip sensitive keys from a URL's query string for <c>url.full</c>. Keeps the
    /// scheme/host/path (needed for grouping) but never leaks tokens/secrets.
    /// </summary>
    internal static string RedactUrl(Uri uri, IReadOnlyList<string> redactKeys)
    {
        try
        {
            if (string.IsNullOrEmpty(uri.Query))
            {
                return uri.GetLeftPart(UriPartial.Path);
            }

            var redact = new HashSet<string>(
                redactKeys.Select(k => k.ToLowerInvariant()),
                StringComparer.Ordinal);

            Dictionary<string, Microsoft.Extensions.Primitives.StringValues> parsed =
                QueryHelpers.ParseQuery(uri.Query);

            var sb = new StringBuilder(uri.GetLeftPart(UriPartial.Path));
            bool first = true;
            foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> kv in parsed)
            {
                bool sensitive = redact.Contains(kv.Key.ToLowerInvariant());
                foreach (string? raw in kv.Value)
                {
                    sb.Append(first ? '?' : '&');
                    first = false;
                    sb.Append(Uri.EscapeDataString(kv.Key));
                    sb.Append('=');
                    sb.Append(sensitive ? "REDACTED" : Uri.EscapeDataString(raw ?? string.Empty));
                }
            }

            return sb.ToString();
        }
        catch
        {
            // On any parse failure, fall back to the path-only form (never the query).
            return uri.GetLeftPart(UriPartial.Path);
        }
    }
}
