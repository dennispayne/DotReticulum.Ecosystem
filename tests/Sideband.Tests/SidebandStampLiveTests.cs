using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using DotReticulum.Applications;
using LXMF.Tests.Interop;

namespace Sideband.Tests;

public sealed partial class SidebandLiveInteropTests
{
    [Theory(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Stamp application interop requires DOTSIDEBAND_LIVE=1, DOTRETICULUM_LIVE=1 and pinned Python/RNS/LXMF/LXST/Sideband paths.")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryStampAdvertisementAdmissionIndependentRejectionAndRestartRecover(int topology)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        var root = Path.Combine(AppContext.BaseDirectory, "stamp-live-" + Guid.NewGuid().ToString("N"));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        try
        {
            var common = "[reticulum]\n share_instance = no\n enable_transport = ";
            var config = Path.Combine(root, "native-rns");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"), common +
                $"no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n [[Stamps]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
            await using var pythonTransport = topology == 1
                ? await PyLinkNode.StartAsync("StampTransport", tcpServerPort: port, harness: "sideband_live_node.py", extraArguments: ["--transport"])
                : null;
            var transportConfig = Path.Combine(root, "transport");
            Directory.CreateDirectory(transportConfig);
            await File.WriteAllTextAsync(Path.Combine(transportConfig, "config"), common +
                $"yes\n\n[logging]\n loglevel = 0\n\n[interfaces]\n [[Stamps]]\n type = TCPServerInterface\n enabled = yes\n listen_ip = 127.0.0.1\n listen_port = {port}\n", token);
            await using var nativeTransport = topology == 2 ? ReticulumNode.Open(transportConfig) : null;
            if (nativeTransport is not null) await nativeTransport.StartAsync(token);
            await using var primary = await PyLinkNode.StartAsync("ActualStampCore",
                tcpServerPort: topology == 0 ? port : 0, rnsdPort: topology == 0 ? 0 : port,
                harness: "sideband_application_node.py", extraArguments:
                ["--source", Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")!]);
            var store = new SidebandStateStore(Path.Combine(root, "data"));
            await store.ConfigureStampPolicyAsync(new(true, 8, true), token);
            await using var client = new SidebandClient(store);
            var errors = new ConcurrentQueue<string>();
            client.ErrorReported += errors.Enqueue;
            await client.ConnectAsync(config, token);
            var local = client.ConnectionInfo!.DeliveryHash;
            await primary.WaitForAnnounce(Convert.FromHexString(local), TimeSpan.FromSeconds(30));
            await WaitPrimaryCost(8);
            await client.AddContactAsync("Stamp primary", primary.HashHex, token);
            await Task.Delay(1_100, token);
            var policy = await primary.Command("stamp-policy " + Base64(
                """{"lxmf_require_stamps":true,"lxmf_inbound_stamp_cost":8,"lxmf_ignore_invalid_stamps":true}"""),
                "stamp-policy", TimeSpan.FromSeconds(15));
            Assert.True(policy.GetProperty("enforce").GetBoolean());
            Assert.Equal(8, policy.GetProperty("effective_cost").GetInt32());
            await Until(async () => (await client.GetAnnouncesAsync(token)).Any(item =>
                item.DestinationHash == primary.HashHex && item.StampCost == 8));
            const string outgoing = "Native reference-validated stamp";
            await client.SendTextAsync(primary.HashHex, outgoing, token);
            var hash = "";
            await Until(async () =>
            {
                var snapshot = await primary.Command("snapshot " + local, "snapshot", TimeSpan.FromSeconds(10));
                var message = snapshot.GetProperty("messages").EnumerateArray().Where(item =>
                    item.GetProperty("incoming").GetBoolean() &&
                    item.GetProperty("sha256").GetString() == Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(outgoing)))).ToArray();
                if (message.Length != 1) return false;
                hash = message[0].GetProperty("hash").GetString()!;
                return true;
            });
            var verified = await primary.Command($"inspect-stamp {local} {hash} 8", "stamp-inspected", TimeSpan.FromSeconds(30));
            Assert.True(verified.GetProperty("valid").GetBoolean());
            Assert.Equal(32, verified.GetProperty("size").GetInt32());
            await SendPrimary("Primary valid stamp");
            await Until(() => Received("Primary valid stamp"));
            await primary.Command("reset-stamp-cost " + local, "stamp-cost-reset", TimeSpan.FromSeconds(10));
            await SendPrimary("Primary stale-cost rejected");
            await Until(() => Task.FromResult(errors.Any(message => message.Contains("with invalid stamp", StringComparison.Ordinal))));
            Assert.False(await Received("Primary stale-cost rejected"));
            await Task.Delay(1_100, token);
            await client.ConfigureStampPolicyAsync(new(true, 8, false), token);
            await WaitPrimaryCost(8);
            await primary.Command("reset-stamp-cost " + local, "stamp-cost-reset", TimeSpan.FromSeconds(10));
            await SendPrimary("Primary stale-cost admitted explicitly");
            await Until(() => Received("Primary stale-cost admitted explicitly"));
            await Task.Delay(1_100, token);
            await client.ConfigureStampPolicyAsync(new(false, 12, true), token);
            await WaitPrimaryCost(null);
            await SendPrimary("Primary no-cost accepted");
            await Until(() => Received("Primary no-cost accepted"));
            await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(15), token);
            await primary.Command("delete-announce " + local, "announce-deleted", TimeSpan.FromSeconds(10));
            // Transport forwarding requires a fresh whole-second announce timestamp, not a queued earlier event.
            await Task.Delay(1_100, token);
            await client.ConnectAsync(config, token);
            Assert.Equal(local, client.ConnectionInfo!.DeliveryHash);
            Assert.Equal(new(false, 12, true), SidebandStampConfiguration.FromState(await client.LoadAsync(token)));
            await Until(async () => (await primary.Command("announces", "announces", TimeSpan.FromSeconds(10)))
                .GetProperty("records").EnumerateArray().Any(item => item.GetProperty("dest").GetString() == local));
            await SendPrimary("Primary after native restart");
            await Until(() => Received("Primary after native restart"));
            await client.SendTextAsync(primary.HashHex, "Native healthy stamp after restart", token);
            await Until(async () => (await client.LoadAsync(token)).Messages.Any(message =>
                !message.Incoming && message.Content == "Native healthy stamp after restart" && message.DeliveryState == "Delivered"));

            async Task SendPrimary(string content) => await primary.Command("send " + local + " " + Base64(content),
                "sent_queued", TimeSpan.FromSeconds(30));
            async Task<bool> Received(string content) => (await client.LoadAsync(token)).Messages.Any(message =>
                message.Incoming && message.Content == content);
            async Task WaitPrimaryCost(int? expected) => await Until(async () =>
            {
                var observed = (await primary.Command("stamp-cost " + local, "stamp-cost", TimeSpan.FromSeconds(10))).GetProperty("cost");
                return expected is null ? observed.ValueKind == JsonValueKind.Null :
                    observed.ValueKind == JsonValueKind.Number && observed.GetInt32() == expected;
            });
            async Task Until(Func<Task<bool>> condition)
            {
                var until = DateTime.UtcNow.AddSeconds(60);
                while (!await condition())
                {
                    if (DateTime.UtcNow >= until) throw new TimeoutException("Stamp application assertion timed out.\n" + string.Join('\n', errors) + "\n" + primary.Log);
                    await Task.Delay(50, token);
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
