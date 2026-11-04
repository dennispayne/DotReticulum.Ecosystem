using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;
using LXMF.Tests.Interop;

namespace LXMF.Tests;

[Trait("Category", "Integration")]
public class LiveLxmRetryTests
{
    private sealed class ObservedInterface(IPacketInterface inner) : IPacketInterface
    {
        public readonly ConcurrentQueue<(double Time, string Destination, string Hash)> Requests = new();
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public long Bitrate => inner.Bitrate;
        public int HardwareMtu => inner.HardwareMtu;
        public bool SupportsMtuDiscovery => inner.SupportsMtuDiscovery;
        public ValueTask StartAsync(CancellationToken cancellationToken) => inner.StartAsync(cancellationToken);
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) => inner.ReceiveAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (Packet.TryParse(packet, out var parsed) && parsed.PacketType == PacketType.LinkRequest)
                Requests.Enqueue((Clock.Elapsed.TotalSeconds, Convert.ToHexStringLower(parsed.DestinationHash.Span),
                    Convert.ToHexStringLower(parsed.CalculateTruncatedHash())));
            return inner.SendAsync(packet, cancellationToken);
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
        while (!condition()) await Task.Delay(20, deadline.Token);
    }

    [LiveTheory]
    [InlineData("direct")]
    [InlineData("python-hop")]
    [InlineData("native-hop")]
    [Trait("Category", "Live")]
    public async Task DefaultRefusal_ChargesFiveAttemptsForFourClosedLinks_ThenFreshPeerRecovers(string topology)
    {
        using var stop = new CancellationTokenSource();
        using var nativeIdentity = Identity.Generate();
        using var refusedIdentity = Identity.Generate();
        using var bridgeIdentity = Identity.Generate();
        var endpointPort = FreePort();
        await using var python = await PyLinkNode.StartAsync("retry-reference-" + topology, tcpServerPort: endpointPort,
            harness: "lxmf_retry_node.py");
        var bridgePort = topology == "direct" ? endpointPort : FreePort();
        PyLinkNode? pythonBridge = null;
        PacketInterfaceManager? nativeBridge = null;
        TransportRouter? bridgeRouter = null;
        Task bridgeRun = Task.CompletedTask;
        var observed = new ObservedInterface(new TcpClientPacketInterface("127.0.0.1", bridgePort));
        await using var interfaces = new PacketInterfaceManager([observed]);
        await using var transport = new TransportRouter(interfaces);
        await using var router = new LxmRouter(transport, nativeIdentity);
        var refused = new LocalDestination(refusedIdentity,
            Destination.Create(DestinationType.Single, "lxmf", ["delivery"], refusedIdentity)) { AcceptLinks = false };
        transport.RegisterLocalDestination(refused);
        var inbox = new ConcurrentQueue<ReceivedMessage>();
        router.MessageReceived = inbox.Enqueue;
        var logs = new ConcurrentQueue<string>();
        router.Log = logs.Enqueue;
        var lifecycle = new List<object>();
        Task run = Task.CompletedTask;
        LxMessage? message = null;
        var outcome = "failed";
        try
        {
            if (topology == "python-hop")
                pythonBridge = await PyLinkNode.StartAsync("retry-python-transport", tcpServerPort: bridgePort,
                    rnsdPort: endpointPort, harness: "lxmf_retry_node.py", extraArguments: ["--transport"]);
            if (topology == "native-hop")
            {
                nativeBridge = new PacketInterfaceManager([
                    new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, bridgePort), perClientInterfaces: true),
                    new TcpClientPacketInterface("127.0.0.1", endpointPort)]);
                bridgeRouter = new TransportRouter(nativeBridge, new TransportRouterOptions
                {
                    TransportEnabled = true, TransportIdentity = bridgeIdentity,
                });
                await nativeBridge.StartAsync(stop.Token);
                bridgeRun = Task.Run(() => bridgeRouter.RunAsync(stop.Token));
            }
            await interfaces.StartAsync(stop.Token);
            run = Task.Run(() => transport.RunAsync(stop.Token));
            router.Start();
            await Task.Delay(1000);
            await router.AnnounceAsync();
            await transport.AnnounceAsync(refused);
            await python.Command("announce", "announced", TimeSpan.FromSeconds(10));
            var pythonRefused = Convert.FromHexString(python.Events("ready")[0].GetProperty("refused_dest").GetString()!);
            await WaitFor(() => transport.HasPath(pythonRefused) && transport.HasPath(python.Hash), TimeSpan.FromSeconds(45));
            await python.WaitForAnnounce(refused.Destination.Hash, TimeSpan.FromSeconds(45));
            await python.WaitForAnnounce(router.DeliveryHash, TimeSpan.FromSeconds(45));
            Assert.Equal(topology == "direct" ? 1 : 2, transport.HopsTo(pythonRefused));

            var primaryQueued = await python.Command($"send {Convert.ToHexStringLower(refused.Destination.Hash)} refused",
                "sent_queued", TimeSpan.FromSeconds(10));
            var primaryHash = primaryQueued.GetProperty("hash").GetString();
            message = LxMessage.Create(pythonRefused, nativeIdentity, "refused", "refused");
            var elapsed = Stopwatch.StartNew();
            await router.SendAsync(message);
            var seen = new HashSet<Link>();
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180)))
                while (!message.Completion.IsCompleted)
                {
                    foreach (var link in transport.Links.Links.Where(link => link.IsInitiator))
                        if (seen.Add(link))
                        {
                            lifecycle.Add(new { kind = "pending", time = elapsed.Elapsed.TotalSeconds,
                                attempts = message.DeliveryAttempts, link = link.LinkIdHex, timeout = link.EstablishmentTimeout });
                            link.Closed += closed =>
                            {
                                lock (lifecycle) lifecycle.Add(new { kind = "closed", time = elapsed.Elapsed.TotalSeconds,
                                    attempts = message.DeliveryAttempts, link = closed.LinkIdHex, message_state = message.State.ToString() });
                            };
                        }
                    await Task.Delay(20, deadline.Token);
                }
            Assert.Equal(LxmState.Failed, await message.Completion);
            Assert.Equal(5, message.DeliveryAttempts);
            Assert.Equal(4, seen.Count);
            Assert.All(seen, link => Assert.Equal(LinkStatus.Closed, link.Status));
            var expectedDeadline = Link.EstablishmentTimeoutPerHop * transport.HopsTo(pythonRefused)!.Value +
                transport.FirstHopTimeout(pythonRefused);
            Assert.All(seen, link => Assert.Equal(expectedDeadline, link.EstablishmentTimeout, 6));
            Assert.Empty(transport.Links.Links);
            var requests = observed.Requests.Where(request => request.Destination == Convert.ToHexStringLower(pythonRefused)).ToArray();
            Assert.Equal(4, requests.Length);
            Assert.Equal(4, requests.Select(request => request.Hash).Distinct().Count());
            lock (lifecycle)
            {
                var events = lifecycle.Select(entry => JsonSerializer.SerializeToElement(entry)).ToArray();
                Assert.Equal(new[] { "pending", "closed", "pending", "closed", "pending", "closed", "pending", "closed" },
                    events.Select(entry => entry.GetProperty("kind").GetString()));
                Assert.All(events.Where(entry => entry.GetProperty("kind").GetString() == "closed"), entry =>
                    Assert.Equal("Outbound", entry.GetProperty("message_state").GetString()));
            }
            var failed = await python.NextAsync("failed", 0, TimeSpan.FromSeconds(90),
                entry => entry.GetProperty("hash").GetString() == primaryHash);
            Assert.Equal(5, failed.GetProperty("attempts").GetInt32());
            Assert.True(failed.GetProperty("link_status").ValueKind == JsonValueKind.Null ||
                failed.GetProperty("link_status").GetInt32() == (int)LinkStatus.Closed);
            var primaryLinks = python.Events("link-observation").Where(entry =>
                entry.GetProperty("message").GetString() == primaryHash).ToArray();
            Assert.Equal(4, primaryLinks.Select(entry => entry.GetProperty("link").GetString()).Distinct().Count());
            Assert.Equal(4, primaryLinks.Count(entry => entry.GetProperty("status").GetInt32() == (int)LinkStatus.Closed));
            Assert.All(primaryLinks, entry =>
                Assert.Equal(expectedDeadline, entry.GetProperty("establishment_timeout").GetDouble(), 6));

            var recovery = LxMessage.Create(python.Hash, nativeIdentity, "healthy fresh peer", "recovered");
            await router.SendAsync(recovery);
            Assert.Equal(LxmState.Delivered, await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
            var received = await python.NextAsync("received", 0, TimeSpan.FromSeconds(15));
            Assert.Equal("recovered", received.GetProperty("title").GetString());
            Assert.True(received.GetProperty("signature_validated").GetBoolean());
            await python.Command($"send {router.DeliveryHashHex} recovered-primary", "sent_queued", TimeSpan.FromSeconds(10));
            await python.NextAsync("delivered", 0, TimeSpan.FromSeconds(30));
            await WaitFor(() => inbox.Any(entry => entry.Message.Title == "recovered-primary"), TimeSpan.FromSeconds(15));
            outcome = "passed";
        }
        finally
        {
            var evidence = Environment.GetEnvironmentVariable("DOTRETICULUM_RETRY_EVIDENCE");
            if (!string.IsNullOrEmpty(evidence))
            {
                Directory.CreateDirectory(evidence);
                File.WriteAllText(Path.Combine(evidence, topology + ".json"), JsonSerializer.Serialize(new
                {
                    outcome, topology, attempts = message?.DeliveryAttempts, state = message?.State.ToString(),
                    requests = observed.Requests.Select(request => new { time = request.Time, destination = request.Destination, hash = request.Hash }),
                    lifecycle, logs, primary = python.Log,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            await router.DisposeAsync();
            await stop.CancelAsync();
            try { await Task.WhenAll(run, bridgeRun).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
            if (bridgeRouter is not null) await bridgeRouter.DisposeAsync();
            if (nativeBridge is not null) await nativeBridge.DisposeAsync();
            if (pythonBridge is not null) await pythonBridge.DisposeAsync();
            transport.DeregisterLocalDestination(refused);
            refused.Dispose();
        }
    }
}
