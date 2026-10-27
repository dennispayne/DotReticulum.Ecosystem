using System.Text.Json;
using DotReticulum.Core;

namespace DotLXMF.Tests;

[Trait("Category", "Integration")]
public class LxmRuntimePolicyTests
{
    // GenerateLxmPolicyVectors.py executes primary LXMRouter.py at e8ba02483217af43be284a72f1c059c730b7d047.
    [Fact]
    public async Task RuntimeNodePolicies_ChangeAuthorizationLimitsAndEnabledStateWithoutLosingStoreOwnership()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-policy-vectors.json")));
        var root = vectors.RootElement;
        var path = Path.Combine(AppContext.BaseDirectory, "runtime-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { StoragePath = path });
            var node = pair.Server.EnablePropagationNode(new LxmPropagationOptions
            {
                StampCost = 1, SyncLimit = 1, MaxPeers = 0, MaxInboundSyncs = 0, AutoPeer = false,
            });
            Assert.True(LxmfPropagationAnnounce.TryParse(node.GetAnnounceAppData(), out var announce));
            Assert.Equal(root.GetProperty("options").GetProperty("stamp_cost").GetInt32(), announce!.StampCost);
            Assert.Equal(root.GetProperty("options").GetProperty("sync_limit").GetDouble(), announce.SyncLimitKilobytes);
            var authorization = new List<bool> { node.IdentityAllowed(pair.ClientIdentity) };
            node.SetAuthentication(true);
            authorization.Add(node.IdentityAllowed(pair.ClientIdentity));
            await Assert.ThrowsAsync<IOException>(() => pair.Client.RequestMessagesFromPropagationNodeAsync(node.DestinationHash));
            Assert.Equal(LxmPropagationTransferState.NoAccess, pair.Client.PropagationTransferState);
            node.AllowIdentity(pair.ClientIdentity.Hash);
            node.AllowIdentity(pair.ClientIdentity.Hash);
            authorization.Add(node.IdentityAllowed(pair.ClientIdentity));
            Assert.Equal(root.GetProperty("authorization").EnumerateArray().Select(value => value.GetBoolean()), authorization);
            Assert.Equal(0, await pair.Client.RequestMessagesFromPropagationNodeAsync(node.DestinationHash));
            node.DisallowIdentity(pair.ClientIdentity.Hash);
            Assert.False(node.IdentityAllowed(pair.ClientIdentity));
            Assert.Throws<ArgumentException>(() => node.AllowIdentity(new byte[15]));
            Assert.Throws<InvalidOperationException>(() => node.SetPeerSyncStrategy(new byte[16], LxmPeerSyncStrategy.Persistent));
            var limitCase = root.GetProperty("storage_limits")[0];
            node.SetMessageStorageLimit(kilobytes: 1.5, megabytes: 0.001);
            Assert.Equal(limitCase.GetProperty("result").GetInt64(), node.StorageLimit);
            Assert.Throws<ArgumentOutOfRangeException>(() => node.SetMessageStorageLimit());
            Assert.Throws<ArgumentOutOfRangeException>(() => node.SetMessageStorageLimit(kilobytes: -1));
            await pair.Server.DisablePropagationNodeAsync();
            Assert.False(node.Enabled);
            Assert.True(LxmfPropagationAnnounce.TryParse(node.GetAnnounceAppData(), out announce));
            Assert.Equal(root.GetProperty("disabled").GetProperty("announce_enabled").GetBoolean(), announce!.Enabled);
            Assert.Null(node.CompileStats());
            Assert.Same(node, pair.Server.EnablePropagationNode());
            Assert.True(node.Enabled);
            Assert.NotNull(node.CompileStats());
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task DeferredStamp_CanBeInspectedAndCancelledByRawMessageId()
    {
        await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { InboundStampCost = 254 });
        await pair.Server.AnnounceAsync();
        await pair.WaitAsync(() => pair.Client.GetOutboundStampCost(pair.Server.DeliveryHash) == 254);
        var message = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "Cancellable work", timestamp: 1700000100);
        var id = message.ComputeHash();
        var sending = pair.Client.SendAsync(message);
        await pair.WaitAsync(() => pair.Client.GetOutboundMessageStampCost(id) is not null);
        Assert.Equal(254, pair.Client.GetOutboundMessageStampCost(id));
        Assert.Equal(0, pair.Client.GetOutboundProgress(id));
        Assert.True(pair.Client.CancelOutbound(id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Equal(LxmState.Cancelled, message.State);
        Assert.Null(pair.Client.GetOutboundProgress(id));
        Assert.False(pair.Client.CancelOutbound(id));
    }

    [Fact]
    public async Task RuntimeStampEnforcement_AppliesToSubsequentMessagesAndCanBeDisabled()
    {
        await using var pair = await LxmfPair.CreateAsync(serverOptions: new LxmRouterOptions { InboundStampCost = 254 });
        var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Server.Log = text => { if (text.Contains("invalid stamp")) dropped.TrySetResult(); };
        async Task SendUnstamped(string content)
        {
            var message = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, content);
            message.Pack();
            await pair.Client.SendAsync(message);
            Assert.Equal(LxmState.Delivered, await message.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        Assert.False(pair.Server.StampsEnforced);
        await SendUnstamped("Reference default permits invalid stamp");
        await pair.WaitAsync(() => pair.ServerInbox.Count == 1);
        Assert.False(pair.ServerInbox[0].StampValid);
        pair.Server.EnforceStamps();
        await SendUnstamped("Now rejected");
        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(pair.ServerInbox);
        pair.Server.IgnoreStamps();
        await SendUnstamped("Allowed again");
        await pair.WaitAsync(() => pair.ServerInbox.Count == 2);
    }

    [Fact]
    public async Task ExtendedFieldDelivery_PreservesTicketsAndRunsWithoutReceiveObservers()
    {
        await using var pair = await LxmfPair.CreateAsync();
        pair.Server.MessageReceived = null;
        var first = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "No observer");
        first.IncludeTicket = true;
        first.SetFieldMap(new MsgPackMap([new(null, "preserved")]));
        await pair.Client.SendAsync(first);
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await pair.WaitAsync(() => pair.Server.HasMessage(first.Hash!));
        var ticket = pair.Server.Tickets.GetOutbound(pair.Client.DeliveryHash);
        Assert.NotNull(ticket);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(ticket.Token);
        var received = new TaskCompletionSource<LxMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Server.MessageReceived = message => received.TrySetResult(message.Message);
        var second = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "Observer");
        second.SetFieldMap(new MsgPackMap([new(null, "still preserved")]));
        await pair.Client.SendAsync(second);
        var delivered = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(delivered.SignatureValidated);
        Assert.Equal(second.Hash, delivered.Hash);
        Assert.Equal(MsgPack.Pack(second.FieldsValue), MsgPack.Pack(delivered.FieldsValue));
    }
}
