using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Restlytics.AspNetCore;

// OTLP/JSON wire-format payload model — the shared contract between the SDKs, the
// ingestion service, and the dashboard (must match packages/contract/src/otlp.ts).
//
// The three classic footguns are enforced structurally here:
//  - trace/span ids are lowercase hex of the right length and never all-zero
//    (produced by Ids);
//  - every *UnixNano field is a decimal STRING (int64-safe in JSON);
//  - intValue inside an AnyValue is a STRING, never a JSON number.
//
// All "null when empty" fields are decorated with JsonIgnoreCondition.WhenWritingNull
// so omitted attributes/status don't appear on the wire. Property names map 1:1 to
// the contract via [JsonPropertyName].

/// <summary>OTLP AnyValue. In practice exactly one field is set; we emit exactly one.</summary>
internal sealed class AnyValue
{
    [JsonPropertyName("stringValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StringValue { get; init; }

    [JsonPropertyName("boolValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BoolValue { get; init; }

    /// <summary>CONTRACT: int64 serialized as a (signed) decimal STRING.</summary>
    [JsonPropertyName("intValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IntValue { get; init; }

    [JsonPropertyName("doubleValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DoubleValue { get; init; }

    public static AnyValue String(string value) => new() { StringValue = value };

    public static AnyValue Bool(bool value) => new() { BoolValue = value };

    public static AnyValue Int(long value)
        => new() { IntValue = value.ToString(CultureInfo.InvariantCulture) };

    public static AnyValue Double(double value) => new() { DoubleValue = value };
}

internal sealed class KeyValue
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("value")]
    public required AnyValue Value { get; init; }
}

internal sealed class SpanStatus
{
    public const int Unset = 0;
    public const int Ok = 1;
    public const int Error = 2;

    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}

internal sealed class OtlpSpan
{
    [JsonPropertyName("traceId")]
    public required string TraceId { get; init; }

    [JsonPropertyName("spanId")]
    public required string SpanId { get; init; }

    /// <summary>Empty string or 16 hex chars; omitted for the root SERVER span.</summary>
    [JsonPropertyName("parentSpanId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentSpanId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required int Kind { get; init; }

    /// <summary>Decimal STRING — int64-safe in JSON.</summary>
    [JsonPropertyName("startTimeUnixNano")]
    public required string StartTimeUnixNano { get; init; }

    [JsonPropertyName("endTimeUnixNano")]
    public required string EndTimeUnixNano { get; init; }

    [JsonPropertyName("attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<KeyValue>? Attributes { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SpanStatus? Status { get; init; }
}

internal sealed class InstrumentationScope
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }
}

internal sealed class ScopeSpans
{
    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InstrumentationScope? Scope { get; init; }

    [JsonPropertyName("spans")]
    public required List<OtlpSpan> Spans { get; init; }
}

internal sealed class Resource
{
    [JsonPropertyName("attributes")]
    public required List<KeyValue> Attributes { get; init; }
}

internal sealed class ResourceSpans
{
    [JsonPropertyName("resource")]
    public required Resource Resource { get; init; }

    [JsonPropertyName("scopeSpans")]
    public required List<ScopeSpans> ScopeSpans { get; init; }
}

/// <summary>Top-level OTLP traces export request — the body of <c>POST /v1/traces</c>.</summary>
internal sealed class ExportTraceServiceRequest
{
    [JsonPropertyName("resourceSpans")]
    public required List<ResourceSpans> ResourceSpans { get; init; }
}

/// <summary>
/// Builds the top-level OTLP/JSON <see cref="ExportTraceServiceRequest"/> body.
///
/// Shape (matches packages/contract ExportTraceServiceRequest exactly):
///   { "resourceSpans": [ {
///       "resource":   { "attributes": [ ...resource KVs... ] },
///       "scopeSpans": [ { "scope": {"name": "restlytics-dotnet"}, "spans": [ ... ] } ]
///   } ] }
///
/// We emit a single resourceSpans/scopeSpans envelope because every span in one
/// request shares the same resource.
/// </summary>
internal static class Payload
{
    /// <summary>Stable identifiers for the SDK, surfaced as resource attributes and the scope name.</summary>
    public const string SdkName = "restlytics-dotnet";
    public const string SdkLanguage = "dotnet";
    public const string SdkVersion = "0.1.0";

    /// <summary>Serializer options: omit nulls (belt-and-suspenders) and don't escape '/'.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ExportTraceServiceRequest Build(
        string serviceName,
        string environment,
        IReadOnlyList<OtlpSpan> spans)
    {
        return new ExportTraceServiceRequest
        {
            ResourceSpans = new List<ResourceSpans>
            {
                new()
                {
                    Resource = new Resource
                    {
                        Attributes = ResourceAttributes(serviceName, environment),
                    },
                    ScopeSpans = new List<ScopeSpans>
                    {
                        new()
                        {
                            Scope = new InstrumentationScope
                            {
                                Name = SdkName,
                                Version = SdkVersion,
                            },
                            Spans = new List<OtlpSpan>(spans),
                        },
                    },
                },
            },
        };
    }

    public static byte[] Serialize(ExportTraceServiceRequest request)
        => JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);

    private static List<KeyValue> ResourceAttributes(string serviceName, string environment)
    {
        return new List<KeyValue>
        {
            StringAttr("service.name", serviceName),
            StringAttr("deployment.environment", environment),
            StringAttr("telemetry.sdk.name", SdkName),
            StringAttr("telemetry.sdk.language", SdkLanguage),
            StringAttr("telemetry.sdk.version", SdkVersion),
        };
    }

    private static KeyValue StringAttr(string key, string value)
        => new() { Key = key, Value = AnyValue.String(value) };
}
