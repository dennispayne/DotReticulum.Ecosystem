namespace DotLXMF.Tests;

[Trait("Category", "Integration")]
public class LxmPropagationTests
{
    [Fact]
    public async Task StoreCapacity_PreservesLowerWeightPriorityMessages()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "propagation-capacity-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
            using var preferredIdentity = DotReticulum.Core.Identity.Generate();
            var preferred = LxMessage.DeliveryDestinationHash(preferredIdentity);
            pair.ClientRouter.Engine.Known.Remember(preferred, new byte[32], preferredIdentity.ExportPublicKey(), null);
            var node = pair.Server.EnablePropagationNode(new LxmPropagationOptions
            {
                StampCost = 13, StorageLimit = 800, AutoPeer = false, PrioritisedDestinations = [preferred],
            });
            await node.AnnounceAsync();
            await pair.WaitAsync(() => pair.ClientRouter.HasPath(node.DestinationHash));
            pair.Client.OutboundPropagationNode = node.DestinationHash;
            var first = LxMessage.Create(pair.Client.DeliveryHash, pair.ClientIdentity, new string('a', 32), method: LxmMethod.Propagated);
            await pair.Client.SendAsync(first);
            Assert.Equal(LxmState.Sent, await first.Completion.WaitAsync(TimeSpan.FromSeconds(60)));
            await pair.WaitAsync(() => node.GetSize(first.TransientId!) is not null);
            var large = LxMessage.Create(pair.Client.DeliveryHash, pair.ClientIdentity, new string('b', 384), method: LxmMethod.Propagated);
            await pair.Client.SendAsync(large);
            Assert.Equal(LxmState.Sent, await large.Completion.WaitAsync(TimeSpan.FromSeconds(60)));
            var priority = LxMessage.Create(preferred, pair.ClientIdentity, new string('c', 384), method: LxmMethod.Propagated);
            await pair.Client.SendAsync(priority);
            Assert.Equal(LxmState.Sent, await priority.Completion.WaitAsync(TimeSpan.FromSeconds(60)));
            await pair.WaitAsync(() => node.GetSize(priority.TransientId!) is not null);
            Assert.Null(node.GetSize(first.TransientId!));
            Assert.Null(node.GetSize(large.TransientId!));
            Assert.InRange(node.StoredBytes, 1, 800);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task PeerRotation_MatchesUpstreamAndProtectsStaticPeers()
    {
        using var vectors = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-contract-vectors.json")));
        foreach (var testCase in vectors.RootElement.GetProperty("peer_rotation").EnumerateArray())
        {
            var path = Path.Combine(AppContext.BaseDirectory, "propagation-rotation-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(path, "lxmf"));
                File.WriteAllBytes(Path.Combine(path, "lxmf", "peers"), DotReticulum.Core.MsgPack.Pack(
                    testCase.GetProperty("peers").EnumerateArray().Select(peer => (object?)Convert.FromHexString(peer.GetString()!)).ToArray()));
                await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
                var node = pair.Server.EnablePropagationNode(new LxmPropagationOptions
                {
                    MaxPeers = 3,
                    AutoPeer = false,
                    StaticPeers = testCase.GetProperty("static").EnumerateArray().Select(peer => Convert.FromHexString(peer.GetString()!)).ToArray(),
                });
                Assert.Equal(1, node.RotatePeers());
                Assert.Equal(testCase.GetProperty("remaining").EnumerateArray().Select(peer => peer.GetString()),
                    node.Peers.Select(peer => Convert.ToHexStringLower(peer.DestinationHash)));
            }
            finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlRequests_RequireSeparateIdentityAuthorization(bool allowControl)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "propagation-control-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
            var node = pair.Server.EnablePropagationNode(new LxmPropagationOptions
            {
                AutoPeer = false,
                ControlAllowedIdentities = allowControl ? [pair.ClientIdentity.Hash] : [],
                AllowedIdentities = [pair.ClientIdentity.Hash],
                RequireAuthentication = true,
            });
            await pair.ClientRouter.RequestPathAsync(node.ControlDestinationHash);
            await pair.WaitAsync(() => pair.ClientRouter.HasPath(node.ControlDestinationHash));
            var active = new TaskCompletionSource<DotReticulum.Transport.Link>(TaskCreationOptions.RunContinuationsAsynchronously);
            var link = pair.ClientRouter.Links.Connect(pair.ServerIdentity, node.ControlDestinationHash, ready => active.TrySetResult(ready));
            await active.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                var unidentified = link.Request("/pn/get/stats", null, useApplicationValues: true)!;
                Assert.Equal(0xf0L, await unidentified.ResponseTask.WaitAsync(TimeSpan.FromSeconds(10)));
                link.Identify(pair.ClientIdentity);
                var stats = link.Request("/pn/get/stats", null, useApplicationValues: true)!;
                var result = await stats.ResponseTask.WaitAsync(TimeSpan.FromSeconds(10));
                if (!allowControl)
                {
                    Assert.Equal(0xf1L, result);
                }
                else
                {
                    var values = Assert.IsType<Dictionary<object, object?>>(result);
                    Assert.Equal(node.DestinationHash, Assert.IsType<byte[]>(values["destination_hash"]));
                    var invalid = link.Request("/pn/peer/sync", "not a destination", useApplicationValues: true)!;
                    Assert.Equal(0xf4L, await invalid.ResponseTask.WaitAsync(TimeSpan.FromSeconds(10)));
                    var absent = link.Request("/pn/peer/unpeer", new byte[16], useApplicationValues: true)!;
                    Assert.Equal(0xfdL, await absent.ResponseTask.WaitAsync(TimeSpan.FromSeconds(10)));
                }
            }
            finally { link.Teardown(); }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task PropagatedMessage_IsStoredRetrievedAndAcknowledged()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "propagation-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
            var node = pair.Server.EnablePropagationNode(new LxmPropagationOptions { StampCost = 13, StampFlexibility = 3, PeeringCost = 0 });
            await node.AnnounceAsync();
            await pair.WaitAsync(() => pair.ClientRouter.HasPath(node.DestinationHash));
            pair.Client.OutboundPropagationNode = node.DestinationHash;
            pair.ClientRouter.Engine.Known.Remember(pair.Client.DeliveryHash, new byte[32], pair.ClientIdentity.ExportPublicKey(), null);
            var message = LxMessage.Create(pair.Client.DeliveryHash, pair.ClientIdentity, "stored message", method: LxmMethod.Propagated);
            await pair.Client.SendAsync(message);
            Assert.Equal(LxmState.Sent, await message.Completion.WaitAsync(TimeSpan.FromSeconds(60)));
            await pair.WaitAsync(() => node.MessageCount == 1);
            Assert.Equal(1, await pair.Client.RequestMessagesFromPropagationNodeAsync());
            await pair.WaitAsync(() => pair.ClientInbox.Count == 1);
            Assert.Equal("stored message", pair.ClientInbox[0].Message.Content);
            Assert.Equal(LxmMethod.Propagated, pair.ClientInbox[0].Message.Method);
            Assert.True(pair.ClientInbox[0].Message.SignatureValidated);
            Assert.Equal(0, node.MessageCount);
            Assert.Equal(0, await pair.Client.RequestMessagesFromPropagationNodeAsync());
            await node.DisposeAsync();
            await using var reopened = new LxmPropagationNode(pair.ServerRouter, pair.ServerIdentity, path);
            var clients = Assert.IsType<Dictionary<object, object?>>(reopened.CompileStats()["clients"]);
            Assert.Equal(1L, clients["client_propagation_messages_received"]);
            Assert.Equal(1L, clients["client_propagation_messages_served"]);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }
}
