using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LXMF.Tests.Interop;

namespace Sideband.Tests;

public sealed partial class SidebandLiveInteropTests
{
    [Theory(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Startup timing requires actual primary Sideband and opt-in pinned live references.")]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryStartupDelayDisableManualAnnouncementAndRestartRetainIdentity(bool enabled, bool disablePending)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")
            ?? throw new InvalidOperationException("Select the pinned Sideband source.");
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var root = Path.Combine(AppContext.BaseDirectory, "startup-live-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            $"[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 4\n\n[interfaces]\n [[Primary]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
        try
        {
            await using var python = await PyLinkNode.StartAsync("StartupPrimary", tcpServerPort: port,
                harness: "sideband_application_node.py",
                extraArguments: ["--source", source, "--startup-announce", enabled ? "on" : "off"]);
            await using var client = new SidebandClient(new(Path.Combine(root, "data")));
            await client.ConfigureAnnouncementAsync(new(enabled), token);
            var elapsed = Stopwatch.StartNew();
            await client.ConnectAsync(config, token);
            var identity = client.ConnectionInfo!.DeliveryHash;
            if (disablePending) await client.ConfigureAnnouncementAsync(new(false), token);
            if (enabled && !disablePending)
            {
                await python.WaitForAnnounce(Convert.FromHexString(identity), TimeSpan.FromSeconds(30));
                Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(8), "Native startup announcement arrived before the reference eight-second delay.");
                var until = DateTime.UtcNow.AddSeconds(15);
                while (true)
                {
                    var status = await python.Command("startup-status", "startup-status", TimeSpan.FromSeconds(10));
                    if (status.GetProperty("last_announce").GetDouble() != 0)
                    {
                        Assert.True(status.GetProperty("startup_delay").GetDouble() >= 8);
                        break;
                    }
                    if (DateTime.UtcNow >= until) throw new TimeoutException("The actual primary did not perform its delayed startup announcement.");
                    await Task.Delay(50, token);
                }
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(9), token);
                var status = await python.Command("startup-status", "startup-status", TimeSpan.FromSeconds(10));
                if (enabled) Assert.True(status.GetProperty("startup_delay").GetDouble() >= 8);
                else Assert.Equal(0, status.GetProperty("last_announce").GetDouble());
                var observations = await python.Command("announces", "announces", TimeSpan.FromSeconds(10));
                Assert.DoesNotContain(observations.GetProperty("records").EnumerateArray(),
                    record => record.GetProperty("dest").GetString() == identity);
                await client.AnnounceAsync(token);
                await python.WaitForAnnounce(Convert.FromHexString(identity), TimeSpan.FromSeconds(30));
                await python.Command("announce", "announced", TimeSpan.FromSeconds(10));
                Assert.True((await python.Command("startup-status", "startup-status", TimeSpan.FromSeconds(10)))
                    .GetProperty("last_announce").GetDouble() > 0);
            }
            await client.DisconnectAsync();
            await client.ConnectAsync(config, token);
            Assert.Equal(identity, client.ConnectionInfo!.DeliveryHash);
            Assert.Equal(enabled && !disablePending, SidebandAnnounceConfiguration.FromState(await client.LoadAsync(token)).AnnounceOnStartup);
            await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
