using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotReticulum.Core;
using LXMF;
using LXMF.Tests.Interop;
using V = Sideband.SidebandTelemetryValue;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband.Tests;

public sealed partial class SidebandLiveInteropTests
{
    [Theory(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Telemetry application live tests require pinned Sideband/RNS/LXMF and DOTSIDEBAND_LIVE=1/DOTRETICULUM_LIVE=1.")]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryTelemetryRequestsCollectorProvenancePrivacyAndRestart(bool transportHop)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(4));
        var token = stop.Token;
        var source = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")!;
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0)) { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        var root = Path.Combine(AppContext.BaseDirectory, "t-" + Guid.NewGuid().ToString("N"));
        var rns = Path.Combine(root, "rns");
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            $"[reticulum]\n enable_transport = no\n share_instance = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n [[Telemetry]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
        try
        {
            await using var transport = transportHop ? await PyLinkNode.StartAsync("TelemetryTransport", tcpServerPort: port,
                harness: "sideband_live_node.py", extraArguments: ["--transport"]) : null;
            await using var python = await PyLinkNode.StartAsync("ActualSidebandTelemetry", tcpServerPort: transportHop ? 0 : port,
                rnsdPort: transportHop ? port : 0, harness: "sideband_application_node.py", extraArguments: ["--source", source]);
            var store = new SidebandStateStore(Path.Combine(root, "data"));
            await using var client = new SidebandClient(store);
            var failures = new List<string>();
            client.ErrorReported += error => { lock (failures) failures.Add(error); };
            await client.ConfigureTelemetryAsync(new()
            {
                Enabled = true, InformationEnabled = true, Information = "native-manual",
                FixedLocationEnabled = true, FixedLocation = new(51.5, -0.125, 10),
                AllowRequestsFromTrusted = true, CollectorEnabled = true
            }, token);
            await client.ConnectAsync(rns, token);
            var local = client.ConnectionInfo!.DeliveryHash;
            await python.WaitForAnnounce(Convert.FromHexString(local), TimeSpan.FromSeconds(30));
            await python.Command("announce", "announced", TimeSpan.FromSeconds(30));
            await client.AddContactAsync("Primary", python.HashHex, token);
            await client.SetConversationTrustAsync(python.HashHex, true, token);
            await client.SetConversationTelemetryAsync(python.HashHex, true, true, token);
            await python.Command($"trust {local} yes", "trust", TimeSpan.FromSeconds(10));
            await python.Command($"telemetry-permission {local} yes yes", "telemetry-permission", TimeSpan.FromSeconds(10));
            await ConfigurePython("""
                {"telemetry_enabled":true,"telemetry_s_information":true,"telemetry_s_information_text":"primary-manual",
                 "telemetry_s_fixed_location":true,"telemetry_s_fixed_latlon":[48.8566,2.3522],"telemetry_s_fixed_altitude":35,
                 "telemetry_allow_requests_from_trusted":true,"telemetry_collector_enabled":true}
                """);
            Assert.True((await client.SendLatestTelemetryAsync(python.HashHex, token)).Queued);
            var receivedByPython = await WaitPythonRecord(local);
            var primaryReport = SidebandTelemetryCodec.Decode(Convert.FromBase64String(receivedByPython.GetProperty("packed").GetString()!));
            Assert.Equal(local, Convert.ToHexStringLower(primaryReport.Get(S.Received)!.Data.Get("via")!.GetBinary()!));
            Assert.Equal(python.HashHex, Convert.ToHexStringLower(primaryReport.Get(S.Received)!.Data.Get("by")!.GetBinary()!));
            Assert.InRange(primaryReport.Get(S.Received)!.Data.Get("distance")!.Get("geodesic")!.Number!.Value, 340_000, 350_000);
            await UntilTelemetry(async () => (await client.LoadAsync(token)).TelemetryPeers?.Any(p => p.DeliveryHash == python.HashHex && p.LastSendSuccessTimebase > 0) == true);
            await python.Command($"telemetry-send {local}", "telemetry-queued", TimeSpan.FromSeconds(10));
            var nativeReport = await WaitNativeRecord(python.HashHex);
            Assert.Equal(python.HashHex, Convert.ToHexStringLower(nativeReport.Report.Get(S.Received)!.Data.Get("via")!.GetBinary()!));
            Assert.InRange(nativeReport.Report.Get(S.Received)!.Data.Get("distance")!.Get("geodesic")!.Number!.Value, 340_000, 350_000);
            Assert.Empty((await client.LoadAsync(token)).Messages);

            var claimed = new string('3', 32);
            var originalReceiver = new string('4', 32);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10;
            var synthetic = SidebandTelemetryCodec.Create(timestamp,
                SidebandTelemetryCodec.FromData(S.Information, V.Fields(("contents", V.From("reported collector source")))),
                SidebandTelemetryCodec.FromWireValue((byte)S.Received, V.Array(V.From(Convert.FromHexString(originalReceiver)), V.Nil, V.From(123d), V.From(456d))));
            await python.Command($"telemetry-seed {claimed} {Convert.ToBase64String(SidebandTelemetryCodec.Encode(synthetic))}", "telemetry-seeded", TimeSpan.FromSeconds(10));
            await Task.Delay(1100, token);
            await ConfigurePython("""{"telemetry_s_information_text":"fresh primary collector reading"}""");
            var configured = (await client.LoadAsync(token)).Telemetry;
            await client.ConfigureTelemetryAsync(configured with { Collector = python.HashHex }, token);
            Assert.True((await client.RequestTelemetryAsync(python.HashHex, token)).Queued);
            var relayed = await WaitNativeRecord(claimed);
            Assert.Equal(originalReceiver, Convert.ToHexStringLower(relayed.Report.Get(S.Received)!.Data.Get("by")!.GetBinary()!));
            Assert.Equal(python.HashHex, Convert.ToHexStringLower(relayed.Report.Get(S.Received)!.Data.Get("via")!.GetBinary()!));
            Assert.Equal(123, relayed.Report.Get(S.Received)!.Data.Get("distance")!.Get("geodesic")!.Number);

            var reverse = new string('5', 32);
            var excluded = new string('7', 32);
            var reverseReport = SidebandTelemetryCodec.Create(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5,
                SidebandTelemetryCodec.FromData(S.Information, V.Fields(("contents", V.From("native collector source")))));
            // Owned local store setup is synthetic test data, not evidence that the asserted origin sent anything.
            await client.DisconnectAsync();
            await client.DisposeAsync();
            await using (var seed = await SidebandTelemetryStore.OpenAsync(Path.Combine(store.DataDirectory, "telemetry"), cancellationToken: token))
            {
                await seed.AppendAsync(reverse, SidebandTelemetryCodec.Encode(reverseReport), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), token);
                await seed.AppendAsync(excluded, SidebandTelemetryCodec.Encode(reverseReport), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), token);
            }
            await using var reopened = new SidebandClient(new(store.DataDirectory));
            reopened.ErrorReported += error => { lock (failures) failures.Add(error); };
            await reopened.ConnectAsync(rns, token);
            await reopened.ConfigureTelemetryAsync((await reopened.LoadAsync(token)).Telemetry with { ExcludedSources = [excluded] }, token);
            Assert.NotEmpty(await reopened.GetTelemetryAsync(new(claimed), token));
            await Task.Delay(1100, token);
            await ConfigurePython($$"""{"telemetry_collector":"{{local}}"}""");
            await python.Command($"telemetry-request-collector {local}", "telemetry-queued", TimeSpan.FromSeconds(10));
            await WaitPythonRecord(reverse);
            var collectorSnapshot = await python.Command($"telemetry-snapshot {local}", "telemetry-snapshot", TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(collectorSnapshot.GetProperty("records").EnumerateArray(),
                record => record.GetProperty("source").GetString() == excluded);

            await reopened.ConfigureTelemetryAsync((await reopened.LoadAsync(token)).Telemetry with { ReceiveTrustedOnly = true }, token);
            await reopened.SetConversationTrustAsync(python.HashHex, false, token);
            var blocked = new string('6', 32);
            var blockReport = SidebandTelemetryCodec.Create(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2);
            await python.Command($"telemetry-seed {blocked} {Convert.ToBase64String(SidebandTelemetryCodec.Encode(blockReport))}", "telemetry-seeded", TimeSpan.FromSeconds(10));
            await Task.Delay(1100, token);
            await ConfigurePython("""{"telemetry_s_information_text":"new primary reading"}""");
            var entries = new object?[] { new object?[] { Convert.FromHexString(blocked), blockReport.TimestampUtcSeconds,
                SidebandTelemetryCodec.Encode(blockReport), null } };
            await python.Command($"telemetry-stream {local} {Convert.ToBase64String(MsgPack.Pack(entries))}", "telemetry-queued", TimeSpan.FromSeconds(10));
            await Task.Delay(1500, token);
            Assert.Empty(await reopened.GetTelemetryAsync(new(blocked), token));
            Assert.Empty((await reopened.LoadAsync(token)).Messages);

            var profileSource = Path.Combine(root, "ps");
            var profileTarget = Path.Combine(root, "pi");
            await python.Command("telemetry-profile-snapshot " + Convert.ToBase64String(Encoding.UTF8.GetBytes(profileSource)),
                "telemetry-profile-snapshotted", TimeSpan.FromSeconds(10));
            await SidebandProfileImporter.ImportAsync(profileSource, profileTarget, token);
            await using (var imported = new SidebandClient(new(profileTarget)))
            {
                var importedState = await imported.LoadAsync(token);
                Assert.True(importedState.Telemetry.Enabled);
                Assert.True(importedState.Telemetry.CollectorEnabled);
                var importedContact = Assert.Single(importedState.Contacts, c => c.DeliveryHash == local);
                Assert.True(importedContact.SendTelemetry);
                Assert.True(importedContact.AllowTelemetryRequests);
                Assert.NotNull(importedContact.Appearance);
                Assert.NotEmpty(await imported.GetTelemetryAsync(new(reverse), token));
                Assert.True(importedState.TelemetryPeers!.Single(p => p.DeliveryHash == local).LastSendSuccessTimebase > 0);
                Assert.True(importedState.TelemetryPeers!.Single(p => p.DeliveryHash == local).LastRequestSuccessTimebase > 0);
            }

            var deniedTimebase = (await reopened.LoadAsync(token)).TelemetryPeers!.Single(p => p.DeliveryHash == python.HashHex).LastSendSuccessTimebase;
            await python.Command($"telemetry-request-collector {local}", "telemetry-queued", TimeSpan.FromSeconds(10));
            await Task.Delay(1500, token);
            Assert.Equal(deniedTimebase, (await reopened.LoadAsync(token)).TelemetryPeers!.Single(p => p.DeliveryHash == python.HashHex).LastSendSuccessTimebase);

            await reopened.SetConversationTrustAsync(python.HashHex, true, token);
            await reopened.ConfigureTelemetryAsync((await reopened.LoadAsync(token)).Telemetry with
            {
                ReceiveTrustedOnly = false, SendToCollector = true, RequestFromCollector = true,
                SendIntervalSeconds = 2, RequestIntervalSeconds = 2
            }, token);
            await UntilTelemetry(async () =>
            {
                var peer = (await reopened.LoadAsync(token)).TelemetryPeers!.Single(p => p.DeliveryHash == python.HashHex);
                return peer.LastSendSuccessTimebase > deniedTimebase && peer.LastRequestSuccessTimebase > 0 &&
                    peer.ScheduledSendAttempts == 0;
            });
            await reopened.ConfigureTelemetryAsync((await reopened.LoadAsync(token)).Telemetry with
            {
                SendToCollector = false, RequestFromCollector = false, Collector = null,
                Tracking = [new(python.HashHex, 2, DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeSeconds())]
            }, token);
            await UntilTelemetry(async () => (await reopened.LoadAsync(token)).Telemetry.Tracking.Single().LastRequestUtcSeconds > 0);
            var trackingAttempt = (await reopened.LoadAsync(token)).Telemetry.Tracking.Single().LastRequestUtcSeconds;
            await reopened.DisconnectAsync();
            await reopened.ConnectAsync(rns, token);
            Assert.Equal(trackingAttempt, (await reopened.LoadAsync(token)).Telemetry.Tracking.Single().LastRequestUtcSeconds);
            await reopened.ConfigureTelemetryAsync((await reopened.LoadAsync(token)).Telemetry with { Tracking = [] }, token);

            var validSource = new string('8', 32);
            var futureSource = new string('9', 32);
            var mismatchedSource = new string('a', 32);
            var validTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var validPacked = SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.Create(validTime));
            var mixed = new object?[]
            {
                new object?[] { Convert.FromHexString(validSource), validTime, validPacked, null },
                new object?[] { Convert.FromHexString(futureSource), validTime + 241, validPacked, null },
                new object?[] { Convert.FromHexString(mismatchedSource), validTime - 10, validPacked, null }
            };
            await SendFields(new() { [LxmField.TelemetryStream] = mixed });
            await UntilTelemetry(async () => (await reopened.GetTelemetryAsync(new(validSource), token)).Count == 1);
            Assert.Empty(await reopened.GetTelemetryAsync(new(futureSource), token));
            Assert.Empty(await reopened.GetTelemetryAsync(new(mismatchedSource), token));
            Assert.Equal(validTime, (await reopened.LoadAsync(token)).TelemetryPeers!.Single(p => p.DeliveryHash == python.HashHex).LastReceivedTimebase);
            await SendFields(new()
            {
                [LxmField.Commands] = new object?[]
                {
                    new Dictionary<object, object?> { [(long)SidebandTelemetryCommand.TelemetryRequest] = new object?[] { DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600, true } }
                }
            });
            await UntilTelemetry(() =>
            {
                lock (failures) return Task.FromResult(failures.Any(e => e.Contains("future telemetry request cursor", StringComparison.Ordinal)));
            });
            var unsignedPacked = SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.Create(validTime + 120));
            await SendFields(new() { [LxmField.Telemetry] = unsignedPacked }, invalidSignature: true);
            await UntilTelemetry(() =>
            {
                lock (failures) return Task.FromResult(failures.Any(e => e.Contains("Unverified telemetry/commands/appearance", StringComparison.Ordinal)));
            });
            Assert.DoesNotContain(await reopened.GetTelemetryAsync(new(python.HashHex), token), r => r.TimestampUtcSeconds == validTime + 120);
            await SendFields(new() { [LxmField.Telemetry] = new byte[] { 0xc1 } });
            await UntilTelemetry(async () => (await reopened.LoadAsync(token)).Messages.Count(m => m.Content == "metadata isolation test") >= 4);

            await Task.Delay(1100, token);
            await reopened.SendTextAsync(python.HashHex, "Ordinary authorized telemetry", token);
            await UntilTelemetry(async () =>
            {
                var snapshot = await python.Command($"snapshot {local}", "snapshot", TimeSpan.FromSeconds(10));
                return snapshot.GetProperty("messages").EnumerateArray().Any(m =>
                    m.GetProperty("sha256").GetString() == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("Ordinary authorized telemetry"))) &&
                    m.GetProperty("telemetry_included").GetBoolean() && m.GetProperty("appearance_included").GetBoolean());
            });
            await reopened.DisconnectAsync();

            async Task SendFields(Dictionary<object, object?> fields, bool invalidSignature = false) =>
                await python.Command($"telemetry-fields{(invalidSignature ? "-invalid-signature" : "")} {local} " +
                    Convert.ToBase64String(MsgPack.Pack(fields)), "telemetry-fields-queued", TimeSpan.FromSeconds(10));
            async Task ConfigurePython(string json) =>
                await python.Command("telemetry-config " + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)), "telemetry-configured", TimeSpan.FromSeconds(10));
            async Task<JsonElement> WaitPythonRecord(string hash)
            {
                var result = default(JsonElement);
                await UntilTelemetry(async () =>
                {
                    var snapshot = await python.Command($"telemetry-snapshot {local}", "telemetry-snapshot", TimeSpan.FromSeconds(10));
                    foreach (var record in snapshot.GetProperty("records").EnumerateArray())
                        if (record.GetProperty("source").GetString() == hash) { result = record.Clone(); return true; }
                    return false;
                });
                return result;
            }
            async Task<SidebandTelemetryStoredReport> WaitNativeRecord(string hash)
            {
                SidebandTelemetryStoredReport? found = null;
                await UntilTelemetry(async () => (found = (await client.GetTelemetryAsync(new(hash), token)).FirstOrDefault()) is not null);
                return found!;
            }
            async Task UntilTelemetry(Func<Task<bool>> condition)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(35));
                try { while (!await condition()) await Task.Delay(100, deadline.Token); }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
                {
                    string errors;
                    lock (failures) errors = string.Join("\n", failures.TakeLast(20));
                    throw new TimeoutException("Telemetry application observation timed out.\n" + errors + "\n" + python.Log, ex);
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
