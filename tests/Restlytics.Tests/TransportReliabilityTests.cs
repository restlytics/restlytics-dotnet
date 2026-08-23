using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Restlytics.AspNetCore;
using Xunit;

namespace Restlytics.Tests;

public sealed class TransportReliabilityTests
{
    [Fact]
    public async Task SendIsNonBlockingBoundedObservableAndFlushable()
    {
        var handler = new GateHandler();
        using var client = new HttpClient(handler);
        var transport = new HttpTransport(
            "http://ingest.test", "rl_test", 500, client, queueCapacity: 4);
        ExportTraceServiceRequest payload = Payload.Build("test", "test", Array.Empty<OtlpSpan>());

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            transport.Send(payload);
        }
        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(250));
        RestlyticsTransportDiagnostics snapshot = transport.Snapshot;
        Assert.True(snapshot.AcceptedBatches <= 5);
        Assert.True(snapshot.DroppedBatches >= 5);
        Assert.Equal(4, snapshot.QueueCapacity);

        handler.Release();
        Assert.True(await transport.FlushAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(snapshot.AcceptedBatches, transport.Snapshot.DeliveredBatches);
        await transport.DisposeAsync();
        transport.Send(payload);
        Assert.Equal(snapshot.DroppedBatches + 1, transport.Snapshot.DroppedBatches);
    }

    [Fact]
    public async Task TimeoutIsCountedSwallowedAndNeverRetried()
    {
        var handler = new NeverRespondHandler();
        using var client = new HttpClient(handler);
        await using var transport = new HttpTransport("http://ingest.test", "rl_test", 20, client);
        ExportTraceServiceRequest payload = Payload.Build("test", "test", Array.Empty<OtlpSpan>());

        Exception? error = Record.Exception(() => transport.Send(payload));
        Assert.Null(error);
        Assert.True(await transport.FlushAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, handler.Attempts);
        Assert.Equal(1, transport.Snapshot.FailedBatches);
        Assert.Equal(0, transport.Snapshot.DeliveredBatches);
    }

    private sealed class GateHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private sealed class NeverRespondHandler : HttpMessageHandler
    {
        public int Attempts;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
