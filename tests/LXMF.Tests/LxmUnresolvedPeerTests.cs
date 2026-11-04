using DotReticulum.Core;

namespace LXMF.Tests;

[Trait("Category", "Integration")]
public sealed class LxmUnresolvedPeerTests
{
    [Fact]
    public async Task UnresolvedRecordSurvivesRestartThenSignedDiscoveryActivatesExactlyPreservedState()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "unresolved-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var pair = await LxmfPair.CreateAsync(clientOptions: new LxmRouterOptions { StoragePath = root },
                serverOptions: new LxmRouterOptions { StoragePath = Path.Combine(root, "sender") });
            var destination = Destination.ComputeHashFromNameAndIdentity("lxmf.propagation", pair.ServerIdentity.Hash);
            var peer = new LxmPeer(destination)
            {
                Alive = true, LastHeard = 2000000000, PeeringTimebase = 2000000000,
                Offered = 9007199254740993, Outgoing = 17, Incoming = 3, RxBytes = 99, TxBytes = 123,
                PeeringKey = Enumerable.Repeat((byte)42, 32).ToArray(), PeeringKeyValue = 7,
                StampCost = 16, StampFlexibility = 3, PeeringCost = 18,
                TransferLimitKilobytes = 256, SyncLimitKilobytes = 10240,
            };
            Directory.CreateDirectory(Path.Combine(root, "lxmf"));
            File.WriteAllBytes(Path.Combine(root, "lxmf", "peers"), MsgPack.Pack(new object?[] { peer.ToBytes() }));
            for (var restart = 0; restart < 2; restart++)
            {
                await using var unresolved = new LxmPropagationNode(pair.ClientRouter, pair.ClientIdentity, root,
                    new LxmPropagationOptions { AutoPeer = false });
                Assert.Empty(unresolved.Peers);
                Assert.Equal(peer.ToBytes(), Assert.Single(unresolved.UnresolvedPeers).ToBytes());
                await Assert.ThrowsAsync<IOException>(() => unresolved.SynchronizePeerAsync(destination));
                Assert.Equal(0, Assert.Single(unresolved.UnresolvedPeers).LastSyncAttempt);
            }
            if (!OperatingSystem.IsWindows())
                foreach (var name in new[] { "peers", "node_stats" })
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                        File.GetUnixFileMode(Path.Combine(root, "lxmf", name)));
            await using var receiver = new LxmPropagationNode(pair.ClientRouter, pair.ClientIdentity, root,
                new LxmPropagationOptions { AutoPeer = false });
            using var wrong = Identity.Generate();
            pair.ClientRouter.Engine.Known.Remember(destination, new byte[32], wrong.ExportPublicKey(), null);
            Assert.Equal(0, receiver.ResolvePersistedPeers());
            Assert.Single(receiver.UnresolvedPeers);
            Assert.True(pair.ClientRouter.Engine.Known.Forget(destination));
            var observed = 0;
            receiver.Log = _ => throw new IOException("optional observer");
            receiver.Log += _ => observed++;
            var sender = pair.Server.EnablePropagationNode(new LxmPropagationOptions { AutoPeer = false });
            await sender.AnnounceAsync();
            await pair.WaitAsync(() => receiver.UnresolvedPeers.Count == 0);
            Assert.Equal(peer.ToBytes(), Assert.Single(receiver.Peers).ToBytes());
            Assert.Equal(1, observed);
            Assert.Equal(0, receiver.ResolvePersistedPeers());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MalformedAndDuplicateUnresolvedRecordsFailBeforeAnyPeerFileIsRewritten()
    {
        using var identity = Identity.Generate();
        var peer = new LxmPeer(Destination.ComputeHashFromNameAndIdentity("lxmf.propagation", identity.Hash));
        var map = (Dictionary<object, object?>)MsgPack.UnpackApplicationValue(peer.ToBytes())!;
        var cases = new List<byte[]>
        {
            MsgPack.Pack(new object?[] { peer.ToBytes(), peer.ToBytes() }),
            MsgPack.Pack(new object?[] { new byte[] { 0xc1 } }),
            MsgPack.Pack(new object?[] { new byte[LxmPeer.MaximumPersistedBytes + 1] }),
        };
        foreach (var (name, value) in new (string, object?)[]
        {
            ("offered", -1L), ("outgoing", 1.5), ("peering_key", new object?[] { new byte[31], 18L }),
            ("sync_strategy", 3L), ("last_heard", double.NaN), ("metadata", new string('x', 100)),
            ("last_sync_attempt", null), ("sync_transfer_rate", null), ("link_establishment_rate", null),
            ("unexpected_identity", new byte[64]), ("propagation_stamp_cost", 257L),
            ("unhandled_ids", new object?[] { new byte[32], new byte[32] }),
        })
        {
            var changed = new Dictionary<object, object?>(map) { [name] = value };
            cases.Add(MsgPack.Pack(new object?[] { MsgPack.Pack(changed) }));
        }
        foreach (var bytes in cases)
        {
            var root = Path.Combine(AppContext.BaseDirectory, "bad-unresolved-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = Path.Combine(root, "lxmf", "peers");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                await using var interfaces = new PacketInterfaceManager([]);
                await using var transport = new DotReticulum.Transport.TransportRouter(interfaces);
                var error = Assert.ThrowsAny<Exception>(() => new LxmPropagationNode(transport, identity, root));
                Assert.True(error is FormatException or InvalidDataException);
                Assert.Equal(bytes, File.ReadAllBytes(path));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task ReentrantObserversAndConcurrentDisposalCannotResetOrDuplicateResolvedRecords()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "reentrant-unresolved-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var own = Identity.Generate();
            using var first = Identity.Generate();
            using var second = Identity.Generate();
            var peers = new[] { first, second }.Select((identity, index) =>
                new LxmPeer(Destination.ComputeHashFromNameAndIdentity("lxmf.propagation", identity.Hash))
                { Offered = 23 + index, Outgoing = 17 + index }).ToArray();
            var path = Path.Combine(root, "lxmf", "peers");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, MsgPack.Pack(peers.Select(peer => (object?)peer.ToBytes()).ToArray()));
            await using var interfaces = new PacketInterfaceManager([]);
            await using var transport = new DotReticulum.Transport.TransportRouter(interfaces);
            await using var node = new LxmPropagationNode(transport, own, root);
            Assert.Equal(2, node.UnresolvedPeers.Count);
            foreach (var identity in new[] { first, second })
                transport.Engine.Known.Remember(Destination.ComputeHashFromNameAndIdentity("lxmf.propagation", identity.Hash),
                    new byte[32], identity.ExportPublicKey(), null);
            var observed = 0;
            var nested = 0;
            node.Log = _ =>
            {
                nested += node.ResolvePersistedPeers();
                throw new IOException("optional reentrant observer");
            };
            node.Log += _ => observed++;
            Assert.Equal(2, node.ResolvePersistedPeers());
            Assert.Equal(2, observed);
            Assert.Equal(0, nested);
            Assert.Empty(node.UnresolvedPeers);
            foreach (var peer in peers)
                Assert.Equal(peer.ToBytes(), node.Peers.Single(active =>
                    active.DestinationHash.AsSpan().SequenceEqual(peer.DestinationHash)).ToBytes());
            var concurrent = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            {
                try { Assert.Equal(0, node.ResolvePersistedPeers()); }
                catch (ObjectDisposedException) { }
            })).ToArray();
            await node.DisposeAsync();
            await Task.WhenAll(concurrent);
            Assert.Throws<ObjectDisposedException>(() => node.ResolvePersistedPeers());
            var saved = (object?[])MsgPack.UnpackApplicationValue(File.ReadAllBytes(path))!;
            Assert.Equal(2, saved.Length);
            Assert.Equal(new long[] { 23, 24 }, saved.Cast<byte[]>().Select(bytes =>
                LxmPeer.FromBytes(bytes, _ => true).Offered).Order().ToArray());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void StrictPeerCodecRetainsFullIntegerCountersAndRejectsOverlappingIndices()
    {
        var peer = new LxmPeer(new byte[16]) { Offered = long.MaxValue, Outgoing = 9007199254740993 };
        var id = Enumerable.Repeat((byte)6, 32).ToArray();
        peer.AddUnhandled(id);
        var restored = LxmPeer.FromBytes(peer.ToBytes(), _ => true);
        Assert.Equal(long.MaxValue, restored.Offered);
        Assert.Equal(9007199254740993, restored.Outgoing);
        Assert.Equal(peer.ToBytes(), restored.ToBytes());
        var map = (Dictionary<object, object?>)MsgPack.UnpackApplicationValue(peer.ToBytes())!;
        map["handled_ids"] = new object?[] { id };
        Assert.Throws<FormatException>(() => LxmPeer.FromBytes(MsgPack.Pack(map), _ => true));
    }
}
