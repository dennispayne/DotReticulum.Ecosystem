namespace DotLXMF.Tests;

[Trait("Category", "Integration")]
public class LxmPropagationTests
{
    [Fact]
    public async Task RetrievalAuthorizationError_PreservesSourceErrorUntilResetAndRecoversAfterAllow()
    {
        using var vectors = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "lxmf-retrieval-vectors.json")));
        var path = Path.Combine(AppContext.BaseDirectory, "retrieval-access-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
            var node = pair.Server.EnablePropagationNode(new LxmPropagationOptions { AutoPeer = false, RequireAuthentication = true });
            await node.AnnounceAsync();
            await pair.WaitAsync(() => pair.ClientRouter.HasPath(node.DestinationHash));
            await Assert.ThrowsAsync<IOException>(() => pair.Client.RequestMessagesFromPropagationNodeAsync(node.DestinationHash));
            pair.Client.AcknowledgeSyncCompletion();
            Assert.Equal((LxmPropagationTransferState)vectors.RootElement.GetProperty("acknowledge_error").GetProperty("state").GetInt32(),
                pair.Client.PropagationTransferState);
            Assert.Null(pair.Client.PropagationLinkId);
            Assert.Null(pair.Client.PropagationTransferSize);
            Assert.Null(pair.Client.PropagationTransferLastResult);
            pair.Client.AcknowledgeSyncCompletion(resetState: true);
            Assert.Equal(LxmPropagationTransferState.Idle, pair.Client.PropagationTransferState);
            node.AllowIdentity(pair.ClientIdentity.Hash);
            Assert.Equal(0, await pair.Client.RequestMessagesFromPropagationNodeAsync(node.DestinationHash));
            Assert.Equal(LxmPropagationTransferState.Complete, pair.Client.PropagationTransferState);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task Retrieval_ReusesCachedLinkAndMatchesSourceCompletionResetAndCancellation()
    {
        using var vectors = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "lxmf-retrieval-vectors.json")));
        var empty = vectors.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("label").GetString() == "empty-list");
        var path = Path.Combine(AppContext.BaseDirectory, "retrieval-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
            var node = pair.Server.EnablePropagationNode(new LxmPropagationOptions { AutoPeer = false });
            await node.AnnounceAsync();
            await pair.WaitAsync(() => pair.ClientRouter.HasPath(node.DestinationHash));
            pair.Client.OutboundPropagationNode = node.DestinationHash;
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            pair.Client.Log = errors.Enqueue;
            var observations = 0;
            pair.Client.PropagationProgressChanged = _ => throw new IOException("observer isolation");
            pair.Client.PropagationProgressChanged += _ => observations++;
            Assert.Equal(0, await pair.Client.RequestMessagesFromPropagationNodeAsync());
            Assert.Equal((LxmPropagationTransferState)empty.GetProperty("state").GetInt32(), pair.Client.PropagationTransferState);
            Assert.Equal(empty.GetProperty("progress").GetDouble(), pair.Client.PropagationTransferProgress);
            Assert.Equal(empty.GetProperty("last_result").GetInt32(), pair.Client.PropagationTransferLastResult);
            var firstLink = Assert.IsType<byte[]>(pair.Client.PropagationLinkId);
            Assert.Equal(0, await pair.Client.RequestMessagesFromPropagationNodeAsync(maxMessages: 1));
            Assert.Equal(1, pair.Client.PropagationTransferMaxMessages);
            Assert.Equal(firstLink, pair.Client.PropagationLinkId);
            pair.Client.AcknowledgeSyncCompletion();
            Assert.Equal(LxmPropagationTransferState.Idle, pair.Client.PropagationTransferState);
            Assert.Equal(0, pair.Client.PropagationTransferProgress);
            Assert.Null(pair.Client.PropagationTransferSize);
            Assert.Null(pair.Client.PropagationTransferLastResult);
            Assert.Equal(firstLink, pair.Client.PropagationLinkId);
            pair.Client.CancelPropagationNodeRequests();
            Assert.Null(pair.Client.PropagationLinkId);
            Assert.True(observations >= 4);
            Assert.Contains(errors, error => error.Contains("propagation progress observer failed", StringComparison.Ordinal));
            pair.Client.PropagationProgressChanged = null;
            Assert.Equal(0, await pair.Client.RequestMessagesFromPropagationNodeAsync());
            Assert.NotEqual(firstLink, pair.Client.PropagationLinkId);
            var pending = pair.Client.RequestMessagesFromPropagationNodeAsync(Enumerable.Repeat((byte)9, 16).ToArray());
            await pair.WaitAsync(() => pair.Client.PropagationTransferState == LxmPropagationTransferState.PathRequested);
            pair.Client.CancelPropagationNodeRequests();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(LxmPropagationTransferState.Idle, pair.Client.PropagationTransferState);
            Assert.Null(pair.Client.PropagationLinkId);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task PropagationPathResponse_AdvertisesConfigurationWithoutPriorAnnounce()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "propagation-path-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
            var node = pair.Server.EnablePropagationNode(new LxmPropagationOptions { Name = "Path-only PN", AutoPeer = false, SyncLimit = 1000 });
            Assert.False(pair.ClientRouter.HasPath(node.DestinationHash));
            await pair.ClientRouter.RequestPathAsync(node.DestinationHash);
            await pair.WaitAsync(() => LxmfPropagationAnnounce.TryParse(pair.ClientRouter.Engine.Known.RecallAppData(node.DestinationHash), out _));
            Assert.True(LxmfPropagationAnnounce.TryParse(pair.ClientRouter.Engine.Known.RecallAppData(node.DestinationHash), out var announce));
            Assert.Equal(16, announce!.StampCost);
            Assert.Equal(256, announce.SyncLimitKilobytes);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

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
                StampCost = 13,
                StorageLimit = 800,
                AutoPeer = false,
            });
            node.Prioritise(preferred);
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
            await pair.WaitAsync(() => pair.ServerRouter.Links.Links.Any(inbound => !inbound.IsInitiator));
            var incoming = pair.ServerRouter.Links.Links.Single(inbound => !inbound.IsInitiator);
            using var releaseObserver = new ManualResetEventSlim();
            var observerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            incoming.RemoteIdentified += (_, _) =>
            {
                observerStarted.TrySetResult();
                releaseObserver.Wait(TimeSpan.FromSeconds(15));
            };
            try
            {
                var unidentified = link.Request("/pn/get/stats", null, useApplicationValues: true)!;
                Assert.Equal(0xf0L, await unidentified.ResponseTask.WaitAsync(TimeSpan.FromSeconds(10)));
                link.Identify(pair.ClientIdentity);
                await observerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var stats = link.Request("/pn/get/stats", null, useApplicationValues: true)!;
                var result = await stats.ResponseTask.WaitAsync(TimeSpan.FromSeconds(10));
                if (!allowControl)
                {
                    Assert.Equal(0xf1L, result);
                    node.AllowControl(pair.ClientIdentity.Hash);
                    var permitted = link.Request("/pn/get/stats", null, useApplicationValues: true)!;
                    Assert.IsType<Dictionary<object, object?>>(await permitted.ResponseTask.WaitAsync(TimeSpan.FromSeconds(10)));
                    node.DisallowControl(pair.ClientIdentity.Hash);
                    var revoked = link.Request("/pn/get/stats", null, useApplicationValues: true)!;
                    Assert.Equal(0xf1L, await revoked.ResponseTask.WaitAsync(TimeSpan.FromSeconds(10)));
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
            finally { releaseObserver.Set(); link.Teardown(); }
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
            Assert.Equal(1, pair.Client.PropagationTransferLastResult);
            Assert.Equal(1, pair.Client.PropagationTransferProgress);
            Assert.True(pair.Client.PropagationTransferSize > 0);
            var cachedLink = pair.Client.PropagationLinkId;
            await pair.WaitAsync(() => pair.ClientInbox.Count == 1);
            Assert.Equal("stored message", pair.ClientInbox[0].Message.Content);
            Assert.Equal(LxmMethod.Propagated, pair.ClientInbox[0].Message.Method);
            Assert.True(pair.ClientInbox[0].Message.SignatureValidated);
            Assert.Equal(0, node.MessageCount);
            Assert.Equal(0, await pair.Client.RequestMessagesFromPropagationNodeAsync());
            Assert.Equal(cachedLink, pair.Client.PropagationLinkId);
            await node.DisposeAsync();
            await using var reopened = new LxmPropagationNode(pair.ServerRouter, pair.ServerIdentity, path);
            var clients = Assert.IsType<Dictionary<object, object?>>(reopened.CompileStats()!["clients"]);
            Assert.Equal(1L, clients["client_propagation_messages_received"]);
            Assert.Equal(1L, clients["client_propagation_messages_served"]);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }
}
