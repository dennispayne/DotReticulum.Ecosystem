using System.Text.Json;
using System.Threading.Channels;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace LXMF.Tests;

[Trait("Category", "Unit")]
public class LxmRetryAccountingTests
{
    private sealed class Clock : TimeProvider
    {
        public double Now { get; set; } = 1700000300;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Now);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new InertTimer();
        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Medium : IPacketInterface
    {
        private readonly Channel<ReadOnlyMemory<byte>> _inbound = System.Threading.Channels.Channel.CreateBounded<ReadOnlyMemory<byte>>(1);
        public long Bitrate => 1_000_000_000;
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) => _inbound.Reader.ReadAllAsync(cancellationToken);
        public ValueTask DisposeAsync() { _inbound.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    // Executed process_outbound, not a transcription: lxmf_retry_vectors.py;
    // LXMF e8ba02483217af43be284a72f1c059c730b7d047, source SHA-256 in fixture.
    [Theory]
    [InlineData(LxmMethod.Direct, "refused-known-path")]
    [InlineData(LxmMethod.Direct, "closed-before-deadline")]
    [InlineData(LxmMethod.Direct, "path-arrives")]
    [InlineData(LxmMethod.Direct, "no-path")]
    [InlineData(LxmMethod.Propagated, "refused-known-path")]
    [InlineData(LxmMethod.Propagated, "closed-before-deadline")]
    [InlineData(LxmMethod.Propagated, "path-arrives")]
    [InlineData(LxmMethod.Propagated, "no-path")]
    public async Task OutboundBranches_MatchExecutedPrimary(LxmMethod method, string scenario)
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-retry-vectors.json")));
        var row = vectors.RootElement.GetProperty("cases").EnumerateArray().Single(entry =>
            entry.GetProperty("method").GetInt32() == (int)method && entry.GetProperty("scenario").GetString() == scenario);
        var clock = new Clock();
        var medium = new Medium();
        await using var interfaces = new PacketInterfaceManager([medium]);
        await interfaces.StartAsync();
        await using var transport = new TransportRouter(interfaces, new TransportRouterOptions { TimeProvider = clock });
        using var source = Identity.Generate();
        using var peer = Identity.Generate();
        var destination = Destination.Create(DestinationType.Single, "lxmf",
            [method == LxmMethod.Direct ? "delivery" : "propagation"], peer);
        var wire = Announce.Create(peer, destination, []).ToArray();
        wire[1] = 1;
        var announce = Packet.Parse(wire);
        void AddPath() => transport.Engine.Process(announce, medium, queueRebroadcast: false);
        if (scenario is "refused-known-path" or "closed-before-deadline") AddPath();
        await using var router = new LxmRouter(transport, source);
        var message = LxMessage.Create(LxMessage.DeliveryDestinationHash(peer), source, "refused", method: method);
        message.Pack(destinationIdentity: method == LxmMethod.Propagated ? peer : null);
        message.State = LxmState.Outbound;
        message.PropagationNodeHash = method == LxmMethod.Propagated ? destination.Hash : null;
        var connects = 0;
        var step = 0;
        foreach (var expected in row.GetProperty("rows").EnumerateArray())
        {
            clock.Now = vectors.RootElement.GetProperty("fixed_now").GetDouble() + expected.GetProperty("time").GetDouble();
            if (scenario == "path-arrives" && step == 1) AddPath();
            foreach (var pending in transport.Links.Links) pending.Teardown();
            await router.SendDirectAsync(message, CancellationToken.None);
            Assert.Equal(expected.GetProperty("attempts").GetInt32(), message.DeliveryAttempts);
            Assert.Equal(expected.GetProperty("state").GetInt32(), (int)message.State);
            Assert.Equal(expected.GetProperty("next_attempt").GetDouble(), message.NextAttempt, 6);
            Assert.Equal(expected.GetProperty("awaiting_path").GetBoolean(), message.AwaitingPath);
            Assert.Equal(expected.GetProperty("path_retried").GetBoolean(), message.PathRequestRetried);
            var status = expected.GetProperty("link_status");
            if (status.ValueKind == JsonValueKind.Null) Assert.Empty(transport.Links.Links);
            else
            {
                Assert.Equal(status.GetInt32(), (int)Assert.Single(transport.Links.Links).Status);
                connects++;
            }
            step++;
        }
        Assert.Equal(LxmState.Failed, message.State);
        Assert.Equal(LxmState.Failed, await message.Completion);
        Assert.Equal(scenario == "no-path" ? 0 : scenario == "path-arrives" ? 3 : 4, connects);
        Assert.Empty(transport.Links.Links);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(5, 4)]
    public async Task PendingLinks_AreNotChargedOrFailed_AndCustomLimitRemainsAnUpperBound(int limit, int links)
    {
        var clock = new Clock();
        var medium = new Medium();
        await using var interfaces = new PacketInterfaceManager([medium]);
        await interfaces.StartAsync();
        await using var transport = new TransportRouter(interfaces, new TransportRouterOptions { TimeProvider = clock });
        using var source = Identity.Generate();
        using var peer = Identity.Generate();
        var destination = Destination.Create(DestinationType.Single, "lxmf", ["delivery"], peer);
        transport.Engine.Process(Announce.Create(peer, destination, []), medium, queueRebroadcast: false);
        await using var router = new LxmRouter(transport, source, options: new LxmRouterOptions { MaxDeliveryAttempts = limit });
        var message = LxMessage.Create(destination.Hash, source, "pending");
        message.Pack();
        message.State = LxmState.Outbound;
        for (var attempt = 1; attempt <= limit; attempt++)
        {
            clock.Now += 20;
            await router.SendDirectAsync(message, CancellationToken.None);
            Assert.Equal(attempt, message.DeliveryAttempts);
            if (attempt == limit) break;
            var link = Assert.Single(transport.Links.Links);
            clock.Now += 1000;
            await router.SendDirectAsync(message, CancellationToken.None);
            Assert.Equal(attempt, message.DeliveryAttempts);
            Assert.Equal(LxmState.Outbound, message.State);
            Assert.Same(link, Assert.Single(transport.Links.Links));
            link.Teardown();
            await router.SendDirectAsync(message, CancellationToken.None);
            Assert.Empty(transport.Links.Links);
        }
        Assert.Equal(links + 1, message.DeliveryAttempts);
        Assert.Equal(LxmState.Failed, message.State);
        Assert.Empty(transport.Links.Links);
    }
}
