using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LXMF.Tests.Interop;
using Sideband;
using NativeConsole = Sideband.Tests.SidebandMessageRetryNativeTests.NativeRetryConsole;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMessageCancellationNativeTests
{
    public static bool Disabled => Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MESSAGE_CANCELLATION") != "1";

    [Fact(SkipWhen = nameof(Disabled), Skip = "Requires explicit active-cancellation acceptance with exact binary/hash, isolated primary reference and persistent results.")]
    [Trait("Category", "Live")]
    [Trait("TestId", "T444")]
    public async Task SelectedNativeCancelsActualPrimaryStampPreparationAndContinuesBothDirectionsAcrossRestart()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_LIVE"));
        var binary = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_BINARY")
            ?? throw new InvalidOperationException("Select the exact native executable.");
        var expectedHash = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MESSAGE_CANCELLATION_SHA256");
        var source = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")
            ?? throw new InvalidOperationException("Select the exact primary checkout.");
        var results = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MESSAGE_CANCELLATION_RESULTS")
            ?? throw new InvalidOperationException("Select persistent cancellation results.");
        Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(
            await File.ReadAllBytesAsync(binary, TestContext.Current.CancellationToken))));
        var root = Path.Combine(results, "native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(3));
        var token = stop.Token;
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        await using var python = await PyLinkNode.StartAsync("NativeCancellationPrimary", tcpServerPort: port,
            harness: "sideband_console_node.py", extraArguments: ["--source", source]);
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n[logging]\n loglevel = 0\n[interfaces]\n" +
            $" [[Primary cancellation]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
        var profile = Path.Combine(root, "data");
        string own;
        Guid cancelledId;
        await using (var native = new NativeConsole(binary, profile, config, Path.Combine(root, "first.log")))
        {
            var ready = await native.WaitAsync("READY identity=", token);
            own = ready.Split("delivery=", StringSplitOptions.None)[1].Split(' ')[0];
            await native.SendAsync("conv announce", token);
            await native.WaitAsync("LXMF announce sent.", token);
            await python.WaitForAnnounce(Convert.FromHexString(own), TimeSpan.FromSeconds(30));
            await Primary("config set lxmf inbound_stamp_cost 254");
            await Primary("config set lxmf require_stamps true");
            await Primary("conv announce");
            await MessageRetryPeer.UntilAsync(async () => (await State()).Announces?.Any(item =>
                item.DestinationHash == python.HashHex && item.StampCost == 254) == true, token);
            await native.SendAsync($"conv send {python.HashHex} 'native cancelled preparation'", token);
            await MessageRetryPeer.UntilAsync(async () => (await State()).Messages.Any(message => message.Content == "native cancelled preparation"), token);
            cancelledId = Assert.Single((await State()).Messages, message => message.Content == "native cancelled preparation").Id;
            await native.SendAsync($"conv cancel-message {python.HashHex} {cancelledId}", token);
            await native.WaitAsync("Pending message cancellation requested", token);
            await MessageRetryPeer.UntilAsync(async () => (await State()).Messages.Any(message =>
                message.Id == cancelledId && message.DeliveryState == "Cancelled"), token);
            await native.SendAsync($"conv cancel-message {python.HashHex} {cancelledId}", token);
            await native.WaitAsync("no longer pending in this runtime", token);
            Assert.Empty(Messages(await Snapshot()));
            await Primary("config set lxmf require_stamps false");
            await Task.Delay(TimeSpan.FromSeconds(1.1), token);
            await Primary("conv announce");
            await MessageRetryPeer.UntilAsync(async () => (await State()).Announces?.Any(item =>
                item.DestinationHash == python.HashHex && item.StampCost is null) == true, token);
            await native.SendAsync($"conv send {python.HashHex} 'healthy after cancellation'", token);
            await MessageRetryPeer.UntilAsync(async () => Messages(await Snapshot()).Any(message =>
                Encoding.UTF8.GetString(Convert.FromBase64String(message.GetProperty("content").GetString()!)) == "healthy after cancellation"), token);
            await MessageRetryPeer.UntilAsync(async () => (await State()).Messages.Any(message =>
                message.Content == "healthy after cancellation" && message.DeliveryState == "Delivered"), token);
            await Primary($"conv send {own} 'primary reverse after cancellation'");
            await MessageRetryPeer.UntilAsync(async () => (await State()).Messages.Any(message =>
                message.Incoming && message.Content == "primary reverse after cancellation"), token);
            await native.QuitAsync(token);
        }
        await using (var native = new NativeConsole(binary, profile, config, Path.Combine(root, "restart.log")))
        {
            Assert.Contains("delivery=" + own, await native.WaitAsync("READY identity=", token));
            await native.SendAsync($"conv cancel-message {python.HashHex} {cancelledId}", token);
            await native.WaitAsync("no longer pending in this runtime", token);
            await Primary($"conv send {own} 'primary reverse after cancellation restart'");
            await MessageRetryPeer.UntilAsync(async () => (await State()).Messages.Any(message =>
                message.Incoming && message.Content == "primary reverse after cancellation restart"), token);
            await native.QuitAsync(token);
        }
        var final = await State();
        Assert.Equal(own, final.OwnDeliveryHash);
        Assert.Equal("Cancelled", Assert.Single(final.Messages, message => message.Id == cancelledId).DeliveryState);
        Assert.Equal(4, final.Messages.Count);
        var primaryMessages = Messages(await Snapshot());
        Assert.Equal(3, primaryMessages.Length);
        Assert.DoesNotContain(primaryMessages, message =>
            Encoding.UTF8.GetString(Convert.FromBase64String(message.GetProperty("content").GetString()!)) == "native cancelled preparation");
        Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token))));

        Task<SidebandState> State() => new SidebandStateStore(profile).LoadAsync(cancellationToken: token);
        async Task Primary(string text)
        {
            var result = await python.Command("cli " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), "console", TimeSpan.FromSeconds(15));
            await File.AppendAllTextAsync(Path.Combine(root, "primary-commands.jsonl"), result.GetRawText() + "\n", token);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("error").ValueKind);
            var output = result.GetProperty("result").GetString() ?? "";
            Assert.DoesNotContain("Unknown", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Failed to", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Error:", output, StringComparison.Ordinal);
        }
        async Task<JsonElement> Snapshot()
        {
            var result = await python.Command("snapshot", "snapshot", TimeSpan.FromSeconds(10));
            await File.WriteAllTextAsync(Path.Combine(root, "primary-snapshot.json"), result.GetRawText(), token);
            return result;
        }
        static JsonElement[] Messages(JsonElement snapshot) => snapshot.GetProperty("conversations").EnumerateArray()
            .SelectMany(item => item.GetProperty("messages").EnumerateArray()).ToArray();
    }
}
