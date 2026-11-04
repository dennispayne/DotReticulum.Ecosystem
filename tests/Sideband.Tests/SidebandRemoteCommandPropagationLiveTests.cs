using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DotReticulum.Applications;
using LXMF;
using LXMF.Tests.Interop;

namespace Sideband.Tests;

public sealed partial class SidebandLiveInteropTests
{
    [Theory(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Command-specific propagation requires both live opt-ins and the pinned actual-primary references.")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryRemotePropagatedOriginsRetrievalCancellationAndRestarts(int topology)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(8));
        await using var f = await RemotePropagationFixture.OpenAsync(topology, stop.Token);
        var command = await f.Client.SendCommandAsync(f.Primary.HashHex, "echo native-propagated", true, stop.Token);
        Assert.True(command.Queued);
        await f.Until(async () => await f.Stored(f.Primary.HashHex) == 1 &&
            (await f.Client.LoadAsync(stop.Token)).Messages.Any(m => m.Id == command.OperationId &&
                m.DeliveryState == "Sent" && m.DeliveryMethod == "Propagated"));
        Assert.Equal(0, f.Primary.Count("delivery"));
        Assert.DoesNotContain((await f.Client.LoadAsync(stop.Token)).Messages, m => m.Incoming);
        var pnHash = f.Propagation.HashHex;
        await f.RestartPropagation();
        Assert.Equal(pnHash, f.Propagation.HashHex);
        Assert.Equal(1, await f.Stored(f.Primary.HashHex));
        await f.Primary.Command("retrieve", "retrieved", TimeSpan.FromSeconds(90));
        var request = await f.Primary.NextAsync("delivery", 0, TimeSpan.FromSeconds(30));
        Assert.True(request.GetProperty("commands").GetBoolean());
        Assert.True(request.GetProperty("signature_valid").GetBoolean());
        Assert.Equal((int)LxmMethod.Propagated, request.GetProperty("method").GetInt32());
        await f.NativeReply("Echo reply: native-propagated");
        Assert.Equal("Sent", (await f.Client.LoadAsync(stop.Token)).Messages.Single(m => m.Id == command.OperationId).DeliveryState);

        await f.PrimaryCommand("echo primary-propagated", propagation: true);
        await f.Until(async () => await f.Stored(f.Local) == 1);
        var outbound = await f.PrimaryCommandSnapshot("echo primary-propagated", LxmState.Sent);
        Assert.Equal((int)LxmMethod.Propagated, outbound.GetProperty("method").GetInt32());
        Assert.Equal((int)LxmState.Sent, outbound.GetProperty("state").GetInt32());
        await f.RestartPrimary();
        await f.RestartNative(observers: true);
        Assert.Equal(outbound.GetProperty("hash").GetString(),
            (await f.PrimaryCommandSnapshot("echo primary-propagated")).GetProperty("hash").GetString());
        Assert.Equal(0, f.Primary.Count("delivery"));
        var pausedBefore = f.Proxy.PausedReads;
        f.Proxy.Pause();
        try
        {
            var sync = f.Client.SynchronizeAsync(cancellationToken: stop.Token);
            await f.Until(() => Task.FromResult(f.Proxy.PausedReads > pausedBefore));
            Assert.False(sync.IsCompleted);
            f.Client.CancelSynchronization();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.WaitAsync(TimeSpan.FromSeconds(10), stop.Token));
        }
        finally { f.Proxy.Resume(); }
        Assert.Equal(1, await f.Stored(f.Local));
        Assert.Equal(1, await f.Client.SynchronizeAsync(cancellationToken: stop.Token));
        var reply = await f.Primary.NextAsync("delivery", 0, TimeSpan.FromSeconds(30));
        Assert.Equal("Echo reply: primary-propagated", DecodeContent(reply));
        Assert.True(reply.GetProperty("signature_valid").GetBoolean());
        Assert.False(reply.GetProperty("commands").GetBoolean());
        await f.RestartNative(observers: false);
        Assert.Equal(0, await f.Client.SynchronizeAsync(cancellationToken: stop.Token));
        Assert.Single((await f.Client.LoadAsync(stop.Token)).Messages, m => !m.Incoming);
        Assert.DoesNotContain((await f.Client.LoadAsync(stop.Token)).Messages, m => m.Incoming && m.Title == "Command");
        Assert.Empty(await f.Client.GetTelemetryAsync(cancellationToken: stop.Token));
    }

    [Theory(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Command-specific failed-direct recovery requires both live opt-ins and the pinned actual-primary references.")]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryRemoteDirectFailureFallbackPolicyAndHealthyPeerRecovery(bool fallback)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(8));
        await using var f = await RemotePropagationFixture.OpenAsync(0, stop.Token);
        await f.Client.ConfigurePropagationFallbackAsync(fallback, stop.Token);
        await f.Primary.Command("delivery-links teardown", "delivery-links", TimeSpan.FromSeconds(10));
        await f.Primary.Command("delivery-packets off", "delivery-packets", TimeSpan.FromSeconds(10));
        // Exceeds opportunistic packet capacity, so Pack selects actual Direct even with a cached ratchet.
        var text = "echo " + new string('d', 768);
        var queued = await f.Client.SendCommandAsync(f.Primary.HashHex, text, cancellationToken: stop.Token);
        Assert.True(queued.Queued);
        await f.Until(async () => (await f.Client.LoadAsync(stop.Token)).Messages.Any(m => m.Id == queued.OperationId &&
            m.DeliveryState == (fallback ? "Sent" : "Failed")), TimeSpan.FromMinutes(5));
        var stored = (await f.Client.LoadAsync(stop.Token)).Messages.Single(m => m.Id == queued.OperationId);
        Assert.Equal(fallback ? "Propagated" : "Direct", stored.DeliveryMethod);
        Assert.Equal(fallback ? 1 : 0, await f.Stored(f.Primary.HashHex));
        Assert.Equal(0, f.Primary.Count("delivery"));
        if (fallback)
        {
            await f.Primary.Command("retrieve", "retrieved", TimeSpan.FromSeconds(90));
            await f.NativeReply("Echo reply: " + new string('d', 768));
            Assert.Equal("Sent", (await f.Client.LoadAsync(stop.Token)).Messages.Single(m => m.Id == queued.OperationId).DeliveryState);
        }
        await f.Primary.Command("delivery-links on", "delivery-links", TimeSpan.FromSeconds(10));
        await f.Primary.Command("delivery-packets on", "delivery-packets", TimeSpan.FromSeconds(10));
        var healthy = await f.Client.SendCommandAsync(f.Primary.HashHex, "echo direct-recovered", cancellationToken: stop.Token);
        Assert.True(healthy.Queued);
        await f.NativeReply("Echo reply: direct-recovered");
        await f.Until(async () => (await f.Client.LoadAsync(stop.Token)).Messages.Any(m => m.Id == healthy.OperationId && m.DeliveryState == "Delivered"));
        Assert.Empty(await f.Client.GetTelemetryAsync(cancellationToken: stop.Token));
    }

    [Fact(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Offline reply fallback requires both live opt-ins and the pinned actual-primary references.")]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryRemoteOfflineRepliesBothOriginsRequirePropagationRetrieval()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(12));
        await using var f = await RemotePropagationFixture.OpenAsync(2, stop.Token);
        Assert.True((await f.Client.SendCommandAsync(f.Primary.HashHex, "echo primary-offline-reply", true, stop.Token)).Queued);
        await f.Until(async () => await f.Stored(f.Primary.HashHex) == 1);
        await f.Client.DisconnectAsync();
        await f.Primary.Command("retrieve", "retrieved", TimeSpan.FromSeconds(90));
        await f.Until(async () => await f.Stored(f.Local) == 1, TimeSpan.FromMinutes(5));
        await f.RestartNative(observers: false);
        Assert.Equal(1, await f.Client.SynchronizeAsync(cancellationToken: stop.Token));
        await f.NativeReply("Echo reply: primary-offline-reply");

        await f.PrimaryCommand("echo native-offline-reply", propagation: true);
        await f.Until(async () => await f.Stored(f.Local) == 1);
        var primaryHash = f.Primary.HashHex;
        await f.StopPrimary();
        Assert.Equal(1, await f.Client.SynchronizeAsync(cancellationToken: stop.Token));
        await f.Until(async () => await f.Stored(primaryHash) == 1, TimeSpan.FromMinutes(5));
        Assert.Single((await f.Client.LoadAsync(stop.Token)).Messages, m => !m.Incoming);
        await f.RestartPrimary();
        await f.RestartNative(observers: false);
        await f.Primary.Command("retrieve", "retrieved", TimeSpan.FromSeconds(90));
        var reply = await f.Primary.NextAsync("delivery", 0, TimeSpan.FromSeconds(30));
        Assert.Equal("Echo reply: native-offline-reply", DecodeContent(reply));
        Assert.Equal((int)LxmMethod.Propagated, reply.GetProperty("method").GetInt32());
        Assert.True(reply.GetProperty("signature_valid").GetBoolean());
        Assert.False(reply.GetProperty("commands").GetBoolean());
        Assert.Empty(await f.Client.GetTelemetryAsync(cancellationToken: stop.Token));
    }

    [Fact(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Bounded remote service endurance requires both live opt-ins and the pinned actual-primary references.")]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryRemoteBoundedCancellationAndSixtyFourHealthyOperationsWithoutObservers()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(6));
        await using var f = await RemotePropagationFixture.OpenAsync(1, stop.Token);
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var blocked = await f.Client.SendCommandAsync(new string('e', 32), "ping", cancellationToken: batch.Token);
        Assert.True(blocked.Queued);
        await f.Until(async () => (await f.Client.LoadAsync(stop.Token)).Messages.Any(m => m.Id == blocked.OperationId));
        for (var index = 1; index < SidebandClient.MaximumPendingCommands; index++)
            Assert.True((await f.Client.SendCommandAsync(f.Primary.HashHex, "echo cancelled-" + index, cancellationToken: batch.Token)).Queued);
        Assert.Equal(SidebandCommandQueueStatus.QueueFull,
            (await f.Client.SendCommandAsync(f.Primary.HashHex, "ping", cancellationToken: batch.Token)).Status);
        await batch.CancelAsync();
        await f.Until(async () => (await f.Client.LoadAsync(stop.Token)).Messages.Any(m => m.Id == blocked.OperationId && m.DeliveryState == "Cancelled"));
        await f.RestartNative(observers: false);
        Assert.Equal(0, f.Primary.Count("delivery"));
        for (var index = 0; index < 32; index++)
            Assert.True((await f.Client.SendCommandAsync(f.Primary.HashHex, "echo endurance-native-" + index, cancellationToken: stop.Token)).Queued);
        await f.Until(async () => (await f.Client.LoadAsync(stop.Token)).Messages.Count(m =>
            m.Incoming && m.Content.StartsWith("Echo reply: endurance-native-", StringComparison.Ordinal)) == 32);
        for (var index = 0; index < 32; index++)
            await f.PrimaryCommand("echo endurance-primary-" + index, propagation: false);
        var expectedReplies = Enumerable.Range(0, 32).Select(index => "Echo reply: endurance-primary-" + index)
            .ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < 32; index++)
        {
            var reply = await f.Primary.NextAsync("delivery", 32 + index, TimeSpan.FromSeconds(40));
            Assert.True(reply.GetProperty("signature_valid").GetBoolean());
            Assert.False(reply.GetProperty("commands").GetBoolean());
            Assert.True(expectedReplies.Remove(DecodeContent(reply)));
        }
        Assert.Empty(expectedReplies);
        var state = await f.Client.LoadAsync(stop.Token);
        Assert.Equal(32, state.Messages.Count(m => !m.Incoming && m.DeliveryState == "Delivered"));
        Assert.Equal(32, state.Messages.Where(m => m.Incoming).Select(m => m.Content).Distinct(StringComparer.Ordinal).Count());
        for (var index = 0; index < 32; index++)
            Assert.Contains(state.Messages, m => m.Incoming && m.Content == "Echo reply: endurance-native-" + index);
        Assert.DoesNotContain(state.Messages, m => m.Content.Contains("cancelled-", StringComparison.Ordinal));
        Assert.Empty(await f.Client.GetTelemetryAsync(cancellationToken: stop.Token));
    }

    private sealed class RemotePropagationFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(AppContext.BaseDirectory, "rp-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationToken _token;
        private readonly int _topology;
        private readonly int _port;
        private bool _primaryStopped;
        private readonly ConcurrentQueue<string> _errors = new();
        private IAsyncDisposable? _transport;
        private string Rns => Path.Combine(_root, "rns");
        private string Profile => Path.Combine(_root, "native");
        public SidebandClient Client { get; private set; } = null!;
        public PyLinkNode Primary { get; private set; } = null!;
        public PyLinkNode Propagation { get; private set; } = null!;
        public RemoteCommandDelayProxy Proxy { get; private set; } = null!;
        public string Local { get; private set; } = "";

        private RemotePropagationFixture(int topology, CancellationToken token)
        {
            _topology = topology;
            _token = token;
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public static async Task<RemotePropagationFixture> OpenAsync(int topology, CancellationToken token)
        {
            var fixture = new RemotePropagationFixture(topology, token);
            try { await fixture.Start(); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private async Task Start()
        {
            Directory.CreateDirectory(Rns);
            if (_topology == 1)
                _transport = await PyLinkNode.StartAsync("RemotePropagationTransport", tcpServerPort: _port,
                    harness: "sideband_live_node.py", extraArguments: ["--transport"]);
            if (_topology == 2)
            {
                var config = Path.Combine(_root, "transport");
                Directory.CreateDirectory(config);
                await File.WriteAllTextAsync(Path.Combine(config, "config"),
                    $"[reticulum]\n share_instance = no\n enable_transport = yes\n\n[logging]\n loglevel = 0\n\n[interfaces]\n [[Remote]]\n type = TCPServerInterface\n enabled = yes\n listen_ip = 127.0.0.1\n listen_port = {_port}\n", _token);
                var node = ReticulumNode.Open(config);
                _transport = node;
                await node.StartAsync(_token);
            }
            Propagation = await StartPropagation();
            Primary = await StartPrimary();
            Proxy = new(_port, _token);
            await File.WriteAllTextAsync(Path.Combine(Rns, "config"),
                $"[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n [[Remote]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {Proxy.Port}\n", _token);
            Client = NewClient(observers: true);
            await Client.ConfigureCommandAuthorizationAsync(new(true, false, true), _token);
            await Client.ConnectAsync(Rns, _token);
            Local = Client.ConnectionInfo!.DeliveryHash;
            await Primary.WaitForAnnounce(Convert.FromHexString(Local), TimeSpan.FromSeconds(30));
            await Primary.Command("announce", "announced", TimeSpan.FromSeconds(10));
            await Propagation.Command("announce", "announced", TimeSpan.FromSeconds(10));
            await Client.AddContactAsync("Actual primary", Primary.HashHex, _token);
            await Client.ConfigurePropagationAsync(Propagation.HashHex, false, _token);
            await Primary.Command("use-pn " + Propagation.HashHex, "pn-selected", TimeSpan.FromSeconds(10));
            await Primary.Command("permission " + Local + " no no", "permission", TimeSpan.FromSeconds(10));
            await Primary.Command("configure " + Text("""{"telemetry_allow_requests_from_anyone":true,"lxmf_ignore_unknown":true,"lxmf_try_propagation_on_fail":true}"""),
                "configured", TimeSpan.FromSeconds(10));
        }

        private SidebandClient NewClient(bool observers)
        {
            var client = new SidebandClient(new(Profile));
            if (observers)
            {
                client.StateChanged += _ => throw new InvalidOperationException("Synthetic propagation state observer");
                client.ErrorReported += _ => throw new InvalidOperationException("Synthetic propagation error observer");
                client.ErrorReported += _errors.Enqueue;
            }
            return client;
        }
        private Task<PyLinkNode> StartPrimary() => PyLinkNode.StartAsync("ActualPrimaryRemotePropagation", rnsdPort: _port,
            harness: "sideband_remote_node.py", extraArguments: ["--source", Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")!,
                "--profile", Path.Combine(_root, "python")]);
        private Task<PyLinkNode> StartPropagation()
        {
            List<string> arguments = ["--identity", Path.Combine(_root, "pn-identity"), "--storage", Path.Combine(_root, "pn-storage")];
            if (_topology == 0) arguments.Add("--transport");
            return PyLinkNode.StartAsync("RemotePropagation",
                tcpServerPort: _topology == 0 ? _port : 0, rnsdPort: _topology == 0 ? 0 : _port,
                harness: "sideband_remote_pn.py", extraArguments: arguments.ToArray());
        }
        public async Task<int> Stored(string destination) =>
            (await Propagation.Command("pn-count " + destination, "pn-count", TimeSpan.FromSeconds(10))).GetProperty("count").GetInt32();
        public async Task RestartPropagation()
        {
            await Propagation.DisposeAsync();
            Propagation = await StartPropagation();
            await Propagation.Command("announce", "announced", TimeSpan.FromSeconds(10));
            if (_topology == 0)
            {
                // This topology's PN is also the TCP hub. Reconnect actual endpoints, not stale hub sockets.
                await RestartPrimary();
                await RestartNative(observers: true);
            }
        }
        public async Task RestartPrimary()
        {
            var hash = Primary.HashHex;
            await StopPrimary();
            Primary = await StartPrimary();
            _primaryStopped = false;
            Assert.Equal(hash, Primary.HashHex);
            await Primary.Command("announce", "announced", TimeSpan.FromSeconds(10));
        }
        public async Task StopPrimary()
        {
            if (Primary is null || _primaryStopped) return;
            _primaryStopped = true;
            await Primary.DisposeAsync();
        }
        public async Task RestartNative(bool observers)
        {
            await Client.DisposeAsync();
            Client = NewClient(observers);
            await Client.ConnectAsync(Rns, _token);
            Assert.Equal(Local, Client.ConnectionInfo!.DeliveryHash);
            Assert.True((await Client.GetCommandAuthorizationAsync(_token)).IgnoreUnknown);
            await Primary.WaitForAnnounce(Convert.FromHexString(Local), TimeSpan.FromSeconds(30));
        }
        public async Task PrimaryCommand(string command, bool propagation)
        {
            Assert.True((await Primary.Command($"{(propagation ? "command-propagated" : "command")} {Local} {Text(command)}",
                "queued", TimeSpan.FromSeconds(15))).GetProperty("accepted").GetBoolean(), Primary.Log);
        }
        public async Task<JsonElement> PrimaryCommandSnapshot(string command, LxmState? state = null)
        {
            JsonElement found = default;
            var expected = SidebandRemoteCommands.CreateCommandField(command)!.Encode();
            await Until(async () =>
            {
                var snapshot = await Primary.Command("snapshot " + Local, "snapshot", TimeSpan.FromSeconds(10));
                foreach (var row in snapshot.GetProperty("messages").EnumerateArray())
                    if (!row.GetProperty("incoming").GetBoolean() && row.GetProperty("commands").GetBoolean() &&
                        (state is null || row.GetProperty("state").GetInt32() == (int)state) &&
                        SidebandTelemetryValue.Decode(Convert.FromBase64String(row.GetProperty("commands_packed").GetString()!)).Encode().AsSpan().SequenceEqual(expected))
                    { found = row.Clone(); return true; }
                return false;
            });
            return found;
        }
        public Task NativeReply(string content) => Until(async () =>
            (await Client.LoadAsync(_token)).Messages.Any(m => m.Incoming && m.Content == content));
        public async Task Until(Func<Task<bool>> condition, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
            while (DateTime.UtcNow < deadline)
            {
                _token.ThrowIfCancellationRequested();
                if (await condition()) return;
                await Task.Delay(100, _token);
            }
            var log = Primary?.Log ?? "";
            Assert.Fail("Command propagation condition timed out.\n" + string.Join('\n', _errors.TakeLast(20)) +
                "\nPrimary tail:\n" + log[Math.Max(0, log.Length - 8000)..]);
        }
        public async ValueTask DisposeAsync()
        {
            if (Client is not null) await Client.DisposeAsync();
            await StopPrimary();
            if (Propagation is not null) await Propagation.DisposeAsync();
            if (Proxy is not null) await Proxy.DisposeAsync();
            if (_transport is not null) await _transport.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
