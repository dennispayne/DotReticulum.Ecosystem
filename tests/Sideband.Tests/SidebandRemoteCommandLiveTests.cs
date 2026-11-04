using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotReticulum.Applications;
using DotReticulum.Core;
using LXMF;
using LXMF.Tests.Interop;

namespace Sideband.Tests;

public sealed partial class SidebandLiveInteropTests
{
    [Theory(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Actual command application tests require DOTSIDEBAND_LIVE=1, DOTRETICULUM_LIVE=1 and pinned Python/RNS/LXMF/LXST/Sideband sources.")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryRemoteCommandsAuthorizationMalformedRecoveryAndRestart(int topology)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(5));
        var token = stop.Token;
        var source = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")!;
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        var root = Path.Combine(AppContext.BaseDirectory, "rc-" + Guid.NewGuid().ToString("N"));
        var rns = Path.Combine(root, "rns");
        var profile = Path.Combine(root, "python");
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n", token);
        PyLinkNode? python = null;
        try
        {
            await using var transport = topology == 1 ? await PyLinkNode.StartAsync("RemoteTransport", tcpServerPort: port,
                harness: "sideband_live_node.py", extraArguments: ["--transport"]) : null;
            var transportConfig = Path.Combine(root, "transport");
            Directory.CreateDirectory(transportConfig);
            await File.WriteAllTextAsync(Path.Combine(transportConfig, "config"),
                $"[reticulum]\n share_instance = no\n enable_transport = yes\n\n[logging]\n loglevel = 0\n\n[interfaces]\n [[Remote]]\n type = TCPServerInterface\n enabled = yes\n listen_ip = 127.0.0.1\n listen_port = {port}\n", token);
            await using var nativeTransport = topology == 2 ? ReticulumNode.Open(transportConfig) : null;
            if (nativeTransport is not null) await nativeTransport.StartAsync(token);
            python = await StartPrimary();
            // Transparent 100 ms delay per TCP read in both directions. No RNS decoding or reference-side patch.
            // Primary's sub-millisecond loopback delivery callback can race its own SQLite originator insert.
            await using var proxy = new RemoteCommandDelayProxy(port, token);
            await File.AppendAllTextAsync(Path.Combine(rns, "config"),
                $" [[Remote]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {proxy.Port}\n", token);
            var store = new SidebandStateStore(Path.Combine(root, "native"));
            await using var client = new SidebandClient(store);
            var errors = new List<string>();
            client.StateChanged += _ => throw new InvalidOperationException("Synthetic command state observer");
            client.ErrorReported += _ => throw new InvalidOperationException("Synthetic command error observer");
            client.ErrorReported += error => { lock (errors) errors.Add(error); };
            await client.ConnectAsync(rns, token);
            var local = client.ConnectionInfo!.DeliveryHash;
            await python.WaitForAnnounce(Convert.FromHexString(local), TimeSpan.FromSeconds(30));
            await python.Command("announce", "announced", TimeSpan.FromSeconds(10));

            // Actual primary drops a valid signed request from an unknown conversation even in anyone mode.
            await ConfigurePrimary("""{"telemetry_allow_requests_from_anyone":true,"lxmf_ignore_unknown":true}""");
            await NativeCommand("echo blocked-unknown-primary");
            await AssertNoNativeReply("Echo reply: blocked-unknown-primary");
            await PermissionPrimary(false, true);
            await ConfigurePrimary("""{"telemetry_allow_requests_from_anyone":false,"telemetry_allow_requests_from_trusted":true}""");
            await NativeCommand("echo blocked-untrusted-primary");
            await AssertNoNativeReply("Echo reply: blocked-untrusted-primary");
            await PermissionPrimary(true, true);
            await client.AddContactAsync("Actual primary", python.HashHex, token);
            await client.SetConversationTrustAsync(python.HashHex, true, token);
            await client.ConfigureCommandAuthorizationAsync(new(false, true, true), token);
            var unavailable = await client.SendCommandAsync(new string('e', 32), "ping", cancellationToken: token);
            Assert.True(unavailable.Queued);
            await UntilRemote(async () => (await client.LoadAsync(token)).Messages.Any(message =>
                message.Id == unavailable.OperationId && message.DeliveryState == "Failed"));

            foreach (var (command, reply) in new[]
            {
                ("pinganything", "Ping reply"), ("echo hello ✓", "Echo reply: hello ✓"),
                ("echo echo a echo b", "Echo reply: a b"), ("signal", "No reception info available")
            })
            {
                await NativeCommand(command);
                await UntilRemote(async () => (await client.LoadAsync(token)).Messages.Any(message => message.Incoming && message.Content == reply));
            }

            foreach (var (command, reply) in new[]
            {
                ("ping", "Ping reply"), ("echo native response ✓", "Echo reply: native response ✓"),
                ("sig", "No reception info available")
            })
            {
                var before = python.Count("delivery");
                Assert.True((await python.Command($"command {local} {Text(command)}", "queued", TimeSpan.FromSeconds(10)))
                    .GetProperty("accepted").GetBoolean(), $"Primary rejected {command}:\n{python.Log}");
                var delivered = await python.NextAsync("delivery", before, TimeSpan.FromSeconds(30));
                Assert.Equal(reply, Encoding.UTF8.GetString(Convert.FromBase64String(delivered.GetProperty("content").GetString()!)));
                Assert.True(delivered.GetProperty("signature_valid").GetBoolean());
                Assert.False(delivered.GetProperty("commands").GetBoolean());
                Assert.False(delivered.GetProperty("telemetry").GetBoolean());
                Assert.False(delivered.GetProperty("appearance").GetBoolean());
            }
            Assert.Empty(await client.GetTelemetryAsync(cancellationToken: token));
            Assert.DoesNotContain((await client.LoadAsync(token)).Messages, message => !message.Incoming && message.Title != "Command");

            // Strict trusted-only mode suppresses a permissive per-conversation request flag.
            await client.SetConversationTrustAsync(python.HashHex, false, token);
            await client.SetConversationTelemetryAsync(python.HashHex, false, true, token);
            await RejectedNativeRequest("echo blocked-native", "not authorized");
            await client.ConfigureCommandAuthorizationAsync(new(false, false, true), token);
            var beforeAllowed = python.Count("delivery");
            await python.Command($"command {local} {Text("echo conversation-permission")}", "queued", TimeSpan.FromSeconds(10));
            var allowedReply = await python.NextAsync("delivery", beforeAllowed, TimeSpan.FromSeconds(30));
            Assert.Equal("Echo reply: conversation-permission", DecodeContent(allowedReply));
            await client.ConfigureCommandAuthorizationAsync(new(true, true, true), token);
            await client.SetConversationTelemetryAsync(python.HashHex, false, false, token);

            await RejectedFields(new object?[] { new Dictionary<object, object?> { [2L] = true } }, "signature not validated", badSignature: true);
            await RejectedFields(new object?[] { new Dictionary<object, object?> { [99L] = true } }, "unknown command");
            await RejectedNativeRequest("not-a-native-plugin 'two words'", "unsupported Python plugin");
            await RejectedFields(new object?[] { new Dictionary<object, object?> { [3L] = new byte[] { 255 } },
                new Dictionary<object, object?> { [2L] = true } }, "metadata was rejected");
            await RejectedFields(Enumerable.Range(0, 65).Select(_ => (object?)new Dictionary<object, object?> { [2L] = true }).ToArray(),
                "at most 64");
            var recovery = python.Count("delivery");
            await python.Command($"command {local} {Text("echo recovered-native")}", "queued", TimeSpan.FromSeconds(10));
            Assert.Equal("Echo reply: recovered-native", DecodeContent(await python.NextAsync("delivery", recovery, TimeSpan.FromSeconds(30))));

            await UntilRemote(async () => (await client.LoadAsync(token)).Messages
                .Where(message => !message.Incoming && message.PeerDeliveryHash == python.HashHex)
                .All(message => message.DeliveryState == "Delivered"));
            var persistedCommands = (await client.LoadAsync(token)).Messages.Count(message => !message.Incoming);
            var pythonHash = python.HashHex;
            await ConfigurePrimary("""{"start_announce":false}""");
            await client.DisconnectAsync();
            await client.DisposeAsync();
            await python.DisposeAsync();
            // Explicit isolated native fixture: retain known cryptographic identity/cache, remove conversations.
            // Disabling actual primary startup announces prevents rediscovery from recreating the conversation.
            var nativeState = JsonNode.Parse(await File.ReadAllTextAsync(store.StatePath, token))!;
            nativeState["Contacts"] = new JsonArray();
            await File.WriteAllTextAsync(store.StatePath, nativeState.ToJsonString(), token);
            python = await StartPrimary();
            Assert.Equal(pythonHash, python.HashHex);
            // No state/error observers: processing must not depend on them.
            await using var reopened = new SidebandClient(new(store.DataDirectory));
            Assert.Equal(new(true, true, true), await reopened.GetCommandAuthorizationAsync(token));
            Assert.Equal(persistedCommands, (await reopened.LoadAsync(token)).Messages.Count(message => !message.Incoming));
            await reopened.ConnectAsync(rns, token);
            Assert.Equal(local, reopened.ConnectionInfo!.DeliveryHash);
            await python.WaitForAnnounce(Convert.FromHexString(local), TimeSpan.FromSeconds(30));
            var unknownErrors = new List<string>();
            void ObserveUnknown(string error) { lock (unknownErrors) unknownErrors.Add(error); }
            reopened.ErrorReported += ObserveUnknown;
            var unknownBefore = python.Count("delivery");
            await python.Command($"command {local} {Text("echo blocked-unknown-native")}", "queued", TimeSpan.FromSeconds(10));
            await UntilRemote(() =>
            {
                lock (unknownErrors) return Task.FromResult(unknownErrors.Any(error => error.Contains("not authorized", StringComparison.Ordinal)));
            });
            await Task.Delay(500, token);
            Assert.Equal(unknownBefore, python.Count("delivery"));
            Assert.Empty((await reopened.LoadAsync(token)).Contacts);
            reopened.ErrorReported -= ObserveUnknown;
            await reopened.AddContactAsync("Restored primary", python.HashHex, token);
            await python.Command("announce", "announced", TimeSpan.FromSeconds(10));
            var restarted = python.Count("delivery");
            await python.Command($"command {local} {Text("echo restart-native")}", "queued", TimeSpan.FromSeconds(10));
            Assert.Equal("Echo reply: restart-native", DecodeContent(await python.NextAsync("delivery", restarted, TimeSpan.FromSeconds(30))));
            var queued = await reopened.SendCommandAsync(python.HashHex, "echo restart-primary", cancellationToken: token);
            Assert.True(queued.Queued);
            await UntilRemote(async () => (await reopened.LoadAsync(token)).Messages.Any(message => message.Incoming && message.Content == "Echo reply: restart-primary"));
            Assert.Equal(persistedCommands + 1, (await reopened.LoadAsync(token)).Messages.Count(message => !message.Incoming));
            Assert.DoesNotContain((await reopened.LoadAsync(token)).Messages, message => message.Incoming && message.Title == "Command");
            Assert.DoesNotContain((await reopened.LoadAsync(token)).Messages,
                message => message.Incoming && message.Content.Contains("blocked-", StringComparison.Ordinal));

            Task<PyLinkNode> StartPrimary() => PyLinkNode.StartAsync("ActualPrimaryRemote", tcpServerPort: topology == 0 ? port : 0,
                rnsdPort: topology == 0 ? 0 : port, harness: "sideband_remote_node.py",
                extraArguments: ["--source", source, "--profile", profile]);
            Task<JsonElement> ConfigurePrimary(string json) => python!.Command("configure " + Text(json), "configured", TimeSpan.FromSeconds(10));
            Task<JsonElement> PermissionPrimary(bool trusted, bool allowed) =>
                python!.Command($"permission {local} {(trusted ? "yes" : "no")} {(allowed ? "yes" : "no")}", "permission", TimeSpan.FromSeconds(10));
            async Task NativeCommand(string command)
            {
                var before = python!.Count("delivery");
                var accepted = await client.SendCommandAsync(python.HashHex, command, cancellationToken: token);
                Assert.True(accepted.Queued);
                var observed = await python.NextAsync("delivery", before, TimeSpan.FromSeconds(30));
                Assert.True(observed.GetProperty("signature_valid").GetBoolean());
                Assert.True(observed.GetProperty("commands").GetBoolean());
                await UntilRemote(async () => (await client.LoadAsync(token)).Messages.Any(message =>
                    message.Id == accepted.OperationId && message.DeliveryState == "Delivered" &&
                    message.DeliveryMethod == ((LxmMethod)observed.GetProperty("method").GetInt32()).ToString()));
            }
            async Task AssertNoNativeReply(string content)
            {
                await Task.Delay(750, token);
                Assert.DoesNotContain((await client.LoadAsync(token)).Messages, message => message.Incoming && message.Content == content);
            }
            async Task RejectedNativeRequest(string command, string reason)
            {
                int errorStart;
                lock (errors) errorStart = errors.Count;
                var deliveryStart = python!.Count("delivery");
                await python.Command($"command {local} {Text(command)}", "queued", TimeSpan.FromSeconds(10));
                await UntilRemote(() =>
                {
                    lock (errors) return Task.FromResult(errors.Skip(errorStart).Any(error => error.Contains(reason, StringComparison.Ordinal)));
                });
                await Task.Delay(500, token);
                Assert.Equal(deliveryStart, python.Count("delivery"));
            }
            async Task RejectedFields(object?[] commands, string reason, bool badSignature = false)
            {
                int errorStart;
                lock (errors) errorStart = errors.Count;
                var deliveryStart = python!.Count("delivery");
                var fields = MsgPack.Pack(new Dictionary<object, object?> { [LxmField.Commands] = commands });
                await python.Command($"{(badSignature ? "bad-signature" : "fields")} {local} {Convert.ToBase64String(fields)}",
                    "queued", TimeSpan.FromSeconds(10));
                await UntilRemote(() =>
                {
                    lock (errors) return Task.FromResult(errors.Skip(errorStart).Any(error => error.Contains(reason, StringComparison.Ordinal)));
                });
                await Task.Delay(500, token);
                Assert.Equal(deliveryStart, python.Count("delivery"));
            }
            async Task UntilRemote(Func<Task<bool>> condition)
            {
                var deadline = DateTime.UtcNow.AddSeconds(35);
                while (DateTime.UtcNow < deadline)
                {
                    if (await condition()) return;
                    await Task.Delay(50, token);
                }
                Assert.Fail($"Remote condition timed out. Python:\n{python?.Log}\nNative:\n{string.Join('\n', errors)}");
            }
        }
        finally
        {
            if (python is not null) await python.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string Text(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string DecodeContent(JsonElement value) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(value.GetProperty("content").GetString()!));

    private sealed class RemoteCommandDelayProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop;
        private readonly SemaphoreSlim _slots = new(4, 4);
        private readonly List<Task> _connections = [];
        private readonly Task _accept;
        private readonly int _targetPort;
        private TaskCompletionSource? _paused;
        private int _pausedReads;

        public RemoteCommandDelayProxy(int targetPort, CancellationToken token)
        {
            _targetPort = targetPort;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _listener.Start(4);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accept = AcceptAsync();
        }

        public int Port { get; }
        public int PausedReads => Volatile.Read(ref _pausedReads);
        public void Pause()
        {
            if (Interlocked.CompareExchange(ref _paused, new(TaskCreationOptions.RunContinuationsAsynchronously), null) is not null)
                throw new InvalidOperationException("The transparent proxy is already paused.");
        }
        public void Resume() => Interlocked.Exchange(ref _paused, null)?.TrySetResult();
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var peer = await _listener.AcceptTcpClientAsync(_stop.Token);
                    if (!_slots.Wait(0)) { peer.Dispose(); continue; }
                    _connections.Add(ForwardAsync(peer));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        private async Task ForwardAsync(TcpClient peer)
        {
            using (peer)
            using (var remote = new TcpClient())
            using (var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                try
                {
                    await remote.ConnectAsync(IPAddress.Loopback, _targetPort, connection.Token);
                    var outgoing = PipeAsync(peer.GetStream(), remote.GetStream(), connection.Token);
                    var incoming = PipeAsync(remote.GetStream(), peer.GetStream(), connection.Token);
                    await Task.WhenAny(outgoing, incoming);
                    await connection.CancelAsync();
                    await Task.WhenAll(outgoing, incoming);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
                finally { _slots.Release(); }
            }
        }

        private async Task PipeAsync(Stream source, Stream destination, CancellationToken token)
        {
            var buffer = new byte[16_384];
            while (await source.ReadAsync(buffer, token) is var length && length != 0)
            {
                await Task.Delay(100, token);
                if (Volatile.Read(ref _paused) is { } paused)
                {
                    Interlocked.Increment(ref _pausedReads);
                    await paused.Task.WaitAsync(token);
                }
                await destination.WriteAsync(buffer.AsMemory(0, length), token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accept;
            await Task.WhenAll(_connections);
            _slots.Dispose();
            _stop.Dispose();
        }
    }
}
