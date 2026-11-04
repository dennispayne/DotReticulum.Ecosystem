using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Applications;
using LXMF.Tests.Interop;

namespace Sideband.Tests;

public sealed partial class SidebandLiveInteropTests
{
    [Theory(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Delivery-limit application interop requires DOTSIDEBAND_LIVE=1, DOTRETICULUM_LIVE=1 and pinned Python/RNS/LXMF/LXST/Sideband paths.")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryDeliveryLimitsRejectOversizeRecoverBothDirectionsAndPersistAcrossRestart(int topology)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(6));
        var token = deadline.Token;
        var root = Path.Combine(AppContext.BaseDirectory, "delivery-limit-live-" + Guid.NewGuid().ToString("N"));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        try
        {
            const string common = "[reticulum]\n share_instance = no\n enable_transport = ";
            var config = Path.Combine(root, "native-rns");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"), common +
                $"no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n [[Limits]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
            await using var pythonTransport = topology == 1
                ? await PyLinkNode.StartAsync("LimitTransport", tcpServerPort: port, harness: "sideband_live_node.py", extraArguments: ["--transport"])
                : null;
            var transportConfig = Path.Combine(root, "transport");
            Directory.CreateDirectory(transportConfig);
            await File.WriteAllTextAsync(Path.Combine(transportConfig, "config"), common +
                $"yes\n\n[logging]\n loglevel = 0\n\n[interfaces]\n [[Limits]]\n type = TCPServerInterface\n enabled = yes\n listen_ip = 127.0.0.1\n listen_port = {port}\n", token);
            await using var nativeTransport = topology == 2 ? ReticulumNode.Open(transportConfig) : null;
            if (nativeTransport is not null) await nativeTransport.StartAsync(token);
            await using var primary = await PyLinkNode.StartAsync("ActualLimitCore",
                tcpServerPort: topology == 0 ? port : 0, rnsdPort: topology == 0 ? 0 : port,
                harness: "sideband_application_node.py", extraArguments:
                ["--source", Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")!, "--no-propagation-retry"]);
            await using var client = new SidebandClient(new(Path.Combine(root, "data")));
            await client.ConnectAsync(config, token);
            var local = client.ConnectionInfo!.DeliveryHash;
            await primary.WaitForAnnounce(Convert.FromHexString(local), TimeSpan.FromSeconds(30));
            await client.AddContactAsync("Delivery limit primary", primary.HashHex, token);
            Assert.Equal(1000, (await PrimaryLimit("{}")).GetProperty("limit_kilobytes").GetInt32());
            var data = RandomNumberGenerator.GetBytes(1_050_000);
            var source = Path.Combine(root, "native.bin");
            await File.WriteAllBytesAsync(source, data, token);
            await client.SendFileAsync(primary.HashHex, "Native over primary default limit", source, token);
            await Until(async () => (await client.LoadAsync(token)).Messages.Any(message =>
                !message.Incoming && message.Content == "Native over primary default limit" && message.DeliveryState == "Rejected"));
            Assert.DoesNotContain((await Snapshot()).EnumerateArray(), message => message.GetProperty("incoming").GetBoolean());
            await primary.Command($"send-file {local} 1050000 rejected rejected.bin", "file_queued", TimeSpan.FromSeconds(30));
            await Until(async () => (await Snapshot()).EnumerateArray().Any(message =>
                !message.GetProperty("incoming").GetBoolean() &&
                (message.GetProperty("rejected").GetBoolean() || message.GetProperty("failed").GetBoolean())));
            Assert.DoesNotContain((await client.LoadAsync(token)).Messages, message => message.Incoming);
            Assert.Equal(128000, (await PrimaryLimit("""{"lxm_limit_1mb":false}""")).GetProperty("limit_kilobytes").GetInt32());
            await client.ConfigureDeliveryAsync(new(false), token);
            await client.SendFileAsync(primary.HashHex, "Native large policy recovered", source, token);
            var expected = Convert.ToHexStringLower(SHA256.HashData(data));
            await Until(async () => (await Snapshot()).EnumerateArray().Any(message =>
                message.GetProperty("incoming").GetBoolean() && message.TryGetProperty("files", out var files) &&
                files.EnumerateArray().Any(file => file.GetProperty("sha256").GetString() == expected)));
            var queued = await primary.Command($"send-file {local} 1050000 accepted accepted.bin", "file_queued", TimeSpan.FromSeconds(30));
            var referenceHash = queued.GetProperty("sha256").GetString();
            await Until(async () => (await client.LoadAsync(token)).Messages.Any(message =>
                message.Incoming && message.Attachments?.Any(file => file.ContentHash == referenceHash && file.Length == 1_050_000) == true));
            await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(15), token);
            await primary.Command("delete-announce " + local, "announce-deleted", TimeSpan.FromSeconds(10));
            await Task.Delay(1_100, token);
            await client.ConnectAsync(config, token);
            Assert.Equal(local, client.ConnectionInfo!.DeliveryHash);
            Assert.False(SidebandDeliveryConfiguration.FromState(await client.LoadAsync(token)).LimitIncomingToOneMegabyte);
            await Until(async () => (await primary.Command("announces", "announces", TimeSpan.FromSeconds(10)))
                .GetProperty("records").EnumerateArray().Any(item => item.GetProperty("dest").GetString() == local));
            await primary.Command($"send-file {local} 1050000 restarted restarted.bin", "file_queued", TimeSpan.FromSeconds(30));
            await Until(async () => (await client.LoadAsync(token)).Messages.Any(message =>
                message.Incoming && message.Attachments?.Any(file => file.Name == "restarted.bin") == true));
            await client.SendTextAsync(primary.HashHex, "Native healthy after limit restart", token);
            await Until(async () => (await client.LoadAsync(token)).Messages.Any(message =>
                !message.Incoming && message.Content == "Native healthy after limit restart" && message.DeliveryState == "Delivered"));
            await client.ConfigureDeliveryAsync(new(true), token);
            Assert.True(SidebandDeliveryConfiguration.FromState(await client.LoadAsync(token)).LimitIncomingToOneMegabyte);

            async Task<JsonElement> PrimaryLimit(string settings) => await primary.Command("delivery-limit " + Base64(settings),
                "delivery-limit", TimeSpan.FromSeconds(10));
            async Task<JsonElement> Snapshot() => (await primary.Command("snapshot " + local, "snapshot", TimeSpan.FromSeconds(10))).GetProperty("messages");
            async Task Until(Func<Task<bool>> condition)
            {
                var until = DateTime.UtcNow.AddSeconds(90);
                while (!await condition())
                {
                    if (DateTime.UtcNow >= until) throw new TimeoutException("Delivery-limit assertion timed out.\n" + primary.Log);
                    await Task.Delay(50, token);
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
