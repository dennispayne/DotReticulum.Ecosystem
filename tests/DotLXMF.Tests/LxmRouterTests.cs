using System.Net;
using System.Net.Sockets;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotLXMF.Tests;

/// <summary>Two in-process Reticulum nodes joined by a loopback TCP interface, each with an LXMF router.</summary>
internal sealed class LxmfPair : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly PacketInterfaceManager _serverManager;
    private readonly PacketInterfaceManager _clientManager;
    private readonly List<Task> _runs = [];

    public TransportRouter ServerRouter { get; }
    public TransportRouter ClientRouter { get; }
    public Identity ServerIdentity { get; } = Identity.Generate();
    public Identity ClientIdentity { get; } = Identity.Generate();
    public LxmRouter Server { get; private set; } = null!;
    public LxmRouter Client { get; private set; } = null!;
    public List<ReceivedMessage> ServerInbox { get; } = [];
    public List<ReceivedMessage> ClientInbox { get; } = [];

    private LxmfPair(TimeProvider? timeProvider)
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
        }

        _serverManager = new PacketInterfaceManager([new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, port), perClientInterfaces: true)]);
        _clientManager = new PacketInterfaceManager([new TcpClientPacketInterface("127.0.0.1", port)]);
        ServerRouter = new TransportRouter(_serverManager, new TransportRouterOptions { TimeProvider = timeProvider });
        ClientRouter = new TransportRouter(_clientManager, new TransportRouterOptions { TimeProvider = timeProvider });
    }

    public static async Task<LxmfPair> CreateAsync(LxmRouterOptions? serverOptions = null, LxmRouterOptions? clientOptions = null,
        TimeProvider? transportTimeProvider = null)
    {
        var pair = new LxmfPair(transportTimeProvider);
        await pair._serverManager.StartAsync(pair._cts.Token);
        await pair._clientManager.StartAsync(pair._cts.Token);
        pair._runs.Add(Task.Run(() => pair.ServerRouter.RunAsync(pair._cts.Token)));
        pair._runs.Add(Task.Run(() => pair.ClientRouter.RunAsync(pair._cts.Token)));
        var fast = new LxmRouterOptions { ProcessingInterval = TimeSpan.FromMilliseconds(200), DeliveryRetryWait = TimeSpan.FromSeconds(2), PathRequestWait = TimeSpan.FromSeconds(1) };
        pair.Server = new LxmRouter(pair.ServerRouter, pair.ServerIdentity, "Server", serverOptions ?? fast);
        pair.Client = new LxmRouter(pair.ClientRouter, pair.ClientIdentity, "Client", clientOptions ?? fast);
        pair.Server.MessageReceived = m => { lock (pair.ServerInbox) pair.ServerInbox.Add(m); };
        pair.Client.MessageReceived = m => { lock (pair.ClientInbox) pair.ClientInbox.Add(m); };
        pair.Server.Start();
        pair.Client.Start();
        await Task.Delay(500);
        await pair.Server.AnnounceAsync();
        await pair.Client.AnnounceAsync();
        await pair.WaitAsync(() => pair.ClientRouter.HasPath(pair.Server.DeliveryHash) && pair.ServerRouter.HasPath(pair.Client.DeliveryHash));
        return pair;
    }

    public async Task WaitAsync(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(50);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        await Client.DisposeAsync();
        await _cts.CancelAsync();
        try { await Task.WhenAll(_runs).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        await _serverManager.DisposeAsync();
        await _clientManager.DisposeAsync();
    }
}

[Trait("Category", "Integration")]
public class LxmRouterTests
{
    private sealed class LinkClock : TimeProvider
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => _start.AddTicks(GetTimestamp());
        public void Advance(TimeSpan interval) => Interlocked.Add(ref _ticks, interval.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new InertTimer();
        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(10000)]
    public async Task IdentifiedBackchannel_ReusesLinkWithoutAPathAndReceivesOnInitiator(int replySize)
    {
        await using var pair = await LxmfPair.CreateAsync(clientOptions: new LxmRouterOptions
        {
            ProcessingInterval = TimeSpan.FromMilliseconds(50),
            DeliveryRetryWait = TimeSpan.FromMinutes(1),
        });
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        pair.Server.Log = logs.Enqueue;
        pair.ServerRouter.Log = logs.Enqueue;
        pair.ClientRouter.Log = logs.Enqueue;
        var first = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "open a backchannel");
        await pair.Client.SendAsync(first);
        Assert.Equal(LxmState.Delivered, await first.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        using var schedules = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "lxmf-scheduling-vectors.json")));
        Assert.Equal(schedules.RootElement.GetProperty("active_link_send_attempts").GetInt32(), first.DeliveryAttempts);
        await pair.WaitAsync(() => pair.ServerInbox.Count == 1);
        var incoming = Assert.Single(pair.ServerRouter.Links.Links, link => link.Status == LinkStatus.Active);
        await pair.WaitAsync(() => incoming.RemoteIdentity is not null);
        Assert.Null(incoming.RemoteIdentified);
        Assert.True(pair.ServerRouter.DropPath(pair.Client.DeliveryHash));
        var reply = LxMessage.Create(pair.Client.DeliveryHash, pair.ServerIdentity, new string('b', replySize));
        await pair.Server.SendAsync(reply);
        Assert.Equal(LxmState.Delivered, await reply.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        await pair.WaitAsync(() => pair.ClientInbox.Count == 1);
        Assert.Equal(reply.Content, pair.ClientInbox[0].Message.Content);
        Assert.True(incoming.LinkId.AsSpan().SequenceEqual(Assert.Single(pair.ServerRouter.Links.Links,
            link => link.Status == LinkStatus.Active).LinkId),
            $"Original {incoming.Status}/{incoming.TeardownReason}, RTT {incoming.Rtt}, attempts {reply.DeliveryAttempts}: {string.Join("\n", logs)}");
        Assert.Equal(incoming.LinkId, Assert.Single(pair.ClientRouter.Links.Links, link => link.Status == LinkStatus.Active).LinkId);
        Assert.Equal(incoming.LinkId, reply.RatchetId);
        Assert.Equal(incoming.LinkId, pair.ClientInbox[0].Message.RatchetId);
    }

    [Fact]
    public async Task BlackholedMessageSource_IsRejectedBeforeHistoryAndObserverDelivery()
    {
        using var source = Identity.Generate();
        using var destination = Identity.Generate();
        var blocked = true;
        await using var interfaces = new PacketInterfaceManager([]);
        var transport = new TransportRouter(interfaces, new TransportRouterOptions
        {
            IsIdentityBlocked = hash => blocked && hash.AsSpan().SequenceEqual(source.Hash),
        });
        transport.Engine.Known.Remember(LxMessage.DeliveryDestinationHash(source), new byte[32], source.ExportPublicKey(), null);
        await using var router = new LxmRouter(transport, destination);
        var deliveries = 0;
        router.MessageReceived = _ => deliveries++;
        var message = LxMessage.Create(router.DeliveryHash, source, "block actual message source", method: LxmMethod.Paper);
        message.Pack(destinationIdentity: destination);
        Assert.Equal(LxmPaperIngestResult.Rejected, await router.IngestPaperUriAsync(message.AsUri()));
        Assert.False(router.HasMessage(message.Hash!));
        Assert.Equal(0, deliveries);
        blocked = false;
        Assert.Equal(LxmPaperIngestResult.LocalDelivery, await router.IngestPaperUriAsync(message.AsUri(), allowDuplicate: true));
        Assert.True(router.HasMessage(message.Hash!));
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public async Task IdleDeliveryLink_MatchesExecutedReferenceStrictExpiryAndReconnects()
    {
        var clock = new LinkClock();
        await using var pair = await LxmfPair.CreateAsync(clientOptions: new LxmRouterOptions
        {
            ProcessingInterval = TimeSpan.FromHours(1),
        }, transportTimeProvider: clock);
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        pair.Client.Log = logs.Enqueue;
        pair.ClientRouter.Log = logs.Enqueue;
        pair.ServerRouter.Log = logs.Enqueue;
        var message = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "before idle cleanup");
        await pair.Client.SendAsync(message);
        Assert.Equal(LxmState.Delivered, await message.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        var original = Assert.Single(pair.ClientRouter.Links.Links, link => link.Status == LinkStatus.Active);
        clock.Advance(TimeSpan.FromSeconds(600));
        Assert.Equal(0, pair.Client.CleanLinks());
        Assert.Equal(LinkStatus.Active, original.Status);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, pair.Client.CleanLinks());
        Assert.Equal(LinkStatus.Closed, original.Status);
        var next = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "after idle cleanup");
        await pair.Client.SendAsync(next);
        LxmState result;
        try { result = await next.Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Idle-link recovery stalled in {next.State}, attempts {next.DeliveryAttempts}, " +
                $"awaiting path {next.AwaitingPath}, next attempt {next.NextDeliveryAttempt}, clock {pair.ClientRouter.Links.UnixNow}, " +
                $"path {pair.ClientRouter.HasPath(pair.Server.DeliveryHash)}; " +
                string.Join("; ", pair.ClientRouter.Links.Links.Select(link => $"{link.Status}/{link.TeardownReason}/RTT{link.Rtt}")) +
                $"\n{string.Join("\n", logs)}", ex);
        }
        Assert.Equal(LxmState.Delivered, result);
        await pair.WaitAsync(() => pair.ServerInbox.Count == 2);
        Assert.NotEqual(original.LinkId, Assert.Single(pair.ClientRouter.Links.Links, link => link.Status == LinkStatus.Active).LinkId);
    }

    [Fact]
    public async Task IdlePropagationLinks_ExpireStrictlyAfter180SecondsAndReconnect()
    {
        var storage = Path.Combine(AppContext.BaseDirectory, "idle-propagation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new LinkClock();
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = storage },
                transportTimeProvider: clock);
            var node = pair.Server.EnablePropagationNode(new LxmPropagationOptions
            {
                StampCost = 13,
                TimeProvider = clock,
            });
            await node.AnnounceAsync();
            await pair.WaitAsync(() => pair.ClientRouter.HasPath(node.DestinationHash));
            pair.Client.OutboundPropagationNode = node.DestinationHash;
            var message = LxMessage.Create(pair.Client.DeliveryHash, pair.ClientIdentity, "before propagation idle", method: LxmMethod.Propagated);
            await pair.Client.SendAsync(message);
            Assert.Equal(LxmState.Sent, await message.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
            var link = Assert.Single(pair.ClientRouter.Links.Links, value => value.Status == LinkStatus.Active);
            clock.Advance(TimeSpan.FromSeconds(180));
            Assert.Equal(0, node.CleanLinks());
            Assert.Equal(0, pair.Client.CleanLinks());
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.Equal(1, node.CleanLinks());
            Assert.Equal(1, pair.Client.CleanLinks());
            Assert.Equal(LinkStatus.Closed, link.Status);
            var next = LxMessage.Create(pair.Client.DeliveryHash, pair.ClientIdentity, "after propagation idle", method: LxmMethod.Propagated);
            await pair.Client.SendAsync(next);
            Assert.Equal(LxmState.Sent, await next.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.NotEqual(link.LinkId, Assert.Single(pair.ClientRouter.Links.Links, value => value.Status == LinkStatus.Active).LinkId);
            Assert.Equal(2, node.MessageCount);
        }
        finally { if (Directory.Exists(storage)) Directory.Delete(storage, true); }
    }

    [Fact]
    public async Task OutboundStampCache_ImportsExecutedPythonFormatExpiresAndSurvivesRestart()
    {
        using var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-contract-vectors.json")));
        var root = fixture.RootElement;
        var cache = root.GetProperty("stamp_cost_cache");
        var destination = Identity.FromPrivateKey(Convert.FromHexString(root.GetProperty("destination_private_key").GetString()!));
        var hash = LxMessage.DeliveryDestinationHash(destination);
        var path = Path.Combine(AppContext.BaseDirectory, "stamp-cost-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(path, "lxmf"));
            var file = Path.Combine(path, "lxmf", "outbound_stamp_costs");
            var packed = Convert.FromHexString(cache.GetProperty("packed").GetString()!);
            File.WriteAllBytes(file, packed);
            await using var interfaces = new PacketInterfaceManager([]);
            var transport = new TransportRouter(interfaces, new TransportRouterOptions());
            var options = new LxmRouterOptions { StoragePath = path };
            await using (var expired = new LxmRouter(transport, destination, options: options))
                Assert.Null(expired.GetOutboundStampCost(hash));
            var map = Assert.IsType<Dictionary<object, object?>>(MsgPack.UnpackApplicationValue(packed));
            foreach (var value in map.Values)
                Assert.IsType<object?[]>(value)[0] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            File.WriteAllBytes(file, MsgPack.Pack(map));
            await using (var imported = new LxmRouter(transport, destination, options: options))
            {
                Assert.Equal(12, imported.GetOutboundStampCost(hash));
                imported.UpdateStampCost(hash, null);
                var other = Enumerable.Repeat((byte)8, 16).ToArray();
                imported.UpdateStampCost(other, 14);
                imported.SaveOutboundStampCosts();
                Assert.Throws<ArgumentOutOfRangeException>(() => imported.UpdateStampCost(hash, 255));
                Assert.Throws<ArgumentException>(() => imported.UpdateStampCost(new byte[15], 12));
            }
            await using var reopened = new LxmRouter(transport, destination, options: options);
            Assert.Null(reopened.GetOutboundStampCost(hash));
            Assert.Equal(14, reopened.GetOutboundStampCost(Enumerable.Repeat((byte)8, 16).ToArray()));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task MutableStampCost_AnnouncesNewRequirementAndNoStampPolicy()
    {
        await using var pair = await LxmfPair.CreateAsync();
        const int stampCost = 4;
        Assert.True(pair.Server.SetInboundStampCost(stampCost));
        Assert.False(pair.Server.SetInboundStampCost(255));
        var emitted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await pair.WaitAsync(() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() > emitted);
        await pair.Server.AnnounceAsync();
        await pair.WaitAsync(() => pair.Client.GetOutboundStampCost(pair.Server.DeliveryHash) == stampCost);
        var stamped = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "new stamp requirement");
        await pair.Client.SendAsync(stamped);
        Assert.Equal(LxmState.Delivered, await stamped.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        await pair.WaitAsync(() => pair.ServerInbox.Count == 1);
        Assert.True(pair.ServerInbox[0].StampValid);
        Assert.True(pair.ServerInbox[0].Message.StampChecked);
        Assert.True(pair.ServerInbox[0].Message.StampValid);
        Assert.InRange(pair.ServerInbox[0].Message.StampValue!.Value, stampCost, 256);
        Assert.True(pair.Server.SetInboundStampCost(-1));
        emitted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await pair.WaitAsync(() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() > emitted);
        await pair.Server.AnnounceAsync();
        await pair.WaitAsync(() => pair.Client.GetOutboundStampCost(pair.Server.DeliveryHash) is null);
        var unstamped = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "no stamp requirement");
        await pair.Client.SendAsync(unstamped);
        Assert.Equal(LxmState.Delivered, await unstamped.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Null(unstamped.Stamp);
    }

    [Fact]
    public async Task PaperIngestion_MatchesUpstreamStampBypassDuplicatesAndRestart()
    {
        using var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-contract-vectors.json")));
        var root = fixture.RootElement;
        var source = Identity.FromPrivateKey(Convert.FromHexString(root.GetProperty("source_private_key").GetString()!));
        var destination = Identity.FromPrivateKey(Convert.FromHexString(root.GetProperty("destination_private_key").GetString()!));
        var uri = root.GetProperty("paper").GetProperty("uri").GetString()!;
        var expected = root.GetProperty("paper_ingestion").GetProperty("observations")[0];
        var path = Path.Combine(AppContext.BaseDirectory, "paper-ingest-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var interfaces = new PacketInterfaceManager([]);
            var transport = new TransportRouter(interfaces, new TransportRouterOptions());
            transport.Engine.Known.Remember(LxMessage.DeliveryDestinationHash(source), new byte[32], source.ExportPublicKey(), null);
            var options = new LxmRouterOptions { StoragePath = path, InboundStampCost = 254, EnforceStamps = true };
            await using var router = new LxmRouter(transport, destination, options: options);
            var received = new List<ReceivedMessage>();
            router.Log = _ => throw new InvalidOperationException("broken logger");
            router.MessageReceived = _ => throw new InvalidOperationException("broken observer");
            router.MessageReceived += received.Add;
            Assert.Equal(LxmPaperIngestResult.LocalDelivery, await router.IngestPaperUriAsync(uri));
            Assert.Equal(LxmPaperIngestResult.Duplicate, await router.IngestPaperUriAsync(uri));
            Assert.Equal(LxmPaperIngestResult.LocalDelivery, await router.IngestPaperUriAsync(uri, allowDuplicate: true));
            Assert.Equal(2, received.Count);
            Assert.All(received, item =>
            {
                Assert.Equal(expected.GetProperty("hash").GetString(), Convert.ToHexStringLower(item.Message.Hash!));
                Assert.Equal(expected.GetProperty("method").GetInt32(), (int)item.Message.Method);
                Assert.Equal(expected.GetProperty("signature_validated").GetBoolean(), item.Message.SignatureValidated);
                Assert.Equal(expected.GetProperty("stamp_valid").GetBoolean(), item.StampValid);
            });
            Assert.Equal(LxmPaperIngestResult.Rejected, await router.IngestPaperUriAsync("lxm://not-valid"));
            await router.DisposeAsync();
            await using var reopened = new LxmRouter(transport, destination, options: options);
            Assert.Equal(LxmPaperIngestResult.Duplicate, await reopened.IngestPaperUriAsync(uri));
            Assert.True(reopened.HasMessage(Convert.FromHexString(expected.GetProperty("hash").GetString()!)));
            var processed = Assert.IsType<Dictionary<object, object?>>(MsgPack.UnpackApplicationValue(
                File.ReadAllBytes(Path.Combine(path, "lxmf", "locally_processed"))));
            Assert.Single(processed);
            Assert.Equal(32, Assert.IsType<byte[]>(processed.Keys.Single()).Length);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task PaperIngestion_RecordsDeliveryWithNoObserver()
    {
        await using var interfaces = new PacketInterfaceManager([]);
        var transport = new TransportRouter(interfaces, new TransportRouterOptions());
        var destination = Identity.Generate();
        await using var router = new LxmRouter(transport, destination);
        var paper = LxMessage.Create(router.DeliveryHash, Identity.Generate(), "no observer", method: LxmMethod.Paper);
        paper.Pack(destinationIdentity: destination);
        Assert.Equal(LxmPaperIngestResult.LocalDelivery, await router.IngestPaperUriAsync(paper.AsUri()));
        Assert.True(router.HasMessage(paper.Hash!));
    }

    [Fact]
    public async Task PaperIngestion_BoundsQueuedWorkAndCancelsPendingDeliveryOnDisposal()
    {
        await using var interfaces = new PacketInterfaceManager([]);
        var transport = new TransportRouter(interfaces, new TransportRouterOptions());
        var destination = Identity.Generate();
        await using var router = new LxmRouter(transport, destination, options: new LxmRouterOptions { InboundQueueCapacity = 1 });
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        router.MessageReceived = _ =>
        {
            Interlocked.Increment(ref count);
            started.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        string Paper(string content)
        {
            var message = LxMessage.Create(router.DeliveryHash, Identity.Generate(), content, method: LxmMethod.Paper);
            message.Pack(destinationIdentity: destination);
            return message.AsUri();
        }
        var active = router.IngestPaperUriAsync(Paper("active"));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = router.IngestPaperUriAsync(Paper("queued"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => router.IngestPaperUriAsync(Paper("over capacity")));
            var disposal = router.DisposeAsync().AsTask();
            release.Set();
            await disposal;
            Assert.Equal(LxmPaperIngestResult.LocalDelivery, await active);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Equal(1, count);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task OutboundCapacity_IncludesCancellableStampPreparation()
    {
        await using var pair = await LxmfPair.CreateAsync(
            serverOptions: new LxmRouterOptions { InboundStampCost = 254, EnforceStamps = true },
            clientOptions: new LxmRouterOptions { OutboundQueueCapacity = 1 });
        var first = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "cancel expensive preparation");
        var preparing = pair.Client.SendAsync(first);
        var excess = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "over capacity");
        await Assert.ThrowsAsync<InvalidOperationException>(() => pair.Client.SendAsync(excess));
        pair.Client.Cancel(first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        Assert.Equal(LxmState.Cancelled, await first.Completion);
        var issued = pair.Server.Tickets.Generate(pair.Client.DeliveryHash)!;
        try { pair.Client.Tickets.Remember(pair.Server.DeliveryHash, issued); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(issued.Token); }
        var recovery = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "after cancellation");
        await pair.Client.SendAsync(recovery);
        Assert.Equal(LxmState.Delivered, await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task IgnoredSource_IsDroppedAndCanBeReenabled()
    {
        await using var pair = await LxmfPair.CreateAsync();
        pair.Server.IgnoreDestination(pair.Client.DeliveryHash);
        var ignored = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "ignored", method: LxmMethod.Opportunistic);
        await pair.Client.SendAsync(ignored);
        var barrier = LxMessage.Create(pair.Server.DeliveryHash, pair.ServerIdentity, "barrier", method: LxmMethod.Opportunistic);
        await pair.Client.SendAsync(barrier);
        await pair.WaitAsync(() => pair.ServerInbox.Count == 1);
        Assert.False(pair.Server.HasMessage(ignored.Hash!));
        pair.Server.UnignoreDestination(pair.Client.DeliveryHash);
        var resumed = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "resumed", method: LxmMethod.Opportunistic);
        await pair.Client.SendAsync(resumed);
        await pair.WaitAsync(() => pair.ServerInbox.Count == 2);
        Assert.Equal(new[] { "barrier", "resumed" }, pair.ServerInbox.Select(message => message.Message.Content));
    }

    [Fact]
    public async Task DeliveryHistory_ImportsUpstreamBinaryKeysAndExpiresOldEntries()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "delivery-history-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var vectors = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-contract-vectors.json")));
            var packed = Convert.FromHexString(vectors.RootElement.GetProperty("delivery_cache_packed").GetString()!);
            var history = Assert.IsType<Dictionary<object, object?>>(MsgPack.UnpackApplicationValue(packed));
            var hash = Assert.IsType<byte[]>(Assert.Single(history).Key);
            Directory.CreateDirectory(Path.Combine(path, "lxmf"));
            var historyPath = Path.Combine(path, "lxmf", "local_deliveries");
            File.WriteAllBytes(historyPath, packed);
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
            Assert.False(pair.Server.HasMessage(hash));
            await pair.Server.DisposeAsync();
            history[hash] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            File.WriteAllBytes(historyPath, MsgPack.Pack(history));
            await using var restarted = new LxmRouter(pair.ServerRouter, pair.ServerIdentity,
                options: new LxmRouterOptions { StoragePath = path });
            Assert.True(restarted.HasMessage(hash));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task PersistentDeliveryHistory_SuppressesDuplicatesAfterRouterRestart()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "delivery-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
            var first = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "before restart", method: LxmMethod.Opportunistic);
            await pair.Client.SendAsync(first);
            await pair.WaitAsync(() => pair.Server.HasMessage(first.Hash!));
            await pair.Server.DisposeAsync();
            await using var restarted = new LxmRouter(pair.ServerRouter, pair.ServerIdentity, options: new LxmRouterOptions { StoragePath = path });
            Assert.True(restarted.HasMessage(first.Hash!));
            var inbox = new System.Collections.Concurrent.ConcurrentQueue<ReceivedMessage>();
            restarted.MessageReceived = inbox.Enqueue;
            restarted.Start();
            var duplicate = LxMessage.Unpack(first.Packed!, _ => pair.ClientIdentity, LxmMethod.Opportunistic);
            await pair.Client.SendAsync(duplicate);
            var next = LxMessage.Create(restarted.DeliveryHash, pair.ClientIdentity, "after restart", method: LxmMethod.Opportunistic);
            await pair.Client.SendAsync(next);
            await pair.WaitAsync(() => restarted.HasMessage(next.Hash!));
            await pair.WaitAsync(() => !inbox.IsEmpty);
            Assert.Equal("after restart", Assert.Single(inbox).Message.Content);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task CancelAtResourceStart_PreventsTransferAndAllowsContinuedDelivery()
    {
        await using var pair = await LxmfPair.CreateAsync();
        var cancelled = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, new string('x', 8192));
        cancelled.StateChanged += message =>
        {
            if (message.State == LxmState.Sending) pair.Client.Cancel(message);
        };
        await pair.Client.SendAsync(cancelled);
        Assert.Equal(LxmState.Cancelled, await cancelled.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        var next = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "continued");
        await pair.Client.SendAsync(next);
        Assert.Equal(LxmState.Delivered, await next.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        await pair.WaitAsync(() => pair.ServerInbox.Count == 1);
        Assert.Equal("continued", Assert.Single(pair.ServerInbox).Message.Content);
    }

    [Fact]
    public async Task ReceiveObservers_AreIsolated_AndCoreProcessingRunsWithoutSubscribers()
    {
        await using var pair = await LxmfPair.CreateAsync();
        pair.Server.MessageReceived = null;
        var first = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "no subscriber", method: LxmMethod.Opportunistic);
        await pair.Client.SendAsync(first);
        Assert.Equal(LxmState.Delivered, await first.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        await Task.Delay(200);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = 0;
        pair.Server.Log = errors.Enqueue;
        pair.Server.MessageReceived = _ => throw new InvalidOperationException("receive observer failed");
        pair.Server.MessageReceived += _ =>
        {
            Interlocked.Increment(ref observed);
            delivered.TrySetResult();
        };
        var duplicate = LxMessage.Unpack(first.Packed!, _ => pair.ClientIdentity, LxmMethod.Opportunistic);
        await pair.Client.SendAsync(duplicate);
        Assert.Equal(LxmState.Delivered, await duplicate.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        var next = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "observers", method: LxmMethod.Opportunistic);
        await pair.Client.SendAsync(next);
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, observed);
        Assert.Contains(errors, error => error.Contains("receive observer failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispose_CancelsPendingMessagesAndRejectsFurtherSends()
    {
        await using var pair = await LxmfPair.CreateAsync();
        var pending = LxMessage.Create(new byte[16], pair.ClientIdentity, "no path");
        await pair.Client.SendAsync(pending);
        await pair.Client.DisposeAsync();
        Assert.Equal(LxmState.Cancelled, await pending.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pair.Client.SendAsync(
            LxMessage.Create(new byte[16], pair.ClientIdentity, "disposed")));
    }

    [Fact]
    public async Task DeliveryRatchets_AreAdvertisedAndEnforced()
    {
        await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { EnforceRatchets = true });
        Assert.NotNull(pair.ClientRouter.Engine.Known.GetRatchet(pair.Server.DeliveryHash));
        var message = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "ratcheted", method: LxmMethod.Opportunistic);
        await pair.Client.SendAsync(message);
        Assert.Equal(LxmState.Delivered, await message.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        await pair.WaitAsync(() => pair.ServerInbox.Count == 1);
        Assert.True(pair.ServerInbox[0].Message.SignatureValidated);
    }

    [Fact]
    public async Task ReplyTicket_IsRememberedAndBypassesAdvertisedStampCost()
    {
        await using var pair = await LxmfPair.CreateAsync(clientOptions: new LxmRouterOptions { InboundStampCost = 8, EnforceStamps = true });
        var first = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "with reply ticket", method: LxmMethod.Opportunistic);
        first.IncludeTicket = true;
        await pair.Client.SendAsync(first);
        Assert.Equal(LxmState.Delivered, await first.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        await pair.WaitAsync(() => pair.ServerInbox.Count == 1);
        Assert.NotNull(pair.Server.Tickets.GetOutbound(pair.Client.DeliveryHash));
        var reply = LxMessage.Create(pair.Client.DeliveryHash, pair.ServerIdentity, "ticket reply", method: LxmMethod.Opportunistic);
        await pair.Server.SendAsync(reply);
        Assert.Equal(16, reply.Stamp!.Length);
        Assert.Equal(LxmState.Delivered, await reply.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        await pair.WaitAsync(() => pair.ClientInbox.Count == 1);
        Assert.True(pair.ClientInbox[0].StampValid);
        Assert.Null(pair.Client.Tickets.Generate(pair.Server.DeliveryHash));
    }

    [Fact]
    public async Task FaultingStateObserver_IsLogged_AndOtherObserversAndDeliveryContinue()
    {
        await using var pair = await LxmfPair.CreateAsync();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        pair.Client.Log = errors.Enqueue;
        var message = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "observer isolation", method: LxmMethod.Opportunistic);
        var finalObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        message.StateChanged += _ => throw new InvalidOperationException("state observer failed");
        message.StateChanged += m =>
        {
            if (m.State == LxmState.Delivered) finalObserved.TrySetResult();
        };

        await pair.Client.SendAsync(message);

        Assert.Equal(LxmState.Delivered, await message.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        await finalObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await pair.WaitAsync(() => pair.ServerInbox.Count == 1);
        Assert.Contains(errors, error => error.Contains("state observer failed", StringComparison.Ordinal)
            && error.Contains(Convert.ToHexStringLower(pair.Server.DeliveryHash), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Opportunistic_IsDeliveredAndProved()
    {
        await using var pair = await LxmfPair.CreateAsync();
        var message = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "ping", "t", method: LxmMethod.Opportunistic);
        await pair.Client.SendAsync(message);
        Assert.Equal(LxmState.Delivered, await message.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        await pair.WaitAsync(() => pair.ServerInbox.Count == 1);
        var received = pair.ServerInbox[0].Message;
        Assert.Equal("ping", received.Content);
        Assert.Equal("t", received.Title);
        Assert.True(received.SignatureValidated);
        Assert.Equal(LxmMethod.Opportunistic, received.Method);
        Assert.Equal(message.Hash, received.Hash);
    }

    [Fact]
    public async Task Direct_SmallMessageOverLink_IsDeliveredAndProved()
    {
        await using var pair = await LxmfPair.CreateAsync();
        var message = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, new string('a', 250), fields: new() { [1L] = new object?[] { "x" } });
        await pair.Client.SendAsync(message);
        Assert.Equal(LxmState.Delivered, await message.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        await pair.WaitAsync(() => pair.ServerInbox.Count == 1);
        Assert.Equal(LxmMethod.Direct, pair.ServerInbox[0].Message.Method);
        Assert.Equal(new string('a', 250), pair.ServerInbox[0].Message.Content);
    }

    [Fact]
    public async Task Direct_LargeMessage_GoesAsResource_AndBothDirections()
    {
        await using var pair = await LxmfPair.CreateAsync();
        var content = string.Concat(Enumerable.Range(0, 20_000).Select(i => (char)('a' + (i % 26))));
        var toServer = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, content, "big");
        var toClient = LxMessage.Create(pair.Client.DeliveryHash, pair.ServerIdentity, content + "!", "reply");
        await pair.Client.SendAsync(toServer);
        await pair.Server.SendAsync(toClient);
        Assert.True(toServer.IsResource);
        Assert.Equal(LxmState.Delivered, await toServer.Completion.WaitAsync(TimeSpan.FromSeconds(40)));
        Assert.Equal(LxmState.Delivered, await toClient.Completion.WaitAsync(TimeSpan.FromSeconds(40)));
        await pair.WaitAsync(() => pair.ServerInbox.Count == 1 && pair.ClientInbox.Count == 1);
        Assert.Equal(content, pair.ServerInbox[0].Message.Content);
        Assert.Equal(content + "!", pair.ClientInbox[0].Message.Content);
    }

    [Fact]
    public async Task OversizedOpportunistic_FallsBackToDirect()
    {
        await using var pair = await LxmfPair.CreateAsync();
        var message = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, new string('q', 600), method: LxmMethod.Opportunistic);
        await pair.Client.SendAsync(message);
        Assert.Equal(LxmMethod.Direct, message.Method);
        Assert.Equal(LxmState.Delivered, await message.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task UnknownDestination_FailsAfterRetries()
    {
        var options = new LxmRouterOptions
        {
            ProcessingInterval = TimeSpan.FromMilliseconds(50),
            PathRequestWait = TimeSpan.FromMilliseconds(100),
            DeliveryRetryWait = TimeSpan.FromMilliseconds(100),
            MaxDeliveryAttempts = 2,
        };
        await using var pair = await LxmfPair.CreateAsync(clientOptions: options);
        var message = LxMessage.Create(new byte[16] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 }, pair.ClientIdentity, "nobody home", method: LxmMethod.Opportunistic);
        await pair.Client.SendAsync(message);
        Assert.Equal(LxmState.Failed, await message.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(0, pair.Client.PendingOutbound);
    }

    [Fact]
    public async Task StampRequired_IsGeneratedFromAnnounce_AndEnforced()
    {
        var serverOptions = new LxmRouterOptions
        {
            ProcessingInterval = TimeSpan.FromMilliseconds(200),
            DeliveryRetryWait = TimeSpan.FromSeconds(2),
            InboundStampCost = 5,
            EnforceStamps = true,
        };
        await using var pair = await LxmfPair.CreateAsync(serverOptions: serverOptions);
        Assert.Equal(5, LxmfAppData.StampCost(pair.ClientRouter.Engine.Known.RecallAppData(pair.Server.DeliveryHash)));

        var stamped = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "with stamp");
        await pair.Client.SendAsync(stamped);
        Assert.NotNull(stamped.Stamp);
        Assert.Equal(LxmState.Delivered, await stamped.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        await pair.WaitAsync(() => pair.ServerInbox.Count == 1);
        Assert.True(pair.ServerInbox[0].StampValid);

        // Bypass stamp generation: the receiver acknowledges the packet but drops the message.
        var unstamped = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "no stamp");
        unstamped.Pack();
        await pair.Client.SendAsync(unstamped);
        Assert.Equal(LxmState.Delivered, await unstamped.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        await Task.Delay(500);
        Assert.Single(pair.ServerInbox);
    }

    [Fact]
    public async Task DuplicateMessages_AreDeliveredOnce()
    {
        await using var pair = await LxmfPair.CreateAsync();
        var message = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "once", method: LxmMethod.Opportunistic);
        message.Pack();
        var copy = LxMessage.Unpack(message.Packed!, _ => pair.ClientIdentity, LxmMethod.Opportunistic);
        await pair.Client.SendAsync(message);
        await pair.WaitAsync(() => pair.ServerInbox.Count == 1);
        await pair.Client.SendAsync(LxMessageClone(copy, pair.ClientIdentity));
        await Task.Delay(1000);
        Assert.Single(pair.ServerInbox);
    }

    // Re-sends the identical wire bytes under a new LxMessage (same hash) to exercise duplicate suppression.
    private static LxMessage LxMessageClone(LxMessage original, Identity source)
    {
        var clone = LxMessage.Create(original.DestinationHash, source, original.Content, original.Title, original.Fields, LxmMethod.Opportunistic, original.Timestamp);
        clone.Pack();
        return clone;
    }
}
