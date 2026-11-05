using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DotReticulum.Applications;
using DotReticulum.Core;
using LXMF;
using LXMF.Tests.Interop;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandPropagationTelemetryLiveTests
{
    public static bool Disabled => Environment.GetEnvironmentVariable("DOTSIDEBAND_PROPAGATION_LIVE") != "1" ||
        Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1";

    [Fact(SkipWhen = nameof(Disabled), Skip = "Requires DOTSIDEBAND_PROPAGATION_LIVE=1, DOTRETICULUM_LIVE=1 and pinned Python/RNS references.")]
    [Trait("Category", "Live")]
    [Trait("TestId", "T910")]
    public async Task ActualPrimaryLxmdAndNativeStatusBothDirectionsAuthorizationFailureAndRecovery()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(2));
        var token = stop.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var root = Path.Combine(AppContext.BaseDirectory, "propagation-sensor-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var receipts = new List<object>();
        try
        {
            var config = Path.Combine(root, "native");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"),
                $"[reticulum]\n share_instance = no\n enable_transport = no\n[logging]\n loglevel = 0\n[interfaces]\n [[Primary]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
            await using var python = await PyLinkNode.StartAsync("PropagationSensor", tcpServerPort: port,
                harness: "sideband_propagation_telemetry_node.py");
            var ready = await python.NextAsync("ready", 0, TimeSpan.FromSeconds(5));
            using var primaryIdentity = IdentityFile.Load(ready.GetProperty("identity_path").GetString()!);
            await using var native = ReticulumNode.Open(config);
            await native.StartAsync(token);
            var statusClient = new LxmPropagationStatusClient(native.Router);
            var source = new SidebandPropagationTelemetrySource(native.Router);
            await using var provider = new SidebandPropagationTelemetry(primaryIdentity, source);
            var report = await provider.CollectAsync(token);
            Assert.Equal(SidebandPropagationTelemetryFailure.None, report.Failure);
            Assert.Equal(primaryIdentity.Hash, report.Reading.Data.Get("identity_hash")!.GetBinary());
            receipts.Add(new { Case = "native-provider-to-actual-lxmd", Failure = report.Failure.ToString(),
                WireHex = Convert.ToHexStringLower(SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.FromSensors(report.Reading))) });

            using var stranger = Identity.Generate();
            var denied = await statusClient.QueryAsync(stranger, primaryIdentity, cancellationToken: token);
            // Actual RNS ALLOW_LIST rejects before stats_get_request, so an unauthorized request times out rather than returning0xf1.
            Assert.Equal(LxmPropagationStatusClient.ErrorTimeout, Assert.IsType<long>(denied));
            receipts.Add(new { Case = "actual-lxmd-unauthorized-request", Response = denied });
            await python.Command("allow " + Convert.ToHexStringLower(stranger.Hash), "allowed", TimeSpan.FromSeconds(5));
            var allowed = await statusClient.QueryAsync(stranger, primaryIdentity, cancellationToken: token);
            Assert.IsType<Dictionary<object, object?>>(allowed);
            receipts.Add(new { Case = "actual-lxmd-authorized-recovery", Passed = true });

            using var nativeIdentity = Identity.Generate();
            await using var router = new LxmRouter(native.Router, nativeIdentity, options: new LxmRouterOptions { StoragePath = Path.Combine(root, "store") });
            var pn = router.EnablePropagationNode(new LxmPropagationOptions { AutoPeer = false });
            await pn.AnnounceAsync(token);
            var remoteKey = Convert.ToHexStringLower(nativeIdentity.ExportPublicKey());
            var nativeDenied = await python.Command("query " + remoteKey, "query", TimeSpan.FromSeconds(15));
            // Existing native control policy reports0xf1 explicitly, unlike primary RNS ALLOW_LIST silence above.
            Assert.Equal(LxmPropagationStatusClient.ErrorNoAccess,
                V.Decode(Convert.FromHexString(nativeDenied.GetProperty("response_hex").GetString()!)).Integer);
            pn.AllowControl(primaryIdentity.Hash);
            var nativeAllowed = await python.Command("query " + remoteKey, "query", TimeSpan.FromSeconds(15));
            var nativeStats = V.Decode(Convert.FromHexString(nativeAllowed.GetProperty("response_hex").GetString()!));
            Assert.Equal(nativeIdentity.Hash, nativeStats.Get("identity_hash")!.GetBinary());
            receipts.Add(new { Case = "actual-lxmd-query-to-native-authorization-recovery", Passed = true,
                DeniedResponseHex = nativeDenied.GetProperty("response_hex").GetString(),
                ResponseHex = nativeAllowed.GetProperty("response_hex").GetString() });
            await pn.DisposeAsync();
            using var missing = Identity.Generate();
            Assert.Equal(LxmPropagationStatusClient.ErrorTimeout,
                await statusClient.QueryAsync(primaryIdentity, missing, TimeSpan.FromMilliseconds(200), cancellationToken: token));
            using var canceled = new CancellationTokenSource();
            await canceled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                statusClient.QueryAsync(primaryIdentity, cancellationToken: canceled.Token));
            receipts.Add(new { Case = "missing-path-timeout-and-cancellation", Passed = true });
            var recovered = await source.QueryAsync(primaryIdentity, token);
            Assert.Equal(primaryIdentity.Hash, recovered.Get("identity_hash")!.GetBinary());
            receipts.Add(new { Case = "primary-source-healthy-after-failed-peer", Passed = true,
                RnsModule = ready.GetProperty("rns_module").GetString(), LxmdModule = ready.GetProperty("lxmd_module").GetString() });
        }
        finally
        {
            var evidence = Environment.GetEnvironmentVariable("DOTSIDEBAND_PROPAGATION_EVIDENCE");
            if (!string.IsNullOrEmpty(evidence))
            {
                Directory.CreateDirectory(evidence);
                await File.WriteAllTextAsync(Path.Combine(evidence, "live-observations.json"),
                    JsonSerializer.Serialize(receipts, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
            }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
