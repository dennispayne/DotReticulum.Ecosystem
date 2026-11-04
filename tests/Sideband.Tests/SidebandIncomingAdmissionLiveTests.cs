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
    [Theory(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Actual ordinary-message admission requires both live opt-ins and pinned primary Python/RNS/LXMF/LXST/Sideband sources.")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryOrdinaryAdmissionDistinguishesDiscoveryMetadataAndConversation(int topology)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(4));
        var token = stop.Token;
        var source = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")!;
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        var root = Path.Combine(AppContext.BaseDirectory, "admit-live-" + Guid.NewGuid().ToString("N"));
        var rns = Path.Combine(root, "rns");
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n", token);
        PyLinkNode? python = null;
        try
        {
            await using var transport = topology == 1 ? await PyLinkNode.StartAsync("AdmissionTransport", tcpServerPort: port,
                harness: "sideband_live_node.py", extraArguments: ["--transport"]) : null;
            var transportConfig = Path.Combine(root, "transport");
            Directory.CreateDirectory(transportConfig);
            await File.WriteAllTextAsync(Path.Combine(transportConfig, "config"),
                $"[reticulum]\n share_instance = no\n enable_transport = yes\n\n[logging]\n loglevel = 0\n\n[interfaces]\n [[Admission]]\n type = TCPServerInterface\n enabled = yes\n listen_ip = 127.0.0.1\n listen_port = {port}\n", token);
            await using var nativeTransport = topology == 2 ? ReticulumNode.Open(transportConfig) : null;
            if (nativeTransport is not null) await nativeTransport.StartAsync(token);
            python = await PyLinkNode.StartAsync("ActualPrimaryAdmission", tcpServerPort: topology == 0 ? port : 0,
                rnsdPort: topology == 0 ? 0 : port, harness: "sideband_remote_node.py",
                extraArguments: ["--source", source, "--profile", Path.Combine(root, "python")]);
            await using var proxy = new RemoteCommandDelayProxy(port, token);
            await File.AppendAllTextAsync(Path.Combine(rns, "config"),
                $" [[Admission]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {proxy.Port}\n", token);
            var store = new SidebandStateStore(Path.Combine(root, "native"));
            await using var initial = new SidebandClient(store);
            await initial.ConfigureCommandAuthorizationAsync(new(true, false, true), token);
            await initial.ConnectAsync(rns, token);
            var local = initial.ConnectionInfo!.DeliveryHash;
            await python.WaitForAnnounce(Convert.FromHexString(local), TimeSpan.FromSeconds(30));
            await python.Command("announce", "announced", TimeSpan.FromSeconds(10));
            await Until(async () => (await initial.LoadAsync(token)).Contacts.Any(c => c.DeliveryHash == python.HashHex));
            Assert.False(Assert.Single((await initial.LoadAsync(token)).Contacts).ConversationKnown);
            await python.Command("configure " + Text("""{"lxmf_ignore_unknown":true}"""), "configured", TimeSpan.FromSeconds(10));

            var before = python.Count("delivery");
            await initial.SendTextAsync(python.HashHex, "blocked-ordinary-primary", token);
            var observed = await python.NextAsync("delivery", before, TimeSpan.FromSeconds(30));
            Assert.True(observed.GetProperty("signature_valid").GetBoolean());
            await Until(async () => (await initial.LoadAsync(token)).Messages.Any(m => m.Content == "blocked-ordinary-primary" && m.DeliveryState == "Delivered"));
            var snapshot = await Snapshot();
            Assert.False(snapshot.GetProperty("conversation_known").GetBoolean());
            Assert.DoesNotContain(snapshot.GetProperty("messages").EnumerateArray(),
                m => m.GetProperty("incoming").GetBoolean() && DecodeContent(m) == "blocked-ordinary-primary");
            Assert.True(Assert.Single((await initial.LoadAsync(token)).Contacts).ConversationKnown);

            await initial.DisconnectAsync();
            await initial.DisposeAsync();
            // Isolated fixture removes conversation membership, retaining real identity/cache and prior history.
            var stateJson = JsonNode.Parse(await File.ReadAllTextAsync(store.StatePath, token))!;
            stateJson["Contacts"] = new JsonArray();
            await File.WriteAllTextAsync(store.StatePath, stateJson.ToJsonString(), token);
            await using var client = new SidebandClient(new(store.DataDirectory));
            var errors = new List<string>();
            client.StateChanged += _ => throw new InvalidOperationException("Synthetic admission state observer");
            client.ErrorReported += _ => throw new InvalidOperationException("Synthetic admission error observer");
            client.ErrorReported += error => { lock (errors) errors.Add(error); };
            await client.ConnectAsync(rns, token);
            Assert.Equal(local, client.ConnectionInfo!.DeliveryHash);
            await python.Command("announce", "announced", TimeSpan.FromSeconds(10));
            await Until(async () => (await client.LoadAsync(token)).Contacts.Any(c => c.DeliveryHash == python.HashHex));
            Assert.False(Assert.Single((await client.LoadAsync(token)).Contacts).ConversationKnown);
            var errorStart = ErrorCount();
            await PrimaryMessage("blocked-announced-native");
            await Until(() => Task.FromResult(ErrorCount() > errorStart));

            var metadata = new Dictionary<object, object?>
            {
                [LxmField.Telemetry] = SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.Create(DateTimeOffset.UtcNow.ToUnixTimeSeconds())),
                [LxmField.IconAppearance] = new object?[] { "map-marker", new byte[] { 17, 34, 51 }, new byte[] { 170, 187, 204 } }
            };
            var combined = new Dictionary<object, object?>(metadata)
            {
                [LxmField.FileAttachments] = new object?[] { new object?[] { "metadata.txt", Encoding.UTF8.GetBytes("Synthetic admission attachment") } }
            };
            errorStart = ErrorCount();
            await Fields(combined);
            await Until(() => Task.FromResult(ErrorCount() > errorStart));
            errorStart = ErrorCount();
            await Fields(combined, badSignature: true);
            await Until(() => Task.FromResult(ErrorCount() > errorStart));
            Assert.Empty(await client.GetTelemetryAsync(cancellationToken: token));
            Assert.False(Directory.Exists(Path.Combine(store.DataDirectory, "attachments")));
            var discovered = Assert.Single((await client.LoadAsync(token)).Contacts);
            Assert.False(discovered.ConversationKnown);
            Assert.Null(discovered.Appearance);
            Assert.DoesNotContain((await client.LoadAsync(token)).Messages, m => m.Incoming);
            Assert.False(Assert.Single((await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token)).Contacts).ConversationKnown);
            await client.DisconnectAsync();
            await client.DisposeAsync();

            // No observers registered: admission, metadata and promotion still run after restart.
            await using var reopened = new SidebandClient(new(store.DataDirectory));
            await reopened.ConnectAsync(rns, token);
            Assert.True((await reopened.GetCommandAuthorizationAsync(token)).IgnoreUnknown);
            Assert.False(Assert.Single((await reopened.LoadAsync(token)).Contacts).ConversationKnown);
            await reopened.ConfigureCommandAuthorizationAsync(new(true, false, false), token);
            await Fields(metadata);
            await Until(async () => (await reopened.GetTelemetryAsync(cancellationToken: token)).Count > 0);
            Assert.False(Assert.Single((await reopened.LoadAsync(token)).Contacts).ConversationKnown);
            await Fields(combined);
            await Until(async () => (await reopened.LoadAsync(token)).Messages.Any(m => m.Incoming && m.Attachments?.Any(a => a.Name == "metadata.txt") == true));
            Assert.False(Assert.Single((await reopened.LoadAsync(token)).Contacts).ConversationKnown);
            await reopened.ConfigureCommandAuthorizationAsync(new(true, false, true), token);
            await PrimaryMessage("blocked-after-metadata-native");
            await Task.Delay(500, token);
            Assert.DoesNotContain((await reopened.LoadAsync(token)).Messages, m => m.Content == "blocked-after-metadata-native");
            await reopened.ConfigureCommandAuthorizationAsync(new(true, false, false), token);
            await Fields([]);
            await Until(async () => Assert.Single((await reopened.LoadAsync(token)).Contacts).ConversationKnown);
            await reopened.ConfigureCommandAuthorizationAsync(new(true, false, true), token);
            await PrimaryMessage("accepted-untrusted-native");
            await Until(async () => (await reopened.LoadAsync(token)).Messages.Any(m => m.Incoming && m.Content == "accepted-untrusted-native"));
            var known = Assert.Single((await reopened.LoadAsync(token)).Contacts);
            Assert.False(known.Trusted);
            Assert.False(known.AllowTelemetryRequests);
            Assert.True(known.ConversationKnown);
            await Fields(new()
            {
                [LxmField.FileAttachments] = new object?[] { new object?[] { "unverified.txt", Encoding.UTF8.GetBytes("Explicit signature-fault fixture") } }
            }, badSignature: true);
            await Until(async () => (await reopened.LoadAsync(token)).Messages.Any(m => m.Incoming && m.Attachments?.Any(a => a.Name == "unverified.txt") == true));
            await reopened.SendTextAsync(python.HashHex, "accepted-ordinary-primary", token);
            await Until(async () => (await Snapshot()).GetProperty("messages").EnumerateArray().Any(m =>
                m.GetProperty("incoming").GetBoolean() && m.GetProperty("signature_valid").GetBoolean() && DecodeContent(m) == "accepted-ordinary-primary"));
            await python.Command("announce", "announced", TimeSpan.FromSeconds(10));
            await reopened.DisconnectAsync();
            Assert.True(Assert.Single((await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token)).Contacts).ConversationKnown);

            Task<JsonElement> Snapshot() => python.Command($"snapshot {local}", "snapshot", TimeSpan.FromSeconds(10));
            int ErrorCount() { lock (errors) return errors.Count; }
            async Task PrimaryMessage(string content)
            {
                Assert.True((await python.Command($"message {local} {Text(content)}", "queued", TimeSpan.FromSeconds(10)))
                    .GetProperty("accepted").GetBoolean(), python.Log);
                await Until(async () => (await Snapshot()).GetProperty("messages").EnumerateArray().Any(m =>
                    !m.GetProperty("incoming").GetBoolean() && m.GetProperty("delivered").GetBoolean() && DecodeContent(m) == content));
            }
            async Task Fields(Dictionary<object, object?> fields, bool badSignature = false)
            {
                var packed = Convert.ToBase64String(MsgPack.Pack(fields));
                Assert.True((await python.Command($"{(badSignature ? "bad-signature" : "fields")} {local} {packed}",
                    "queued", TimeSpan.FromSeconds(10))).GetProperty("accepted").GetBoolean());
            }
            async Task Until(Func<Task<bool>> condition)
            {
                var deadline = DateTime.UtcNow.AddSeconds(35);
                while (DateTime.UtcNow < deadline)
                {
                    if (await condition()) return;
                    await Task.Delay(50, token);
                }
                var log = python.Log;
                Assert.Fail($"Admission condition timed out. Primary tail:\n{log[Math.Max(0, log.Length - 12_000)..]}");
            }
        }
        finally
        {
            if (python is not null) await python.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
