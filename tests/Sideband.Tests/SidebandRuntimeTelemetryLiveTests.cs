using System.Net;
using System.Net.Sockets;
using System.Text;
using LXMF.Tests.Interop;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband.Tests;

public sealed partial class SidebandLiveInteropTests
{
    [Theory(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Runtime application telemetry requires the pinned Sideband/RNS/LXMF environment and both live opt-ins.")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryRuntimeTelemetryBothDirectionsRenderingMigrationAndRestart(bool transportHop, bool includeTables)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(3));
        var token = stop.Token;
        var source = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")!;
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var root = Path.Combine(AppContext.BaseDirectory, "runtime-live-" + Guid.NewGuid().ToString("N"));
        var rns = Path.Combine(root, "rns");
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"), $"""
            [reticulum]
            enable_transport=no
            share_instance=no
            [logging]
            loglevel=0
            [interfaces]
            [[RuntimePrimary]]
            type=TCPClientInterface
            enabled=yes
            target_host=127.0.0.1
            target_port={port}
            """, token);
        try
        {
            await using var bridge = transportHop ? await PyLinkNode.StartAsync("RuntimeTransport", tcpServerPort: port,
                harness: "sideband_live_node.py", extraArguments: ["--transport"]) : null;
            await using var python = await PyLinkNode.StartAsync("RuntimePrimary", tcpServerPort: transportHop ? 0 : port,
                rnsdPort: transportHop ? port : 0, harness: "sideband_application_node.py", extraArguments: ["--source", source]);
            var data = Path.Combine(root, "data");
            await using var client = new SidebandClient(new(data));
            await client.ConfigureTelemetryAsync(new()
            {
                Enabled = true, RnsTransportEnabled = true, RnsTransportIncludeTables = includeTables,
                AllowRequestsFromTrusted = true
            }, token);
            await client.ConnectAsync(rns, token);
            var local = client.ConnectionInfo!.DeliveryHash;
            await python.WaitForAnnounce(Convert.FromHexString(local), TimeSpan.FromSeconds(30));
            await python.Command("announce", "announced", TimeSpan.FromSeconds(30));
            await client.AddContactAsync("Actual primary runtime", python.HashHex, token);
            await client.SetConversationTrustAsync(python.HashHex, true, token);
            await client.SetConversationTelemetryAsync(python.HashHex, true, true, token);
            await python.Command($"trust {local} yes", "trust", TimeSpan.FromSeconds(10));
            await python.Command($"telemetry-permission {local} yes yes", "telemetry-permission", TimeSpan.FromSeconds(10));
            var settings = Convert.ToBase64String(Encoding.UTF8.GetBytes("""
                {"telemetry_enabled":true,"telemetry_s_rns_transport":true,"telemetry_allow_requests_from_trusted":true}
                """));
            await python.Command("telemetry-config " + settings, "telemetry-configured", TimeSpan.FromSeconds(15));
            await python.Command("telemetry-runtime-observe " + local, "telemetry-runtime-observing", TimeSpan.FromSeconds(10));
            var observed = await python.Command("telemetry-runtime-snapshot", "telemetry-runtime-snapshot", TimeSpan.FromSeconds(10));
            var primarySensor = SidebandTelemetryCodec.FromWireValue((byte)S.RnsTransport,
                SidebandTelemetryValue.Decode(Convert.FromBase64String(observed.GetProperty("packed").GetString()!)));
            Assert.NotEqual(SidebandTelemetryStatus.Malformed, primarySensor.Status);
            Assert.Equal(observed.GetProperty("transport_identity").GetString(),
                Convert.ToHexStringLower(primarySensor.Data.Get("transport_identity")!.GetBinary()!));
            Assert.True(primarySensor.Data.Get("ifstats")!.IsNil);
            var own = await client.RefreshLocalTelemetryAsync(token);
            Assert.True((await client.SendLatestTelemetryAsync(python.HashHex, token)).Queued);
            byte[]? received = null;
            await UntilRuntime(async () =>
            {
                var snapshot = await python.Command("telemetry-snapshot " + local, "telemetry-snapshot", TimeSpan.FromSeconds(10));
                var match = snapshot.GetProperty("records").EnumerateArray().FirstOrDefault(record =>
                    record.GetProperty("source").GetString() == local);
                if (match.ValueKind == System.Text.Json.JsonValueKind.Undefined) return false;
                received = Convert.FromBase64String(match.GetProperty("packed").GetString()!);
                return SidebandTelemetryCodec.Decode(received).Get(S.RnsTransport) is not null;
            });
            var receivedSensor = SidebandTelemetryCodec.Decode(received!).Get(S.RnsTransport)!;
            var rawReceipt = await python.Command("telemetry-runtime-received", "telemetry-runtime-received", TimeSpan.FromSeconds(10));
            var rawTelemetry = Convert.FromBase64String(rawReceipt.GetProperty("packed").GetString()!);
            Assert.Equal(own!.Get(S.RnsTransport)!.WireValue.Encode(),
                SidebandTelemetryCodec.Decode(rawTelemetry).Get(S.RnsTransport)!.WireValue.Encode());
            var rendered = await python.Command("telemetry-runtime-render " + Convert.ToBase64String(rawTelemetry),
                "telemetry-runtime-render", TimeSpan.FromSeconds(10));
            Assert.Equal(Convert.FromBase64String(rendered.GetProperty("repacked").GetString()!), receivedSensor.WireValue.Encode());
            Assert.Equal(Convert.ToHexStringLower(receivedSensor.Data.Get("transport_identity")!.GetBinary()!),
                rendered.GetProperty("transport_identity").GetString());
            Assert.Equal(receivedSensor.Data.Get("interface_count")!.Integer, rendered.GetProperty("interface_count").GetInt64());
            if (includeTables)
                Assert.Contains(rendered.GetProperty("interface_names").EnumerateArray(), entry => entry.GetString() == "RuntimePrimary");
            else Assert.Empty(rendered.GetProperty("interface_names").EnumerateArray());
            await python.Command("telemetry-send " + local, "telemetry-queued", TimeSpan.FromSeconds(10));
            await UntilRuntime(async () => (await client.GetTelemetryAsync(new(python.HashHex), token))
                .Any(report => report.Report.Get(S.RnsTransport) is not null));
            var saved = (await client.GetTelemetryAsync(new(python.HashHex), token)).First(report => report.Report.Get(S.RnsTransport) is not null);
            Assert.Equal(primarySensor.WireValue.Encode(), saved.Report.Get(S.RnsTransport)!.WireValue.Encode());
            var profile = Path.Combine(root, "primary-profile");
            await python.Command("telemetry-profile-snapshot " + Convert.ToBase64String(Encoding.UTF8.GetBytes(profile)),
                "telemetry-profile-snapshotted", TimeSpan.FromSeconds(10));
            var importedPath = Path.Combine(root, "imported");
            await SidebandProfileImporter.ImportAsync(profile, importedPath, token);
            Assert.True((await new SidebandStateStore(importedPath).LoadAsync(cancellationToken: token)).Telemetry.RnsTransportEnabled);
            await client.DisconnectAsync();
            await client.DisposeAsync();
            await using var reopened = new SidebandClient(new(data));
            Assert.True((await reopened.LoadAsync(token)).Telemetry.RnsTransportEnabled);
            Assert.Equal(includeTables, (await reopened.LoadAsync(token)).Telemetry.RnsTransportIncludeTables);
            Assert.Equal(saved.GetPackedTelemetry(), (await reopened.GetTelemetryAsync(new(python.HashHex), token)).First().GetPackedTelemetry());

            async Task UntilRuntime(Func<Task<bool>> condition)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                while (!await condition()) await Task.Delay(100, deadline.Token);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
