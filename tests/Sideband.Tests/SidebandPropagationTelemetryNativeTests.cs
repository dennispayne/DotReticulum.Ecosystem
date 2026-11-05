using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Core;
using LXMF.Tests.Interop;
using S = Sideband.SidebandTelemetrySensorId;
using static Sideband.Tests.SidebandMessageRetryNativeTests;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandPropagationTelemetryNativeTests
{
    public static bool Disabled => Environment.GetEnvironmentVariable("DOTSIDEBAND_PROPAGATION_APPLICATION") != "1";

    [Fact(SkipWhen = nameof(Disabled), Skip = "Requires explicit frozen published application and actual pinned primary lxmd.")]
    [Trait("Category", "Live")]
    [Trait("TestId", "T918")]
    public async Task PublishedSidebandAutomaticallyCollectsActualLxmdAndPersistsAcrossCleanAndAbruptRestartsWithExplicitFailureRecovery()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE"));
        var binary = Required("DOTRETICULUM_SIDEBAND_BINARY");
        var expectedHash = Required("DOTSIDEBAND_PROPAGATION_APPLICATION_SHA256");
        var evidence = Required("DOTSIDEBAND_PROPAGATION_EVIDENCE");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(3));
        var token = stop.Token;
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token)));
        Assert.Equal(expectedHash, hash);
        var root = Path.Combine(evidence, "application-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        await using var python = await PyLinkNode.StartAsync("PropagationApplication", tcpServerPort: port,
            harness: "sideband_propagation_telemetry_node.py");
        var ready = await python.NextAsync("ready", 0, TimeSpan.FromSeconds(5));
        var identityPath = ready.GetProperty("identity_path").GetString()!;
        using var identity = IdentityFile.Load(identityPath);
        var profile = Path.Combine(root, "profile");
        var network = Path.Combine(root, "network");
        Directory.CreateDirectory(network);
        await File.WriteAllTextAsync(Path.Combine(network, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n[logging]\n loglevel = 0\n[interfaces]\n" +
            $" [[Primary propagation sensor]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
        var store = new SidebandStateStore(profile);
        await store.ConfigureTelemetryAsync(new() { Enabled = true }, token);
        var observations = new List<object>();
        var acknowledgements = new Dictionary<(NativeRetryConsole Console, string Text), int>();
        try
        {
            string own;
            await using (var native = Console("initial"))
            {
                own = await Ready(native);
                await native.SendAsync("telemetry", token);
                await native.WaitAsync("time (Available)", token);
                Assert.False(native.ContainsLine("lxmf_propagation"));
                await Setting(native, "propagation_identity", identityPath);
                await Setting(native, "propagation", "true");
                await RefreshAvailable(native);
                Assert.True((await new SidebandStateStore(profile).LoadAsync(cancellationToken: token)).Telemetry.PropagationEnabled);
                await native.QuitAsync(token);
            }
            var first = await Latest();
            Assert.Equal(identity.Hash, first.Report.Get(S.LxmfPropagation)!.Data.Get("identity_hash")!.GetBinary());
            observations.Add(new { Case = "explicit-default-off-activation", WireHex = Convert.ToHexStringLower(first.GetPackedTelemetry()),
                PublicIdentityHash = Convert.ToHexStringLower(identity.Hash) });
            await Task.Delay(1100, token);
            await using (var native = Console("clean-restart"))
            {
                Assert.Equal(own, await Ready(native));
                // No telemetry console command: the application schedule collects from persisted opt-in.
                await Until(async () => (await Latest()).Report.TimestampUtcSeconds > first.Report.TimestampUtcSeconds);
                var automatic = await Latest();
                Assert.Equal(identity.Hash, automatic.Report.Get(S.LxmfPropagation)!.Data.Get("identity_hash")!.GetBinary());
                await Setting(native, "information_text", "synthetic abrupt restart marker");
                observations.Add(new { Case = "automatic-clean-restart", WireHex = Convert.ToHexStringLower(automatic.GetPackedTelemetry()) });
                // Dispose deliberately kills only this tracked process, simulating abrupt termination.
            }
            var beforeAbrupt = await Latest();
            await Task.Delay(1100, token);
            await using (var native = Console("abrupt-restart"))
            {
                Assert.Equal(own, await Ready(native));
                await Until(async () => (await Latest()).TimestampUtcSeconds > beforeAbrupt.TimestampUtcSeconds);
                Assert.Equal(SidebandTelemetryStatus.Available, (await Latest()).Report.Get(S.LxmfPropagation)!.Status);
                Assert.Equal("synthetic abrupt restart marker", (await new SidebandStateStore(profile).LoadAsync(cancellationToken: token)).Telemetry.Information);
                await Setting(native, "propagation", "false");
                Assert.Null((await Latest()).Report.Get(S.LxmfPropagation));
                var missing = Path.Combine(root, "unavailable-explicit-identity");
                await Setting(native, "propagation_identity", missing);
                await Setting(native, "propagation", "true");
                await native.WaitAsync("Propagation telemetry could not load/use the explicitly configured identity file.", token);
                Assert.Equal(SidebandTelemetryStatus.Unavailable, (await Latest()).Report.Get(S.LxmfPropagation)!.Status);
                Assert.False(File.Exists(missing));
                observations.Add(new { Case = "abrupt-restart-disable-unavailable-identity", Passed = true });
                await python.Command("control-policy deny", "control-policy", TimeSpan.FromSeconds(5));
                await Setting(native, "propagation_identity", identityPath);
                await native.WaitAsync("LXMF propagation telemetry status query failed: Timeout.", token);
                Assert.Equal(SidebandTelemetryStatus.Unavailable, (await Latest()).Report.Get(S.LxmfPropagation)!.Status);
                Assert.True((await new SidebandStateStore(profile).LoadAsync(cancellationToken: token)).Telemetry.PropagationEnabled);
                await python.Command("control-policy own", "control-policy", TimeSpan.FromSeconds(5));
                await Setting(native, "propagation", "false");
                await Setting(native, "propagation", "true");
                await RefreshAvailable(native);
                Assert.Equal(identity.Hash, (await Latest()).Report.Get(S.LxmfPropagation)!.Data.Get("identity_hash")!.GetBinary());
                observations.Add(new { Case = "actual-primary-authorization-timeout-explicit-toggle-recovery", Passed = true,
                    PolicyOnlyMutation = "router.control_allowed_list.clear(); restore via actual allow_control(identity.hash). Unmodified request handlers." });
                await native.QuitAsync(token);
            }
            Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token))));
            observations.Add(new { Case = "boundary", NativeSha256 = hash,
                RnsModule = ready.GetProperty("rns_module").GetString(), LxmdModule = ready.GetProperty("lxmd_module").GetString(),
                NotExercised = "Physical devices, Linux, hop/loss, live five-minute stale-error retention. Deterministic provider tests cover stale/error retention. Recovery here is explicit disable/re-enable, not a bypass of the300-second query gate." });
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(root, "observations.json"),
                JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(root, "primary.log"), python.Log, CancellationToken.None);
        }

        NativeRetryConsole Console(string phase) => new(binary, profile, network, Path.Combine(root, phase + ".log"));
        async Task<string> Ready(NativeRetryConsole native) =>
            (await native.WaitAsync("READY identity=", token)).Split("delivery=", StringSplitOptions.None)[1].Split(' ')[0];
        async Task RefreshAvailable(NativeRetryConsole native) =>
            await Until(async () =>
            {
                await Task.Delay(1100, token);
                await native.SendAsync("telemetry", token);
                await Task.Delay(100, token);
                return native.ContainsLine("lxmf_propagation (Available)");
            });
        async Task Setting(NativeRetryConsole native, string name, string value)
        {
            // Each mutation gets a separate persisted acknowledgement even for a repeated value.
            await Task.Delay(1100, token);
            var quoted = "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
            await native.SendAsync("config set sensors " + name + " " + quoted, token);
            var acknowledgement = "sensors " + name + " = " + value + " (";
            var key = (native, acknowledgement);
            acknowledgements.TryGetValue(key, out var previous);
            acknowledgements[key] = previous + 1;
            await native.WaitForOccurrencesAsync(acknowledgement, previous + 1, token);
        }
        async Task<SidebandTelemetryStoredReport> Latest()
        {
            // Read an atomic data-file snapshot, never contend for the application's exclusive writer lease.
            var snapshot = Path.Combine(root, "store-observation");
            Directory.CreateDirectory(snapshot);
            File.Copy(Path.Combine(profile, "telemetry", "telemetry.sbdb"), Path.Combine(snapshot, "telemetry.sbdb"), true);
            await using var telemetry = await SidebandTelemetryStore.OpenAsync(snapshot, cancellationToken: token);
            return (await telemetry.QueryAsync(new(Limit: 1), token)).Single();
        }
        async Task Until(Func<Task<bool>> condition)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (!await condition()) await Task.Delay(50, timeout.Token);
        }
        static string Required(string name) =>
            Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Select " + name + ".");
    }
}
