using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Restlytics.AspNetCore;
using Xunit;

namespace Restlytics.Tests;

public class ConformanceTests
{
    [Fact]
    public void MatchesSharedOtlpPropagationRedactionErrorAndSamplingFixture()
    {
        IReadOnlyDictionary<string, string> fixture = Properties();
        var span = new SpanBuilder(
            fixture["trace.id"],
            fixture["span.id"],
            fixture["span.parent_id"],
            fixture["span.name"],
            int.Parse(fixture["span.kind"], CultureInfo.InvariantCulture),
            long.Parse(fixture["span.start_ns"], CultureInfo.InvariantCulture),
            long.Parse(fixture["span.end_ns"], CultureInfo.InvariantCulture));
        span.SetString(fixture["attribute.string.key"], fixture["attribute.string.value"])
            .SetInt(
                fixture["attribute.int.key"],
                long.Parse(fixture["attribute.int.value"], CultureInfo.InvariantCulture))
            .SetBool(
                fixture["attribute.bool.key"],
                bool.Parse(fixture["attribute.bool.value"]))
            .SetString(fixture["redaction.attribute_key"], fixture["redaction.attribute_value"])
            .SetStatus(
                int.Parse(fixture["error.status_code"], CultureInfo.InvariantCulture),
                fixture["error.message"]);

        ExportTraceServiceRequest payload = Payload.Build(
            fixture["service.name"],
            fixture["deployment.environment"],
            new[] { span.ToOtlp() });
        JsonNode? actual = JsonNode.Parse(Payload.Serialize(payload));
        string expectedText = File.ReadAllText(FixturePath("otlp.expected.json"))
            .Replace("${SDK_NAME}", Payload.SdkName, StringComparison.Ordinal)
            .Replace("${SDK_LANGUAGE}", Payload.SdkLanguage, StringComparison.Ordinal)
            .Replace("${SDK_VERSION}", Payload.SdkVersion, StringComparison.Ordinal);
        JsonNode? expected = JsonNode.Parse(expectedText);
        Assert.True(JsonNode.DeepEquals(expected, actual), $"actual: {actual}\nexpected: {expected}");

        Ids.Traceparent? sampled = Ids.ParseTraceparent(fixture["propagation.sampled"]);
        Assert.NotNull(sampled);
        Assert.Equal(fixture["trace.id"], sampled.Value.TraceId);
        Assert.Equal(fixture["span.id"], sampled.Value.ParentSpanId);
        Assert.True(sampled.Value.Sampled);
        Assert.False(Ids.ParseTraceparent(fixture["propagation.unsampled"])?.Sampled);
        Assert.Null(Ids.ParseTraceparent(fixture["propagation.invalid"]));

        var zero = new Tracer(
            new NullTransport(),
            "fixture",
            "fixture",
            double.Parse(fixture["sampling.root_rate_zero"], CultureInfo.InvariantCulture));
        Assert.False(zero.StartServerSpan("fixture").Sampled);
        var one = new Tracer(
            new NullTransport(),
            "fixture",
            "fixture",
            double.Parse(fixture["sampling.root_rate_one"], CultureInfo.InvariantCulture));
        Assert.True(one.StartServerSpan("fixture").Sampled);
    }

    private static IReadOnlyDictionary<string, string> Properties()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(FixturePath("vectors.properties")))
        {
            int separator = line.IndexOf('=');
            values[line[..separator]] = line[(separator + 1)..];
        }
        return values;
    }

    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1", name);
}
