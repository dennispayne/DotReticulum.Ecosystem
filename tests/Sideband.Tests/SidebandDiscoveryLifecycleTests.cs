using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DotReticulum.Applications;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandDiscoveryLifecycleTests
{
    [Fact]
    public async Task SharedClientReadsSelectedCacheAndAppliesActualOwnersBlackholePolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-shared-discovery-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        var probes = new[] { new TcpListener(IPAddress.Loopback, 0), new TcpListener(IPAddress.Loopback, 0) };
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            foreach (var probe in probes) probe.Start();
            var settings = $"""
                [reticulum]
                share_instance=yes
                shared_instance_type=tcp
                shared_instance_port={((IPEndPoint)probes[0].LocalEndpoint).Port}
                instance_control_port={((IPEndPoint)probes[1].LocalEndpoint).Port}
                rpc_key={Convert.ToHexStringLower(key)}
                discover_interfaces=no
                required_discovery_value=8
                autoconnect_discovered_interfaces=0
                """;
            foreach (var probe in probes) probe.Stop();
            var ownerConfig = Path.Combine(root, "owner-rns");
            var clientConfig = Path.Combine(root, "client-rns");
            foreach (var config in new[] { ownerConfig, clientConfig })
            {
                Directory.CreateDirectory(config);
                await File.WriteAllTextAsync(Path.Combine(config, "config"), settings, token);
            }
            using var source = Identity.Generate();
            var storage = Path.Combine(clientConfig, "storage", "discovery");
            await using (var seed = new DiscoveryService(storage,
                new DiscoveryOptions { Enabled = true, RequiredStampValue = 8 }))
            {
                var payload = DiscoveryCodec.Create(new DiscoveryAdvertisement
                {
                    TransportIdentity = source.Hash, Name = "Selected shared-client cache", Transport = true,
                    ReachableOn = "reference.example", Port = 4242, Latitude = 0, Longitude = 0, StampValue = 8,
                }, cancellationToken: token);
                Assert.True(await seed.ReceiveAsync(source, payload, 2, token));
            }
            await using var owner = ReticulumNode.Open(ownerConfig);
            await owner.StartAsync(token);
            await using var client = new SidebandClient(new SidebandStateStore(Path.Combine(root, "data")));
            await client.ConnectAsync(clientConfig, token);
            Assert.Equal(SharedInstanceRole.Client, client.ConnectionInfo!.SharedRole);
            Assert.Equal("Selected shared-client cache", Assert.Single((await client.GetMapDiscoveryAsync(token)).Interfaces).Name);
            Assert.True(Assert.IsType<bool>(await owner.CallManagementAsync(new Dictionary<object, object?>
            {
                ["blackhole_identity"] = source.Hash, ["until"] = null, ["reason"] = "Synthetic policy test",
            }, token)));
            Assert.Empty((await client.GetMapDiscoveryAsync(token)).Interfaces);
            Assert.Empty(Directory.GetFiles(storage));
            Assert.True(Assert.IsType<bool>(await owner.CallManagementAsync(new Dictionary<object, object?>
            {
                ["unblackhole_identity"] = source.Hash,
            }, token)));
            Assert.Empty((await client.GetMapDiscoveryAsync(token)).Interfaces);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            foreach (var probe in probes) probe.Stop();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SelectedCacheSnapshotExcludesCredentialsAndDoesNotStartDiscoveryOrAutoconnect()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-discovery-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var config = await Config(root, token);
            var storage = Path.Combine(config, "storage", "discovery");
            using var source = Identity.Generate();
            await using (var seed = new DiscoveryService(storage,
                new DiscoveryOptions { Enabled = true, RequiredStampValue = 8 }))
            {
                var payload = DiscoveryCodec.Create(new DiscoveryAdvertisement
                {
                    TransportIdentity = source.Hash, Name = "Synthetic cached interface", Transport = true,
                    ReachableOn = "reference.example", Port = 4242, Latitude = 0, Longitude = 0,
                    PublishIfac = true, NetworkName = "synthetic-network", Passphrase = "synthetic-test-secret",
                    StampValue = 8,
                }, cancellationToken: token);
                Assert.True(await seed.ReceiveAsync(source, payload, 2, token));
            }
            await using var client = new SidebandClient(new SidebandStateStore(Path.Combine(root, "data")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetMapDiscoveryAsync(token));
            await client.ConnectAsync(config, token);
            var snapshot = await client.GetMapDiscoveryAsync(token);
            Assert.Equal(512, snapshot.ScanCandidateLimit);
            Assert.Equal(256, snapshot.RecordLimit);
            var record = Assert.Single(snapshot.Interfaces);
            Assert.Equal(source.HexHash, record.NetworkIdentityHash);
            Assert.Equal(new SidebandGeoCoordinate(0, 0), record.Location);
            Assert.Equal("available", record.Status);
            Assert.DoesNotContain("synthetic-test-secret", record.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-network", record.ToString(), StringComparison.Ordinal);
            Assert.Single(SidebandMapDiscovery.Project(snapshot.Interfaces));
            Assert.Single(Directory.GetFiles(storage));
            Assert.False(client.ConnectionInfo!.SharedInstanceEnabled);
            var diagnostics = new List<string>();
            client.ErrorReported += diagnostics.Add;
            await using (var seed = new DiscoveryService(storage,
                new DiscoveryOptions { Enabled = true, RequiredStampValue = 8 }))
            {
                var invalidLocation = DiscoveryCodec.Create(new DiscoveryAdvertisement
                {
                    TransportIdentity = source.Hash, Name = "Out-of-range location", Transport = true,
                    ReachableOn = "reference.example", Port = 4242, Latitude = 91, Longitude = 0,
                    StampValue = 8,
                }, cancellationToken: token);
                Assert.True(await seed.ReceiveAsync(source, invalidLocation, 2, token));
            }
            snapshot = await client.GetMapDiscoveryAsync(token);
            Assert.Equal(2, snapshot.Interfaces.Count);
            Assert.Null(snapshot.Interfaces.Single(item => item.Name == "Out-of-range location").Location);
            Assert.Single(SidebandMapDiscovery.Project(snapshot.Interfaces));
            Assert.Contains(diagnostics, message => message.Contains("out-of-range coordinates", StringComparison.Ordinal));
            await client.DisconnectAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetMapDiscoveryAsync(token));
            await client.ConnectAsync(config, token);
            Assert.Equal(2, (await client.GetMapDiscoveryAsync(token)).Interfaces.Count);
            await client.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetMapDiscoveryAsync(token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AdmissionCallerCancellationAndDisconnectCancelQueuedLifecycleReadsAndAllowRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-discovery-lifecycle-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var config = await Config(root, token);
            await using var client = new SidebandClient(new SidebandStateStore(Path.Combine(root, "data")));
            await client.ConnectAsync(config, token);
            var lifecycle = Assert.IsType<SemaphoreSlim>(typeof(SidebandClient)
                .GetField("_lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client));
            await lifecycle.WaitAsync(token);
            try
            {
                using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
                var pending = client.GetMapDiscoveryAsync(caller.Token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetMapDiscoveryAsync(token));
                await caller.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2), token));
            }
            finally { lifecycle.Release(); }
            Assert.Empty((await client.GetMapDiscoveryAsync(token)).Interfaces);
            await lifecycle.WaitAsync(token);
            Task disconnect;
            try
            {
                var pending = client.GetMapDiscoveryAsync(token);
                disconnect = client.DisconnectAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2), token));
            }
            finally { lifecycle.Release(); }
            await disconnect.WaitAsync(TimeSpan.FromSeconds(5), token);
            await client.ConnectAsync(config, token);
            Assert.Empty((await client.GetMapDiscoveryAsync(token)).Interfaces);
            await lifecycle.WaitAsync(token);
            Task disposal;
            try
            {
                var pending = client.GetMapDiscoveryAsync(token);
                disposal = client.DisposeAsync().AsTask();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2), token));
            }
            finally { lifecycle.Release(); }
            await disposal.WaitAsync(TimeSpan.FromSeconds(5), token);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetMapDiscoveryAsync(token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    internal static async Task<string> Config(string root, CancellationToken token)
    {
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[reticulum]\nshare_instance=no\ndiscover_interfaces=no\nrequired_discovery_value=8\nautoconnect_discovered_interfaces=0\n", token);
        return config;
    }
}
