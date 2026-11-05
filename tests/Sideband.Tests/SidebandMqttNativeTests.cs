using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Diagnostics;
using LXMF.Tests.Interop;
using Sideband;
using static Sideband.Tests.SidebandMessageRetryNativeTests;
using V = Sideband.SidebandTelemetryValue;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMqttNativeTests
{
    public static bool Disabled => Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MQTT") != "1";

    [Fact(SkipWhen = nameof(Disabled), Skip = "Requires frozen published Sideband and explicit synthetic MQTT acceptance opt-in.")]
    [Trait("Category", "Live")]
    public async Task PublishedProducerSamplesAtThePrimaryMinuteIntervalAndRecoversItsRetainedReportAfterBrokerOutage()
    {
        var binary = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_BINARY")
            ?? throw new InvalidOperationException("Select the exact published Sideband executable.");
        var expectedHash = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MQTT_SHA256")
            ?? throw new InvalidOperationException("Select the exact published MQTT candidate hash.");
        var results = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MQTT_RESULTS")
            ?? throw new InvalidOperationException("Select a persistent synthetic results directory.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(2));
        var token = stop.Token;
        Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token))));
        var root = Path.Combine(results, "cadence-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "reticulum");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"), "[reticulum]\n share_instance = no\n[interfaces]\n", token);
        await using var broker = await SidebandMqttTestBroker.StartAsync(token);
        var profile = Path.Combine(root, "profile");
        await new SidebandStateStore(profile).ConfigureTelemetryAsync(new() { Enabled = true, Mqtt = broker.Configuration }, token);
        var log = Path.Combine(root, "native.log");
        await using (var native = new NativeRetryConsole(binary, profile, config, log))
        {
            var ready = await native.WaitAsync("READY identity=", token);
            var own = ready.Split("delivery=", StringSplitOptions.None)[1].Split(' ')[0];
            var topic = $"lxmf/telemetry/{own}/time/utc";
            var first = Timestamp(await broker.ReadUntilAsync(message => message.Topic == topic, token));
            await broker.StopAsync(token);
            await MessageRetryPeer.UntilAsync(() => Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeSeconds() > first), token);
            await native.SendAsync("telemetry", token);
            await native.WaitAsync("MQTT broker operation failed", token);
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            await native.WaitForOccurrencesAsync("MQTT broker operation failed", 2, token);
            var recovery = Stopwatch.StartNew();
            await broker.RestartAsync(token);
            var recovered = Timestamp(await broker.ReadUntilAsync(message => message.Topic == topic && Timestamp(message) > first, token));
            recovery.Stop();
            Assert.InRange(recovery.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
            Assert.InRange(recovered - first, 1, 10);
            var periodic = Timestamp(await broker.ReadUntilAsync(message => message.Topic == topic && Timestamp(message) > recovered,
                token, TimeSpan.FromSeconds(75)));
            Assert.InRange(periodic - first, 60, 65);
            await File.WriteAllTextAsync(Path.Combine(root, "timebases.json"),
                JsonSerializer.Serialize(new { first, recovered, periodic, recoveryMilliseconds = recovery.Elapsed.TotalMilliseconds }), token);
            await native.QuitAsync(token);
        }
        Assert.True((await File.ReadAllLinesAsync(log, token)).Count(line =>
            line.Contains("MQTT broker operation failed", StringComparison.Ordinal)) >= 2);
        Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token))));

        static long Timestamp(SidebandMqttObservedMessage message)
        {
            Assert.Equal(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, message.Quality);
            Assert.False(message.Retain);
            return long.Parse(Encoding.ASCII.GetString(message.Payload), CultureInfo.InvariantCulture);
        }
    }

    [Theory(SkipWhen = nameof(Disabled), Skip = "Requires frozen published Sideband, pinned actual primary, explicit MQTT/live opt-ins and synthetic loopback broker.")]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Live")]
    public async Task PublishedAndActualPrimaryForwardLocalDeliveryAndCollectorReportsAcrossIdentityPreservingRestart(bool abrupt)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE"));
        var binary = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_BINARY")
            ?? throw new InvalidOperationException("Select the exact published Sideband executable.");
        var expectedHash = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MQTT_SHA256")
            ?? throw new InvalidOperationException("Select the exact published MQTT candidate hash.");
        var source = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")
            ?? throw new InvalidOperationException("Select the actual primary checkout.");
        var results = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MQTT_RESULTS")
            ?? throw new InvalidOperationException("Select a persistent synthetic results directory.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(4));
        var token = stop.Token;
        var root = Path.Combine(results, (abrupt ? "abrupt-" : "clean-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token))));
        await using var broker = await SidebandMqttTestBroker.StartAsync(token);
        await using var primaryBroker = await SidebandMqttPrimaryBroker.StartAsync(Path.Combine(root, "primary-broker"), token);
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        await using var primary = await PyLinkNode.StartAsync("NativeMqttPrimary", tcpServerPort: port,
            harness: "sideband_application_node.py", extraArguments:
            ["--source", source, "--mqtt-broker-port", primaryBroker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        await primary.Command("telemetry-config " +
            Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"telemetry_to_mqtt":true}""")),
            "telemetry-configured", TimeSpan.FromSeconds(15));
        Assert.Equal(Path.GetFullPath(Path.Combine(source, "sbapp", "assets", "geoids")),
            Path.GetFullPath(Environment.GetEnvironmentVariable("TELEMETER_GEOID_PATH") ??
                throw new InvalidOperationException("Select the external actual-primary grid directory for published location acceptance.")));
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n[logging]\n loglevel = 0\n[interfaces]\n" +
            $" [[Synthetic MQTT peer]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
        var profile = Path.Combine(root, "profile");
        await new SidebandStateStore(profile).ConfigureTelemetryAsync(new()
        {
            Enabled = true, InformationEnabled = true, Information = "native-first",
            FixedLocationEnabled = true, FixedLocation = new(51.5, -0.125, 120),
            Mqtt = broker.Configuration, SendToTrusted = true
        }, token);
        string? own = null;
        long previousTime = 0;
        for (var phase = 0; phase < 2; phase++)
        {
            await MessageRetryPeer.UntilAsync(() => Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeSeconds() > previousTime), token);
            var observationStart = broker.History.Count;
            await using (var native = new NativeRetryConsole(binary, profile, config, Path.Combine(root, $"native-{phase}.log")))
            {
                var address = (await native.WaitAsync("READY identity=", token)).Split("delivery=", StringSplitOptions.None)[1].Split(' ')[0];
                if (own is null) own = address;
                else Assert.Equal(own, address);
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
                var nativeText = phase == 0 ? "native-first" : "native-after-restart";
                if (phase != 0) await native.SendAsync("config set sensors information_text " + nativeText, token);
                await Observe(own, nativeText, broker.Configuration.Username!);
                VerifyPublishedGeoid(own, observationStart);
                await native.SendAsync("telemetry send Primary", token);
                await Observe(own, nativeText, "synthetic-primary-mqtt-user");

                var primaryText = phase == 0 ? "primary-first" : "primary-after-native-restart";
                var settings = "{\"telemetry_enabled\":true,\"telemetry_s_information\":true," +
                    "\"telemetry_to_mqtt\":true,\"telemetry_s_information_text\":\"" + primaryText + "\"}";
                var prepared = await primary.Command("telemetry-config " + Convert.ToBase64String(Encoding.UTF8.GetBytes(settings)),
                    "telemetry-configured", TimeSpan.FromSeconds(15));
                var primaryTimestamp = SidebandTelemetryCodec.Decode(Convert.FromBase64String(prepared.GetProperty("packed").GetString()!))
                    .TimestampUtcSeconds!.Value;
                await Observe(primary.HashHex, primaryText, "synthetic-primary-mqtt-user");
                await primary.Command($"telemetry-send {own}", "telemetry-queued", TimeSpan.FromSeconds(15));
                await Observe(primary.HashHex, primaryText, broker.Configuration.Username!);
                await MessageRetryPeer.UntilAsync(async () =>
                {
                    var snapshot = await primary.Command($"telemetry-snapshot {own}", "telemetry-snapshot", TimeSpan.FromSeconds(10));
                    return snapshot.GetProperty("last_send").ValueKind == JsonValueKind.Number &&
                        snapshot.GetProperty("last_send").GetDouble() >= primaryTimestamp;
                }, token);
                await MessageRetryPeer.UntilAsync(() =>
                    Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeSeconds() > primaryTimestamp), token);

                var claimed = new string(phase == 0 ? 'c' : 'd', 32);
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var relayText = "synthetic-collector-phase-" + phase;
                var relayed = SidebandTelemetryCodec.Create(timestamp,
                    SidebandTelemetryCodec.FromData(S.Information, V.Fields(("contents", V.From(relayText)))));
                var stream = V.Array(V.Array(V.From(Convert.FromHexString(claimed)), V.From(timestamp),
                    V.From(SidebandTelemetryCodec.Encode(relayed)), V.Nil)).Encode();
                await primary.Command($"telemetry-stream {own} {Convert.ToBase64String(stream)}",
                    "telemetry-queued", TimeSpan.FromSeconds(15));
                await Observe(claimed, relayText, broker.Configuration.Username!);
                previousTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (phase != 0 || !abrupt) await native.QuitAsync(token);
            }
            await using var stored = await SidebandTelemetryStore.OpenAsync(Path.Combine(profile, "telemetry"), cancellationToken: token);
            var records = await stored.QueryAsync(new(Limit: 256), token);
            Assert.Contains(records, report => report.SourceHash == primary.HashHex);
            Assert.Contains(records, report => report.SourceHash == new string(phase == 0 ? 'c' : 'd', 32));
            Assert.Equal(own, (await new SidebandStateStore(profile).LoadAsync(cancellationToken: token)).OwnDeliveryHash);
        }
        var sourceProfile = Path.Combine(root, "actual-primary-profile");
        await primary.Command("telemetry-profile-snapshot " + Convert.ToBase64String(Encoding.UTF8.GetBytes(sourceProfile)),
            "telemetry-profile-snapshotted", TimeSpan.FromSeconds(15));
        var importedProfile = Path.Combine(root, "imported-profile");
        var import = new ProcessStartInfo(binary)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "--import-profile", sourceProfile, "--data-dir", importedProfile })
            import.ArgumentList.Add(argument);
        using (var command = Process.Start(import) ?? throw new InvalidOperationException("Could not start native profile import."))
        {
            var stdout = command.StandardOutput.ReadToEndAsync(token);
            var stderr = command.StandardError.ReadToEndAsync(token);
            try
            {
                await command.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token);
                await File.WriteAllTextAsync(Path.Combine(root, "native-import.log"), await stdout + await stderr, token);
                Assert.Equal(0, command.ExitCode);
            }
            finally { if (!command.HasExited) { command.Kill(entireProcessTree: true); await command.WaitForExitAsync(CancellationToken.None); } }
        }
        var importedStore = new SidebandStateStore(importedProfile);
        var imported = (await importedStore.LoadAsync(cancellationToken: token)).Telemetry;
        Assert.True(imported.Mqtt.Enabled);
        Assert.Equal(primaryBroker.Port, imported.Mqtt.Port);
        Assert.Equal("synthetic-primary-mqtt-user", imported.Mqtt.Username);
        Assert.Equal("synthetic-mqtt-password", imported.Mqtt.Password);
        var isolated = Path.Combine(importedProfile, "reticulum");
        Directory.CreateDirectory(isolated);
        await File.WriteAllTextAsync(Path.Combine(isolated, "config"), "[reticulum]\n share_instance = no\n[interfaces]\n", token);
        for (var restart = 0; restart < 2; restart++)
        {
            await using var native = new NativeRetryConsole(binary, importedProfile, isolated,
                Path.Combine(root, $"imported-native-{restart}.log"));
            var ready = await native.WaitAsync("READY identity=", token);
            Assert.Contains("delivery=" + primary.HashHex, ready);
            Assert.Equal(imported.Mqtt, (await importedStore.LoadAsync(cancellationToken: token)).Telemetry.Mqtt);
            var information = "imported-native-only-" + restart;
            await native.SendAsync("config set sensors information_text " + information, token);
            await Observe(primary.HashHex, information, "synthetic-primary-mqtt-user");
            await native.QuitAsync(token);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "primary.log"), primary.Log, token);
        Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token))));

        async Task Observe(string context, string information, string username)
        {
            var topic = $"lxmf/telemetry/{context}/information/text";
            SidebandMqttObservedMessage observed;
            try
            {
                bool Matches(SidebandMqttObservedMessage message) => message.Topic == topic && message.Username == username &&
                    Encoding.UTF8.GetString(message.Payload) == information;
                observed = username == "synthetic-primary-mqtt-user"
                    ? await primaryBroker.ReadUntilAsync(Matches, token)
                    : await broker.ReadUntilAsync(Matches, token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                await File.WriteAllTextAsync(Path.Combine(root, "primary-failure.log"), primary.Log, CancellationToken.None);
                var snapshot = await primary.Command($"telemetry-snapshot {own}", "telemetry-snapshot", TimeSpan.FromSeconds(10));
                await File.WriteAllTextAsync(Path.Combine(root, "primary-telemetry-failure.json"),
                    snapshot.GetRawText(), CancellationToken.None);
                var mqtt = await primary.Command("mqtt-snapshot", "mqtt-snapshot", TimeSpan.FromSeconds(10));
                await File.WriteAllTextAsync(Path.Combine(root, "primary-mqtt-failure.json"),
                    mqtt.GetRawText(), CancellationToken.None);
                await File.WriteAllLinesAsync(Path.Combine(root, "broker-failure.txt"), broker.History.Select(message =>
                    message.Username + " " + message.Topic + " " + Encoding.UTF8.GetString(message.Payload)), CancellationToken.None);
                await File.WriteAllLinesAsync(Path.Combine(root, "broker-errors.txt"), broker.Errors, CancellationToken.None);
                throw;
            }
            Assert.Equal(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, observed.Quality);
            Assert.False(observed.Retain);
            await File.AppendAllTextAsync(Path.Combine(root, "broker-observations.txt"),
                $"{username} {topic} {information} qos=1 retain=false\n", token);
        }

        void VerifyPublishedGeoid(string context, int observationStart)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-mqtt.json")));
            var ordinary = fixture.RootElement.GetProperty("cases").EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == "location-geoid-0");
            var reference = ordinary.GetProperty("messages").EnumerateArray()
                .Single(item => item.GetProperty("topic").GetString()!.EndsWith("/location/altitude", StringComparison.Ordinal));
            var expected = double.Parse(Encoding.ASCII.GetString(Convert.FromHexString(reference.GetProperty("payload_hex").GetString()!)),
                CultureInfo.InvariantCulture);
            var observed = broker.History.Skip(observationStart).Last(message => message.Username == broker.Configuration.Username &&
                message.Topic == $"lxmf/telemetry/{context}/location/altitude");
            var actual = double.Parse(Encoding.ASCII.GetString(observed.Payload), CultureInfo.InvariantCulture);
            Assert.InRange(actual, expected - 1e-8, expected + 1e-8);
        }
    }
}
