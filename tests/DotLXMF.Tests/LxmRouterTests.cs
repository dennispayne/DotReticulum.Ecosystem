using System.Net;
using System.Net.Sockets;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotReticulum.Lxmf.Tests;

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

    private LxmfPair()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
        }

        _serverManager = new PacketInterfaceManager([new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, port), perClientInterfaces: true)]);
        _clientManager = new PacketInterfaceManager([new TcpClientPacketInterface("127.0.0.1", port)]);
        ServerRouter = new TransportRouter(_serverManager, new TransportRouterOptions());
        ClientRouter = new TransportRouter(_clientManager, new TransportRouterOptions());
    }

    public static async Task<LxmfPair> CreateAsync(LxmRouterOptions? serverOptions = null, LxmRouterOptions? clientOptions = null)
    {
        var pair = new LxmfPair();
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

public class LxmRouterTests
{
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
