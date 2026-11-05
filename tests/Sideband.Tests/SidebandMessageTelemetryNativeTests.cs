using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LXMF.Tests.Interop;
using static Sideband.Tests.SidebandMessageRetryNativeTests;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMessageTelemetryNativeTests
{
    public static bool Disabled => Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MESSAGE_TELEMETRY") != "1";

    [Theory(SkipWhen = nameof(Disabled), Skip = "Requires frozen published Sideband, pinned actual primary and explicit live selected-message telemetry opt-ins.")]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Live")]
    [Trait("TestId", "T837")]
    public async Task PublishedMessagesRetainTheirOwnTelemetryAcrossNewerReportsAndCleanOrAbruptRestart(bool abrupt)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE"));
        var binary = Required("DOTRETICULUM_SIDEBAND_BINARY");
        var hash = Required("DOTRETICULUM_SIDEBAND_MESSAGE_TELEMETRY_SHA256");
        var source = Required("DOTRETICULUM_SIDEBAND_UPSTREAM");
        var results = Required("DOTRETICULUM_SIDEBAND_MESSAGE_TELEMETRY_RESULTS");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(4));
        var token = stop.Token;
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token))));
        var root = Path.Combine(results, (abrupt ? "abrupt-" : "clean-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        await using var primary = await PyLinkNode.StartAsync("MessageTelemetryPrimary", tcpServerPort: port,
            harness: "sideband_application_node.py", extraArguments: ["--source", source]);
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n[logging]\n loglevel = 0\n[interfaces]\n" +
            $" [[Synthetic telemetry peer]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
        var profile = Path.Combine(root, "profile");
        var store = new SidebandStateStore(profile);
        await store.ConfigureTelemetryAsync(new() { Enabled = true, InformationEnabled = true, Information = "native-first", SendToTrusted = true }, token);
        string? own = null;
        double previousTime = 0;
        Dictionary<Guid, string?> previous = [];
        for (var phase = 0; phase < 2; phase++)
        {
            await using (var native = new NativeRetryConsole(binary, profile, config, Path.Combine(root, $"native-{phase}.log")))
            {
                var ready = await native.WaitAsync("READY identity=", token);
                var address = ready.Split("delivery=", StringSplitOptions.None)[1].Split(' ')[0];
                if (own is null) own = address;
                else Assert.Equal(own, address);
                var loaded = await Load();
                foreach (var item in previous) Assert.Equal(item.Value, Assert.Single(loaded.Messages, message => message.Id == item.Key).TelemetryHex);
                await native.SendAsync("conv announce", token);
                await native.WaitAsync("LXMF announce sent.", token);
                await primary.WaitForAnnounce(Convert.FromHexString(own), TimeSpan.FromSeconds(30));
                await primary.Command("announce", "announced", TimeSpan.FromSeconds(10));
                if (phase == 0)
                {
                    await native.SendAsync($"conv create {primary.HashHex} Primary", token);
                    await native.SendAsync("conv trust Primary", token);
                    await primary.Command($"trust {own} yes", "trust", TimeSpan.FromSeconds(10));
                    await primary.Command($"telemetry-permission {own} yes yes", "telemetry-permission", TimeSpan.FromSeconds(10));
                }
                var nativeText = "native-message-" + phase;
                await native.SendAsync("conv send Primary " + nativeText, token);
                await MessageRetryPeer.UntilAsync(async () =>
                    (await primary.Command($"snapshot {own}", "snapshot", TimeSpan.FromSeconds(10)))
                    .GetProperty("messages").EnumerateArray().Any(item => item.GetProperty("sha256").GetString() == Digest(nativeText)), token);
                var observed = await primary.Command($"inspect-message-telemetry {own} {Digest(nativeText)}", "message-telemetry", TimeSpan.FromSeconds(10));
                var outgoing = Assert.Single((await Load()).Messages, message => message.Content == nativeText);
                Assert.Equal(observed.GetProperty("field_hex").GetString(), outgoing.TelemetryHex);
                Assert.Equal(observed.GetProperty("field_copy").GetString(), SidebandMessageTelemetry.CopyText(outgoing.TelemetryHex!));
                await File.WriteAllTextAsync(Path.Combine(root, $"primary-{phase}.json"), observed.GetRawText(), token);

                var primaryInformation = "primary-information-" + phase;
                await MessageRetryPeer.UntilAsync(() => Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeSeconds() > previousTime), token);
                var prepared = await Configure(primaryInformation);
                var incomingText = "primary-message-" + phase;
                await primary.Command($"send {own} {Convert.ToBase64String(Encoding.UTF8.GetBytes(incomingText))}", "sent_queued", TimeSpan.FromSeconds(15));
                await MessageRetryPeer.UntilAsync(async () => (await Load()).Messages.Any(message => message.Content == incomingText), token);
                var incoming = Assert.Single((await Load()).Messages, message => message.Content == incomingText);
                Assert.NotNull(incoming.TelemetryHex);
                Assert.Contains(primaryInformation, SidebandMessageTelemetry.CopyText(incoming.TelemetryHex));
                await VerifyCopy(incoming.TelemetryHex);
                var firstTimestamp = SidebandTelemetryCodec.Decode(Convert.FromBase64String(prepared.GetProperty("packed").GetString()!)).TimestampUtcSeconds!.Value;
                await MessageRetryPeer.UntilAsync(() => Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeSeconds() > firstTimestamp), token);
                prepared = await Configure("newer-report-" + phase);
                previousTime = SidebandTelemetryCodec.Decode(Convert.FromBase64String(prepared.GetProperty("packed").GetString()!)).TimestampUtcSeconds!.Value;
                await primary.Command($"telemetry-send {own}", "telemetry-queued", TimeSpan.FromSeconds(15));
                await MessageRetryPeer.UntilAsync(async () =>
                {
                    await native.SendAsync("telemetry list Primary", token);
                    return native.ContainsLine("newer-report-" + phase);
                }, token);
                Assert.Equal(incoming.TelemetryHex, Assert.Single((await Load()).Messages, message => message.Id == incoming.Id).TelemetryHex);
                if (phase == 0)
                {
                    var fields = V.Map(KeyValuePair.Create(V.From(2L), V.From(Convert.FromBase64String(prepared.GetProperty("packed").GetString()!))),
                        KeyValuePair.Create(V.From(3L), V.From(42L))).Encode();
                    await primary.Command($"telemetry-fields {own} {Convert.ToBase64String(fields)}", "telemetry-fields-queued", TimeSpan.FromSeconds(15));
                    await MessageRetryPeer.UntilAsync(async () => (await Load()).Messages.Any(message => message.Content == "metadata isolation test"), token);
                    var isolated = Assert.Single((await Load()).Messages, message => message.Content == "metadata isolation test");
                    Assert.NotNull(isolated.TelemetryHex);
                    Assert.Contains("newer-report-0", SidebandMessageTelemetry.CopyText(isolated.TelemetryHex));
                    await VerifyCopy(isolated.TelemetryHex);
                    await native.WaitAsync("Telemetry stream rejected; accepted message telemetry is retained", token);
                }
                previous = (await Load()).Messages.ToDictionary(message => message.Id, message => message.TelemetryHex);
                Assert.All(previous.Values, Assert.NotNull);
                if (phase != 0 || !abrupt) await native.QuitAsync(token);
            }
        }
        var final = await Load();
        Assert.Equal(own, final.OwnDeliveryHash);
        Assert.Equal(5, final.Messages.Count);
        foreach (var item in previous) Assert.Equal(item.Value, Assert.Single(final.Messages, message => message.Id == item.Key).TelemetryHex);
        var sourceProfile = Path.Combine(root, "actual-primary-profile");
        await primary.Command("telemetry-profile-snapshot " + Convert.ToBase64String(Encoding.UTF8.GetBytes(sourceProfile)),
            "telemetry-profile-snapshotted", TimeSpan.FromSeconds(15));
        var fingerprints = Directory.GetFiles(sourceProfile).Order(StringComparer.Ordinal)
            .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        var importedProfile = Path.Combine(root, "imported-profile");
        var start = new ProcessStartInfo(binary)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--import-profile", sourceProfile, "--data-dir", importedProfile })
            start.ArgumentList.Add(argument);
        using (var command = Process.Start(start) ?? throw new InvalidOperationException("Could not start native profile import."))
        {
            var stdout = command.StandardOutput.ReadToEndAsync(token);
            var stderr = command.StandardError.ReadToEndAsync(token);
            try { await command.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token); }
            finally { if (!command.HasExited) { command.Kill(true); await command.WaitForExitAsync(CancellationToken.None); } }
            var output = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(root, "native-import.log"), output, token);
            Assert.True(command.ExitCode == 0, output);
        }
        Assert.Equal(fingerprints, Directory.GetFiles(sourceProfile).Order(StringComparer.Ordinal)
            .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray());
        var imported = await new SidebandStateStore(importedProfile).LoadAsync(cancellationToken: token);
        Assert.Equal(primary.HashHex, imported.OwnDeliveryHash);
        foreach (var content in new[] { "native-message-0", "native-message-1", "primary-message-0", "primary-message-1" })
        {
            var observed = await primary.Command($"inspect-message-telemetry {own} {Digest(content)}", "message-telemetry", TimeSpan.FromSeconds(10));
            var selected = Assert.Single(imported.Messages, message => message.Content == content);
            Assert.NotNull(selected.TelemetryHex);
            Assert.Equal(observed.GetProperty("saved_copy").GetString(), SidebandMessageTelemetry.CopyText(selected.TelemetryHex));
        }
        Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token))));

        Task<SidebandState> Load() => new SidebandStateStore(profile).LoadAsync(cancellationToken: token);
        Task<JsonElement> Configure(string text) => primary.Command("telemetry-config " +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { telemetry_enabled = true, telemetry_s_information = true, telemetry_s_information_text = text }))),
            "telemetry-configured", TimeSpan.FromSeconds(15));
        async Task VerifyCopy(string hex)
        {
            await primary.Command("telemetry-verify-copy " + Convert.ToBase64String(Convert.FromHexString(hex)) + " " +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(SidebandMessageTelemetry.CopyText(hex))), "telemetry-copy-verified", TimeSpan.FromSeconds(10));
        }
        static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Select " + name + ".");
    }
}
