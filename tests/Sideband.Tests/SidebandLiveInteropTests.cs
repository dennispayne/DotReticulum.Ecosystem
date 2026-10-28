using System.Net;
using System.Net.Sockets;
using System.Text;
using LXMF.Tests.Interop;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandLiveInteropTests
{
    [Fact(SkipWhen = nameof(LiveInteropIsDisabled), Skip = "Live Sideband/Python interop is opt-in: set DOTRETICULUM_LIVE=1, DOTRETICULUM_PYTHON, and DOTRETICULUM_UPSTREAM.")]
    [Trait("Category", "Live")]
    public async Task DesktopClientSendsAndReceivesAgainstPinnedPythonLxmfNode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-live-" + Guid.NewGuid().ToString("N"));
        var rnsDirectory = Path.Combine(root, "rns");
        var dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(rnsDirectory);
        await File.WriteAllTextAsync(Path.Combine(rnsDirectory, "config"),
            $"[reticulum]\n  enable_transport = No\n  share_instance = No\n  instance_name = sideband_live_test\n\n[logging]\n  loglevel = 4\n\n[interfaces]\n  [[PythonReference]]\n    type = TCPClientInterface\n    enabled = yes\n    target_host = 127.0.0.1\n    target_port = {port}\n", cancellationToken);

        try
        {
            await using var reference = await PyLinkNode.StartAsync("Sideband", tcpServerPort: port, harness: "sideband_live_node.py");
            await using var client = new SidebandClient(new SidebandStateStore(dataDirectory));
            await client.ConnectAsync(rnsDirectory, cancellationToken);
            var localHash = (await client.LoadAsync(cancellationToken)).OwnDeliveryHash!;
            await reference.WaitForAnnounce(Convert.FromHexString(localHash), TimeSpan.FromSeconds(60));

            var pythonHash = reference.HashHex;
            await reference.Command("announce", "announced", TimeSpan.FromSeconds(30));
            await reference.Command($"send {localHash} direct 16 sideband sideband-note", "sent_queued", TimeSpan.FromSeconds(60));
            var incoming = await WaitForMessageAsync(client, pythonHash, cancellationToken);
            Assert.True(incoming.Incoming);
            Assert.Equal("sideband-note", incoming.Title);
            Assert.Equal("Received", incoming.DeliveryState);

            await client.SendTextAsync(pythonHash, "message from native Sideband", cancellationToken);
            var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("message from native Sideband")));
            var delivered = await reference.NextAsync("received", 0, TimeSpan.FromSeconds(90),
                item => item.GetProperty("sha256").GetString() == expected);
            Assert.Equal(expected, delivered.GetProperty("sha256").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<SidebandChatMessage> WaitForMessageAsync(SidebandClient client, string peerHash, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var messages = await client.GetMessagesAsync(peerHash, cancellationToken);
            if (messages.Count > 0) return messages[0];
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException("Sideband did not persist a message delivered by the Python reference node.");
    }

    public static bool LiveInteropIsDisabled => Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1";
}
