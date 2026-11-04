using System.Text.Json;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;
using LXMF;

namespace NomadNet.Tests;

// The original discard diagnostic is retained in artifacts/tests/nomad-node-migration/frozen-{win,linux}.
// Current shared-LXMF intentionally retains unresolved peers instead of reproducing upstream's drop.
// Fixture executes the unmodified primary NomadNetworkApp constructor and LXMPeer.to_bytes
// at f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2 / LXMF e8ba024 / RNS a29bfb5.
[Trait("Category", "Integration")]
public sealed class NomadNetNodeProfileBoundaryTests
{
    [Fact]
    public void ExecutedPrimaryPeerStoreHasNoPublicIdentityMaterial()
    {
        using var fixture = Fixture();
        var root = fixture.RootElement;
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", root.GetProperty("commit").GetString());
        Assert.True(root.GetProperty("propagationNode").GetBoolean());
        Assert.Equal(1, root.GetProperty("peersAfterSave").GetInt32());
        var fields = root.GetProperty("peerFields").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("peering_key", fields);
        Assert.DoesNotContain("identity", fields);
        Assert.DoesNotContain("public_key", fields);
        var peer = LxmPeer.FromBytes(Convert.FromHexString(root.GetProperty("peerHex").GetString()!), _ => false);
        Assert.Equal(23, peer.Offered);
        Assert.Equal(17, peer.Outgoing);
        Assert.Null(peer.PeeringKey);
        Assert.Equal(Convert.FromHexString(root.GetProperty("peerHashHex").GetString()!), peer.DestinationHash);
    }

    [Fact]
    public async Task PublicNodeLoaderSeparatelyRetainsUnresolvedPeersUntilIdentityRecall()
    {
        using var fixture = Fixture();
        foreach (var known in new[] { true, false })
        {
            var root = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-node-boundary", Guid.NewGuid().ToString("N")));
            try
            {
                await WriteStoresAsync(root, fixture.RootElement);
                using var identity = Identity.Generate();
                await using var interfaces = new PacketInterfaceManager([]);
                await using var transport = new TransportRouter(interfaces);
                if (known) Remember(transport, fixture.RootElement);
                await using (var node = new LxmPropagationNode(transport, identity, root))
                {
                    Assert.Equal(known ? 1 : 0, node.Peers.Count);
                    Assert.Equal(known ? 0 : 1, node.UnresolvedPeers.Count);
                    if (known) Assert.Equal(23, Assert.Single(node.Peers).Offered);
                    else Assert.Equal(23, Assert.Single(node.UnresolvedPeers).Offered);
                    Assert.Equal(0, node.MessageCount);
                }
                var saved = Assert.IsType<object?[]>(MsgPack.UnpackApplicationValue(
                    await File.ReadAllBytesAsync(Path.Combine(root, "lxmf", "peers"))));
                Assert.Single(saved);
                Assert.Equal(23, LxmPeer.FromBytes(Assert.IsType<byte[]>(saved[0]), _ => true).Offered);
                var stats = Assert.IsType<Dictionary<object, object?>>(MsgPack.UnpackApplicationValue(
                    await File.ReadAllBytesAsync(Path.Combine(root, "lxmf", "node_stats"))));
                Assert.Equal(7, MsgPack.ToInt64(stats["client_propagation_messages_received"]!));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task MatchingIdentityLearnedAfterRestartActivatesPreservedPeerWithoutReset()
    {
        using var fixture = Fixture();
        var root = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-node-boundary", Guid.NewGuid().ToString("N")));
        try
        {
            await WriteStoresAsync(root, fixture.RootElement);
            using var identity = Identity.Generate();
            await using var interfaces = new PacketInterfaceManager([]);
            await using var transport = new TransportRouter(interfaces);
            await using (var node = new LxmPropagationNode(transport, identity, root))
            {
                Assert.Empty(node.Peers);
                Assert.Equal(23, Assert.Single(node.UnresolvedPeers).Offered);
            }
            await using var restart = new LxmPropagationNode(transport, identity, root);
            Assert.Empty(restart.Peers);
            Assert.Equal(23, Assert.Single(restart.UnresolvedPeers).Offered);
            Remember(transport, fixture.RootElement);
            Assert.Equal(1, restart.ResolvePersistedPeers());
            Assert.Empty(restart.UnresolvedPeers);
            Assert.Equal(23, Assert.Single(restart.Peers).Offered);
            Assert.Equal(17, Assert.Single(restart.Peers).Outgoing);
            Assert.Equal(0, restart.ResolvePersistedPeers());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Remember(TransportRouter transport, JsonElement root) =>
        transport.Engine.Known.Remember(Convert.FromHexString(root.GetProperty("peerHashHex").GetString()!),
            new byte[32], Convert.FromHexString(root.GetProperty("publicKeyHex").GetString()!), null);

    private static async Task WriteStoresAsync(string root, JsonElement fixture)
    {
        Directory.CreateDirectory(Path.Combine(root, "lxmf"));
        await File.WriteAllBytesAsync(Path.Combine(root, "lxmf", "peers"), MsgPack.Pack(new object?[]
        {
            Convert.FromHexString(fixture.GetProperty("peerHex").GetString()!),
        }));
        await File.WriteAllBytesAsync(Path.Combine(root, "lxmf", "node_stats"),
            Convert.FromHexString(fixture.GetProperty("statsHex").GetString()!));
    }

    private static JsonDocument Fixture() => JsonDocument.Parse(typeof(NomadNetNodeProfileBoundaryTests).Assembly
        .GetManifestResourceStream("NomadNet.Tests.NomadNetNodeProfileBoundaryVectors.json")!);
}
