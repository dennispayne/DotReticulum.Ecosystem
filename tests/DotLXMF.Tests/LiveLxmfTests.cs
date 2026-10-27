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
        public Identity Identity { get; } = Identity.Generate();
        public LxmRouter Lxmf { get; }
        public List<ReceivedMessage> Inbox { get; } = [];
        public System.Collections.Concurrent.ConcurrentQueue<string> Log { get; } = new();

        private Rig(int port, int? stampCost, string? storagePath)
        {
            _manager = new PacketInterfaceManager([new TcpClientPacketInterface("127.0.0.1", port)]);
            Router = new TransportRouter(_manager, new TransportRouterOptions());
            Lxmf = new LxmRouter(Router, Identity, "DotNetLxmf", new LxmRouterOptions
            {
                ProcessingInterval = TimeSpan.FromMilliseconds(500),
                InboundStampCost = stampCost,
                StoragePath = storagePath,
            });
            Lxmf.MessageReceived = m => { lock (Inbox) Inbox.Add(m); };
            Lxmf.Log = Log.Enqueue;
            Router.Log = Log.Enqueue;
            _run = Task.Run(() => Router.RunAsync(_cts.Token));
        }

        public static async Task<Rig> StartAsync(int? stampCost = null, string? storagePath = null)
        {
            var port = FreePort();
            var py = await PyLinkNode.StartAsync("lxmf", tcpServerPort: port, harness: "live_lxmf_node.py",
                extraArguments: stampCost is { } cost ? ["--stamp-cost", cost.ToString()] : null);
            var rig = new Rig(port, stampCost, storagePath) { Py = py };
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
        }
    }

    private static string HexContent(int size, int seed) => Convert.ToHexStringLower(new Random(seed).GetItems(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), size / 2));

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

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
    public async Task PropagationNodes_SynchronizeInBothDirectionsWithRealPythonPeer()
    {
        var storage = Path.Combine(AppContext.BaseDirectory, "live-peering-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await using var rig = await Rig.StartAsync(storagePath: storage);
            var dotnetNode = rig.Lxmf.EnablePropagationNode(new LxmPropagationOptions { StampCost = 13 });
            await dotnetNode.AnnounceAsync();
            var ready = await rig.Py.Command("enable-pn", "pn-ready", TimeSpan.FromSeconds(15));
            var pythonNode = Convert.FromHexString(ready.GetProperty("dest").GetString()!);
            await WaitFor(() => rig.Router.HasPath(pythonNode), 20);
            using var offline = Identity.Generate();
            var offlineHash = LxMessage.DeliveryDestinationHash(offline);
            rig.Router.Engine.Known.Remember(offlineHash, new byte[32], offline.ExportPublicKey(), null);
            rig.Lxmf.OutboundPropagationNode = pythonNode;
            var first = LxMessage.Create(offlineHash, rig.Identity, "stored for offline recipient", method: LxmMethod.Propagated);
            await rig.Lxmf.SendAsync(first, deadline.Token);
            Assert.Equal(LxmState.Sent, await first.Completion.WaitAsync(TimeSpan.FromSeconds(60)));
            var synced = await rig.Py.Command($"peer-sync {Convert.ToHexStringLower(dotnetNode.DestinationHash)}", "peer-synced", TimeSpan.FromSeconds(90));
            Assert.Equal(1, synced.GetProperty("outgoing").GetInt32());
            await WaitFor(() => dotnetNode.MessageCount == 1, 20);
            await rig.Py.Command($"use-pn {Convert.ToHexStringLower(dotnetNode.DestinationHash)}", "pn-selected", TimeSpan.FromSeconds(15));
            await rig.Py.Command($"send {rig.Py.HashHex} propagated 200 peer-seed peer-return", "sent_queued", TimeSpan.FromSeconds(30));
            await rig.Py.NextAsync("delivered", 0, TimeSpan.FromSeconds(60));
            await WaitFor(() => dotnetNode.MessageCount == 2, 20);
            Assert.Equal(1, await dotnetNode.SynchronizePeerAsync(pythonNode, deadline.Token));
            var delivered = await rig.Py.NextAsync("received", 0, TimeSpan.FromSeconds(30));
            Assert.True(delivered.GetProperty("signature_validated").GetBoolean());
            Assert.Equal((int)LxmMethod.Propagated, delivered.GetProperty("method").GetInt32());
            Assert.Equal(0, await dotnetNode.SynchronizePeerAsync(pythonNode, deadline.Token));
        }
        finally { if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true); }
    }
}