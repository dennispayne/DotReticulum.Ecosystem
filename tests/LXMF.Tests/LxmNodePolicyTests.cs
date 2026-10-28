using System.Text.Json;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace LXMF.Tests;

// GenerateLxmNodePolicyVectors.py executes LXMF 1.2.1 e8ba02483217af43be284a72f1c059c730b7d047.
// The fixture records actual LXMRouter/LXMessage source hashes and RNS a29bfb51c468945c96a1ee22cae0114e993e2391.
[Trait("Category", "Integration")]
public class LxmNodePolicyTests
{
    private sealed class RecordedMedium : IPacketInterface
    {
        private readonly System.Threading.Channels.Channel<ReadOnlyMemory<byte>> _inbound =
            System.Threading.Channels.Channel.CreateBounded<ReadOnlyMemory<byte>>(1);
        public readonly System.Collections.Concurrent.ConcurrentQueue<Packet> Sent = new();
        public long Bitrate => 10_000_000;
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            Sent.Enqueue(Packet.Parse(packet.ToArray()));
            return ValueTask.CompletedTask;
        }
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) => _inbound.Reader.ReadAllAsync(cancellationToken);
        public ValueTask DisposeAsync() { _inbound.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, limit.Token);
    }

    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "lxmf-node-policy-vectors.json")));

    private sealed class ScheduledClock(double unixSeconds = 1700000400) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ClockTimer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(unixSeconds).AddTicks(GetTimestamp());
        public bool HasTimerAt(TimeSpan offset) { lock (_gate) return _timers.Any(timer => timer.Due == offset.Ticks); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            lock (_gate) { _timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        public void Advance(TimeSpan interval)
        {
            ClockTimer[] ready;
            lock (_gate)
            {
                _ticks += interval.Ticks;
                ready = _timers.Where(timer => timer.Due <= _ticks).ToArray();
                foreach (var timer in ready) timer.Due = timer.Period > 0 ? _ticks + timer.Period : long.MaxValue;
            }
            foreach (var timer in ready) timer.Fire();
        }
        private sealed class ClockTimer(ScheduledClock clock, TimerCallback callback, object? state) : ITimer
        {
            public long Due { get; set; }
            public long Period { get; private set; }
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                    Period = period.Ticks;
                    return true;
                }
            }
            public void Dispose() { lock (clock._gate) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task RepeatedDeposit_CountsPacketsButStoresOneMessageLikeReference()
    {
        using var vectors = Vectors();
        var source = vectors.RootElement.GetProperty("duplicate_packets");
        var container = Assert.IsType<Dictionary<object, object?>>(MsgPack.UnpackApplicationValue(
            Convert.FromHexString(vectors.RootElement.GetProperty("message_file").GetProperty("container").GetString()!)));
        var packed = Assert.IsType<byte[]>(container["lxmf_bytes"]);
        var envelope = MsgPack.Pack(new object?[] { 1700000400.0, new object?[] { packed.Concat(new byte[32]).ToArray() } });
        var path = Path.Combine(AppContext.BaseDirectory, "duplicate-counter-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
            var node = pair.Server.EnablePropagationNode(new LxmPropagationOptions
            {
                AutoPeer = false, StampCost = 13, StampFlexibility = 13,
            });
            await node.AnnounceAsync();
            await pair.WaitAsync(() => pair.ClientRouter.HasPath(node.DestinationHash));
            var active = new TaskCompletionSource<DotReticulum.Transport.Link>(TaskCreationOptions.RunContinuationsAsynchronously);
            var link = pair.ClientRouter.Links.Connect(pair.ServerIdentity, node.DestinationHash, ready => active.TrySetResult(ready));
            await active.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var received = 1; received <= source.GetProperty("received").GetInt32(); received++)
            {
                link.Send(envelope, createReceipt: false);
                await pair.WaitAsync(() => (long)((Dictionary<object, object?>)node.CompileStats()!["clients"]!)
                    ["client_propagation_messages_received"]! == received);
            }
            Assert.Equal(source.GetProperty("stored").GetInt32(), node.MessageCount);
            link.Teardown();
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task OutboundPropagationCost_DiscoversPathWithoutAnnounceAndChangingNodeRetiresCachedRetrieval()
    {
        using var vectors = Vectors();
        var source = vectors.RootElement.GetProperty("cost_discovery");
        var path = Path.Combine(AppContext.BaseDirectory, "cost-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
            var node = pair.Server.EnablePropagationNode(new LxmPropagationOptions { AutoPeer = false });
            Assert.False(pair.ClientRouter.HasPath(node.DestinationHash));
            pair.Client.OutboundPropagationNode = node.DestinationHash;
            var message = LxMessage.Create(pair.Client.DeliveryHash, pair.ClientIdentity, "path-discovered propagation", method: LxmMethod.Propagated);
            await pair.Client.SendAsync(message);
            Assert.Equal(LxmState.Sent, await message.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(source.GetProperty("cached").GetInt32(), await pair.Client.GetOutboundPropagationCostAsync());
            Assert.Equal(1, await pair.Client.RequestMessagesFromPropagationNodeAsync());
            Assert.NotNull(pair.Client.PropagationLinkId);
            pair.Client.OutboundPropagationNode = Enumerable.Repeat((byte)'z', 16).ToArray();
            Assert.Null(pair.Client.PropagationLinkId);
            Assert.Null(await pair.Client.GetOutboundPropagationCostAsync());
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pair.Client.GetOutboundPropagationCostAsync(cancelled.Token));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task MessageFileHelpers_MatchExecutedReferenceAtomicNamingAndBoundReads()
    {
        using var vectors = Vectors();
        var source = vectors.RootElement.GetProperty("message_file");
        var container = Convert.FromHexString(source.GetProperty("container").GetString()!);
        using var identity = Identity.FromPublicKey(Convert.FromHexString(source.GetProperty("source_public_key").GetString()!));
        var message = LxMessage.UnpackContainer(container, _ => identity);
        Assert.True(message.SignatureValidated);
        var path = Path.Combine(AppContext.BaseDirectory, "message-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
            var writes = await Task.WhenAll(message.WriteToDirectoryAsync(path), message.WriteToDirectoryAsync(path));
            Assert.Equal(writes[0], writes[1]);
            Assert.Equal(source.GetProperty("filename").GetString(), Path.GetFileName(writes[0]));
            Assert.Equal(container, await File.ReadAllBytesAsync(writes[0]));
            var restored = await LxMessage.UnpackFromFileAsync(writes[0], _ => identity);
            Assert.True(restored.SignatureValidated);
            Assert.Equal(message.Hash, restored.Hash);
            Assert.Equal(message.State, restored.State);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                LxMessage.UnpackFromFileAsync(writes[0], _ => identity, maximumBytes: container.Length - 1));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => message.WriteToDirectoryAsync(path, cancelled.Token));
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => message.WriteToDirectoryAsync(Path.Combine(path, "missing")));
            Assert.Empty(Directory.GetFiles(path, "*.out"));
            Assert.Equal(container, await File.ReadAllBytesAsync(writes[0]));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task StoreRestart_RemovesOwnedPartialWritesPreservesCommittedReferenceEntryAndReleasesCorruptLease()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "lxmf-contract-vectors.json")));
        var source = vectors.RootElement.GetProperty("propagation");
        var envelope = Assert.IsType<object?[]>(MsgPack.UnpackApplicationValue(
            Convert.FromHexString(source.GetProperty("envelope").GetString()!)));
        var encrypted = Assert.IsType<byte[]>(Assert.Single(Assert.IsType<object?[]>(envelope[1])));
        byte[] stamped = [.. encrypted, .. Convert.FromHexString(source.GetProperty("stamp").GetString()!)];
        var path = Path.Combine(AppContext.BaseDirectory, "store-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = Path.Combine(path, "lxmf", "messagestore");
            Directory.CreateDirectory(store);
            var committed = Path.Combine(store, source.GetProperty("transient_id").GetString() + "_1700000400_" +
                source.GetProperty("stamp_value").GetInt32());
            File.WriteAllBytes(committed, stamped);
            File.WriteAllBytes(committed + ".out", stamped[..40]);
            var unrelated = Path.Combine(store, "notes.out");
            File.WriteAllBytes(unrelated, [1]);
            foreach (var name in new[] { "peers", "node_stats", "local_deliveries", "locally_processed", "outbound_stamp_costs" })
                File.WriteAllBytes(Path.Combine(path, "lxmf", name + "." + Guid.NewGuid().ToString("N") + ".out"), [0xc1]);
            await using var interfaces = new PacketInterfaceManager([new RecordedMedium()]);
            await interfaces.StartAsync();
            await using var transport = new TransportRouter(interfaces);
            using var identity = Identity.Generate();
            await using var router = new LxmRouter(transport, identity, options: new LxmRouterOptions { StoragePath = path });
            var options = new LxmPropagationOptions { AutoPeer = false, TimeProvider = new ScheduledClock() };
            await using (var node = new LxmPropagationNode(transport, identity, path, options))
            {
                Assert.Equal(1, node.MessageCount);
                Assert.Equal(stamped.Length, node.StoredBytes);
                Assert.False(File.Exists(committed + ".out"));
                Assert.True(File.Exists(unrelated));
                Assert.Empty(Directory.GetFiles(Path.Combine(path, "lxmf"), "*.out"));
                Assert.Throws<IOException>(() => new LxmPropagationNode(transport, identity, path, options));
            }
            var corrupt = stamped.ToArray();
            corrupt[20] ^= 1;
            File.WriteAllBytes(committed, corrupt);
            Assert.Throws<InvalidDataException>(() => new LxmPropagationNode(transport, identity, path, options));
            File.WriteAllBytes(committed, stamped);
            await using var recovered = new LxmPropagationNode(transport, identity, path, options);
            Assert.Equal(1, recovered.MessageCount);
            Assert.Equal(stamped, File.ReadAllBytes(committed));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task TransientCleanup_MatchesSourceStrictBoundaryAndRunsWhileRouterIsIdle()
    {
        using var vectors = Vectors();
        var source = vectors.RootElement.GetProperty("jobs");
        var retention = TimeSpan.FromDays(180);
        var path = Path.Combine(AppContext.BaseDirectory, "transient-jobs-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(path, "lxmf"));
            var history = source.GetProperty("history").EnumerateObject().ToDictionary(
                item => (object)Convert.FromHexString(item.Name), item => (object?)item.Value.GetDouble());
            foreach (var name in new[] { "local_deliveries", "locally_processed" })
                File.WriteAllBytes(Path.Combine(path, "lxmf", name), MsgPack.Pack(history));
            var clock = new ScheduledClock(1700000400 - retention.TotalSeconds - 2);
            await using var interfaces = new PacketInterfaceManager([new RecordedMedium()]);
            await interfaces.StartAsync();
            await using var transport = new TransportRouter(interfaces, new TransportRouterOptions { TimeProvider = clock });
            using var identity = Identity.Generate();
            await using var router = new LxmRouter(transport, identity, options: new LxmRouterOptions
            {
                StoragePath = path,
                ProcessingInterval = TimeSpan.FromMilliseconds(10),
            });
            foreach (var key in history.Keys.Cast<byte[]>()) Assert.True(router.HasMessage(key));
            clock.Advance(retention + TimeSpan.FromSeconds(2));
            Assert.Equal(2, router.CleanTransientIdCaches());
            foreach (var (name, expected) in new[] { ("local_deliveries", "delivered"), ("locally_processed", "processed") })
            {
                var map = Assert.IsType<Dictionary<object, object?>>(MsgPack.UnpackApplicationValue(
                    File.ReadAllBytes(Path.Combine(path, "lxmf", name))));
                Assert.Equal(source.GetProperty(expected).EnumerateArray().Select(value => value.GetString()).Order(),
                    map.Keys.Cast<byte[]>().Select(Convert.ToHexStringLower).Order());
            }
            var interval = source.GetProperty("interval").GetDouble() *
                source.GetProperty("cadence").GetProperty("clean_transient_id_caches").GetProperty("interval").GetInt32();
            clock.Advance(TimeSpan.FromSeconds(interval));
            router.Start();
            await WaitFor(() => history.Keys.Cast<byte[]>().All(key => !router.HasMessage(key)));
            foreach (var name in new[] { "local_deliveries", "locally_processed" })
                Assert.Empty(Assert.IsType<Dictionary<object, object?>>(MsgPack.UnpackApplicationValue(
                    File.ReadAllBytes(Path.Combine(path, "lxmf", name)))));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task InvalidResourceThrottle_MatchesSourceStrictExpiryAndRecoversAfterObserverFailure()
    {
        using var vectors = Vectors();
        var source = vectors.RootElement.GetProperty("jobs");
        var path = Path.Combine(AppContext.BaseDirectory, "throttle-jobs-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
            var clock = new ScheduledClock();
            var node = pair.Server.EnablePropagationNode(new LxmPropagationOptions { AutoPeer = false, PeeringCost = 0, TimeProvider = clock });
            var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node.Log = _ => throw new InvalidOperationException("test failed log observer");
            node.Log += line => { if (line.Contains("invalid stamps", StringComparison.Ordinal)) rejected.TrySetResult(); };
            await node.AnnounceAsync();
            await pair.WaitAsync(() => pair.ClientRouter.HasPath(node.DestinationHash));
            var link = await LxmPropagationTransport.ConnectAsync(pair.ClientRouter, node.DestinationHash,
                TimeSpan.FromSeconds(10), CancellationToken.None);
            link.Identify(pair.ClientIdentity);
            await LxmPropagationTransport.SendResourceAsync(link,
                MsgPack.Pack(new object?[] { 1700000400.0, new object?[] { "invalid"u8.ToArray() } }),
                TimeSpan.FromSeconds(10), CancellationToken.None);
            await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await pair.WaitAsync(() => link.Status == LinkStatus.Closed);

            async Task<object?> Offer()
            {
                var offerLink = await LxmPropagationTransport.ConnectAsync(pair.ClientRouter, node.DestinationHash,
                    TimeSpan.FromSeconds(10), CancellationToken.None);
                try
                {
                    offerLink.Identify(pair.ClientIdentity);
                    return await LxmPropagationTransport.RequestAsync(offerLink, "/offer",
                        new object?[] { new byte[32], Array.Empty<object?>() }, TimeSpan.FromSeconds(10), 1024, CancellationToken.None);
                }
                finally { offerLink.Teardown(); }
            }
            Assert.Equal(0xf6L, await Offer());
            clock.Advance(TimeSpan.FromSeconds(180));
            Assert.Equal(source.GetProperty("throttle_at_boundary").GetInt32() == 1 ? 0 : 1, node.CleanThrottledPeers());
            Assert.Equal(0xf6L, await Offer());
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.Equal(1 - source.GetProperty("throttle_after_boundary").GetInt32(), node.CleanThrottledPeers());
            Assert.Equal(false, await Offer());
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task ScheduledAnnouncements_MatchSourceDelayControlPolicyAndCancelBoundedWorkOnDisposal()
    {
        using var vectors = Vectors();
        var source = vectors.RootElement.GetProperty("delayed_announce");
        var delay = TimeSpan.FromSeconds(source.GetProperty("sleeps")[0].GetDouble());
        var path = Path.Combine(AppContext.BaseDirectory, "node-announcement-" + Guid.NewGuid().ToString("N"));
        try
        {
            var medium = new RecordedMedium();
            await using var interfaces = new PacketInterfaceManager([medium]);
            await interfaces.StartAsync();
            await using var transport = new TransportRouter(interfaces);
            using var identity = Identity.Generate();
            var clock = new ScheduledClock();
            await using var router = new LxmRouter(transport, identity, options: new LxmRouterOptions { StoragePath = path });
            var node = router.EnablePropagationNode(new LxmPropagationOptions { AutoPeer = false, TimeProvider = clock });
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            node.Log = logs.Enqueue;
            await WaitFor(() => clock.HasTimerAt(delay));
            clock.Advance(delay - TimeSpan.FromMilliseconds(1));
            Assert.Empty(medium.Sent);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await WaitFor(() => logs.Count == 1);
            Assert.Single(medium.Sent);
            node.AllowControl(Enumerable.Repeat((byte)'c', 16).ToArray());
            await router.DisablePropagationNodeAsync();
            Assert.Single(medium.Sent);
            await WaitFor(() => clock.HasTimerAt(delay * 2));
            clock.Advance(delay);
            await WaitFor(() => logs.Count == 2);
            var announcements = medium.Sent.Where(packet => packet.DestinationHash.Span.SequenceEqual(node.DestinationHash))
                .Select(packet =>
                {
                    Assert.True(Announce.TryParse(packet, out var data));
                    Assert.True(LxmfPropagationAnnounce.TryParse(data!.AppData, out var config));
                    return config!.Enabled;
                }).ToArray();
            Assert.Equal(source.GetProperty("propagation_enabled").EnumerateArray().Select(value => value.GetBoolean()), announcements);
            Assert.Equal(source.GetProperty("control_announces").GetInt32(),
                medium.Sent.Count(packet => packet.DestinationHash.Span.SequenceEqual(node.ControlDestinationHash)));
            for (var index = 0; index < 256; index++) node.ScheduleAnnounce();
            Assert.Throws<InvalidOperationException>(node.ScheduleAnnounce);
            await node.DisposeAsync();
            clock.Advance(delay);
            Assert.Equal(3, medium.Sent.Count);
            Assert.Throws<ObjectDisposedException>(node.ScheduleAnnounce);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task InvalidPropagationPacket_RejectsAssociatedMessageWithoutRetrying()
    {
        using var vectors = Vectors();
        Assert.Equal(new object?[] { 0xf5L }, (object?[])MsgPack.UnpackApplicationValue(
            Convert.FromHexString(vectors.RootElement.GetProperty("rejection").GetProperty("packet").GetString()!))!);
        var path = Path.Combine(AppContext.BaseDirectory, "invalid-propagation-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
            var node = pair.Server.EnablePropagationNode(new LxmPropagationOptions { AutoPeer = false, StampCost = 16, StampFlexibility = 0 });
            await node.AnnounceAsync();
            await pair.WaitAsync(() => pair.ClientRouter.HasPath(node.DestinationHash));
            var message = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "invalid stamp", method: LxmMethod.Propagated);
            message.Pack(destinationIdentity: pair.ServerIdentity);
            var stamp = new byte[LxStamper.StampSize];
            var block = LxStamper.Workblock(message.TransientId!, LxStamper.WorkblockExpandRoundsPn);
            for (var attempt = 0; attempt < 256 && LxStamper.IsValid(stamp, 16, block); attempt++) stamp[0]++;
            Assert.False(LxStamper.IsValid(stamp, 16, block));
            message.SetPropagationStamp(stamp);
            message.PropagationNodeHash = node.DestinationHash;
            var states = new List<LxmState>();
            message.StateChanged += value => { lock (states) states.Add(value.State); };
            await pair.Client.SendAsync(message);
            await pair.WaitAsync(() => message.State == LxmState.Rejected);
            var attempts = message.DeliveryAttempts;
            await Task.Delay(300);
            Assert.Equal(LxmState.Rejected, message.State);
            Assert.Equal(attempts, message.DeliveryAttempts);
            Assert.Equal(0, node.MessageCount);
            lock (states) Assert.DoesNotContain(LxmState.Sent, states);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroDiscoveredPeerCapacity_PreservesSourceStaticAndKnownReloadExceptions(bool reload)
    {
        using var vectors = Vectors();
        var path = Path.Combine(AppContext.BaseDirectory, "node-admission-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
            var options = new LxmPropagationOptions
            {
                MaxPeers = 0,
                AutoPeer = false,
                StaticPeers = reload ? [] : [Enumerable.Repeat((byte)'s', 16).ToArray()],
            };
            if (reload)
            {
                var row = vectors.RootElement.GetProperty("reload");
                Directory.CreateDirectory(Path.Combine(path, "lxmf"));
                File.WriteAllBytes(Path.Combine(path, "lxmf", "peers"), MsgPack.Pack(row.GetProperty("serialized")
                    .EnumerateArray().Select(value => (object?)Convert.FromHexString(value.GetString()!)).ToArray()));
                pair.ServerRouter.Engine.Known.Remember(Enumerable.Repeat((byte)'k', 16).ToArray(), new byte[32],
                    Convert.FromHexString(row.GetProperty("known_public_key").GetString()!), null);
            }
            var node = pair.Server.EnablePropagationNode(options);
            Assert.Single(node.Peers);
            Assert.Equal(Enumerable.Repeat((byte)(reload ? 'k' : 's'), 16), node.Peers[0].DestinationHash);
            await node.DisposeAsync();
            await using var reopened = new LxmPropagationNode(pair.ServerRouter, pair.ServerIdentity, path, options);
            Assert.Single(reopened.Peers);
            Assert.Equal(Enumerable.Repeat((byte)(reload ? 'k' : 's'), 16), reopened.Peers[0].DestinationHash);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task IncomingResource_DiscoversCachedAnnouncedPeerUsingExecutedSourceGates()
    {
        using var vectors = Vectors();
        foreach (var row in vectors.RootElement.GetProperty("incoming").EnumerateArray())
        {
            var path = Path.Combine(AppContext.BaseDirectory, "incoming-discovery-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using var pair = await LxmfPair.CreateAsync(
                    serverOptions: new LxmRouterOptions { StoragePath = Path.Combine(path, "sender") },
                    clientOptions: new LxmRouterOptions { StoragePath = Path.Combine(path, "receiver") });
                var sender = pair.Server.EnablePropagationNode(new LxmPropagationOptions
                {
                    AutoPeer = false,
                    PeeringCost = row.GetProperty("peering_cost").GetInt32(),
                });
                sender.SetEnabled(row.GetProperty("enabled").GetBoolean());
                var receiver = pair.Client.EnablePropagationNode(new LxmPropagationOptions
                {
                    AutoPeer = row.GetProperty("autopeer").GetBoolean(),
                    AutoPeerMaxDepth = row.GetProperty("max_depth").GetInt32(),
                });
                var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                receiver.Log = line => { if (line.Contains("Resource accepted", StringComparison.Ordinal)) accepted.TrySetResult(); };
                await receiver.AnnounceAsync();
                await pair.WaitAsync(() => pair.ServerRouter.HasPath(receiver.DestinationHash));
                var destination = Destination.Create(DestinationType.Single, LxmRouter.AppName, ["propagation"], pair.ServerIdentity);
                await pair.ServerRouter.SendAsync(Announce.Create(pair.ServerIdentity, destination, sender.GetAnnounceAppData(), pathResponse: true));
                await pair.WaitAsync(() => pair.ClientRouter.HasPath(sender.DestinationHash));
                Assert.Equal(row.GetProperty("hops").GetInt32(), pair.ClientRouter.HopsTo(sender.DestinationHash));
                Assert.Empty(receiver.Peers);
                var link = await LxmPropagationTransport.ConnectAsync(pair.ServerRouter, receiver.DestinationHash,
                    TimeSpan.FromSeconds(10), CancellationToken.None);
                try
                {
                    link.Identify(pair.ServerIdentity);
                    await LxmPropagationTransport.SendResourceAsync(link,
                        MsgPack.Pack(new object?[] { 1700000400.0, Array.Empty<object?>() }),
                        TimeSpan.FromSeconds(10), CancellationToken.None);
                    await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(row.GetProperty("peer_count").GetInt32(), receiver.Peers.Count);
                    if (receiver.Peers.Count != 0) Assert.Equal(sender.DestinationHash, receiver.Peers[0].DestinationHash);
                }
                finally { link.Teardown(); }
            }
            finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        }
    }
}
