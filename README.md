# restlytics — ASP.NET Core SDK

Zero-config performance + error tracing for ASP.NET Core, shipped to [restlytics](https://restlytics.com) in OTLP/JSON.

- **Fast install** — one `AddRestlytics()` + one `app.UseRestlytics()`.
- **Framework-native** — middleware request spans, EF Core `DbCommandInterceptor` DB spans, and outbound `HttpClient` spans via a `DelegatingHandler`.
- **Zero added latency** — spans are flushed *after* the response, fire-and-forget over `HttpClient` with gzip and a hard ~2s timeout.
- **Safe by default** — head-based sampling, SQL normalized to literal-free templates, bindings never sent, query strings scrubbed, no request/response bodies.

> **This is the canonical, open-source repository for the restlytics ASP.NET Core SDK** — published to NuGet as `Restlytics.AspNetCore`. Open issues and pull requests here. It conforms to the cross-language restlytics wire contract, so the ingestion service accepts it identically to every other restlytics SDK.

---

## Install

```bash
dotnet add package Restlytics.AspNetCore
```

Wire it up in `Program.cs`:

```csharp
using Restlytics.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Reads the "Restlytics" config section + RESTLYTICS_* env vars.
builder.Services.AddRestlytics(builder.Configuration);

var app = builder.Build();

// Add early so the SERVER span brackets the whole request.
app.UseRestlytics();

app.MapControllers();
app.Run();
```

Until an ingest key is configured the SDK stays completely inert (no spans built, no
requests made), so it's safe to deploy before you've provisioned a key.

### EF Core DB spans

Register the interceptor on your `DbContext` (it's added to DI by `AddRestlytics()`):

```csharp
builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    options.UseNpgsql(connectionString);
    options.AddInterceptors(sp.GetRequiredService<RestlyticsDbInterceptor>());
});
```

> The EF Core dependency is optional. To drop it entirely, build with
> `-p:RestlyticsEnableEfCore=false` (the interceptor type is then compiled out).

### Outbound HTTP spans

Attach the handler to any `HttpClient` you want traced:

```csharp
builder.Services.AddHttpClient("api")
    .AddHttpMessageHandler<RestlyticsHttpHandler>();
```

---

## Configuration

Configure via `appsettings.json`:

```jsonc
{
  "Restlytics": {
    "Key": "your-project-ingest-key",
    "IngestUrl": "https://ingest.restlytics.com",
    "ServiceName": "my-api",
    "Environment": "production",
    "SampleRate": 1.0,
    "Transport": "http",
    "CaptureSql": false
  }
}
```

…and/or environment variables (these **override** `appsettings.json`, and use the
same keys as every other restlytics SDK):

```dotenv
RESTLYTICS_KEY=your-project-ingest-key
RESTLYTICS_INGEST_URL=https://ingest.restlytics.com
RESTLYTICS_ENV=production
```

| `appsettings` key | Env var | Default | Purpose |
| --- | --- | --- | --- |
| `Key` | `RESTLYTICS_KEY` | `""` | Project ingest key (sent as `X-Restlytics-Key`). Empty = disabled. |
| `IngestUrl` | `RESTLYTICS_INGEST_URL` | `https://ingest.restlytics.com` | Ingest base URL; SDK POSTs to `{url}/v1/traces`. |
| `ServiceName` | `RESTLYTICS_SERVICE_NAME` | `dotnet` | `service.name` resource attribute. |
| `Environment` | `RESTLYTICS_ENV` | `production` | `deployment.environment` resource attribute. |
| `SampleRate` | `RESTLYTICS_SAMPLE_RATE` | `1.0` | Head-based trace sampling, `0.0`–`1.0`. |
| `Transport` | `RESTLYTICS_TRANSPORT` | `http` | `http` (prod) or `null` (off/tests). |
| `TimeoutMs` | `RESTLYTICS_TIMEOUT_MS` | `2000` | Hard cap on the send. |
| `CaptureSql` | `RESTLYTICS_CAPTURE_SQL` | `false` | Send raw SQL text (capped 2048). Off = template only. |
| `InstrumentDb` / `InstrumentHttp` / `InstrumentCache` | `RESTLYTICS_INSTRUMENT_DB` / `_HTTP` / `_CACHE` | `true` | Per-instrument toggles. |
| `MaxSpans` | `RESTLYTICS_MAX_SPANS` | `2000` | Per-request span buffer cap. |
| `IgnorePaths` | — | health endpoints | Request paths to skip (supports a trailing `*`). |

You can also configure in code:

```csharp
builder.Services.AddRestlytics(builder.Configuration, options =>
{
    options.SampleRate = 0.25;
    options.IgnorePaths.Add("/metrics");
});
```

---

## How it works

1. **Root request span** — `RestlyticsMiddleware` (an `IMiddleware`) opens a `SERVER`
   span before `await next(context)` and finalizes it after, reading the route
   **template** from `endpoint.RoutePattern.RawText`. Closing the span, computing
   self-time, gzipping, and the POST happen fire-and-forget off the request path.
2. **DB spans** — `RestlyticsDbInterceptor` (an EF Core `DbCommandInterceptor`) turns each
   command into a `CLIENT` span. The statement is normalized to a literal-free template
   (`select * from users where id = ?`) used both as the N+1 grouping key and to keep PII
   off the wire. We record the binding **count**, never values.
3. **Outbound HTTP spans** — `RestlyticsHttpHandler` (a `DelegatingHandler`) captures
   method, host, redacted `url.full`, status, and timing for each `HttpClient` call.
4. **Self-time** — child spans are interval-unioned per category (db / http / cache) so
   overlapping work isn't double-counted; `app` self-time is the root's exclusive time.
   Emitted as `restlytics.self_ns.*` on the root span.
5. **Errors** — 5xx responses set the span status to `ERROR (2)`.

Timing uses the monotonic clock (`Stopwatch.GetTimestamp()`) for durations, anchored to one
wall-clock reading for absolute epoch-nanosecond timestamps — durations stay correct across
NTP adjustments.

### Concurrency

Per-request state lives in an `AsyncLocal<T>`, so concurrent requests never see each other's
spans, and the DB interceptor / HTTP handler pick up the originating request's trace across
`await`. The in-request buffer is capped (`MaxSpans`) so memory stays bounded under a severe
N+1.

---

## Trust & redaction

restlytics is built to be safe to run in production against real traffic:

- **Fire-and-forget, never fatal.** Every transport/instrument path is wrapped; telemetry can
  never throw into — or slow — your app. A slow/unreachable ingest endpoint is bounded by a
  short timeout, and the batch is simply dropped on failure (no retries into the request path).
- **No binding values.** SQL is normalized to a template; only a binding *count* is sent.
- **No raw SQL** unless you explicitly set `CaptureSql=true` (then capped at 2048 chars).
- **Scrubbed URLs.** `url.full` query strings have sensitive keys (token, password, secret, …)
  redacted. The `http.route` attribute is always the **template** (`/users/{id}`), never the raw URL.
- **No bodies / headers.** Request and response bodies and headers are never captured.
- **Sampling.** Lower `SampleRate` to capture a fraction of traffic.

---

## Local development

Set `RESTLYTICS_TRANSPORT=null` (or `"Transport": "null"`) to disable delivery while keeping
instrumentation — useful in tests and local dev.

---

## License

MIT © restlytics. See [LICENSE](LICENSE).
