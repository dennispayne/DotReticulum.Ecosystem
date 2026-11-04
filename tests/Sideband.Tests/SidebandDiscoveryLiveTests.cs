using System.Net;
using System.Net.Sockets;
using DotReticulum.Applications;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Transport;
using LXMF.Tests.Interop;

namespace Sideband.Tests;

public sealed partial class SidebandLiveInteropTests
{
    [Theory(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Actual primary discovery requires DOTSIDEBAND_LIVE=1, DOTRETICULUM_LIVE=1 and pinned Python/RNS/Sideband paths.")]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryPassiveDiscoveryAndNativeMapExchangeAdvertisementsAcrossRestart(bool nativeTransport)
    {
        var token = TestContext.Current.CancellationToken;
        var source = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")
            ?? throw new InvalidOperationException("Set DOTRETICULUM_SIDEBAND_UPSTREAM.");
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-discovery-live-" + Guid.NewGuid().ToString("N"));
        int port;
        using (var socket = new TcpListener(IPAddress.Loopback, 0))
        {
            socket.Start();
            port = ((IPEndPoint)socket.LocalEndpoint).Port;
        }
        try
        {
            await using var hub = nativeTransport ? ReticulumNode.Create(RnsdConfigParser.Parse($"""
                [reticulum]
                share_instance=no
                enable_transport=yes
                [interfaces]
                [[Discovery hub]]
                type=TCPServerInterface
                enabled=yes
                listen_ip=127.0.0.1
                listen_port={port}
                """), Path.Combine(root, "hub")) : null;
            if (hub is not null) await hub.StartAsync(token);
            await using var python = await PyLinkNode.StartAsync("ActualPrimaryDiscovery",
                tcpServerPort: nativeTransport ? 0 : port, rnsdPort: nativeTransport ? port : 0,
                harness: "sideband_discovery_node.py", extraArguments: ["--source", source]);
            var ready = Assert.Single(python.Events("ready"));
            Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", ready.GetProperty("revision").GetString());
            Assert.Equal("5b962d9ed2ccbc066bb19865d78c77da9ecf4008900f6f0e7e9dd1eb1c4767aa",
                ready.GetProperty("source_sha256").GetString());
            var config = Path.Combine(root, "rns");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"), $"""
                [reticulum]
                share_instance=no
                discover_interfaces=yes
                required_discovery_value=8
                autoconnect_discovered_interfaces=0
                [interfaces]
                [[Discovery feed]]
                type=TCPClientInterface
                enabled=yes
                target_host=127.0.0.1
                target_port={port}
                """, token);
            await using var client = new SidebandClient(new SidebandStateStore(Path.Combine(root, "data")));
            await client.ConnectAsync(config, token);
            await python.WaitForAnnounce(Convert.FromHexString((await client.LoadAsync(token)).OwnDeliveryHash!),
                TimeSpan.FromSeconds(30));
            await python.Command("announce", "announced", TimeSpan.FromSeconds(30));
            SidebandDiscoveredInterface? received = null;
            await Await(async () =>
            {
                var snapshot = await client.GetMapDiscoveryAsync(token);
                received = snapshot.Interfaces.SingleOrDefault(item => item.Name == "Actual Python discovery");
                return received is not null;
            });
            Assert.Equal(ready.GetProperty("identity").GetString(), received!.NetworkIdentityHash);
            Assert.True(received.Transport);
            Assert.Equal(new SidebandGeoCoordinate(0, 0), received.Location);
            Assert.True(received.StampValue >= 8);
            Assert.DoesNotContain("synthetic-discovery-secret", received.ToString(), StringComparison.Ordinal);
            Assert.Equal(SidebandMapOverlayKind.Discovery,
                Assert.Single(SidebandMapDiscovery.Project([received])).Kind);
            var discoveryHash = received.DiscoveryHash;
            await client.DisconnectAsync();
            await client.ConnectAsync(config, token);
            Assert.Contains((await client.GetMapDiscoveryAsync(token)).Interfaces, item => item.DiscoveryHash == discoveryHash);

            await using var nativeAdvertiser = ReticulumNode.Create(RnsdConfigParser.Parse($"""
                [reticulum]
                share_instance=no
                [interfaces]
                [[Advertise via hub]]
                type=TCPClientInterface
                enabled=yes
                target_host=127.0.0.1
                target_port={port}
                """), Path.Combine(root, "advertiser"));
            await nativeAdvertiser.StartAsync(token);
            using var identity = Identity.Generate();
            using var destination = new LocalDestination(identity,
                Destination.Create(DestinationType.Single, "rnstransport", ["discovery", "interface"], identity));
            nativeAdvertiser.Router.RegisterLocalDestination(destination);
            var payload = DiscoveryCodec.Create(new DiscoveryAdvertisement
            {
                TransportIdentity = identity.Hash, Transport = true, Name = "Native map discovery",
                ReachableOn = "native.example", Port = 4242, Latitude = 51.5, Longitude = -0.1,
                StampValue = 8,
            }, cancellationToken: token);
            await nativeAdvertiser.Router.AnnounceAsync(destination, cancellationToken: token, appData: payload);
            await Await(async () =>
            {
                var listed = await python.Command("list", "listed", TimeSpan.FromSeconds(5));
                Assert.True(listed.GetProperty("passive").GetBoolean());
                var match = listed.GetProperty("interfaces").EnumerateArray()
                    .Where(item => item.GetProperty("name").GetString() == "Native map discovery").ToArray();
                if (match.Length == 0) return false;
                var row = Assert.Single(match);
                Assert.Equal(identity.HexHash, row.GetProperty("network_id").GetString());
                Assert.Equal(51.5, row.GetProperty("latitude").GetDouble());
                Assert.Equal(-0.1, row.GetProperty("longitude").GetDouble());
                Assert.Equal("available", row.GetProperty("status").GetString());
                Assert.True(row.GetProperty("hops").GetInt32() >= (nativeTransport ? 2 : 1));
                return true;
            });
            Assert.Empty((await client.LoadAsync(token)).Contacts);

            async Task Await(Func<Task<bool>> condition)
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!await condition())
                {
                    Assert.True(DateTime.UtcNow < deadline, "Actual discovery announcement/cache observation timed out.\n" + python.Log);
                    await Task.Delay(100, token);
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
