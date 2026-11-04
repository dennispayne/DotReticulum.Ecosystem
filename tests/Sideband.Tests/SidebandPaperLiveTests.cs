using System.Net;
using System.Net.Sockets;
using System.Text;
using LXMF.Tests.Interop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandPaperLiveTests
{
    public static bool IsDisabled =>
        Environment.GetEnvironmentVariable("DOTSIDEBAND_LIVE") != "1" ||
        Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1" ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON"));

    [Theory(SkipWhen = nameof(IsDisabled), Skip = "Paper application interop requires DOTSIDEBAND_LIVE=1, DOTRETICULUM_LIVE=1 and pinned Python/RNS/Sideband paths.")]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryPaperCreationAndIngestionInteroperateBothDirections(bool trusted)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(2));
        var token = stop.Token;
        var root = Path.Combine(AppContext.BaseDirectory, "paper-live-" + Guid.NewGuid().ToString("N"));
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        PyLinkNode? python = null;
        try
        {
            var config = Path.Combine(root, "rns");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"),
                $"[reticulum]\n share_instance = no\n enable_transport = no\n\n[interfaces]\n [[Paper]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
            python = await PyLinkNode.StartAsync("PrimaryPaper", tcpServerPort: port, harness: "sideband_paper_node.py",
                extraArguments: ["--source", Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")!,
                    "--profile", Path.Combine(root, "primary-profile")]);
            var store = new SidebandStateStore(Path.Combine(root, "native"));
            await using var client = new SidebandClient(store);
            await client.ConnectAsync(config, token);
            var local = client.ConnectionInfo!.DeliveryHash;
            await python.WaitForAnnounce(Convert.FromHexString(local), TimeSpan.FromSeconds(30));
            await python.Command("announce", "announced", TimeSpan.FromSeconds(10));
            await client.AddContactAsync("Primary", python.HashHex, token);
            await client.SetConversationTrustAsync(python.HashHex, trusted, token);
            await client.ConfigureTelemetryAsync(new() { Enabled = true, InformationEnabled = true, Information = "Paper authorized metadata" }, token);
            await client.SetConversationTelemetryAsync(python.HashHex, true, true, token);
            var native = await client.CreatePaperMessageAsync(python.HashHex, "Native application paper ✓", token);
            var before = await python.Command("snapshot " + local, "snapshot", TimeSpan.FromSeconds(10));
            Assert.Empty(before.GetProperty("messages").EnumerateArray());
            var result = await python.Command("ingest " + Encode(native.PaperUri!), "ingest", TimeSpan.FromSeconds(10));
            Assert.Contains("added to your conversation list", result.GetProperty("response").GetString());
            var duplicate = await python.Command("ingest " + Encode(native.PaperUri!), "ingest", TimeSpan.FromSeconds(10));
            Assert.Contains("already been processed", duplicate.GetProperty("response").GetString());
            var snapshot = await python.Command("snapshot " + local, "snapshot", TimeSpan.FromSeconds(10));
            var received = Assert.Single(snapshot.GetProperty("messages").EnumerateArray());
            Assert.Equal(native.Content, received.GetProperty("content").GetString());
            Assert.Equal("", received.GetProperty("title").GetString());
            Assert.True(received.GetProperty("signature_valid").GetBoolean());
            Assert.True(received.GetProperty("telemetry_field").GetBoolean());
            Assert.False(received.GetProperty("ticket_field").GetBoolean());
            var primary = await python.Command($"paper {local} {Encode("Primary application paper ✓")} {(trusted ? "yes" : "no")}",
                "paper", TimeSpan.FromSeconds(10));
            Assert.True(primary.GetProperty("accepted").GetBoolean());
            var primaryMessage = primary.GetProperty("message");
            var uri = primaryMessage.GetProperty("uri").GetString()!;
            Assert.False(primaryMessage.GetProperty("ticket_field").GetBoolean());
            var nonlocal = await python.Command("ingest " + Encode(uri), "ingest", TimeSpan.FromSeconds(10));
            Assert.Contains("not addressed", nonlocal.GetProperty("response").GetString());
            Assert.Equal(SidebandPaperIngestResult.LocalDelivery, await client.IngestPaperUriAsync(uri, token));
            Assert.Equal(SidebandPaperIngestResult.Duplicate, await client.IngestPaperUriAsync(uri, token));
            await client.DisconnectAsync();
            var imported = Assert.Single((await client.LoadAsync(token)).Messages, message => message.Incoming);
            Assert.Equal("Primary application paper ✓", imported.Content);
            await client.DisposeAsync();
            await using var reopened = new SidebandClient(new(store.DataDirectory));
            Assert.Equal(native.PaperUri, await reopened.GetPaperMessageUriAsync(native.Id, token));
            await reopened.ConnectAsync(config, token);
            Assert.Equal(SidebandPaperIngestResult.Duplicate, await reopened.IngestPaperUriAsync(uri, token));
            var invalid = await python.Command("ingest " + Encode("lxm://!"), "ingest", TimeSpan.FromSeconds(10));
            Assert.Contains("no decodable", invalid.GetProperty("response").GetString());
            var empty = await python.Command($"paper {local} - no", "paper", TimeSpan.FromSeconds(10));
            Assert.False(empty.GetProperty("accepted").GetBoolean());
            var oversize = await python.Command($"paper {local} {Encode(new string('x', 3000))} no", "paper", TimeSpan.FromSeconds(10));
            Assert.False(oversize.GetProperty("accepted").GetBoolean());
            var recovery = await python.Command($"paper {local} {Encode("Healthy after rejected paper")} no", "paper", TimeSpan.FromSeconds(10));
            Assert.True(recovery.GetProperty("accepted").GetBoolean());
            var primaryHash = python.HashHex;
            await python.DisposeAsync();
            python = null;
            await using var restartedPython = await PyLinkNode.StartAsync("RestartedPrimaryPaper", tcpServerPort: port,
                harness: "sideband_paper_node.py", extraArguments: [
                    "--source", Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")!,
                    "--profile", Path.Combine(root, "primary-profile")]);
            Assert.Equal(primaryHash, restartedPython.HashHex);
            var restored = await restartedPython.Command("snapshot " + local, "snapshot", TimeSpan.FromSeconds(10));
            var restoredPaper = Assert.Single(restored.GetProperty("messages").EnumerateArray(),
                message => message.GetProperty("content").GetString() == "Primary application paper ✓");
            Assert.Equal(uri, restoredPaper.GetProperty("uri").GetString());
            var restoredDuplicate = await restartedPython.Command("ingest " + Encode(native.PaperUri!), "ingest", TimeSpan.FromSeconds(10));
            Assert.Contains("already been processed", restoredDuplicate.GetProperty("response").GetString());
        }
        finally
        {
            if (python is not null) await python.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
