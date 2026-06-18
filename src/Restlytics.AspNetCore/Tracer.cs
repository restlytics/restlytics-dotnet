using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace Restlytics.AspNetCore;

/// <summary>
/// A single span, accumulated in-request and serialized to OTLP/JSON on flush.
///
/// Timestamps are kept as integer nanoseconds internally and only stringified at
/// serialization time — the OTLP/JSON contract requires <c>*UnixNano</c> fields to
/// be decimal STRINGS (to preserve 64-bit precision through JSON).
///
/// Attribute values are kept as typed entries and converted to the OTLP AnyValue
/// wrapper at serialization. The single most error-prone rule lives here:
/// <c>intValue</c> MUST be a string (handled by <see cref="AnyValue.Int"/>).
/// </summary>
internal sealed class SpanBuilder
{
    private readonly List<KeyValue> _attributes = new();

    public string TraceId { get; }
    public string SpanId { get; }
    public string? ParentSpanId { get; }
    public string Name { get; private set; }
    public int Kind { get; }
    public long StartUnixNano { get; }
    public long EndUnixNano { get; private set; }
    public int StatusCode { get; private set; } = SpanStatus.Unset;
    public string? StatusMessage { get; private set; }

    public SpanBuilder(
        string traceId,
        string spanId,
        string? parentSpanId,
        string name,
        int kind,
        long startUnixNano,
        long endUnixNano)
    {
        TraceId = traceId;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        Name = name;
        Kind = kind;
        StartUnixNano = startUnixNano;
        EndUnixNano = endUnixNano;
    }

    public SpanBuilder SetName(string name)
    {
        Name = name;
        return this;
    }

    public SpanBuilder SetEnd(long endUnixNano)
    {
        EndUnixNano = endUnixNano;
        return this;
    }

    public SpanBuilder SetString(string key, string value)
    {
        _attributes.Add(new KeyValue { Key = key, Value = AnyValue.String(value) });
        return this;
    }

    /// <summary>Record an int attribute. Serialized as intValue (a STRING) per the contract.</summary>
    public SpanBuilder SetInt(string key, long value)
    {
        _attributes.Add(new KeyValue { Key = key, Value = AnyValue.Int(value) });
        return this;
    }

    public SpanBuilder SetBool(string key, bool value)
    {
        _attributes.Add(new KeyValue { Key = key, Value = AnyValue.Bool(value) });
        return this;
    }

    public SpanBuilder SetStatus(int code, string? message = null)
    {
        StatusCode = code;
        if (message is not null)
        {
            // Cap to keep payloads bounded; full stack traces don't belong on the wire.
            StatusMessage = message.Length > 1024 ? message.Substring(0, 1024) : message;
        }

        return this;
    }

    /// <summary>Read back the category attribute for self-time bucketing.</summary>
    public string? Category()
    {
        foreach (KeyValue kv in _attributes)
        {
            if (kv.Key == "restlytics.category")
            {
                return kv.Value.StringValue;
            }
        }

        return null;
    }

    /// <summary>Duration in nanoseconds (clamped non-negative against clock skew).</summary>
    public long DurationNs() => Math.Max(0, EndUnixNano - StartUnixNano);

    /// <summary>Serialize to the OTLP/JSON Span shape the ingestion contract validates.</summary>
    public OtlpSpan ToOtlp()
    {
        // parentSpanId is omitted/empty for the root SERVER span.
        string? parent = string.IsNullOrEmpty(ParentSpanId) ? null : ParentSpanId;

        SpanStatus? status = null;
        if (StatusCode != SpanStatus.Unset)
        {
            status = new SpanStatus
            {
                Code = StatusCode,
                Message = string.IsNullOrEmpty(StatusMessage) ? null : StatusMessage,
            };
        }

        return new OtlpSpan
        {
            TraceId = TraceId,
            SpanId = SpanId,
            ParentSpanId = parent,
            Name = Name,
            Kind = Kind,
            // Decimal STRINGS — int64-safe in JSON.
            StartTimeUnixNano = StartUnixNano.ToString(CultureInfo.InvariantCulture),
            EndTimeUnixNano = EndUnixNano.ToString(CultureInfo.InvariantCulture),
            Attributes = _attributes.Count > 0 ? _attributes : null,
            Status = status,
        };
    }
}

/// <summary>
/// Per-request tracer state: the active trace id, the root SERVER span, the child
/// span buffer, sampling, and the clock anchors.
///
/// Concurrency model: ASP.NET Core handles concurrent requests on shared threads,
/// so per-request state lives in an <see cref="AsyncLocal{T}"/> (see <see cref="Tracer"/>).
/// One instance of this class is created per traced request and discarded after flush.
///
/// Timing model: we use <see cref="Stopwatch.GetTimestamp"/> (monotonic) for
/// DURATIONS — immune to NTP/clock adjustments — and anchor it to a single
/// wall-clock reading so we can emit absolute epoch-nanosecond timestamps. Each
/// span's absolute time is <c>wallAnchorNs + (monoNow - monoAnchor)</c>.
/// </summary>
internal sealed class RequestState
{
    // Nanoseconds per Stopwatch tick. Integer division would truncate to 0 on hosts
    // whose Stopwatch.Frequency exceeds 1 GHz, so keep it as a double scale factor.
    private static readonly double NanosPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    public bool Sampled { get; }
    public string TraceId { get; }
    public SpanBuilder? RootSpan { get; private set; }

    public readonly List<SpanBuilder> Children = new();
    public int DbQueryCount;

    private readonly long _wallAnchorNs;
    private readonly long _monoAnchorTicks;
    private readonly int _maxSpans;

    public RequestState(string traceId, string? rootParentSpanId, bool sampled, string rootName, int maxSpans)
    {
        TraceId = traceId;
        Sampled = sampled;
        _maxSpans = maxSpans;

        // Anchor wall-clock ↔ monotonic clocks together.
        _wallAnchorNs = WallClockNs();
        _monoAnchorTicks = Stopwatch.GetTimestamp();

        if (!sampled)
        {
            return; // not sampled: stay cheap, record nothing
        }

        long now = NowNs();
        RootSpan = new SpanBuilder(
            traceId: traceId,
            spanId: Ids.SpanId(),
            parentSpanId: rootParentSpanId,
            name: rootName,
            kind: 2, // SERVER
            startUnixNano: now,
            endUnixNano: now);
    }

    /// <summary>Absolute current time in epoch nanoseconds, derived from the monotonic clock.</summary>
    public long NowNs()
        => _wallAnchorNs + (long)((Stopwatch.GetTimestamp() - _monoAnchorTicks) * NanosPerTick);

    /// <summary>
    /// Create a CLIENT child span over an absolute [startNs, endNs] window. DB/HTTP
    /// instrumentation often only learns of a span AFTER it finished, so callers
    /// back-date the start. Returns null when not sampled or the buffer cap is hit.
    /// </summary>
    public SpanBuilder? AddChild(string name, long startNs, long endNs, int kind = 3 /* CLIENT */)
    {
        if (!Sampled || RootSpan is null)
        {
            return null;
        }

        if (Children.Count >= _maxSpans)
        {
            return null;
        }

        var span = new SpanBuilder(
            traceId: TraceId,
            spanId: Ids.SpanId(),
            parentSpanId: RootSpan.SpanId,
            name: name,
            kind: kind,
            startUnixNano: startNs,
            endUnixNano: endNs);
        Children.Add(span);

        return span;
    }

    private static long WallClockNs()
    {
        // 100-ns ticks since 0001-01-01 → ns since 1970-01-01.
        long ticks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
        return ticks * 100L;
    }
}

/// <summary>
/// The tracer: a process-wide singleton that owns the per-request <see cref="RequestState"/>
/// via an <see cref="AsyncLocal{T}"/>, makes the head-based sampling decision, computes
/// self-time rollups on finish, and flushes the OTLP batch through the transport.
///
/// Because state is async-local, concurrent requests never see each other's spans —
/// the instrument hooks (middleware, EF interceptor, HTTP handler) all read the
/// ambient <see cref="Current"/> state.
/// </summary>
internal sealed class Tracer
{
    private static readonly AsyncLocal<RequestState?> _current = new();

    private readonly ITransport _transport;
    private readonly string _serviceName;
    private readonly string _environment;
    private readonly double _sampleRate;
    private readonly int _maxSpans;

    public Tracer(
        ITransport transport,
        string serviceName,
        string environment,
        double sampleRate = 1.0,
        int maxSpans = 2000)
    {
        _transport = transport;
        _serviceName = serviceName;
        _environment = environment;
        _sampleRate = sampleRate;
        _maxSpans = maxSpans;
    }

    /// <summary>The active per-request state, or null when no request is being traced.</summary>
    public RequestState? Current => _current.Value;

    /// <summary>True when there's an active, sampled request with a root span.</summary>
    public bool IsSampled => _current.Value is { Sampled: true, RootSpan: not null };

    /// <summary>
    /// Open the root SERVER span at request start. Continues an incoming W3C
    /// traceparent if present (distributed tracing), otherwise mints a fresh trace
    /// id. The sampling decision is HEAD-BASED and made exactly once here, keyed off
    /// the trace id, so all spans in a trace share the same fate.
    /// </summary>
    public RequestState StartServerSpan(string name, string? traceparent = null)
    {
        string traceId;
        string? rootParentSpanId;
        bool sampled;

        Ids.Traceparent? incoming = Ids.ParseTraceparent(traceparent);
        if (incoming is { } tp)
        {
            traceId = tp.TraceId;
            rootParentSpanId = tp.ParentSpanId;
            // Respect an upstream "not sampled" decision; only re-roll if it sampled.
            sampled = tp.Sampled && SampleDecision(traceId);
        }
        else
        {
            traceId = Ids.TraceId();
            rootParentSpanId = null;
            sampled = SampleDecision(traceId);
        }

        var state = new RequestState(traceId, rootParentSpanId, sampled, name, _maxSpans);
        _current.Value = state;
        return state;
    }

    /// <summary>
    /// Close the root span, compute self-time rollups, flush the batch, and clear
    /// the async-local state. Never throws into the host.
    /// </summary>
    public void FinishServerSpan()
    {
        RequestState? state = _current.Value;
        if (state is null)
        {
            return;
        }

        try
        {
            if (!state.Sampled || state.RootSpan is null)
            {
                return;
            }

            SpanBuilder root = state.RootSpan;
            root.SetEnd(state.NowNs());

            AttachSelfTime(state, root);
            root.SetInt("restlytics.db_query_count", state.DbQueryCount);
            root.SetString("restlytics.category", "app");

            Flush(state);
        }
        catch
        {
            // Telemetry must never throw into the host application.
        }
        finally
        {
            // Clear per-request state so nothing leaks past the request.
            _current.Value = null;
        }
    }

    /// <summary>Build the OTLP payload and hand it to the transport (fire-and-forget).</summary>
    private void Flush(RequestState state)
    {
        if (state.RootSpan is null)
        {
            return;
        }

        try
        {
            var all = new List<OtlpSpan>(state.Children.Count + 1)
            {
                state.RootSpan.ToOtlp(),
            };
            foreach (SpanBuilder child in state.Children)
            {
                all.Add(child.ToOtlp());
            }

            ExportTraceServiceRequest payload = Payload.Build(_serviceName, _environment, all);
            _transport.Send(payload);
        }
        catch
        {
            // Telemetry must never throw into the host application.
        }
    }

    /// <summary>Compute and attach restlytics.self_ns.{db,http,cache,app} to the root span.</summary>
    private static void AttachSelfTime(RequestState state, SpanBuilder root)
    {
        long rootStart = root.StartUnixNano;
        long rootDur = root.DurationNs();

        var db = new List<Intervals.Interval>();
        var http = new List<Intervals.Interval>();
        var cache = new List<Intervals.Interval>();
        var app = new List<Intervals.Interval>();
        var all = new List<Intervals.Interval>();

        foreach (SpanBuilder s in state.Children)
        {
            // Normalize to offsets from root start; clamp inverted intervals (skew).
            long start = s.StartUnixNano - rootStart;
            long end = s.EndUnixNano - rootStart;
            if (end < start)
            {
                end = start;
            }

            var iv = new Intervals.Interval(start, end);
            all.Add(iv);

            switch (s.Category())
            {
                case "db":
                    db.Add(iv);
                    break;
                case "http":
                    http.Add(iv);
                    break;
                case "cache":
                    cache.Add(iv);
                    break;
                default:
                    app.Add(iv);
                    break;
            }
        }

        long selfDb = Intervals.UnionLength(db);
        long selfHttp = Intervals.UnionLength(http);
        long selfCache = Intervals.UnionLength(cache);
        // app self-time = explicit app-category child time + the root's own exclusive
        // (uncovered) time. Mirrors the ingestion service's computation.
        long selfApp = Intervals.UnionLength(app) + Math.Max(0, rootDur - Intervals.UnionLength(all));

        root.SetInt("restlytics.self_ns.db", selfDb);
        root.SetInt("restlytics.self_ns.http", selfHttp);
        root.SetInt("restlytics.self_ns.cache", selfCache);
        root.SetInt("restlytics.self_ns.app", selfApp);
    }

    /// <summary>
    /// Head-based trace-id-ratio sampling. Deterministic in the trace id so the
    /// decision is stable and unbiased: take the last 32 bits of entropy, map to
    /// [0,1), keep it if it falls under the configured rate.
    /// </summary>
    private bool SampleDecision(string traceId)
    {
        if (_sampleRate >= 1.0)
        {
            return true;
        }

        if (_sampleRate <= 0.0)
        {
            return false;
        }

        // Use the last 8 hex chars (32 bits) as the entropy source.
        string tail = traceId.Length >= 8 ? traceId.Substring(traceId.Length - 8) : traceId;
        uint bucket = uint.Parse(tail, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        double ratio = bucket / (double)uint.MaxValue;

        return ratio < _sampleRate;
    }
}
