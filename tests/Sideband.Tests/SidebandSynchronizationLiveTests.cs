using System.Net;
using System.Net.Sockets;
using System.Text;
using LXMF.Tests.Interop;

namespace Sideband.Tests;

public sealed partial class SidebandLiveInteropTests
{
    [Fact(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Manual fetch interoperability requires DOTSIDEBAND_LIVE=1, DOTRETICULUM_LIVE=1 and pinned actual primary paths.")]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryLimitedRetrievalBothDirectionsAndRestartPreserveRemainingInbox()
    {
        var token = TestContext.Current.CancellationToken;
        var source = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")
            ?? throw new InvalidOperationException("Select the pinned Sideband source.");
        int port;
        using (var socket = new TcpListener(IPAddress.Loopback, 0))
        {
            socket.Start();
            port = ((IPEndPoint)socket.LocalEndpoint).Port;
        }
        var root = Path.Combine(AppContext.BaseDirectory, "sync-limit-live-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            $"[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 4\n\n[interfaces]\n [[PythonPropagation]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
        try
        {
            await using var propagation = await PyLinkNode.StartAsync("SyncLimitPropagation", tcpServerPort: port,
                harness: "sideband_live_node.py", extraArguments: ["--transport"]);
            await using var python = await PyLinkNode.StartAsync("SyncLimitApplication", rnsdPort: port,
                harness: "sideband_application_node.py", extraArguments: ["--source", source]);
            await using var client = new SidebandClient(new(Path.Combine(root, "data")));
            await client.ConnectAsync(config, token);
            var identity = client.ConnectionInfo!.DeliveryHash;
            await python.WaitForAnnounce(Convert.FromHexString(identity), TimeSpan.FromSeconds(30));
            await python.Command("announce", "announced", TimeSpan.FromSeconds(30));
            var pn = (await propagation.Command("enable-pn", "pn-ready", TimeSpan.FromSeconds(30))).GetProperty("dest").GetString()!;
            await client.ConfigurePropagationAsync(pn, false, token);
            await client.ConfigureSynchronizationAsync(new(true, 2), token);
            await client.ConfigurePeriodicSynchronizationAsync(false, cancellationToken: token);
            await client.AddContactAsync("Primary sync", python.HashHex, token);
            await python.Command("use-pn " + pn, "pn-selected", TimeSpan.FromSeconds(10));
            for (var index = 0; index < 5; index++)
                await python.Command("send-propagated " + identity + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes("limited native " + index)),
                    "sent_queued", TimeSpan.FromSeconds(30));
            await WaitForPropagationCountAsync(propagation, 5, token);
            Assert.Equal(2, await client.SynchronizeConfiguredAsync(token));
            await NativeCount(2);
            var state = await client.LoadAsync(token);
            Assert.NotNull(state.LastSynchronization);
            Assert.False(state.SynchronizationRetrying);
            await client.DisconnectAsync();
            await client.ConnectAsync(config, token);
            Assert.Equal(identity, client.ConnectionInfo!.DeliveryHash);
            Assert.Equal(new(true, 2), SidebandSynchronizationConfiguration.FromState(await client.LoadAsync(token)));
            Assert.Equal(2, await client.SynchronizeConfiguredAsync(token));
            await NativeCount(4);
            Assert.Equal(1, await client.SynchronizeAsync(cancellationToken: token));
            await NativeCount(5);
            Assert.Equal(0, await client.SynchronizeConfiguredAsync(token));

            await client.ConfigurePropagationAsync(pn, true, token);
            for (var index = 0; index < 5; index++)
                await client.SendTextAsync(python.HashHex, "limited primary " + index, token);
            await WaitForPropagationCountAsync(propagation, 5, token);
            await python.Command("retrieve 2", "retrieved", TimeSpan.FromSeconds(90));
            await PrimaryCount(2);
            await python.Command("retrieve", "retrieved", TimeSpan.FromSeconds(90));
            await PrimaryCount(5);
            await client.ConfigureSynchronizationAsync(new(false, 2), token);
            Assert.Equal(0, await client.SynchronizeConfiguredAsync(token));
            Assert.Equal(0, (await client.LoadAsync(token)).Messages.Count(message => message.Incoming && message.DeliveryState != "Received"));

            async Task NativeCount(int count)
            {
                var until = DateTime.UtcNow.AddSeconds(30);
                while ((await client.GetMessagesAsync(python.HashHex, token)).Count(message => message.Incoming) != count)
                {
                    if (DateTime.UtcNow >= until) throw new TimeoutException($"Native durable inbox did not contain exactly {count} messages.");
                    await Task.Delay(50, token);
                }
            }

            async Task PrimaryCount(int count)
            {
                var snapshot = await python.Command("snapshot " + identity, "snapshot", TimeSpan.FromSeconds(10));
                Assert.Equal(count, snapshot.GetProperty("messages").EnumerateArray().Count(message => message.GetProperty("incoming").GetBoolean()));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
