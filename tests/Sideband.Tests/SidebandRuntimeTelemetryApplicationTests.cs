using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DotReticulum.Applications;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandRuntimeTelemetryApplicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptInUsesActualOwnerStatisticsAndLocalIdentityWithBoundedCollectionAndRestart(bool shared)
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "sideband-runtime-" + Guid.NewGuid().ToString("N"));
        var probes = Enumerable.Range(0, 3).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            foreach (var probe in probes) probe.Start();
            var ports = probes.Select(probe => ((IPEndPoint)probe.LocalEndpoint).Port).ToArray();
            foreach (var probe in probes) probe.Stop();
            var ownerConfig = Path.Combine(root, "owner");
            var clientConfig = shared ? Path.Combine(root, "client") : ownerConfig;
            var network = $"""
                [reticulum]
                enable_transport=yes
                share_instance={(shared ? "yes" : "no")}
                shared_instance_type=tcp
                shared_instance_port={ports[0]}
                instance_control_port={ports[1]}
                rpc_key={Convert.ToHexStringLower(key)}
                [interfaces]
                [[OwnerOnlyTelemetry]]
                type=TCPServerInterface
                enabled=yes
                listen_ip=127.0.0.1
                listen_port={ports[2]}
                """;
            Directory.CreateDirectory(ownerConfig);
            await File.WriteAllTextAsync(Path.Combine(ownerConfig, "config"), network, token);
            if (shared)
            {
                Directory.CreateDirectory(clientConfig);
                await File.WriteAllTextAsync(Path.Combine(clientConfig, "config"), $"""
                    [reticulum]
                    enable_transport=no
                    static_transport_identity=yes
                    share_instance=yes
                    shared_instance_type=tcp
                    shared_instance_port={ports[0]}
                    instance_control_port={ports[1]}
                    rpc_key={Convert.ToHexStringLower(key)}
                    """, token);
            }
            await using var owner = shared ? ReticulumNode.Open(ownerConfig) : null;
            if (owner is not null) await owner.StartAsync(token);
            var data = Path.Combine(root, "data");
            var store = new SidebandStateStore(data);
            await using var client = new SidebandClient(store);
            await client.ConfigureTelemetryAsync(new() { Enabled = true, RnsTransportEnabled = true }, token);
            var offline = (await client.RefreshLocalTelemetryAsync(token))!.Get(S.RnsTransport)!;
            Assert.Equal(SidebandTelemetryStatus.Unavailable, offline.Status);
            Assert.Contains("Connect", offline.Error);
            await client.ConnectAsync(clientConfig, token);
            if (shared) Assert.Equal(SharedInstanceRole.Client, client.ConnectionInfo!.SharedRole);
            var report = (await client.RefreshLocalTelemetryAsync(token))!.Get(S.RnsTransport)!;
            Assert.True(report.Data.Get("transport_enabled")!.Boolean);
            Assert.True(report.Data.Get("ifstats")!.IsNil);
            Assert.True(report.Data.Get("path_table")!.IsNil);
            Assert.True(report.Data.Get("memory_used")!.Number > 0);
            using var transportIdentity = IdentityFile.Load(Path.Combine(clientConfig, "storage", "transport_identity"));
            Assert.Equal(transportIdentity.Hash, report.Data.Get("transport_identity")!.GetBinary());
            if (owner is not null) Assert.NotEqual(owner.Router.TransportId, transportIdentity.Hash);
            await client.ConfigureTelemetryAsync((await client.LoadAsync(token)).Telemetry with
                { RnsTransportIncludeTables = true }, token);
            var dense = (await client.RefreshLocalTelemetryAsync(token))!.Get(S.RnsTransport)!;
            Assert.Contains(dense.Data.Get("ifstats")!.Get("interfaces")!.Items,
                item => item.Get("name")?.Text == "OwnerOnlyTelemetry");
            Assert.Null(dense.Data.Get("ifstats")!.Get("rss"));
            Assert.Equal(dense.Data.Get("path_count")!.Integer, (long)dense.Data.Get("path_table")!.Items.Count);
            await Task.Delay(1500, token);
            Assert.Contains(await client.GetTelemetryAsync(cancellationToken: token),
                saved => saved.Report.Get(S.RnsTransport)?.Data.Get("transport_enabled")?.Boolean == true);
            var destination = client.ConnectionInfo!.DeliveryHash;
            await client.DisconnectAsync();
            Assert.Equal(SidebandTelemetryStatus.Unavailable,
                (await client.RefreshLocalTelemetryAsync(token))!.Get(S.RnsTransport)!.Status);
            await client.ConnectAsync(clientConfig, token);
            Assert.Equal(destination, client.ConnectionInfo!.DeliveryHash);
            Assert.True((await client.RefreshLocalTelemetryAsync(token))!.Get(S.RnsTransport)!.Data.Get("transport_enabled")!.Boolean);
            await client.ConfigureTelemetryAsync((await client.LoadAsync(token)).Telemetry with
                { RnsTransportEnabled = false }, token);
            Assert.Null((await client.RefreshLocalTelemetryAsync(token))!.Get(S.RnsTransport));
            await client.DisposeAsync();
            await using var reopened = new SidebandClient(new(data));
            Assert.False((await reopened.LoadAsync(token)).Telemetry.RnsTransportEnabled);
            Assert.True((await reopened.LoadAsync(token)).Telemetry.RnsTransportIncludeTables);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            foreach (var probe in probes) probe.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AutomaticManualConflictRejectsConfigurationWithoutMutation()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "sideband-runtime-conflict-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var client = new SidebandClient(new(root));
            await client.ConfigureTelemetryAsync(new() { Enabled = true }, token);
            await Assert.ThrowsAsync<InvalidDataException>(() => client.ConfigureTelemetryAsync(new()
            {
                Enabled = true, RnsTransportEnabled = true,
                ManualSensors = [new((byte)S.RnsTransport, "80")]
            }, token));
            Assert.False((await client.LoadAsync(token)).Telemetry.RnsTransportEnabled);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
