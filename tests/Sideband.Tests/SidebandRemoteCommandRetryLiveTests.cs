using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using LXMF;
using LXMF.Tests.Interop;

namespace Sideband.Tests;

public sealed partial class SidebandLiveInteropTests
{
    [Theory(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Initially-propagated failure acceptance requires both live opt-ins and the pinned actual-primary references.")]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [Trait("Category", "Live")]
    public Task ActualPrimaryInitiallyPropagatedFailureRetryBothOrigins(int policy, bool nativeOrigin) =>
        RunInitiallyPropagatedRetry(policy, nativeOrigin);

    private static async Task RunInitiallyPropagatedRetry(int policy, bool nativeOrigin)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(8));
        await using var f = await InitiallyPropagatedFixture.OpenAsync(stop.Token);
        var fallback = policy != 0;
        await f.Client.ConfigurePropagationFallbackAsync(fallback, stop.Token);
        await f.Primary.Command($"setup {f.Local} {f.Propagation.HashHex} {(fallback ? "yes" : "no")}",
            "configured", TimeSpan.FromSeconds(10));
        var firstText = $"retry-first-{(nativeOrigin ? "native" : "primary")}-{policy}";
        var first = await f.Send(nativeOrigin, "first", firstText);
        await f.Until(() => Task.FromResult(f.Failures(nativeOrigin, first, "first") >= 1), TimeSpan.FromMinutes(3));
        f.AssertFailures(nativeOrigin, first, "first", 1, fallback);
        Assert.True(f.Propagation.Count("pn-teardown") > 0);
        Assert.Equal(0, await f.Stored(nativeOrigin));
        await f.AssertNoExecution(nativeOrigin, 0);
        if (policy == 1)
        {
            await f.SetFault(false);
            await f.Until(async () => await f.SourceState(nativeOrigin, first, "first") == "Sent", TimeSpan.FromMinutes(3));
            await f.Until(async () => await f.Stored(nativeOrigin) == 1);
            f.AssertFailures(nativeOrigin, first, "first", 1, true);
            await f.AssertNoExecution(nativeOrigin, 0);
            await f.RetrieveAndReply(nativeOrigin, firstText, 0);
            Assert.Equal("Sent", await f.SourceState(nativeOrigin, first, "first"));
        }
        else
        {
            var groups = policy == 0 ? 1 : 2;
            await f.Until(async () => f.Failures(nativeOrigin, first, "first") == groups &&
                await f.SourceState(nativeOrigin, first, "first") == "Failed", TimeSpan.FromMinutes(3));
            await Task.Delay(TimeSpan.FromSeconds(8), stop.Token);
            f.AssertFailures(nativeOrigin, first, "first", groups, fallback);
            Assert.Equal(0, await f.Stored(nativeOrigin));
            await f.AssertNoExecution(nativeOrigin, 0);
            await f.SetFault(false);
        }
        var healthyText = $"retry-healthy-{(nativeOrigin ? "native" : "primary")}-{policy}";
        var healthy = await f.Send(nativeOrigin, "healthy", healthyText);
        await f.Until(async () => await f.SourceState(nativeOrigin, healthy, "healthy") == "Sent");
        await f.Until(async () => await f.Stored(nativeOrigin) == 1);
        await f.AssertNoExecution(nativeOrigin, policy == 1 ? 1 : 0);
        await f.RetrieveAndReply(nativeOrigin, healthyText, policy == 1 ? 1 : 0);
        Assert.Equal(0, f.Failures(nativeOrigin, healthy, "healthy"));
        var state = await f.Client.LoadAsync(stop.Token);
        Assert.Equal(nativeOrigin ? 2 : 0, state.Messages.Count(message => !message.Incoming));
        Assert.Empty(await f.Client.GetTelemetryAsync(cancellationToken: stop.Token));
        await f.WriteEvidence(nativeOrigin, policy, first, healthy);
    }

    private sealed class InitiallyPropagatedFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(AppContext.BaseDirectory, "initial-pn-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationToken _token;
        private readonly object _gate = new();
        private readonly Dictionary<Guid, string> _last = [];
        private readonly List<(Guid Id, string State, string? Method)> _outcomes = [];
        private readonly JsonArray _beforeRetrieval = [];
        private readonly ConcurrentQueue<string> _errors = new();
        private RemoteCommandDelayProxy? _proxy;
        private RemoteCommandDelayProxy? _primaryProxy;
        public SidebandClient Client { get; private set; } = null!;
        public PyLinkNode Primary { get; private set; } = null!;
        public PyLinkNode Propagation { get; private set; } = null!;
        public string Local { get; private set; } = "";

        private InitiallyPropagatedFixture(CancellationToken token) => _token = token;

        public static async Task<InitiallyPropagatedFixture> OpenAsync(CancellationToken token)
        {
            var fixture = new InitiallyPropagatedFixture(token);
            try { await fixture.Start(); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private static string Bridge(string name)
        {
            var directory = Environment.GetEnvironmentVariable("DOTRETICULUM_REMOTE_RETRY_BRIDGES")
                ?? Path.Combine(AppContext.BaseDirectory, "Interop");
            var path = Path.GetFullPath(Path.Combine(directory, name));
            if (!File.Exists(path)) throw new FileNotFoundException("Missing actual-primary retry bridge", path);
            return path;
        }

        private async Task Start()
        {
            Directory.CreateDirectory(_root);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            Propagation = await PyLinkNode.StartAsync("InitiallyPropagatedPN", tcpServerPort: port,
                harness: Bridge("sideband_retry_pn.py"),
                extraArguments: ["--identity", Path.Combine(_root, "pn-identity"), "--storage", Path.Combine(_root, "pn-store")]);
            _primaryProxy = new(port, _token);
            Primary = await PyLinkNode.StartAsync("InitiallyPropagatedPrimary", rnsdPort: _primaryProxy.Port,
                harness: Bridge("sideband_retry_primary.py"),
                extraArguments: ["--source", Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")!,
                    "--profile", Path.Combine(_root, "primary")]);
            _proxy = new(port, _token);
            var config = Path.Combine(_root, "rns");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"),
                $"[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n [[Retry]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {_proxy.Port}\n", _token);
            Client = new(new(Path.Combine(_root, "native")));
            Client.StateChanged += _ => throw new InvalidOperationException("Synthetic initially-propagated observer");
            Client.StateChanged += Observe;
            Client.ErrorReported += message =>
            {
                _errors.Enqueue(message);
                while (_errors.Count > 128) _errors.TryDequeue(out _);
            };
            await Client.ConfigureCommandAuthorizationAsync(new(true, false, true), _token);
            await Client.ConnectAsync(config, _token);
            Local = Client.ConnectionInfo!.DeliveryHash;
            await Primary.WaitForAnnounce(Convert.FromHexString(Local), TimeSpan.FromSeconds(30));
            await Primary.Command("announce", "announced", TimeSpan.FromSeconds(10));
            await Propagation.Command("announce", "announced", TimeSpan.FromSeconds(10));
            await Client.AddContactAsync("Actual primary retry peer", Primary.HashHex, _token);
            await Client.ConfigurePropagationAsync(Propagation.HashHex, false, _token);
        }

        private void Observe(SidebandState state)
        {
            lock (_gate)
            {
                foreach (var message in state.Messages.Where(message => !message.Incoming))
                {
                    if (_last.GetValueOrDefault(message.Id) == message.DeliveryState) continue;
                    _last[message.Id] = message.DeliveryState;
                    _outcomes.Add((message.Id, message.DeliveryState, message.DeliveryMethod));
                }
            }
        }

        public async Task<Guid> Send(bool nativeOrigin, string tag, string text)
        {
            if (nativeOrigin)
            {
                var queued = await Client.SendCommandAsync(Primary.HashHex, "echo " + text, true, _token);
                Assert.True(queued.Queued);
                return queued.OperationId;
            }
            var queuedPrimary = await Primary.Command($"send {tag} {Local} {Text("echo " + text)}", "queued", TimeSpan.FromSeconds(15));
            Assert.True(queuedPrimary.GetProperty("accepted").GetBoolean(), Primary.Log);
            return Guid.Empty;
        }

        public int Failures(bool nativeOrigin, Guid id, string tag)
        {
            if (!nativeOrigin) return Primary.Events("group-failed").Count(row => row.GetProperty("tag").GetString() == tag);
            lock (_gate) return _outcomes.Count(row => row.Id == id && row.State is "Failed" or "Rejected");
        }

        public void AssertFailures(bool nativeOrigin, Guid id, string tag, int expected, bool eligible)
        {
            Assert.Equal(expected, Failures(nativeOrigin, id, tag));
            if (nativeOrigin)
            {
                lock (_gate)
                {
                    Assert.DoesNotContain(_outcomes, row => row.Id == id && row.State == "Rejected");
                    Assert.All(_outcomes.Where(row => row.Id == id && row.State == "Failed"),
                        row => Assert.Equal("Propagated", row.Method));
                }
                return;
            }
            var rows = Primary.Events("group-failed").Where(row => row.GetProperty("tag").GetString() == tag).ToArray();
            for (var index = 0; index < rows.Length; index++)
            {
                Assert.Equal((int)LxmState.Failed, rows[index].GetProperty("state").GetInt32());
                Assert.Equal((int)LxmMethod.Propagated, rows[index].GetProperty("method").GetInt32());
                Assert.False(rows[index].GetProperty("stamp_failure").GetBoolean());
                Assert.Equal(index == 0 && eligible, rows[index].GetProperty("eligible_before").GetBoolean());
            }
        }

        public async Task<string> SourceState(bool nativeOrigin, Guid id, string tag)
        {
            if (nativeOrigin) return (await Client.LoadAsync(_token)).Messages.SingleOrDefault(message => message.Id == id)?.DeliveryState ?? "Queued";
            var job = await Primary.Command("job " + tag, "job", TimeSpan.FromSeconds(10));
            Assert.Equal((int)LxmMethod.Propagated, job.GetProperty("method").GetInt32());
            return ((LxmState)job.GetProperty("state").GetInt32()).ToString();
        }

        public async Task<int> Stored(bool nativeOrigin) =>
            (await Propagation.Command("pn-count " + (nativeOrigin ? Primary.HashHex : Local),
                "pn-count", TimeSpan.FromSeconds(10))).GetProperty("count").GetInt32();

        public Task<JsonElement> SetFault(bool enabled) =>
            Propagation.Command("fault " + (enabled ? "on" : "off"), "fault", TimeSpan.FromSeconds(10));

        public async Task AssertNoExecution(bool nativeOrigin, int expected)
        {
            if (nativeOrigin)
            {
                Assert.Equal(expected, Primary.Count("delivery"));
                Assert.Equal(expected, (await Client.LoadAsync(_token)).Messages.Count(message => message.Incoming));
            }
            else
            {
                Assert.Equal(expected, Primary.Count("delivery"));
                Assert.DoesNotContain((await Client.LoadAsync(_token)).Messages, message => message.Incoming && message.Title == "Command");
            }
            _beforeRetrieval.Add(new JsonObject
            {
                ["pn_stored"] = await Stored(nativeOrigin),
                ["observed_primary_deliveries"] = Primary.Count("delivery"),
                ["prior_completed_round_trips"] = expected
            });
        }

        public async Task RetrieveAndReply(bool nativeOrigin, string text, int before)
        {
            if (nativeOrigin)
            {
                await Primary.Command("retrieve", "retrieved", TimeSpan.FromSeconds(90));
                var request = await Primary.NextAsync("delivery", before, TimeSpan.FromSeconds(30));
                Assert.True(request.GetProperty("commands").GetBoolean());
                Assert.True(request.GetProperty("signature_valid").GetBoolean());
                Assert.Equal((int)LxmMethod.Propagated, request.GetProperty("method").GetInt32());
                await Until(async () => (await Client.LoadAsync(_token)).Messages.Any(message => message.Incoming &&
                    message.Content == "Echo reply: " + text));
            }
            else
            {
                Assert.Equal(1, await Client.SynchronizeAsync(cancellationToken: _token));
                var reply = await Primary.NextAsync("delivery", before, TimeSpan.FromSeconds(30));
                Assert.False(reply.GetProperty("commands").GetBoolean());
                Assert.True(reply.GetProperty("signature_valid").GetBoolean());
                Assert.Equal("Echo reply: " + text, DecodeContent(reply));
            }
            await Until(async () => await Stored(nativeOrigin) == 0);
        }

        public async Task Until(Func<Task<bool>> condition, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(90));
            while (DateTime.UtcNow < deadline)
            {
                _token.ThrowIfCancellationRequested();
                if (await condition()) return;
                await Task.Delay(100, _token);
            }
            Assert.Fail("Initially-propagated condition timed out.\n" + string.Join('\n', _errors.TakeLast(15)) +
                "\nPrimary:\n" + Primary.Log + "\nPN:\n" + Propagation.Log);
        }

        public async Task WriteEvidence(bool nativeOrigin, int policy, Guid first, Guid healthy)
        {
            Assert.Contains(_errors, error => error.Contains("Synthetic initially-propagated observer", StringComparison.Ordinal));
            var directory = Environment.GetEnvironmentVariable("DOTRETICULUM_REMOTE_RETRY_EVIDENCE")
                ?? Path.Combine(AppContext.BaseDirectory, "remote-retry-evidence");
            Directory.CreateDirectory(directory);
            JsonArray native;
            lock (_gate) native = new(_outcomes.Select(row => (JsonNode)new JsonObject
            {
                ["id"] = row.Id.ToString(), ["state"] = row.State, ["method"] = row.Method
            }).ToArray());
            var result = new JsonObject
            {
                ["origin"] = nativeOrigin ? "native" : "actual-primary", ["policy"] = policy,
                ["first_failed_groups"] = Failures(nativeOrigin, first, "first"),
                ["healthy_failed_groups"] = Failures(nativeOrigin, healthy, "healthy"),
                ["pn_teardowns"] = Propagation.Count("pn-teardown"),
                ["before_explicit_retrieval"] = _beforeRetrieval.DeepClone(),
                ["native_outcomes"] = native,
                ["primary_failures"] = new JsonArray(Primary.Events("group-failed").Select(row => JsonNode.Parse(row.GetRawText())).ToArray()),
                ["primary_sent"] = new JsonArray(Primary.Events("group-sent").Select(row => JsonNode.Parse(row.GetRawText())).ToArray()),
                ["deliveries"] = new JsonArray(Primary.Events("delivery").Select(row => JsonNode.Parse(row.GetRawText())).ToArray())
            };
            await File.WriteAllTextAsync(Path.Combine(directory, $"{(nativeOrigin ? "native" : "primary")}-{policy}.json"),
                result.ToJsonString(), _token);
        }

        public async ValueTask DisposeAsync()
        {
            if (Client is not null) await Client.DisposeAsync();
            if (Primary is not null) await Primary.DisposeAsync();
            if (Propagation is not null) await Propagation.DisposeAsync();
            if (_proxy is not null) await _proxy.DisposeAsync();
            if (_primaryProxy is not null) await _primaryProxy.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
