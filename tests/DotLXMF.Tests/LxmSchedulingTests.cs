using System.Text.Json;
using System.Threading.Channels;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotLXMF.Tests;

[Trait("Category", "Unit")]
public class LxmSchedulingTests
{
    private sealed class Clock : TimeProvider
    {
        private double _seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(1_000_000 + _seconds);
        public void Advance(double seconds) => _seconds += seconds;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new InertTimer();
        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Medium(long bitrate) : IPacketInterface
    {
        private readonly Channel<ReadOnlyMemory<byte>> _inbound = System.Threading.Channels.Channel.CreateBounded<ReadOnlyMemory<byte>>(1);
        public long Bitrate => bitrate;
        public int Sent { get; private set; }
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) { Sent++; return ValueTask.CompletedTask; }
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) => _inbound.Reader.ReadAllAsync(cancellationToken);
        public ValueTask DisposeAsync() { _inbound.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    [Theory]
    [InlineData(1000, 14, 60)]
    [InlineData(10000, 7, 7)]
    public async Task RuntimePathRequests_UseOnlineMediumAndDebounceUntilStrictBoundary(long bitrate, double pathWait, double debounce)
    {
        var clock = new Clock();
        var medium = new Medium(bitrate);
        await using var interfaces = new PacketInterfaceManager([medium]);
        await interfaces.StartAsync();
        var transport = new TransportRouter(interfaces, new TransportRouterOptions { TimeProvider = clock });
        using var identity = Identity.Generate();
        await using var router = new LxmRouter(transport, identity);
        var destination = new byte[16];
        Assert.Equal(pathWait, router.PathRequestWait, 6);
        Assert.Equal(bitrate < 2000, router.SlowInterfaceOnline);
        Assert.True(await router.RequestPathAsync(destination));
        Assert.True(router.PathRequestPending(destination));
        Assert.False(await router.RequestPathAsync(destination));
        clock.Advance(debounce - 0.001);
        Assert.False(await router.RequestPathAsync(destination));
        clock.Advance(0.001);
        Assert.True(await router.RequestPathAsync(destination));
        Assert.Equal(2, medium.Sent);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => router.RequestPathAsync(destination, cancelled.Token));
        Assert.Equal(2, medium.Sent);
    }

    // GenerateLxmSchedulingVectors.py executes LXMRouter.py at e8ba02483217af43be284a72f1c059c730b7d047.
    [Fact]
    public void AttemptSchedules_MatchActualRouterMediumAndAirtimePolicies()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-scheduling-vectors.json")));
        var now = vectors.RootElement.GetProperty("fixed_now").GetDouble();
        foreach (var row in vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            var pathRequest = row.GetProperty("path_request").GetBoolean();
            var hasPath = row.GetProperty("has_path").GetBoolean();
            var rtt = pathRequest ? row.GetProperty("medium_timeout").GetDouble() : row.GetProperty("round_trip").GetDouble();
            var result = LxmDeliveryTiming.Schedule(now, rtt, pathRequest,
                row.GetProperty("delivery_attempts").GetInt32(), vectors.RootElement.GetProperty("jitter_fraction").GetDouble());
            Assert.Equal(row.GetProperty("next_attempt").GetDouble(), result.NextAttempt, 6);
            Assert.Equal(row.GetProperty("attempt_limit").GetInt32(), result.AttemptLimit);
            var timeout = row.GetProperty("receipt_timeout");
            if (timeout.ValueKind == JsonValueKind.Null) Assert.Null(result.ReceiptTimeout);
            else Assert.Equal(timeout.GetDouble(), result.ReceiptTimeout!.Value, 6);
            Assert.Equal(row.GetProperty("due_at_deadline").GetBoolean(),
                LxmDeliveryTiming.AttemptDue(result.NextAttempt, result.NextAttempt, pathRequest, hasPath));
            Assert.Equal(row.GetProperty("due_after_deadline").GetBoolean(),
                LxmDeliveryTiming.AttemptDue(result.NextAttempt + 0.001, result.NextAttempt, pathRequest, hasPath));
        }
    }

    [Fact]
    public void SlowPathOrdering_MatchesActualRouterQueueAndInflightChecks()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-scheduling-vectors.json")));
        using var source = Identity.Generate();
        var destination = Enumerable.Repeat((byte)'d', 16).ToArray();
        foreach (var row in vectors.RootElement.GetProperty("ordering").EnumerateArray())
        {
            var message = LxMessage.Create(destination, source, "");
            message.State = LxmState.Outbound;
            var other = LxMessage.Create(row.GetProperty("same_target").GetBoolean()
                ? destination : Enumerable.Repeat((byte)'x', 16).ToArray(), source, "",
                method: (LxmMethod)row.GetProperty("other_method").GetInt32());
            other.Method = other.DesiredMethod;
            other.State = (LxmState)row.GetProperty("other_state").GetInt32();
            other.SentOnSlowPath = row.GetProperty("sent_on_slow_path").GetBoolean();
            var pending = row.GetProperty("ahead").GetBoolean() ? new[] { other, message } : new[] { message, other };
            Assert.Equal(row.GetProperty("held").GetBoolean(),
                LxmDeliveryTiming.Held(message, destination, pending, entry =>
                    entry.Method == LxmMethod.Propagated ? Enumerable.Repeat((byte)'p', 16).ToArray() : entry.DestinationHash));
        }
    }
}
