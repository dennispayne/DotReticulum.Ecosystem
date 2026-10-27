using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotLXMF.Tests.Interop;
using DotReticulum.Transport;

namespace DotLXMF.Tests;

/// <summary>.NET LxmRouter against the real upstream Python LXMF 1.2.1 LXMRouter (tests/Interop/live_lxmf_node.py). DOTRETICULUM_LIVE=1.</summary>
[Trait("Category", "Integration")]
public class LiveLxmfTests
{
    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly PacketInterfaceManager _manager;
        private readonly Task _run;
        public PyLinkNode Py = null!;
        public TransportRouter Router { get; }
        public int Port { get; }
        public Identity Identity { get; } = Identity.Generate();
        public LxmRouter Lxmf { get; }
        public List<ReceivedMessage> Inbox { get; } = [];
        public System.Collections.Concurrent.ConcurrentQueue<string> Log { get; } = new();

        private Rig(int port, int? stampCost, string? storagePath, bool largeMessages)
        {
            Port = port;
            _manager = new PacketInterfaceManager([new TcpClientPacketInterface("127.0.0.1", port)]);
            Router = new TransportRouter(_manager, new TransportRouterOptions());
            Lxmf = new LxmRouter(Router, Identity, "DotNetLxmf", new LxmRouterOptions
            {
                ProcessingInterval = TimeSpan.FromMilliseconds(500),
                InboundStampCost = stampCost,
                EnforceStamps = true,
                StoragePath = storagePath,
                DeliveryPerTransferLimit = largeMessages ? 4_000_000 : 1_000_000,
            });
            Lxmf.MessageReceived = m => { lock (Inbox) Inbox.Add(m); };
            Lxmf.Log = Log.Enqueue;
            Router.Log = Log.Enqueue;
            _run = Task.Run(() => Router.RunAsync(_cts.Token));
        }

        public static async Task<Rig> StartAsync(int? stampCost = null, string? storagePath = null, bool largeMessages = false,
            bool smallPropagationBatches = false, int? referenceLogLevel = null)
        {
            var port = FreePort();
            var arguments = new List<string>();
            if (stampCost is { } cost) arguments.AddRange(["--stamp-cost", cost.ToString()]);
            if (referenceLogLevel is { } level) arguments.AddRange(["--loglevel", level.ToString()]);
            if (largeMessages) arguments.AddRange(["--delivery-limit-kb", "4000", "--propagation-limit-kb", "4000"]);
            if (smallPropagationBatches) arguments.AddRange(["--propagation-limit-kb", "1", "--propagation-sync-limit-kb", "1",
                "--parallel-pn-validation"]);
            var py = await PyLinkNode.StartAsync("lxmf", tcpServerPort: port, harness: "live_lxmf_node.py",
                extraArguments: [.. arguments]);
            var rig = new Rig(port, stampCost, storagePath, largeMessages) { Py = py };
            await rig._manager.StartAsync(rig._cts.Token);
            rig.Lxmf.Start();
            await Task.Delay(1000);
            await rig.Lxmf.AnnounceAsync();
            await py.Command("announce", "announced", TimeSpan.FromSeconds(10));
            var seen = await py.WaitForAnnounce(rig.Lxmf.DeliveryHash, TimeSpan.FromSeconds(20));
            Assert.Equal("DotNetLxmf", seen.GetProperty("display_name").GetString());
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!rig.Router.HasPath(py.Hash))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("No path to the Python LXMF node.");
                await Task.Delay(50);
            }

            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            await Lxmf.DisposeAsync();
            await _cts.CancelAsync();
            try { await _run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
            await _manager.DisposeAsync();
            await Py.DisposeAsync();
            Identity.Dispose();
        }
    }

    private static string HexContent(int size, int seed) => Convert.ToHexStringLower(new Random(seed).GetItems(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), size / 2));

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task SignedRatchets_SurviveNativePythonCrossImportAndDelayedPacketRestart()
    {
        var storage = Path.Combine(AppContext.BaseDirectory, "live-ratchets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storage);
        try
        {
            await using var rig = await Rig.StartAsync(storagePath: storage);
            var identityPath = Path.Combine(storage, "test-identity");
            var privateKey = rig.Identity.ExportPrivateKey();
            try { File.WriteAllBytes(identityPath, privateKey); }
            finally { CryptographicOperations.ZeroMemory(privateKey); }
            var hash = rig.Lxmf.DeliveryHashHex;
            var ratchetFile = Path.Combine(storage, "lxmf", "ratchets", hash + ".ratchets");
            Assert.True(File.Exists(ratchetFile));
            var nativePacket = await rig.Py.Command($"prepare-opportunistic {hash} native-key native-file",
                "prepared", TimeSpan.FromSeconds(10));
            Assert.NotEmpty(nativePacket.GetProperty("ratchet_id").GetString()!);
            await rig.Lxmf.DisposeAsync();
            await using (var replacement = await PyLinkNode.StartAsync("python-ratchet-import", rnsdPort: rig.Port,
                harness: "live_lxmf_node.py", extraArguments: ["--identity", identityPath, "--storage", storage, "--enforce-ratchets"]))
            {
                Assert.Equal(hash, replacement.HashHex);
                var before = rig.Py.Events("announce").Count(value => value.GetProperty("dest").GetString() == hash);
                var emitted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await WaitFor(() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() > emitted, 5);
                await rig.Py.Command("announce", "announced", TimeSpan.FromSeconds(10));
                await replacement.WaitForAnnounce(rig.Py.Hash, TimeSpan.FromSeconds(20));
                await replacement.Command("announce", "announced", TimeSpan.FromSeconds(10));
                await rig.Py.NextAsync("announce", before, TimeSpan.FromSeconds(20), value => value.GetProperty("dest").GetString() == hash);
                await rig.Py.Command("send-prepared native-file", "prepared-sent", TimeSpan.FromSeconds(10));
                var imported = await replacement.NextAsync("received", 0, TimeSpan.FromSeconds(20));
                Assert.Equal(nativePacket.GetProperty("hash").GetString(), imported.GetProperty("hash").GetString());
                Assert.Equal(nativePacket.GetProperty("ratchet_id").GetString(), imported.GetProperty("ratchet_id").GetString());
                Assert.True(imported.GetProperty("signature_validated").GetBoolean());
                await rig.Py.NextAsync("prepared-delivered", 0, TimeSpan.FromSeconds(10));
                await rig.Py.Command($"prepare-opportunistic {hash} python-key python-file", "prepared", TimeSpan.FromSeconds(10));
            }

            var pythonPacket = rig.Py.Events("prepared").Last();
            Assert.NotEqual(nativePacket.GetProperty("ratchet_id").GetString(), pythonPacket.GetProperty("ratchet_id").GetString());
            await using var resumed = new LxmRouter(rig.Router, rig.Identity, "ResumedRatchets",
                new LxmRouterOptions { StoragePath = storage, EnforceRatchets = true });
            var inbox = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            resumed.MessageReceived = received => inbox.TrySetResult(received);
            resumed.Start();
            var count = rig.Py.Events("announce").Count(value => value.GetProperty("dest").GetString() == hash);
            var previous = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await WaitFor(() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() > previous, 5);
            await resumed.AnnounceAsync();
            await rig.Py.NextAsync("announce", count, TimeSpan.FromSeconds(20), value => value.GetProperty("dest").GetString() == hash);
            await rig.Py.Command("send-prepared python-file", "prepared-sent", TimeSpan.FromSeconds(10));
            var recovered = await inbox.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(pythonPacket.GetProperty("hash").GetString(), Convert.ToHexStringLower(recovered.Message.Hash!));
            Assert.Equal(pythonPacket.GetProperty("sha256").GetString(), Sha(recovered.Message.ContentBytes));
            Assert.True(recovered.Message.SignatureValidated);
            await rig.Py.NextAsync("prepared-delivered", 1, TimeSpan.FromSeconds(10));
        }
        finally { if (Directory.Exists(storage)) Directory.Delete(storage, true); }
    }

    private static async Task DotNetToPython(LxmMethod method, int size)
    {
        await using var rig = await Rig.StartAsync();
        var content = HexContent(size, 7);
        var fields = new Dictionary<object, object?> { [1L] = new object?[] { "net-field" } };
        var message = LxMessage.Create(rig.Py.Hash, rig.Identity, content, "from dotnet", fields, method);
        await rig.Lxmf.SendAsync(message);
        var received = await rig.Py.NextAsync("received", 0, TimeSpan.FromSeconds(90));
        Assert.Equal("from dotnet", received.GetProperty("title").GetString());
        Assert.Equal(Sha(System.Text.Encoding.UTF8.GetBytes(content)), received.GetProperty("sha256").GetString());
        Assert.True(received.GetProperty("signature_validated").GetBoolean());
        Assert.Equal(Convert.ToHexStringLower(message.Hash!), received.GetProperty("hash").GetString());
        Assert.Equal(LxmState.Delivered, await message.Completion.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(method == LxmMethod.Opportunistic ? LxmMethod.Opportunistic : LxmMethod.Direct, message.Method);
    }

    private static async Task PythonToDotNet(string method, int size)
    {
        await using var rig = await Rig.StartAsync();
        var queued = await rig.Py.Command($"send {rig.Lxmf.DeliveryHashHex} {method} {size} seed1 from-python", "sent_queued", TimeSpan.FromSeconds(30));
        try { await WaitFor(() => { lock (rig.Inbox) return rig.Inbox.Count == 1; }, 90); }
        catch (TimeoutException ex) { throw new TimeoutException($"Python delivery failed. .NET: {string.Join("\n", rig.Log)}\nPython: {rig.Py.Log}", ex); }
        var m = rig.Inbox[0].Message;
        Assert.Equal("from-python", m.Title);
        Assert.Equal(size, m.ContentBytes.Length);
        Assert.Equal(queued.GetProperty("sha256").GetString(), Sha(m.ContentBytes));
        Assert.True(m.SignatureValidated);
        Assert.Equal(queued.GetProperty("hash").GetString(), Convert.ToHexStringLower(m.Hash!));
        await rig.Py.NextAsync("delivered", 0, TimeSpan.FromSeconds(60));
    }

    private static async Task WaitFor(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(50);
        }
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public Task DotNet_ToPython_Opportunistic() => DotNetToPython(LxmMethod.Opportunistic, 120);

    [LiveFact]
    [Trait("Category", "Live")]
    public Task DotNet_ToPython_DirectLink_SinglePacket() => DotNetToPython(LxmMethod.Direct, 200);

    [LiveFact]
    [Trait("Category", "Live")]
    public Task DotNet_ToPython_DirectLink_LargeResource() => DotNetToPython(LxmMethod.Direct, 600_000);

    [LiveFact]
    [Trait("Category", "Live")]
    public Task Python_ToDotNet_Opportunistic() => PythonToDotNet("opportunistic", 120);

    [LiveFact]
    [Trait("Category", "Live")]
    public Task Python_ToDotNet_DirectLink_SinglePacket() => PythonToDotNet("direct", 200);

    [LiveFact]
    [Trait("Category", "Live")]
    public Task Python_ToDotNet_DirectLink_LargeResource() => PythonToDotNet("direct", 600_000);

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task ReplyTickets_AreExchangedAndValidatedInBothDirections()
    {
        await using var rig = await Rig.StartAsync(stampCost: 8);
        var first = LxMessage.Create(rig.Py.Hash, rig.Identity, "ticket offer", method: LxmMethod.Opportunistic);
        first.IncludeTicket = true;
        await rig.Lxmf.SendAsync(first);
        var initial = await rig.Py.NextAsync("received", 0, TimeSpan.FromSeconds(30));
        Assert.True(initial.GetProperty("has_ticket").GetBoolean());
        Assert.True(initial.GetProperty("stamp_valid").GetBoolean());
        Assert.Equal(LxmState.Delivered, await first.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        var queued = await rig.Py.Command($"send {rig.Lxmf.DeliveryHashHex} opportunistic 40 ticket-seed reply ticket", "sent_queued", TimeSpan.FromSeconds(30));
        Assert.Equal(16, queued.GetProperty("stamp_size").GetInt32());
        await WaitFor(() => { lock (rig.Inbox) return rig.Inbox.Count == 1; }, 30);
        Assert.True(rig.Inbox[0].StampValid);
        Assert.True(rig.Inbox[0].Message.SignatureValidated);
        Assert.NotNull(rig.Lxmf.Tickets.GetOutbound(rig.Py.Hash));
        var reply = LxMessage.Create(rig.Py.Hash, rig.Identity, "ticket reply", method: LxmMethod.Opportunistic);
        await rig.Lxmf.SendAsync(reply);
        Assert.Equal(16, reply.Stamp!.Length);
        var received = await rig.Py.NextAsync("received", 1, TimeSpan.FromSeconds(30));
        Assert.Equal(16, received.GetProperty("stamp_size").GetInt32());
        Assert.True(received.GetProperty("stamp_valid").GetBoolean());
        Assert.True(received.GetProperty("signature_validated").GetBoolean());
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task DotNetClient_PropagatesToPythonNodeAndRetrievesWithAcknowledgment()
    {
        await using var rig = await Rig.StartAsync();
        var ready = await rig.Py.Command("enable-pn", "pn-ready", TimeSpan.FromSeconds(15));
        var node = Convert.FromHexString(ready.GetProperty("dest").GetString()!);
        await WaitFor(() => rig.Router.HasPath(node), 20);
        rig.Lxmf.OutboundPropagationNode = node;
        var message = LxMessage.Create(rig.Lxmf.DeliveryHash, rig.Identity, "Python propagation storage", method: LxmMethod.Propagated);
        await rig.Lxmf.SendAsync(message);
        Assert.Equal(LxmState.Sent, await message.Completion.WaitAsync(TimeSpan.FromSeconds(60)));
        var stored = await rig.Py.Command("pn-count", "pn-count", TimeSpan.FromSeconds(15));
        Assert.Equal(1, stored.GetProperty("count").GetInt32());
        Assert.Equal(1, await rig.Lxmf.RequestMessagesFromPropagationNodeAsync());
        await WaitFor(() => rig.Inbox.Count == 1, 20);
        Assert.Equal(message.Content, rig.Inbox[0].Message.Content);
        Assert.True(rig.Inbox[0].Message.SignatureValidated);
        var empty = await rig.Py.Command("pn-count", "pn-count", TimeSpan.FromSeconds(15));
        Assert.Equal(0, empty.GetProperty("count").GetInt32());
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task PythonClient_PropagatesToDotNetNodeAndRetrievesWithAcknowledgment()
    {
        var storage = Path.Combine(AppContext.BaseDirectory, "live-propagation-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var rig = await Rig.StartAsync(storagePath: storage);
            var node = rig.Lxmf.EnablePropagationNode(new LxmPropagationOptions { StampCost = 13, PeeringCost = 1 });
            await node.AnnounceAsync();
            await rig.Py.Command($"use-pn {Convert.ToHexStringLower(node.DestinationHash)}", "pn-selected", TimeSpan.FromSeconds(15));
            await rig.Py.Command($"send {rig.Py.HashHex} propagated 200 propagation-seed stored", "sent_queued", TimeSpan.FromSeconds(30));
            var propagated = await rig.Py.NextAsync("delivered", 0, TimeSpan.FromSeconds(60));
            Assert.Equal((int)LxmState.Sent, propagated.GetProperty("state").GetInt32());
            await WaitFor(() => node.MessageCount == 1, 20);
            var retrieved = await rig.Py.Command("retrieve", "retrieved", TimeSpan.FromSeconds(60));
            Assert.Equal(7, retrieved.GetProperty("state").GetInt32());
            var received = await rig.Py.NextAsync("received", 0, TimeSpan.FromSeconds(15));
            Assert.True(received.GetProperty("signature_validated").GetBoolean());
            Assert.Equal((int)LxmMethod.Propagated, received.GetProperty("method").GetInt32());
            await WaitFor(() => node.MessageCount == 0, 20);
        }
        finally { if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true); }
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public Task PropagationPersistentPeer_DrainsSeparateBoundedBatches() => PeerBatches(LxmPeerSyncStrategy.Persistent);

    [LiveFact]
    [Trait("Category", "Live")]
    public Task PropagationLazyPeer_StopsAfterEachSuccessfulBatch() => PeerBatches(LxmPeerSyncStrategy.Lazy);

    private sealed class UnscheduledClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private static async Task PeerBatches(LxmPeerSyncStrategy strategy)
    {
        var storage = Path.Combine(AppContext.BaseDirectory, "live-peer-batches-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await using var rig = await Rig.StartAsync(storagePath: storage, smallPropagationBatches: true);
            var node = rig.Lxmf.EnablePropagationNode(new LxmPropagationOptions
            {
                StampCost = 13, TransferLimit = 1000, SyncLimit = 1000, AutoPeer = false,
                TimeProvider = new UnscheduledClock(),
            });
            node.Log = _ => throw new InvalidOperationException("test propagation log observer failure");
            node.Log += rig.Log.Enqueue;
            await node.AnnounceAsync();
            var ready = await rig.Py.Command("enable-pn", "pn-ready", TimeSpan.FromSeconds(15));
            var destination = Convert.FromHexString(ready.GetProperty("dest").GetString()!);
            await WaitFor(() => rig.Router.HasPath(destination), 20);
            node.SetPeerSyncStrategy(destination, strategy);
            await rig.Py.Command($"use-pn {Convert.ToHexStringLower(node.DestinationHash)}", "pn-selected", TimeSpan.FromSeconds(15));
            var hashes = new HashSet<string>();
            for (var index = 0; index < 3; index++)
            {
                var queued = await rig.Py.Command($"send {rig.Py.HashHex} propagated 384 batch-{index} batch-{index}",
                    "sent_queued", TimeSpan.FromSeconds(30));
                hashes.Add(queued.GetProperty("hash").GetString()!);
                await rig.Py.NextAsync("delivered", index, TimeSpan.FromSeconds(30));
                await WaitFor(() => node.MessageCount == index + 1, 15);
            }
            Assert.Equal(3, node.Peers.Single().UnhandledCount);
            var first = await node.SynchronizePeerAsync(destination, deadline.Token);
            Assert.Equal(strategy == LxmPeerSyncStrategy.Persistent ? 3 : 1, first);
            Assert.Equal(3 - first, node.Peers.Single().UnhandledCount);
            if (strategy == LxmPeerSyncStrategy.Lazy)
                for (var index = 0; index < 2; index++)
                    Assert.Equal(1, await node.SynchronizePeerAsync(destination, deadline.Token));
            for (var index = 0; index < 3; index++)
            {
                var received = await rig.Py.NextAsync("received", index, TimeSpan.FromSeconds(30));
                Assert.True(received.GetProperty("signature_validated").GetBoolean());
                Assert.True(hashes.Remove(received.GetProperty("hash").GetString()!));
            }
            Assert.Empty(hashes);
            Assert.Equal(0, node.Peers.Single().UnhandledCount);
            Assert.Equal(3, node.Peers.Single().HandledCount);
            Assert.Equal(3, node.Peers.Single().Outgoing);
            Assert.Equal(3, node.Peers.Single().Offered);
            Assert.Equal(0, await node.SynchronizePeerAsync(destination, deadline.Token));
        }
        finally { if (Directory.Exists(storage)) Directory.Delete(storage, true); }
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task PythonPlainMessageSender_DeliversToNativeEndpoint()
    {
        await using var rig = await Rig.StartAsync(stampCost: 254);
        var plainHash = rig.Lxmf.RegisterPlainDelivery();
        var queued = await rig.Py.Command($"send-plain {Convert.ToHexStringLower(plainHash)} opportunistic 100 plain-seed plain-return",
            "sent_queued", TimeSpan.FromSeconds(10));
        await WaitFor(() => { lock (rig.Inbox) return rig.Inbox.Count == 1; }, 30);
        var incoming = rig.Inbox[0].Message;
        Assert.True(incoming.SignatureValidated);
        Assert.Equal(queued.GetProperty("hash").GetString(), Convert.ToHexStringLower(incoming.Hash!));
        Assert.False(incoming.TransportEncrypted);
        Assert.Null(incoming.TransportEncryption);
        Assert.Null(rig.Inbox[0].StampValid);
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task PropagationNodes_SynchronizeInBothDirectionsWithRealPythonPeer()
    {
        var storage = Path.Combine(AppContext.BaseDirectory, "live-peering-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await using var rig = await Rig.StartAsync(storagePath: storage, largeMessages: true);
            var dotnetNode = rig.Lxmf.EnablePropagationNode(new LxmPropagationOptions { StampCost = 13, TransferLimit = 4_000_000 });
            await dotnetNode.AnnounceAsync();
            var ready = await rig.Py.Command("enable-pn", "pn-ready", TimeSpan.FromSeconds(15));
            var pythonNode = Convert.FromHexString(ready.GetProperty("dest").GetString()!);
            await WaitFor(() => rig.Router.HasPath(pythonNode), 20);
            using var offline = Identity.Generate();
            var offlineHash = LxMessage.DeliveryDestinationHash(offline);
            rig.Router.Engine.Known.Remember(offlineHash, new byte[32], offline.ExportPublicKey(), null);
            rig.Lxmf.OutboundPropagationNode = pythonNode;
            var content = RandomNumberGenerator.GetBytes(2_200_000);
            var first = LxMessage.CreateBinary(offlineHash, rig.Identity, content, method: LxmMethod.Propagated);
            await rig.Lxmf.SendAsync(first, deadline.Token);
            Assert.Equal(LxmState.Sent, await first.Completion.WaitAsync(TimeSpan.FromSeconds(60)));
            var storedBy = DateTime.UtcNow.AddSeconds(20);
            while ((await rig.Py.Command("pn-count", "pn-count", TimeSpan.FromSeconds(10))).GetProperty("count").GetInt32() != 1)
            {
                if (DateTime.UtcNow >= storedBy) throw new TimeoutException("Python did not validate/store the propagated Resource. " + rig.Py.Log);
                await Task.Delay(50);
            }
            var synced = await rig.Py.Command($"peer-sync {Convert.ToHexStringLower(dotnetNode.DestinationHash)}", "peer-synced", TimeSpan.FromSeconds(90));
            Assert.Equal(1, synced.GetProperty("outgoing").GetInt32());
            await WaitFor(() => dotnetNode.MessageCount == 1, 20);
            var stored = File.ReadAllBytes(Directory.EnumerateFiles(Path.Combine(storage, "lxmf", "messagestore"),
                Convert.ToHexStringLower(first.TransientId!) + "_*").Single());
            var decrypted = offline.Decrypt(stored.AsSpan(LxMessage.DestinationLength, stored.Length - LxMessage.DestinationLength - LxStamper.StampSize));
            Assert.NotNull(decrypted);
            var recovered = LxMessage.Unpack([.. offlineHash, .. decrypted], _ => rig.Identity, LxmMethod.Propagated);
            Assert.True(recovered.SignatureValidated);
            Assert.Equal(Sha(content), Sha(recovered.ContentBytes));
            await rig.Py.Command($"use-pn {Convert.ToHexStringLower(dotnetNode.DestinationHash)}", "pn-selected", TimeSpan.FromSeconds(15));
            var queued = await rig.Py.Command($"send {rig.Py.HashHex} propagated 2200000 peer-seed peer-return", "sent_queued", TimeSpan.FromSeconds(30));
            await rig.Py.NextAsync("delivered", 0, TimeSpan.FromSeconds(60));
            await WaitFor(() => dotnetNode.MessageCount == 2, 20);
            Assert.Equal(1, await dotnetNode.SynchronizePeerAsync(pythonNode, deadline.Token));
            var delivered = await rig.Py.NextAsync("received", 0, TimeSpan.FromSeconds(30));
            Assert.True(delivered.GetProperty("signature_validated").GetBoolean());
            Assert.Equal((int)LxmMethod.Propagated, delivered.GetProperty("method").GetInt32());
            Assert.Equal(2_200_000, delivered.GetProperty("size").GetInt32());
            Assert.Equal(queued.GetProperty("sha256").GetString(), delivered.GetProperty("sha256").GetString());
            Assert.Equal(0, await dotnetNode.SynchronizePeerAsync(pythonNode, deadline.Token));
        }
        finally { if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true); }
    }
}