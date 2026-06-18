using System;
using System.Collections.Generic;
using System.Globalization;

namespace Restlytics.AspNetCore;

/// <summary>
/// SDK configuration. Bound from <c>appsettings.json</c> (the <c>Restlytics</c>
/// section) and/or environment variables. Env vars take precedence and use the
/// same keys as every other restlytics SDK (<c>RESTLYTICS_*</c>) so configuration
/// is portable across languages.
/// </summary>
public sealed class RestlyticsOptions
{
    /// <summary>
    /// Project ingest key (sent as <c>X-Restlytics-Key</c>). Empty = disabled:
    /// the SDK stays completely inert (no spans built, no requests made).
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Ingest base URL; the SDK POSTs to <c>{IngestUrl}/v1/traces</c>.</summary>
    public string IngestUrl { get; set; } = "https://ingest.restlytics.com";

    /// <summary><c>service.name</c> resource attribute.</summary>
    public string ServiceName { get; set; } = "dotnet";

    /// <summary><c>deployment.environment</c> resource attribute.</summary>
    public string Environment { get; set; } = "production";

    /// <summary>Head-based trace sampling, 0.0–1.0. Decided once per trace.</summary>
    public double SampleRate { get; set; } = 1.0;

    /// <summary>Transport driver: <c>http</c> (prod) or <c>null</c> (off/tests).</summary>
    public string Transport { get; set; } = "http";

    /// <summary>Hard cap on the send, milliseconds.</summary>
    public int TimeoutMs { get; set; } = 2000;

    /// <summary>
    /// Send raw SQL text (<c>db.query.text</c>, capped 2048). OFF = template only.
    /// Binding VALUES are NEVER sent regardless.
    /// </summary>
    public bool CaptureSql { get; set; }

    /// <summary>Per-instrument toggle: DB (EF Core) spans.</summary>
    public bool InstrumentDb { get; set; } = true;

    /// <summary>Per-instrument toggle: outbound HTTP spans.</summary>
    public bool InstrumentHttp { get; set; } = true;

    /// <summary>Per-instrument toggle: cache spans.</summary>
    public bool InstrumentCache { get; set; } = true;

    /// <summary>Per-request span buffer cap. Bounds memory on pathological traces (e.g. N+1).</summary>
    public int MaxSpans { get; set; } = 2000;

    /// <summary>Request paths to skip entirely (no span opened). Supports a trailing <c>*</c> wildcard.</summary>
    public List<string> IgnorePaths { get; set; } = new()
    {
        "/health",
        "/healthz",
        "/livez",
        "/readyz",
    };

    /// <summary>Outbound <c>url.full</c> query keys to redact, plus belt-and-suspenders for inbound scrubbing.</summary>
    public List<string> RedactQueryKeys { get; set; } = new()
    {
        "token", "api_key", "apikey", "password", "secret", "access_token", "key", "signature",
    };

    /// <summary>True when an ingest key is configured.</summary>
    public bool Enabled => !string.IsNullOrEmpty(Key);

    /// <summary>
    /// Overlay environment variables onto this instance. Env wins over appsettings so
    /// deploy-time secrets/overrides take effect without a config file change.
    /// </summary>
    public void ApplyEnvironment(Func<string, string?> getEnv)
    {
        Key = StringOr(getEnv("RESTLYTICS_KEY"), Key);
        IngestUrl = StringOr(getEnv("RESTLYTICS_INGEST_URL"), IngestUrl);
        ServiceName = StringOr(getEnv("RESTLYTICS_SERVICE_NAME"), ServiceName);
        Environment = StringOr(getEnv("RESTLYTICS_ENV"), Environment);
        Transport = StringOr(getEnv("RESTLYTICS_TRANSPORT"), Transport);

        SampleRate = DoubleOr(getEnv("RESTLYTICS_SAMPLE_RATE"), SampleRate);
        TimeoutMs = IntOr(getEnv("RESTLYTICS_TIMEOUT_MS"), TimeoutMs);
        MaxSpans = IntOr(getEnv("RESTLYTICS_MAX_SPANS"), MaxSpans);

        CaptureSql = BoolOr(getEnv("RESTLYTICS_CAPTURE_SQL"), CaptureSql);
        InstrumentDb = BoolOr(getEnv("RESTLYTICS_INSTRUMENT_DB"), InstrumentDb);
        InstrumentHttp = BoolOr(getEnv("RESTLYTICS_INSTRUMENT_HTTP"), InstrumentHttp);
        InstrumentCache = BoolOr(getEnv("RESTLYTICS_INSTRUMENT_CACHE"), InstrumentCache);
    }

    private static string StringOr(string? value, string fallback)
        => string.IsNullOrEmpty(value) ? fallback : value;

    private static double DoubleOr(string? value, double fallback)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;

    private static int IntOr(string? value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static bool BoolOr(string? value, bool fallback)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        // Accept the usual truthy spellings (matches env conventions across SDKs).
        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
    }
}
