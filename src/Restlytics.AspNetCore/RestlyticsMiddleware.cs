using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Restlytics.AspNetCore;

/// <summary>
/// Global middleware that owns the root SERVER span.
///
/// Why <see cref="IMiddleware"/> (factory-activated) rather than the convention-based
/// constructor form: it lets us resolve the singleton <see cref="Tracer"/> and the
/// per-request <see cref="RestlyticsOptions"/> from DI cleanly, and keeps a single
/// shared instance.
///
/// Lifecycle: open the span before <c>await _next</c>, and finalize it AFTER the
/// pipeline has produced a response. Closing the span, computing self-time, gzipping,
/// and the POST all happen on a fire-and-forget task off the response's critical path.
/// Everything is wrapped so a bug in our instrumentation can never break a request.
/// </summary>
public sealed class RestlyticsMiddleware : IMiddleware
{
    private readonly Tracer _tracer;
    private readonly RestlyticsOptions _options;

    internal RestlyticsMiddleware(Tracer tracer, RestlyticsOptions options)
    {
        _tracer = tracer;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Skip ignored paths (health checks, the ingest's own traffic, etc.) before
        // doing any work so they never even open a span.
        if (!ShouldTrace(context.Request.Path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Continue an incoming distributed trace if the upstream sent traceparent.
        string? traceparent = null;
        if (context.Request.Headers.TryGetValue("traceparent", out var tp))
        {
            traceparent = tp.ToString();
        }

        string method = context.Request.Method;

        try
        {
            // Provisional name; the real http.route template isn't known until routing
            // has resolved, so we finalize the name + route attribute after _next.
            _tracer.StartServerSpan($"{method} {context.Request.Path}", traceparent);
        }
        catch
        {
            // If opening the span fails for any reason, run the request untraced.
        }

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            FinalizeSpan(context, method);
        }
    }

    private void FinalizeSpan(HttpContext context, string method)
    {
        try
        {
            RequestState? state = _tracer.Current;
            SpanBuilder? root = state?.RootSpan;
            if (root is null)
            {
                // Not sampled / ignored — still clear any state and bail.
                _tracer.FinishServerSpan();
                return;
            }

            // http.route MUST be the TEMPLATE (e.g. /users/{id}), never the raw URL.
            // ASP.NET exposes it via endpoint.RoutePattern.RawText. Fall back to the
            // request path only when routing didn't resolve (404s, etc.).
            string template = RouteTemplate(context) ?? context.Request.Path.ToString();
            if (string.IsNullOrEmpty(template))
            {
                template = "/";
            }

            int status = context.Response.StatusCode;

            root.SetName($"{method} {template}");
            root.SetString("http.request.method", method);
            root.SetString("http.route", template);
            root.SetInt("http.response.status_code", status);

            // Crash & error detection: 5xx becomes ERROR; otherwise OK.
            if (status >= 500)
            {
                if (root.StatusCode != SpanStatus.Error)
                {
                    root.SetStatus(SpanStatus.Error, "HTTP " + status);
                }
            }
            else if (root.StatusCode == SpanStatus.Unset)
            {
                root.SetStatus(SpanStatus.Ok);
            }

            _tracer.FinishServerSpan();
        }
        catch
        {
            // Never let telemetry break the host app. Best-effort cleanup of state.
            try
            {
                _tracer.FinishServerSpan();
            }
            catch
            {
                // give up silently
            }
        }
    }

    private static string? RouteTemplate(HttpContext context)
    {
        Endpoint? endpoint = context.GetEndpoint();
        if (endpoint is RouteEndpoint routeEndpoint)
        {
            string? raw = routeEndpoint.RoutePattern.RawText;
            if (!string.IsNullOrEmpty(raw))
            {
                // Normalize to a leading slash for consistency with other SDKs.
                return raw.StartsWith('/') ? raw : "/" + raw;
            }
        }

        return null;
    }

    private bool ShouldTrace(PathString path)
    {
        string p = path.HasValue ? path.Value! : "/";
        foreach (string pattern in _options.IgnorePaths)
        {
            if (Matches(pattern, p))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Exact match, or a trailing <c>*</c> prefix match (e.g. <c>/swagger*</c>).</summary>
    private static bool Matches(string pattern, string path)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        if (pattern.EndsWith('*'))
        {
            string prefix = pattern[..^1];
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, path, StringComparison.OrdinalIgnoreCase);
    }
}
