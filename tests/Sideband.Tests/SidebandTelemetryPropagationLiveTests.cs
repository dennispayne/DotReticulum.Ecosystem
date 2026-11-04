using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LXMF.Tests.Interop;

namespace Sideband.Tests;

public sealed partial class SidebandLiveInteropTests
{
    [Theory(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Actual telemetry fallback requires DOTSIDEBAND_LIVE=1, DOTRETICULUM_LIVE=1 and pinned application references.")]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryTelemetryFallbackPolicyAndServiceRecovery(bool fallback)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(6));
        var token = stop.Token;
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var root = Path.Combine(AppContext.BaseDirectory, "telemetry-propagation-" + Guid.NewGuid().ToString("N"));
        var rns = Path.Combine(root, "rns");
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            $"[reticulum]\n enable_transport = no\n share_instance = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n [[Telemetry]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
        try
        {
            await using var propagation = await PyLinkNode.StartAsync("TelemetryPropagation", tcpServerPort: port,
                harness: "sideband_live_node.py", extraArguments: ["--transport"]);
            await using var python = await PyLinkNode.StartAsync("ActualTelemetryFallback", rnsdPort: port,
                harness: "sideband_application_node.py",
                extraArguments: ["--source", Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")!]);
            var ready = await propagation.Command("enable-pn", "pn-ready", TimeSpan.FromSeconds(30));
            var pn = ready.GetProperty("dest").GetString()!;
            var failures = new ConcurrentQueue<string>();
            await using var client = new SidebandClient(new(Path.Combine(root, "data")));
            client.ErrorReported += failures.Enqueue;
            await client.ConfigureTelemetryAsync(new() { Enabled = true, InformationEnabled = true, Information = "fallback probe", TryPropagationOnFail = fallback }, token);
            await client.ConnectAsync(rns, token);
            var local = client.ConnectionInfo!.DeliveryHash;
            await python.WaitForAnnounce(Convert.FromHexString(local), TimeSpan.FromSeconds(30));
            await python.Command("announce", "announced", TimeSpan.FromSeconds(30));
            await client.AddContactAsync("Primary", python.HashHex, token);
            await client.SetConversationTelemetryAsync(python.HashHex, true, true, token);
            await client.ConfigurePropagationAsync(pn, false, token);
            await python.Command("use-pn " + pn, "pn-selected", TimeSpan.FromSeconds(10));
            await python.Command("delivery-links off", "delivery-links", TimeSpan.FromSeconds(10));
            await python.Command("delivery-packets off", "delivery-packets", TimeSpan.FromSeconds(10));
            Assert.True((await client.SendLatestTelemetryAsync(python.HashHex, token)).Queued);
            await Wait(async () => fallback
                ? (await propagation.Command("pn-count", "pn-count", TimeSpan.FromSeconds(10))).GetProperty("count").GetInt32() > 0
                : failures.Any(e => e.Contains("ended as Failed", StringComparison.Ordinal)));
            // Propagation storage is not recipient delivery and must not advance the delivery-success cursor.
            Assert.Equal(0, (await client.LoadAsync(token)).TelemetryPeers!.Single(p => p.DeliveryHash == python.HashHex).LastSendSuccessTimebase);
            if (fallback)
            {
                await python.Command("retrieve", "retrieved", TimeSpan.FromSeconds(90));
                await Wait(async () => (await python.Command($"telemetry-snapshot {local}", "telemetry-snapshot",
                    TimeSpan.FromSeconds(10))).GetProperty("records").EnumerateArray().Any(r => r.GetProperty("source").GetString() == local));
            }
            else
                Assert.Equal(0, (await propagation.Command("pn-count", "pn-count", TimeSpan.FromSeconds(10))).GetProperty("count").GetInt32());
            await python.Command("delivery-links on", "delivery-links", TimeSpan.FromSeconds(10));
            await python.Command("delivery-packets on", "delivery-packets", TimeSpan.FromSeconds(10));
            await Task.Delay(1100, token);
            Assert.True((await client.SendLatestTelemetryAsync(python.HashHex, token)).Queued);
            await Wait(async () => (await client.LoadAsync(token)).TelemetryPeers!.Single(p => p.DeliveryHash == python.HashHex).LastSendSuccessTimebase > 0);
            Assert.Empty((await client.LoadAsync(token)).Messages);
            if (fallback)
            {
                var json = """{"telemetry_enabled":true,"telemetry_s_information":true,"telemetry_s_information_text":"propagated request response"}""";
                await python.Command("telemetry-config " + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)),
                    "telemetry-configured", TimeSpan.FromSeconds(10));
                await python.Command($"telemetry-permission {local} yes yes", "telemetry-permission", TimeSpan.FromSeconds(10));
                await client.ConfigureTelemetryAsync((await client.LoadAsync(token)).Telemetry with { UsePropagationOnly = true }, token);
                var before = (await propagation.Command("pn-count", "pn-count", TimeSpan.FromSeconds(10))).GetProperty("count").GetInt32();
                Assert.True((await client.RequestTelemetryAsync(python.HashHex, token)).Queued);
                await Wait(async () => (await propagation.Command("pn-count", "pn-count", TimeSpan.FromSeconds(10))).GetProperty("count").GetInt32() > before);
                await python.Command("retrieve", "retrieved", TimeSpan.FromSeconds(90));
                await Wait(async () => (await client.GetTelemetryAsync(new(python.HashHex), token)).Count > 0);
                Assert.Equal(0, (await client.LoadAsync(token)).TelemetryPeers!.Single(p => p.DeliveryHash == python.HashHex).LastRequestSuccessTimebase);
            }
            await client.DisconnectAsync();

            async Task Wait(Func<Task<bool>> condition)
            {
                while (!await condition())
                {
                    if (token.IsCancellationRequested)
                        throw new TimeoutException("Telemetry fallback did not complete: " + string.Join("\n", failures.TakeLast(20)));
                    await Task.Delay(250, token);
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
