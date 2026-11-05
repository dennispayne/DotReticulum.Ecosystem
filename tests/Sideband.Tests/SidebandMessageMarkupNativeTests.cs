using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LXMF.Tests.Interop;
using static Sideband.Tests.SidebandMessageRetryNativeTests;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMessageMarkupNativeTests
{
    public static bool Disabled => Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MESSAGE_MARKUP") != "1";

    [Theory(SkipWhen = nameof(Disabled), Skip = "Requires selected native executable/hash, pinned actual primary and explicit rich-message/live opt-ins.")]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Live")]
    [Trait("TestId", "T576")]
    public async Task SelectedNativeAndActualPrimaryExchangeRendererFieldsBothWaysAcrossIdentityPreservingRestart(bool abrupt)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_LIVE"));
        var binary = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_BINARY")
            ?? throw new InvalidOperationException("Select the exact published executable.");
        var expected = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MESSAGE_MARKUP_SHA256");
        var source = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")
            ?? throw new InvalidOperationException("Select the actual primary checkout.");
        var results = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MESSAGE_MARKUP_RESULTS")
            ?? throw new InvalidOperationException("Select a persistent synthetic results directory.");
        var root = Path.Combine(results, (abrupt ? "abrupt-" : "clean-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(4));
        var token = stop.Token;
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token))));
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        await using var python = await PyLinkNode.StartAsync("NativeMarkupPrimary", tcpServerPort: port,
            harness: "sideband_console_node.py", extraArguments: ["--source", source]);
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n[logging]\n loglevel = 0\n[interfaces]\n" +
            $" [[Primary formatting]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
        var store = new SidebandStateStore(Path.Combine(root, "data"));
        string own;
        await using (var native = new NativeRetryConsole(binary, store.DataDirectory, config, Path.Combine(root, "first.log")))
        {
            own = (await native.WaitAsync("READY identity=", token)).Split("delivery=", StringSplitOptions.None)[1].Split(' ')[0];
            await native.SendAsync("conv announce", token);
            await native.WaitAsync("LXMF announce sent.", token);
            await python.WaitForAnnounce(Convert.FromHexString(own), TimeSpan.FromSeconds(30));
            await Primary("conv announce");
            await Native($"conv create {python.HashHex} Primary");
            await Primary($"conv create {own} Native");
            await Native("config set general compose_in_markdown true");
            await Primary("config set general compose_in_markdown true");
            await Native("conv send Primary '**native markdown Ω**'");
            await WaitPrimary("**native markdown Ω**", 2);
            await Primary($"conv send {own} '**primary markdown Ω**'");
            await WaitIncoming("**primary markdown Ω**", 2);
            await Native("config set general compose_in_markdown false");
            await Primary("config set general compose_in_markdown false");
            await Native("conv send Primary '[b]native BB[/b]'");
            await WaitPrimary("[b]native BB[/b]", 3);
            await Primary($"conv send {own} '[b]primary BB[/b]'");
            await WaitIncoming("[b]primary BB[/b]", 3);
            await Native("conv send Primary 'ordinary [b]text[/b]'");
            await WaitPrimary("ordinary [b]text[/b]", null);
            await MessageRetryPeer.UntilAsync(async () => (await ReadState())
                .Messages.Count(message => !message.Incoming && message.DeliveryState == "Delivered") == 3, token);
            await Native("config set general compose_in_markdown true");
            await MessageRetryPeer.UntilAsync(async () =>
                SidebandMessageFormatting.FromState(await ReadState()).ComposeMarkdown, token);
            if (!abrupt) await native.QuitAsync(token);

            async Task Native(string command) => await native.SendAsync(command, token);
        }
        await using (var native = new NativeRetryConsole(binary, store.DataDirectory, config, Path.Combine(root, "restart.log")))
        {
            Assert.Contains("delivery=" + own, await native.WaitAsync("READY identity=", token));
            await native.SendAsync("conv send Primary '**native after restart**'", token);
            await WaitPrimary("**native after restart**", 2);
            await Primary("config set general compose_in_markdown true");
            await Primary($"conv send {own} '**primary after restart**'");
            await WaitIncoming("**primary after restart**", 2);
            await MessageRetryPeer.UntilAsync(async () => (await ReadState())
                .Messages.Single(message => message.Content == "**native after restart**").DeliveryState == "Delivered", token);
            await native.QuitAsync(token);
        }
        var state = await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token);
        Assert.Equal(own, state.OwnDeliveryHash);
        Assert.Equal(7, state.Messages.Count);
        Assert.Equal([2L, 3L, null, 2L], state.Messages.Where(message => !message.Incoming).Select(message => message.Renderer));
        Assert.Equal([2L, 3L, 2L], state.Messages.Where(message => message.Incoming).Select(message => message.Renderer));
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token))));

        async Task Primary(string command)
        {
            var result = await python.Command("cli " + Convert.ToBase64String(Encoding.UTF8.GetBytes(command)),
                "console", TimeSpan.FromSeconds(15));
            await File.AppendAllTextAsync(Path.Combine(root, "primary-commands.jsonl"), result.GetRawText() + "\n", token);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("error").ValueKind);
            Assert.DoesNotContain("Unknown destination", result.GetProperty("result").GetString() ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("Failed to", result.GetProperty("result").GetString() ?? "", StringComparison.Ordinal);
        }

        Task WaitPrimary(string content, long? renderer) => MessageRetryPeer.UntilAsync(async () =>
        {
            var snapshot = await python.Command("snapshot", "snapshot", TimeSpan.FromSeconds(10));
            await File.WriteAllTextAsync(Path.Combine(root, "primary-snapshot.json"), snapshot.GetRawText(), token);
            return snapshot.GetProperty("conversations").EnumerateArray().SelectMany(item => item.GetProperty("messages").EnumerateArray())
                .Any(item => Encoding.UTF8.GetString(Convert.FromBase64String(item.GetProperty("content").GetString()!)) == content &&
                    (item.GetProperty("renderer").ValueKind == JsonValueKind.Null ? null : (long?)item.GetProperty("renderer").GetInt64()) == renderer);
        }, token);

        Task WaitIncoming(string content, long renderer) => MessageRetryPeer.UntilAsync(async () =>
            (await ReadState()).Messages
                .Any(message => message.Incoming && message.Content == content && message.Renderer == renderer), token);

        Task<SidebandState> ReadState() => new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token);
    }
}
