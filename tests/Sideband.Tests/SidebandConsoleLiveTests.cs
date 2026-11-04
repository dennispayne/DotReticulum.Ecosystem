using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotReticulum.Applications;
using LXMF.Tests.Interop;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandConsoleLiveTests
{
    public static bool Disabled => Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1" ||
        Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_LIVE") != "1";

    [Theory(SkipWhen = nameof(Disabled), Skip = "Requires actual pinned primary Sideband/RNS/LXMF/LXST and both live opt-ins.")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryCliAndNativeConsoleMessagesBothWaysAndIdentityPreservingRestart(int topology)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(3));
        var token = stop.Token;
        var source = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM") ??
            throw new InvalidOperationException("Set DOTRETICULUM_SIDEBAND_UPSTREAM to the clean primary source.");
        var root = Path.Combine(AppContext.BaseDirectory, "console-live-" + Guid.NewGuid().ToString("N"));
        var port = Port();
        try
        {
            await using var pythonTransport = topology == 1 ? await PyLinkNode.StartAsync("ConsolePythonTransport", tcpServerPort: port,
                harness: "sideband_live_node.py", extraArguments: ["--transport"]) : null;
            var transportConfig = await Config(root, "transport", port, server: true, transport: true, token);
            await using var nativeTransport = topology == 2 ? ReticulumNode.Open(transportConfig) : null;
            if (nativeTransport is not null) await nativeTransport.StartAsync(token);
            await using var python = await PyLinkNode.StartAsync("ActualPrimaryConsole", tcpServerPort: topology == 0 ? port : 0,
                rnsdPort: topology == 0 ? 0 : port, harness: "sideband_console_node.py", extraArguments: ["--source", source]);
            var config = await Config(root, "rns", port, false, false, token);
            var store = new SidebandStateStore(Path.Combine(root, "data"));
            await using var client = new SidebandClient(store);
            await client.ConfigureAnnouncementAsync(new(false), token);
            await client.ConnectAsync(config, token);
            var own = client.ConnectionInfo!.DeliveryHash;
            var output = new ConcurrentQueue<string>();
            var errors = new ConcurrentQueue<string>();
            await using var console = new SidebandConsole(client, store, output.Enqueue, errors.Enqueue, token);
            await Primary("conv announce");
            await Until(async () => (await client.GetAnnouncesAsync(token)).Any(a => a.DestinationHash == python.HashHex), token);
            await Native("conv announce");
            await python.WaitForAnnounce(Convert.FromHexString(own), TimeSpan.FromSeconds(30));
            await Native("conv create " + python.HashHex + " 'Primary CLI Ω'");
            await Native("conv trust 'Primary CLI Ω'");
            await Primary("conv create " + own + " 'Native CLI Ω'");
            await Primary("conv trust " + own);
            await Native("conv send 'Primary CLI Ω' 'native-to-primary quoted Ω ; $(literal)'");
            await Until(async () => Has(await Snapshot(), "native-to-primary quoted Ω ; $(literal)"), token);
            await Primary("conv send 'Native CLI Ω' 'primary-to-native quoted 雪'");
            await Until(async () => (await client.LoadAsync(token)).Messages.Any(m => m.Incoming && m.Content == "primary-to-native quoted 雪"), token);
            await Native("conv msgs 'Primary CLI Ω' 12");
            Assert.Contains(output, line => line.Contains("primary-to-native quoted 雪", StringComparison.Ordinal));
            await client.DisconnectAsync();
            await client.DisposeAsync();
            await using var reopened = new SidebandClient(new(store.DataDirectory));
            await reopened.ConnectAsync(config, token);
            Assert.Equal(own, reopened.ConnectionInfo!.DeliveryHash);
            Assert.True((await reopened.LoadAsync(token)).Contacts.Single(c => c.DeliveryHash == python.HashHex).Trusted);
            await using var restartedConsole = new SidebandConsole(reopened, new(store.DataDirectory), output.Enqueue, errors.Enqueue, token);
            Assert.True(await restartedConsole.ExecuteAsync(SidebandConsoleSyntax.Resolve(SidebandConsoleSyntax.Parse("conv send 'Primary CLI Ω' 'after clean restart'"))!, token));
            await Until(async () => Has(await Snapshot(), "after clean restart"), token);
            await Primary("conv send 'Native CLI Ω' 'return after restart'");
            await Until(async () => (await reopened.LoadAsync(token)).Messages.Any(m => m.Content == "return after restart"), token);
            Assert.Empty(errors);

            async Task Native(string text) => Assert.True(await console.ExecuteAsync(SidebandConsoleSyntax.Resolve(SidebandConsoleSyntax.Parse(text))!, token));
            async Task Primary(string text)
            {
                var result = await python.Command("cli " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), "console", TimeSpan.FromSeconds(15));
                Assert.Equal(JsonValueKind.Null, result.GetProperty("error").ValueKind);
                Assert.DoesNotContain("Unknown destination", result.GetProperty("result").GetString() ?? "", StringComparison.Ordinal);
                Assert.DoesNotContain("Failed to", result.GetProperty("result").GetString() ?? "", StringComparison.Ordinal);
            }
            Task<JsonElement> Snapshot() => python.Command("snapshot", "snapshot", TimeSpan.FromSeconds(10));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact(SkipWhen = nameof(Disabled), Skip = "Requires actual pinned primary CLI/core and a live Python propagation/transport node.")]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryCliAndNativeConsolePropagationBothDirectionsAndConfiguredSyncLimit()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(4));
        var token = stop.Token;
        var root = Path.Combine(AppContext.BaseDirectory, "console-pn-" + Guid.NewGuid().ToString("N"));
        var source = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")!;
        var port = Port();
        try
        {
            await using var pn = await PyLinkNode.StartAsync("ConsolePN", tcpServerPort: port, harness: "sideband_live_node.py", extraArguments: ["--transport"]);
            var pnReady = await pn.Command("enable-pn", "pn-ready", TimeSpan.FromSeconds(10));
            var node = pnReady.GetProperty("dest").GetString()!;
            await using var python = await PyLinkNode.StartAsync("PrimaryConsolePN", rnsdPort: port, harness: "sideband_console_node.py", extraArguments: ["--source", source]);
            var config = await Config(root, "rns", port, false, false, token);
            var store = new SidebandStateStore(Path.Combine(root, "data"));
            await using var client = new SidebandClient(store);
            await client.ConnectAsync(config, token);
            var own = client.ConnectionInfo!.DeliveryHash;
            var output = new ConcurrentQueue<string>();
            var errors = new ConcurrentQueue<string>();
            await using var console = new SidebandConsole(client, store, output.Enqueue, errors.Enqueue, token);
            await Primary("conv announce");
            await Until(async () => (await client.GetAnnouncesAsync(token)).Any(a => a.DestinationHash == python.HashHex), token);
            await Native("conv announce");
            await python.WaitForAnnounce(Convert.FromHexString(own), TimeSpan.FromSeconds(30));
            await Native("conv create " + python.HashHex + " Primary");
            await Primary("conv create " + own + " Native");
            await Primary("conv trust " + own);
            await Native("net propagation set " + node);
            await Native("config set lxmf propagation_by_default true");
            await Primary("net propagation set " + node);
            await Primary("config set lxmf propagation_by_default true");
            await Native("conv send Primary 'console propagated native'");
            await Until(async () => (await client.LoadAsync(token)).Messages.Any(m => m.Content == "console propagated native" && m.DeliveryState == "Sent"), token);
            await Primary("conv sync");
            await Until(async () => Has(await python.Command("snapshot", "snapshot", TimeSpan.FromSeconds(10)), "console propagated native"), token);
            await Primary("conv send Native 'console propagated primary'");
            await Until(async () => (await python.Command("snapshot", "snapshot", TimeSpan.FromSeconds(10)))
                .GetProperty("conversations").EnumerateArray().SelectMany(c => c.GetProperty("messages").EnumerateArray())
                .Any(m => Encoding.UTF8.GetString(Convert.FromBase64String(m.GetProperty("content").GetString()!)) == "console propagated primary" &&
                    m.GetProperty("state_text").GetString() == "propagated"), token);
            await Native("conv sync 1");
            await Until(async () => (await client.LoadAsync(token)).Messages.Any(m => m.Incoming && m.Content == "console propagated primary"), token);
            await Until(() => Task.FromResult(output.Any(line => line.StartsWith("LXMF sync completed", StringComparison.Ordinal))), token);
            await Native("conv sync status");
            Assert.Contains(output, line => line.StartsWith("Sync status:", StringComparison.Ordinal));
            Assert.Empty(errors);

            async Task Native(string text) => Assert.True(await console.ExecuteAsync(SidebandConsoleSyntax.Resolve(SidebandConsoleSyntax.Parse(text))!, token));
            async Task Primary(string text)
            {
                var result = await python.Command("cli " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), "console", TimeSpan.FromSeconds(15));
                Assert.Equal(JsonValueKind.Null, result.GetProperty("error").ValueKind);
                Assert.DoesNotContain("Unknown destination", result.GetProperty("result").GetString() ?? "", StringComparison.Ordinal);
                Assert.DoesNotContain("Failed to", result.GetProperty("result").GetString() ?? "", StringComparison.Ordinal);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static bool Has(JsonElement snapshot, string text) => snapshot.GetProperty("conversations").EnumerateArray()
        .SelectMany(c => c.GetProperty("messages").EnumerateArray()).Any(m => Encoding.UTF8.GetString(Convert.FromBase64String(m.GetProperty("content").GetString()!)) == text);

    private static async Task Until(Func<Task<bool>> condition, CancellationToken token)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!await condition().ConfigureAwait(false))
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("Console live condition did not complete.");
            await Task.Delay(100, token);
        }
    }

    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<string> Config(string root, string name, int port, bool server, bool transport, CancellationToken token)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "config"),
            $"[reticulum]\n share_instance = no\n enable_transport = {(transport ? "yes" : "no")}\n\n[logging]\n loglevel = 0\n\n[interfaces]\n [[Console]]\n enabled = yes\n " +
            (server ? $"type = TCPServerInterface\n listen_ip = 127.0.0.1\n listen_port = {port}\n" :
                $"type = TCPClientInterface\n target_host = 127.0.0.1\n target_port = {port}\n"), token);
        return path;
    }
}
