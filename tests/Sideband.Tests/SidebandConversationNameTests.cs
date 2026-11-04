using System.Text.Json;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandConversationNameTests
{
    private const string Peer = "aabbccddeeff00112233445566778899";

    [Fact]
    [Trait("TestId", "T369")]
    public async Task ClearingSavedNamesMatchesExecutedPrimarySqlWritesAndFollowsLaterAnnouncementsAfterRestart()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-conversation-policy.json"), token));
        foreach (var item in fixture.RootElement.GetProperty("rename_cases").EnumerateArray())
        {
            var root = Path.Combine(AppContext.BaseDirectory, "name-write-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new SidebandStateStore(root);
                await store.AddContactAsync(SidebandContact.Create("Saved peer", Peer, trusted: true), token);
                await Observe("Announced first");
                var name = item.GetProperty("input").GetString();
                if (item.GetProperty("error").ValueKind == JsonValueKind.Null)
                    await store.SetConversationNameAsync(Peer, name!, token);
                else
                {
                    Assert.Equal("AttributeError", item.GetProperty("error").GetString());
                    await Assert.ThrowsAsync<ArgumentNullException>(() => store.SetConversationNameAsync(Peer, name!, token));
                }
                var contact = Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts);
                Assert.Equal(item.GetProperty("display").GetString(), contact.DisplayName);
                Assert.Equal(item.GetProperty("raw").GetString()!.Length > 0, contact.NameIsUserDefined);
                Assert.True(contact.Trusted);
                store = new SidebandStateStore(root);
                await Observe("Announced replacement");
                contact = Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts);
                Assert.Equal(item.GetProperty("after_announce").GetString(), contact.DisplayName);
                Assert.True(contact.Trusted);

                async Task Observe(string announced)
                {
                    await store.ObserveAnnounceAsync(new(Peer, SidebandAnnounce.Delivery, DateTimeOffset.UtcNow, announced), token);
                    await store.ObserveDeliveryAnnounceAsync(SidebandContact.Create(announced, Peer), token);
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }

    [Fact]
    [Trait("TestId", "T370")]
    public async Task AutomaticCreationPromotesDiscoveryWithoutLosingPolicyAndInvalidOrCancelledWritesLeaveStateIntact()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "name-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SidebandStateStore(root);
            await store.ObserveDeliveryAnnounceAsync(SidebandContact.Create("Discovered", Peer), token);
            await store.CreateConversationAsync(Peer, cancellationToken: token);
            var contact = Assert.Single((await store.LoadAsync(cancellationToken: token)).Contacts);
            Assert.True(contact.ConversationKnown);
            Assert.False(contact.NameIsUserDefined);
            Assert.Equal("Discovered", contact.DisplayName);
            await store.SetConversationTrustAsync(Peer, true, token);
            await store.SetConversationReadAsync(Peer, false, token);
            await store.SetConversationNameAsync(Peer, "Saved", token);
            await store.SetConversationNameAsync(Peer, "", token);
            contact = Assert.Single((await store.LoadAsync(cancellationToken: token)).Contacts);
            Assert.Equal(Peer, contact.DisplayName);
            Assert.True(contact.Trusted);
            Assert.True(contact.Unread);
            Assert.False(contact.NameIsUserDefined);
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateConversationAsync(Peer, cancellationToken: token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetConversationNameAsync(new string('f', 32), "", token));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetConversationNameAsync(Peer, " ", token));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetConversationNameAsync(Peer, new string('a', 81), token));
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetConversationNameAsync(Peer, "Cancelled", cancelled.Token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            await store.ObserveDeliveryAnnounceAsync(SidebandContact.Create("Recovered", Peer), token);
            Assert.Equal("Recovered", Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts).DisplayName);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T319")]
    public async Task SavedNamesOverrideAnnouncementsLikeExecutedPrimaryAndUnnamedConversationsKeepUpdating()
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-conversation-policy.json"), token));
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0",
            fixture.RootElement.GetProperty("revision").GetString());
        Assert.Equal("8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25",
            fixture.RootElement.GetProperty("source_sha256").GetString());
        foreach (var item in fixture.RootElement.GetProperty("name_cases").EnumerateArray())
        {
            var root = Path.Combine(AppContext.BaseDirectory, "name-precedence-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new SidebandStateStore(root);
                var saved = item.GetProperty("saved").GetString();
                if (!string.IsNullOrEmpty(saved))
                    await store.AddContactAsync(SidebandContact.Create(saved, Peer, trusted: true), token);
                else
                {
                    await store.AddMessageAsync(new(Guid.NewGuid(), Peer, true, "", "Synthetic",
                        DateTimeOffset.UnixEpoch, "Delivered"), token);
                    await store.SetConversationTrustAsync(Peer, true, token);
                }
                await store.ObserveDeliveryAnnounceAsync(
                    SidebandContact.Create(item.GetProperty("announced").GetString()!, Peer), token);
                var contact = Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts);
                Assert.Equal(item.GetProperty("display").GetString(), contact.DisplayName);
                Assert.Equal(!string.IsNullOrEmpty(item.GetProperty("raw").GetString()), contact.NameIsUserDefined);
                Assert.True(contact.Trusted);
                await store.ObserveDeliveryAnnounceAsync(SidebandContact.Create("Later replacement", Peer), token);
                contact = Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts);
                Assert.Equal(string.IsNullOrEmpty(saved) ? "Later replacement" : saved, contact.DisplayName);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }

    [Theory]
    [InlineData(true, "Legacy saved", "Legacy saved")]
    [InlineData(true, Peer, "Observed")]
    [InlineData(false, "Old discovery", "Observed")]
    [Trait("TestId", "T320")]
    public async Task LegacyNamesAreConservativelyRetainedWithoutFreezingUnnamedOrDiscoveryContacts(
        bool known, string name, string expected)
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "legacy-name-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var json = $$"""
                {"SchemaVersion":1,"ReticulumConfigDirectory":"isolated-rns","Contacts":[
                {"DeliveryHash":"{{Peer}}","DisplayName":{{JsonSerializer.Serialize(name)}},"ConversationKnown":{{(known ? "true" : "false")}}}],
                "Messages":[]}
                """;
            await File.WriteAllTextAsync(Path.Combine(root, "state.json"), json, token);
            var store = new SidebandStateStore(root);
            await store.ObserveDeliveryAnnounceAsync(SidebandContact.Create("Observed", Peer), token);
            var contact = Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts);
            Assert.Equal(expected, contact.DisplayName);
            Assert.Equal(expected == name, contact.NameIsUserDefined);
            Assert.Equal(known, contact.ConversationKnown);
            await store.AddContactAsync(SidebandContact.Create("Explicit edit", Peer), token);
            await store.ObserveDeliveryAnnounceAsync(SidebandContact.Create("Another announcement", Peer), token);
            Assert.Equal("Explicit edit", Assert.Single(
                (await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts).DisplayName);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
