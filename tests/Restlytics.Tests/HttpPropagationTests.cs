using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Restlytics.AspNetCore;
using Xunit;

namespace Restlytics.Tests;

public class HttpPropagationTests
{
    private const string Sampled =
        "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
    private const string Unsampled =
        "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00";

    [Fact]
    public async Task HandlerInjectsTheRecordedClientSpanContext()
    {
        var tracer = new Tracer(new NullTransport(), "test", "test");
        RequestState state = tracer.StartServerSpan("GET /proxy", Sampled);
        var capture = new CaptureHandler();
        using var client = new HttpClient(new RestlyticsHttpHandler(
            tracer,
            new RestlyticsOptions { InstrumentHttp = true })
        {
            InnerHandler = capture,
        });

        using HttpResponseMessage response = await client.GetAsync("https://api.example.test/orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches(
            "^00-4bf92f3577b34da6a3ce929d0e0e4736-[0-9a-f]{16}-01$",
            capture.Traceparent);
        Assert.Single(state.Children);
        Assert.Equal(capture.Traceparent!.Split('-')[2], state.Children[0].SpanId);
        Assert.Equal(state.RootSpan!.SpanId, state.Children[0].ParentSpanId);
    }

    [Fact]
    public async Task HandlerPropagatesUnsampledContextWithoutRecording()
    {
        var tracer = new Tracer(new NullTransport(), "test", "test");
        RequestState state = tracer.StartServerSpan("GET /proxy", Unsampled);
        var capture = new CaptureHandler();
        using var client = new HttpClient(new RestlyticsHttpHandler(
            tracer,
            new RestlyticsOptions { InstrumentHttp = true })
        {
            InnerHandler = capture,
        });

        using HttpResponseMessage response = await client.GetAsync("https://api.example.test/orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches(
            "^00-4bf92f3577b34da6a3ce929d0e0e4736-[0-9a-f]{16}-00$",
            capture.Traceparent);
        Assert.Empty(state.Children);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Traceparent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Traceparent = request.Headers.TryGetValues("traceparent", out var values)
                ? string.Join(string.Empty, values)
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
