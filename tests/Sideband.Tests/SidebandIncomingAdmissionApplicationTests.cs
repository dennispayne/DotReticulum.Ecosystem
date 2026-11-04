using System.Text.Json.Nodes;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandIncomingAdmissionApplicationTests
{
    private const string Peer = "00112233445566778899aabbccddeeff";

    [Theory]
    [InlineData("contact")]
    [InlineData("message")]
    [InlineData("trust")]
    [InlineData("read")]
    [InlineData("push-to-talk")]
    [InlineData("permission")]
    public async Task ExplicitActionsPromoteDiscoveryAndPersistWithoutResettingOtherFlags(string action)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "admit-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = await SeedDiscovery(root, token);
            Assert.False(Assert.Single((await store.LoadAsync(cancellationToken: token)).Contacts).ConversationKnown);
            switch (action)
            {
                case "contact": await store.AddContactAsync(SidebandContact.Create("Renamed", Peer), token); break;
                case "message": await store.AddMessageAsync(new(Guid.NewGuid(), Peer, false, "", "Sent", DateTimeOffset.UtcNow, "Queued"), token); break;
                case "trust": await store.SetConversationTrustAsync(Peer, true, token); break;
                case "read": await store.SetConversationReadAsync(Peer, false, token); break;
                case "push-to-talk": await store.SetConversationPushToTalkAsync(Peer, true, token); break;
                case "permission": await store.SetConversationTelemetryAsync(Peer, true, true, token); break;
                default: Assert.Fail("Unexercised explicit conversation action."); break;
            }
            var contact = Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts);
            Assert.True(contact.ConversationKnown);
            Assert.True(contact.Trusted);
            Assert.True(contact.SendTelemetry);
            Assert.Equal(new("map-marker", "112233", "aabbcc"), contact.Appearance);
            Assert.Equal(action == "permission", contact.AllowTelemetryRequests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LegacyContactOriginIsAmbiguousAndDefaultsKnownWithoutReclassifyingImportedContacts()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "admit-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = await SeedDiscovery(root, token);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(store.StatePath, token))!;
            json["Contacts"]![0]!.AsObject().Remove("ConversationKnown");
            await File.WriteAllTextAsync(store.StatePath, json.ToJsonString(), token);
            Assert.True(Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts).ConversationKnown);
            Assert.True(SidebandContact.Create("Imported conversation", Peer).ConversationKnown);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancelledAndFailedPromotionLeaveDurableAndCachedDiscoveryIntactThenRecover()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "admit-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = await SeedDiscovery(root, token);
            Assert.False(Assert.Single((await store.LoadAsync(cancellationToken: token)).Contacts).ConversationKnown);
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.SetConversationTelemetryAsync(Peer, true, true, cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.AddMessageAsync(new(Guid.NewGuid(), Peer, false, "", "Cancelled", DateTimeOffset.UtcNow, "Queued"), cancelled.Token));
            var backup = store.StatePath + ".saved";
            File.Move(store.StatePath, backup);
            Directory.CreateDirectory(store.StatePath);
            try
            {
                var error = await Record.ExceptionAsync(() => store.SetConversationTelemetryAsync(Peer, true, true, token));
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected a state replacement failure.");
            }
            finally
            {
                Directory.Delete(store.StatePath);
                File.Move(backup, store.StatePath);
            }
            Assert.False(Assert.Single((await store.LoadAsync(cancellationToken: token)).Contacts).ConversationKnown);
            var durable = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.False(Assert.Single(durable.Contacts).ConversationKnown);
            Assert.Empty(durable.Messages);
            await store.SetConversationTelemetryAsync(Peer, true, true, token);
            Assert.True(Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts).ConversationKnown);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<SidebandStateStore> SeedDiscovery(string root, CancellationToken token)
    {
        var store = new SidebandStateStore(root);
        await store.AddContactAsync(SidebandContact.Create("Discovered", Peer, trusted: true) with
        { SendTelemetry = true, Appearance = new("map-marker", "112233", "aabbcc") }, token);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(store.StatePath, token))!;
        json["Contacts"]![0]!["ConversationKnown"] = false;
        await File.WriteAllTextAsync(store.StatePath, json.ToJsonString(), token);
        return new(root);
    }
}
