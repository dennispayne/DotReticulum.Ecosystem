using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LXMF.Tests.Interop;
using static Sideband.Tests.SidebandMessageRetryNativeTests;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandEnduranceNativeTests
{
    public static bool Disabled => Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_ENDURANCE") != "1";

    [Fact(SkipWhen = nameof(Disabled), Skip = "Requires explicit ten-minute native/actual-primary service acceptance, selected binary/hash and pinned live references.")]
    [Trait("Category", "Live")]
    [Trait("TestId", "T940")]
    public async Task PublishedServiceExchangesForTenMinutesThenSurvivesPeerExitAndRecoversAnotherPrimaryBeforeRestart()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE"));
        var binary = Required("DOTRETICULUM_SIDEBAND_BINARY");
        var hash = Required("DOTRETICULUM_SIDEBAND_ENDURANCE_SHA256");
        var source = Required("DOTRETICULUM_SIDEBAND_UPSTREAM");
        var results = Required("DOTRETICULUM_SIDEBAND_ENDURANCE_RESULTS");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(15));
        var token = stop.Token;
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token))));
        var root = Path.Combine(results, "service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n[logging]\n loglevel = 0\n[interfaces]\n" +
            $" [[Synthetic endurance peer]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
        var profile = Path.Combine(root, "profile");
        await new SidebandStateStore(profile).ConfigureTelemetryAsync(new()
        { Enabled = true, InformationEnabled = true, Information = "synthetic native endurance", SendToTrusted = true }, token);
        PyLinkNode? primary = null;
        string own;
        string failedPeer;
        try
        {
            primary = await StartPrimary();
            await using (var native = new NativeRetryConsole(binary, profile, config, Path.Combine(root, "service.log")))
            {
                own = (await native.WaitAsync("READY identity=", token)).Split("delivery=", StringSplitOptions.None)[1].Split(' ')[0];
                await ConfigurePeer(primary, "First");
                var elapsed = Stopwatch.StartNew();
                for (var index = 0; index < 10; index++)
                {
                    await Exchange(primary, "minute-" + index);
                    await native.SendAsync("status", token);
                    await native.WaitForOccurrencesAsync("LXMF router: active", index + 1, token);
                    await File.WriteAllTextAsync(Path.Combine(root, "progress.json"),
                        JsonSerializer.Serialize(new { completedExchanges = index + 1, elapsedSeconds = elapsed.Elapsed.TotalSeconds }), token);
                    var remaining = TimeSpan.FromMinutes(index + 1) - elapsed.Elapsed;
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
                }
                Assert.True(elapsed.Elapsed >= TimeSpan.FromMinutes(10));
                elapsed.Stop();
                failedPeer = primary.HashHex;
                await File.WriteAllTextAsync(Path.Combine(root, "first-primary.log"), primary.Log, token);
                await primary.DisposeAsync();
                primary = null;
                await native.SendAsync($"conv send {failedPeer} message-after-peer-exit", token);
                await Until(async () => (await Load()).Messages.Any(message =>
                    message.Content == "message-after-peer-exit" && message.DeliveryState == "Failed"), TimeSpan.FromSeconds(110));
                await native.SendAsync("status", token);
                await native.WaitForOccurrencesAsync("LXMF router: active", 11, token);
                primary = await StartPrimary();
                Assert.NotEqual(failedPeer, primary.HashHex);
                await ConfigurePeer(primary, "Replacement");
                await Exchange(primary, "after-peer-exit");
                await File.WriteAllTextAsync(Path.Combine(root, "replacement-primary.log"), primary.Log, token);
                await native.QuitAsync(token);
                await File.WriteAllTextAsync(Path.Combine(root, "elapsed.json"),
                    JsonSerializer.Serialize(new { steadySeconds = elapsed.Elapsed.TotalSeconds, minimumSteadySeconds = 600, exchanges = 11 }), token);

                async Task ConfigurePeer(PyLinkNode peer, string name)
                {
                    await Until(async () =>
                    {
                        await native.SendAsync("conv announce", token);
                        await Task.Delay(TimeSpan.FromSeconds(1), token);
                        return peer.Events("announce").Any(item => item.GetProperty("dest").GetString() == own);
                    }, TimeSpan.FromSeconds(30));
                    await peer.Command("announce", "announced", TimeSpan.FromSeconds(10));
                    await native.SendAsync($"conv create {peer.HashHex} {name}", token);
                    await native.WaitAsync("Conversation created with " + peer.HashHex, token);
                    await native.SendAsync("conv trust " + peer.HashHex, token);
                    await native.WaitAsync("OK conv trust " + peer.HashHex, token);
                    await peer.Command($"trust {own} yes", "trust", TimeSpan.FromSeconds(10));
                    await peer.Command($"telemetry-permission {own} yes yes", "telemetry-permission", TimeSpan.FromSeconds(10));
                }

                async Task Exchange(PyLinkNode peer, string suffix)
                {
                    var outbound = "native-endurance-" + suffix;
                    var inbound = "primary-endurance-" + suffix;
                    await native.SendAsync($"conv send {peer.HashHex} {outbound}", token);
                    var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(outbound)));
                    await Until(async () => (await peer.Command($"snapshot {own}", "snapshot", TimeSpan.FromSeconds(10)))
                        .GetProperty("messages").EnumerateArray().Any(item => item.GetProperty("sha256").GetString() == digest),
                        TimeSpan.FromSeconds(30));
                    var settings = JsonSerializer.Serialize(new
                    { telemetry_enabled = true, telemetry_s_information = true, telemetry_s_information_text = suffix });
                    await peer.Command("telemetry-config " + Convert.ToBase64String(Encoding.UTF8.GetBytes(settings)),
                        "telemetry-configured", TimeSpan.FromSeconds(15));
                    await peer.Command($"send {own} {Convert.ToBase64String(Encoding.UTF8.GetBytes(inbound))}",
                        "sent_queued", TimeSpan.FromSeconds(15));
                    await Until(async () => (await Load()).Messages.Any(message => message.Content == inbound),
                        TimeSpan.FromSeconds(30));
                    var selected = Assert.Single((await Load()).Messages, message => message.Content == inbound);
                    Assert.NotNull(selected.TelemetryHex);
                    Assert.Contains(suffix, SidebandMessageTelemetry.CopyText(selected.TelemetryHex));
                }
            }
            var before = await Load();
            await using (var restarted = new NativeRetryConsole(binary, profile, config, Path.Combine(root, "restart.log")))
            {
                Assert.Contains("delivery=" + own, await restarted.WaitAsync("READY identity=", token));
                Assert.Equal(23, (await Load()).Messages.Count);
                Assert.Contains((await Load()).Messages, message => message.PeerDeliveryHash == failedPeer &&
                    message.Content == "message-after-peer-exit" && message.DeliveryState == "Failed");
                await restarted.QuitAsync(token);
            }
            var after = await Load();
            Assert.Equal(own, after.OwnDeliveryHash);
            Assert.Equal(23, after.Messages.Count);
            foreach (var message in before.Messages)
                Assert.Equal(message.TelemetryHex, Assert.Single(after.Messages, saved => saved.Id == message.Id).TelemetryHex);
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token))));
            await File.WriteAllTextAsync(Path.Combine(root, "receipt.json"),
                JsonSerializer.Serialize(new { nativeSha256 = hash, minimumSteadySeconds = 600, exchanges = 11,
                    failedPeerRecovery = true, identityPreservingRestart = true,
                    boundary = "Direct actual-primary application traffic; clean peer exit, terminal failure and a different primary identity recover. No loss/hop, physical devices, days-long service or current Linux acceptance." }), token);
        }
        finally { if (primary is not null) await primary.DisposeAsync(); }

        Task<PyLinkNode> StartPrimary() => PyLinkNode.StartAsync("EndurancePrimary", tcpServerPort: port,
            harness: "sideband_application_node.py", extraArguments: ["--source", source]);
        Task<SidebandState> Load() => new SidebandStateStore(profile).LoadAsync(cancellationToken: token);
        async Task Until(Func<Task<bool>> condition, TimeSpan limit)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(limit);
            while (!await condition()) await Task.Delay(100, deadline.Token);
        }
        static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Select " + name + ".");
    }
}
