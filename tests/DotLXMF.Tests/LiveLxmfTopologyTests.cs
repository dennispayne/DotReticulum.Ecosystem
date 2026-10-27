using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DotLXMF.Tests.Interop;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotLXMF.Tests;

[Trait("Category", "Integration")]
public class LiveLxmfTopologyTests
{
    private sealed class ImpairedInterface(IPacketInterface inner) : IPacketInterface
    {
        private int _dropNextPart;
        private int _dropped;
        private int _pauseNextPart;
        private int _dropNextMessage;
        private readonly TaskCompletionSource _partPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _continuePart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Dropped => Volatile.Read(ref _dropped);
        public long Bitrate => inner.Bitrate;
        public int HardwareMtu => inner.HardwareMtu;
        public bool SupportsMtuDiscovery => inner.SupportsMtuDiscovery;
        public void DropNextResourcePart() => Volatile.Write(ref _dropNextPart, 1);
        public void DropNextMessagePacket() => Volatile.Write(ref _dropNextMessage, 1);
        public void PauseNextResourcePart() => Volatile.Write(ref _pauseNextPart, 1);
        public Task PartPaused => _partPaused.Task;
        public void ResumePart() => _continuePart.TrySetResult();
        public ValueTask StartAsync(CancellationToken cancellationToken) => inner.StartAsync(cancellationToken);
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) => inner.ReceiveAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (Packet.TryParse(packet, out var message, HardwareMtu) && message.PacketType == PacketType.Data &&
                message.DestinationType is DestinationType.Single or DestinationType.Link && message.Context == LinkContext.None &&
                Interlocked.CompareExchange(ref _dropNextMessage, 0, 1) == 1)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            if (Packet.TryParse(packet, out var parsed, HardwareMtu) && parsed.DestinationType == DestinationType.Link &&
                parsed.Context == LinkContext.Resource)
            {
                if (Interlocked.CompareExchange(ref _dropNextPart, 0, 1) == 1)
                {
                    Interlocked.Increment(ref _dropped);
                    return;
                }
                if (Interlocked.CompareExchange(ref _pauseNextPart, 0, 1) == 1)
                {
                    _partPaused.TrySetResult();
                    await _continuePart.Task.WaitAsync(cancellationToken);
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
            await inner.SendAsync(packet, cancellationToken);
        }
    }

    private sealed class Topology : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _runs = [];
        private readonly Identity _identity = Identity.Generate();
        private readonly Identity _transportIdentity = Identity.Generate();
        private PacketInterfaceManager? _edgeManager;
        private PacketInterfaceManager? _bridgeManager;
        private PyLinkNode? _bridgePython;
        private LxmRouter? _lxmf;
        private PyLinkNode? _python;
        public TransportRouter Router { get; private set; } = null!;
        public ImpairedInterface Interface { get; private set; } = null!;
        public PyLinkNode Python => _python!;
        public LxmRouter Lxmf => _lxmf!;
        public Identity Identity => _identity;
        public ConcurrentQueue<ReceivedMessage> Inbox { get; } = new();

        public static async Task<Topology> StartAsync(bool pythonTransport)
        {
            var topology = new Topology();
            try
            {
                var endpointPort = FreePort();
                topology._python = await PyLinkNode.StartAsync("hop-endpoint", tcpServerPort: endpointPort,
                    harness: "live_lxmf_node.py", extraArguments: ["--delivery-limit-kb", "4000"]);
                var bridgePort = FreePort();
                if (pythonTransport)
                {
                    topology._bridgePython = await PyLinkNode.StartAsync("python-transport", tcpServerPort: bridgePort,
                        rnsdPort: endpointPort, harness: "live_lxmf_node.py", extraArguments: ["--transport"]);
                }
                else
                {
                    topology._bridgeManager = new PacketInterfaceManager([
                        new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, bridgePort), perClientInterfaces: true),
                        new TcpClientPacketInterface("127.0.0.1", endpointPort),
                    ]);
                    var transport = new TransportRouter(topology._bridgeManager, new TransportRouterOptions
                    {
                        TransportEnabled = true,
                        TransportIdentity = topology._transportIdentity,
                    });
                    await topology._bridgeManager.StartAsync(topology._cts.Token);
                    topology._runs.Add(Task.Run(() => transport.RunAsync(topology._cts.Token)));
                }

                topology.Interface = new ImpairedInterface(new TcpClientPacketInterface("127.0.0.1", bridgePort));
                topology._edgeManager = new PacketInterfaceManager([topology.Interface]);
                topology.Router = new TransportRouter(topology._edgeManager, new TransportRouterOptions());
                topology._lxmf = new LxmRouter(topology.Router, topology._identity, "HopOrigin", new LxmRouterOptions
                {
                    DeliveryPerTransferLimit = 4_000_000,
                    ProcessingInterval = TimeSpan.FromMilliseconds(200),
                    DeliveryRetryWait = TimeSpan.FromSeconds(2),
                });
                topology._lxmf.MessageReceived = topology.Inbox.Enqueue;
                await topology._edgeManager.StartAsync(topology._cts.Token);
                topology._runs.Add(Task.Run(() => topology.Router.RunAsync(topology._cts.Token)));
                topology._lxmf.Start();
                await Task.Delay(1000);
                await topology._lxmf.AnnounceAsync();
                await topology.Python.Command("announce", "announced", TimeSpan.FromSeconds(10));
                var announce = await topology.Python.WaitForAnnounce(topology._lxmf.DeliveryHash, TimeSpan.FromSeconds(45));
                Assert.True(announce.GetProperty("hops").GetInt32() >= 2);
                await WaitFor(() => topology.Router.HopsTo(topology.Python.Hash) >= 2, TimeSpan.FromSeconds(45));
                return topology;
            }
            catch { await topology.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            Interface?.ResumePart();
            if (_lxmf is not null) await _lxmf.DisposeAsync();
            await _cts.CancelAsync();
            try { await Task.WhenAll(_runs).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
            if (_edgeManager is not null) await _edgeManager.DisposeAsync();
            if (_bridgeManager is not null) await _bridgeManager.DisposeAsync();
            if (_bridgePython is not null) await _bridgePython.DisposeAsync();
            if (_python is not null) await _python.DisposeAsync();
            _identity.Dispose();
            _transportIdentity.Dispose();
            _cts.Dispose();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (!condition()) await Task.Delay(50, deadline.Token);
    }

    private static async Task TransferWithLossAndRecovery(bool pythonTransport)
    {
        await using var topology = await Topology.StartAsync(pythonTransport);
        var content = RandomNumberGenerator.GetBytes(2_200_000);
        var message = LxMessage.CreateBinary(topology.Python.Hash, topology.Identity, content,
            "lossy-hop"u8, method: LxmMethod.Direct);
        topology.Interface.DropNextResourcePart();
        await topology.Lxmf.SendAsync(message);
        var received = await topology.Python.NextAsync("received", 0, TimeSpan.FromSeconds(150));
        Assert.Equal(2_200_000, received.GetProperty("size").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), received.GetProperty("sha256").GetString());
        Assert.True(received.GetProperty("signature_validated").GetBoolean());
        Assert.Equal(LxmState.Delivered, await message.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, topology.Interface.Dropped);

        var queued = await topology.Python.Command($"send {topology.Lxmf.DeliveryHashHex} direct 2200000 reverse-seed hop-return",
            "sent_queued", TimeSpan.FromSeconds(30));
        await WaitFor(() => !topology.Inbox.IsEmpty, TimeSpan.FromSeconds(150));
        var returned = Assert.Single(topology.Inbox).Message;
        Assert.Equal(2_200_000, returned.ContentBytes.Length);
        Assert.Equal(queued.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(returned.ContentBytes)));
        Assert.True(returned.SignatureValidated);
        await topology.Python.NextAsync("delivered", 0, TimeSpan.FromSeconds(30));

        foreach (var link in topology.Router.Links.Links) link.Teardown();
        var recovery = LxMessage.Create(topology.Python.Hash, topology.Identity, "after teardown");
        await topology.Lxmf.SendAsync(recovery);
        var resumed = await topology.Python.NextAsync("received", 1, TimeSpan.FromSeconds(60));
        Assert.Equal(Convert.ToHexStringLower(recovery.Hash!), resumed.GetProperty("hash").GetString());
        Assert.Equal(LxmState.Delivered, await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public Task PythonTransport_TwoWayMultiMegabyteLossLatencyAndRecovery() => TransferWithLossAndRecovery(pythonTransport: true);

    [LiveFact]
    [Trait("Category", "Live")]
    public Task DotNetTransport_TwoWayMultiMegabyteLossLatencyAndRecovery() => TransferWithLossAndRecovery(pythonTransport: false);

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task CancelDuringResource_PreventsDeliveryAndKeepsThePeerUsable()
    {
        await using var topology = await Topology.StartAsync(pythonTransport: false);
        topology.Interface.PauseNextResourcePart();
        var message = LxMessage.CreateBinary(topology.Python.Hash, topology.Identity,
            RandomNumberGenerator.GetBytes(2_200_000), "cancel-in-flight"u8, method: LxmMethod.Direct);
        await topology.Lxmf.SendAsync(message);
        await topology.Interface.PartPaused.WaitAsync(TimeSpan.FromSeconds(60));
        topology.Lxmf.Cancel(message);
        topology.Interface.ResumePart();
        Assert.Equal(LxmState.Cancelled, await message.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        var recovery = LxMessage.Create(topology.Python.Hash, topology.Identity, "after resource cancellation");
        await topology.Lxmf.SendAsync(recovery);
        var received = await topology.Python.NextAsync("received", 0, TimeSpan.FromSeconds(60));
        Assert.Equal(Convert.ToHexStringLower(recovery.Hash!), received.GetProperty("hash").GetString());
        Assert.Equal(LxmState.Delivered, await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, topology.Python.Count("received"));
    }

    private static async Task RetryDroppedMessage(LxmMethod method)
    {
        await using var topology = await Topology.StartAsync(pythonTransport: false);
        topology.Interface.DropNextMessagePacket();
        var message = LxMessage.Create(topology.Python.Hash, topology.Identity, "retry dropped message", method: method);
        await topology.Lxmf.SendAsync(message);
        Assert.Equal(LxmState.Delivered, await message.Completion.WaitAsync(TimeSpan.FromSeconds(20)));
        var received = await topology.Python.NextAsync("received", 0, TimeSpan.FromSeconds(10));
        Assert.Equal(Convert.ToHexStringLower(message.Hash!), received.GetProperty("hash").GetString());
        Assert.Equal(1, topology.Interface.Dropped);
        Assert.True(message.DeliveryAttempts >= (method == LxmMethod.Direct ? 3 : 2));
        Assert.Equal(1, topology.Python.Count("received"));
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public Task LostOpportunisticPacket_RetriesToActualPython() => RetryDroppedMessage(LxmMethod.Opportunistic);

    [LiveFact]
    [Trait("Category", "Live")]
    public Task LostDirectPacket_RetriesToActualPython() => RetryDroppedMessage(LxmMethod.Direct);
}
