using Restlytics.AspNetCore;
using Xunit;

namespace Restlytics.Tests;

public class TracerSamplingTests
{
    private const string UpstreamTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string UpstreamSpanId = "00f067aa0ba902b7";

    // sampleRate 0.0 means every LOCAL head-based roll returns false, so any
    // re-roll on a continued trace is immediately visible as a dropped trace.
    private static Tracer NeverSamplesLocally()
        => new Tracer(new NullTransport(), "test-service", "test", sampleRate: 0.0);

    private static string Traceparent(bool sampled)
        => $"00-{UpstreamTraceId}-{UpstreamSpanId}-{(sampled ? "01" : "00")}";

    [Fact]
    public void ContinuedTraceInheritsUpstreamSampledFlag()
    {
        // The upstream already decided to keep this trace; re-rolling here would
        // sever the distributed trace at this hop.
        RequestState state = NeverSamplesLocally().StartServerSpan("GET /orders", Traceparent(true));

        Assert.True(state.Sampled);
        Assert.Equal(UpstreamTraceId, state.TraceId);
        Assert.NotNull(state.RootSpan);
    }

    [Fact]
    public void ContinuedTraceHonorsUpstreamNotSampledFlag()
    {
        RequestState state = NeverSamplesLocally().StartServerSpan("GET /orders", Traceparent(false));

        Assert.False(state.Sampled);
        Assert.Null(state.RootSpan);
    }

    [Fact]
    public void RootTraceStillMakesTheLocalSamplingDecision()
    {
        // No traceparent → this IS the root, so the head-based roll applies.
        RequestState state = NeverSamplesLocally().StartServerSpan("GET /orders");

        Assert.False(state.Sampled);
        Assert.Null(state.RootSpan);
    }
}
