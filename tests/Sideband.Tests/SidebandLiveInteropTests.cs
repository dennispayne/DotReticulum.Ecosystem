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

    [Fact(SkipWhen = nameof(LiveInteropIsDisabled), Skip = "Live Sideband/Python propagation is opt-in: set DOTRETICULUM_LIVE=1 and pinned Python reference paths.")]
    [Trait("Category", "Live")]
    public async Task PropagationFetchPersistsPythonMessagesAndSettingsAcrossClientRestart()
    {
        var token = TestContext.Current.CancellationToken;
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-propagation-" + Guid.NewGuid().ToString("N"));
        var rns = Path.Combine(root, "rns");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            $"[reticulum]\n  enable_transport = No\n  share_instance = No\n\n[logging]\n  loglevel = 4\n\n[interfaces]\n  [[PythonPropagation]]\n    type = TCPClientInterface\n    enabled = yes\n    target_host = 127.0.0.1\n    target_port = {port}\n", token);
        try
        {
            await using var propagation = await PyLinkNode.StartAsync("SidebandPropagation", tcpServerPort: port, harness: "sideband_live_node.py",
                extraArguments: ["--transport"]);
            await using var python = await PyLinkNode.StartAsync("SidebandSender", rnsdPort: port, harness: "sideband_live_node.py");
            string pn;
            string hash;
            await using (var client = new SidebandClient(new SidebandStateStore(data)))
            {
                await client.ConnectAsync(rns, token);
                hash = (await client.LoadAsync(token)).OwnDeliveryHash!;
                await python.WaitForAnnounce(Convert.FromHexString(hash), TimeSpan.FromSeconds(30));
                var ready = await propagation.Command("enable-pn", "pn-ready", TimeSpan.FromSeconds(30));
                pn = ready.GetProperty("dest").GetString()!;
                await python.Command("use-pn " + pn, "pn-selected", TimeSpan.FromSeconds(10));
                var discoveryDeadline = DateTime.UtcNow.AddSeconds(30);
                while ((await client.LoadAsync(token)).LastPropagationNode != pn && DateTime.UtcNow < discoveryDeadline)
                    await Task.Delay(20, token);
                var discovered = await client.LoadAsync(token);
                Assert.Null(discovered.PropagationNode);
                Assert.Equal(pn, discovered.LastPropagationNode);
                await client.ConfigurePropagationAsync(pn, true, token);
                await python.Command($"send {hash} propagated 64 sideband propagated-note", "sent_queued", TimeSpan.FromSeconds(30));
                var deadline = DateTime.UtcNow.AddSeconds(90);
                var count = 0;
                while (count == 0 && DateTime.UtcNow < deadline)
                {
                    var stored = await propagation.Command("pn-count", "pn-count", TimeSpan.FromSeconds(10));
                    count = stored.GetProperty("count").GetInt32();
                    if (count == 0) await Task.Delay(100, token);
                }
                Assert.Equal(1, count);
                Assert.Empty(await client.GetMessagesAsync(python.HashHex, token));
                Assert.Equal(1, await client.SynchronizeAsync(cancellationToken: token));
                var received = await WaitForMessageAsync(client, python.HashHex, token);
                Assert.Equal("propagated-note", received.Title);
                Assert.Equal("Received", received.DeliveryState);
                Assert.Equal(LXMF.LxmPropagationTransferState.Complete, client.SyncState);
                Assert.Equal(0, await client.SynchronizeAsync(cancellationToken: token));
                await python.Command("announce", "announced", TimeSpan.FromSeconds(30));
                await client.SendTextAsync(python.HashHex, "propagated native reply", token);
                var deposited = false;
                deadline = DateTime.UtcNow.AddSeconds(90);
                while (!deposited && DateTime.UtcNow < deadline)
                {
                    var stored = await propagation.Command("pn-count", "pn-count", TimeSpan.FromSeconds(10));
                    deposited = stored.GetProperty("count").GetInt32() == 1;
                    if (!deposited) await Task.Delay(100, token);
                }
                Assert.True(deposited, "The native Sideband message was not stored at the Python propagation node.");
                await python.Command("retrieve", "retrieved", TimeSpan.FromSeconds(90));
                var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("propagated native reply")));
                await python.NextAsync("received", 0, TimeSpan.FromSeconds(60), item => item.GetProperty("sha256").GetString() == expected);
                using var cancelled = new CancellationTokenSource();
                await cancelled.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SynchronizeAsync(cancellationToken: cancelled.Token));
                Assert.Equal(0, await client.SynchronizeAsync(cancellationToken: token));
                await client.ConfigurePropagationAsync(null, true, token);
            }
            await using var restarted = new SidebandClient(new SidebandStateStore(data));
            await restarted.ConnectAsync(rns, token);
            var state = await restarted.LoadAsync(token);
            Assert.Equal(hash, state.OwnDeliveryHash);
            Assert.Null(state.PropagationNode);
            Assert.Equal(pn, state.LastPropagationNode);
            Assert.True(state.PropagationByDefault);
            Assert.Equal("propagated-note", Assert.Single(await restarted.GetMessagesAsync(python.HashHex, token), message => message.Incoming).Title);
            Assert.Equal(0, await restarted.SynchronizeAsync(cancellationToken: token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact(SkipWhen = nameof(LiveInteropIsDisabled), Skip = "File-field interoperability requires DOTRETICULUM_LIVE=1 and the pinned Python reference.")]
    [Trait("Category", "Live")]
    public async Task FilesExchangeWithActualPythonAndRemainExportableAfterRestart()
    {
        var token = TestContext.Current.CancellationToken;
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-files-live-" + Guid.NewGuid().ToString("N"));
        var rns = Path.Combine(root, "rns");
        var dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            $"[reticulum]\n  enable_transport = no\n  share_instance = no\n\n[logging]\n  loglevel = 4\n\n[interfaces]\n  [[PythonFiles]]\n    type = TCPClientInterface\n    enabled = yes\n    target_host = 127.0.0.1\n    target_port = {port}\n", token);
        try
        {
            await using var python = await PyLinkNode.StartAsync("SidebandAttachments", tcpServerPort: port, harness: "sideband_attachment_node.py");
            Guid incomingId;
            await using (var client = new SidebandClient(new SidebandStateStore(dataDirectory)))
            {
                var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
                client.ErrorReported += errors.Enqueue;
                await client.ConnectAsync(rns, token);
                var local = (await client.LoadAsync(token)).OwnDeliveryHash!;
                await python.WaitForAnnounce(Convert.FromHexString(local), TimeSpan.FromSeconds(30));
                await python.Command("announce", "announced", TimeSpan.FromSeconds(30));
                var source = Path.Combine(root, "native.bin");
                var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
                await File.WriteAllBytesAsync(source, bytes, token);
                await client.SendFileAsync(python.HashHex, "native file", source, token);
                var observed = await python.NextAsync("received", 0, TimeSpan.FromSeconds(90));
                var file = observed.GetProperty("attachments")[0];
                Assert.Equal("native.bin", file.GetProperty("name").GetString());
                Assert.Equal(bytes.Length, file.GetProperty("size").GetInt32());
                Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), file.GetProperty("sha256").GetString());
                var quota = Path.Combine(dataDirectory, "attachments", "quota-reservation");
                await using (var reservation = File.Create(quota)) reservation.SetLength(128L * 1024 * 1024);
                var small = Path.Combine(root, "quota.bin");
                await File.WriteAllBytesAsync(small, [8, 7, 6], token);
                await Assert.ThrowsAsync<IOException>(() => client.SendFileAsync(python.HashHex, "quota", small, token));
                File.Delete(quota);
                await python.Command($"send-file {local} 3 x ../escape.bin", "sent_queued", TimeSpan.FromSeconds(30));
                var rejectionDeadline = DateTime.UtcNow.AddSeconds(60);
                while (!errors.Any(error => error.Contains("safe plain filename", StringComparison.Ordinal)) && DateTime.UtcNow < rejectionDeadline)
                    await Task.Delay(100, token);
                Assert.Contains(errors, error => error.Contains("safe plain filename", StringComparison.Ordinal));
                var sent = await python.Command($"send-file {local} 2097152 abc reference.bin", "sent_queued", TimeSpan.FromSeconds(30));
                var deadline = DateTime.UtcNow.AddSeconds(90);
                SidebandChatMessage? incoming = null;
                while (incoming is null && DateTime.UtcNow < deadline)
                {
                    incoming = (await client.GetMessagesAsync(python.HashHex, token)).FirstOrDefault(message => message.Incoming);
                    if (incoming is null) await Task.Delay(100, token);
                }
                Assert.NotNull(incoming);
                incomingId = incoming.Id;
                var attachment = Assert.Single(incoming.Attachments!);
                Assert.Equal("reference.bin", attachment.Name);
                Assert.Equal(sent.GetProperty("sha256").GetString(), attachment.ContentHash);
            }
            await using var restarted = new SidebandClient(new SidebandStateStore(dataDirectory));
            var output = Path.Combine(root, "export.bin");
            await restarted.ExportAttachmentAsync(python.HashHex, incomingId, 0, output, token);
            var exported = await File.ReadAllBytesAsync(output, token);
            Assert.Equal(2 * 1024 * 1024, exported.Length);
            Assert.Equal(Encoding.ASCII.GetBytes("abcabc"), exported[..6]);
            Assert.Empty(Directory.GetFiles(dataDirectory, "*.tmp", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
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
