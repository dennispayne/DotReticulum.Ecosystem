using System.Text.Json;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class ConversationStateTests
{
    private const string Peer = "aabbccddeeff00112233445566778899";

    [Fact]
    public async Task OldSchemaOneJsonDefaultsToUntrustedReadAndRoundTripsNewFlags()
    {
        using var profile = new Profile();
        await File.WriteAllTextAsync(profile.StatePath,
            """{"SchemaVersion":1,"ReticulumConfigDirectory":"legacy-rns","Contacts":[{"DeliveryHash":"aabbccddeeff00112233445566778899","DisplayName":"Legacy peer"}],"Messages":[]}""",
            TestContext.Current.CancellationToken);
        var store = new SidebandStateStore(profile.Root);
        var contact = Assert.Single((await store.LoadAsync(cancellationToken: TestContext.Current.CancellationToken)).Contacts);
        Assert.False(contact.Trusted);
        Assert.False(contact.Unread);
        await store.SetConversationTrustAsync(Peer, true, TestContext.Current.CancellationToken);
        await store.SetConversationReadAsync(Peer, false, TestContext.Current.CancellationToken);
        contact = Assert.Single((await profile.ReloadAsync()).Contacts);
        Assert.True(contact.Trusted);
        Assert.True(contact.Unread);
        Assert.Equal("Legacy peer", contact.DisplayName);
    }

    [Theory]
    [InlineData("Trusted", "1")]
    [InlineData("Trusted", "\"true\"")]
    [InlineData("Trusted", "null")]
    [InlineData("Unread", "0")]
    [InlineData("Unread", "\"false\"")]
    [InlineData("Unread", "null")]
    public async Task NativeJsonRejectsMalformedBooleanFlagsWithoutRewritingState(string flag, string value)
    {
        using var profile = new Profile();
        var json = """{"SchemaVersion":1,"ReticulumConfigDirectory":"legacy-rns","Contacts":[{"DeliveryHash":"aabbccddeeff00112233445566778899","DisplayName":"Legacy peer","FLAG":VALUE}],"Messages":[]}"""
            .Replace("FLAG", flag, StringComparison.Ordinal).Replace("VALUE", value, StringComparison.Ordinal);
        await File.WriteAllTextAsync(profile.StatePath, json, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<JsonException>(() => profile.ReloadAsync());
        Assert.Equal(json, await File.ReadAllTextAsync(profile.StatePath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RenameAndAnnounceUpsertPreserveExistingFlags(bool trusted, bool unread)
    {
        using var profile = new Profile();
        var store = new SidebandStateStore(profile.Root);
        await store.AddContactAsync(SidebandContact.Create("Original", Peer, trusted, unread), TestContext.Current.CancellationToken);
        await store.AddContactAsync(new SidebandContact(Peer.ToUpperInvariant(), "Renamed", !trusted, !unread),
            TestContext.Current.CancellationToken);
        var contact = Assert.Single((await profile.ReloadAsync()).Contacts);
        Assert.Equal("Renamed", contact.DisplayName);
        Assert.Equal(Peer, contact.DeliveryHash);
        Assert.Equal(trusted, contact.Trusted);
        Assert.Equal(unread, contact.Unread);
        await store.AddContactAsync(SidebandContact.Create("Announced", Peer), TestContext.Current.CancellationToken);
        contact = Assert.Single((await profile.ReloadAsync()).Contacts);
        Assert.Equal("Announced", contact.DisplayName);
        Assert.Equal(trusted, contact.Trusted);
        Assert.Equal(unread, contact.Unread);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncomingAndOutgoingMessagesCreateUntrustedUnreadConversation(bool incoming)
    {
        using var profile = new Profile();
        var store = new SidebandStateStore(profile.Root);
        var stored = await store.AddMessageAsync(Message(incoming, Peer.ToUpperInvariant()), TestContext.Current.CancellationToken);
        var state = await profile.ReloadAsync();
        var contact = Assert.Single(state.Contacts);
        Assert.Equal(Peer, contact.DeliveryHash);
        Assert.Equal(Peer, contact.DisplayName);
        Assert.False(contact.Trusted);
        Assert.True(contact.Unread);
        Assert.Equal(Peer, stored.PeerDeliveryHash);
        Assert.Single(await store.GetMessagesAsync(Peer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReceiveReadAndTrustPersistWithoutAffectingOtherConversation()
    {
        using var profile = new Profile();
        var store = new SidebandStateStore(profile.Root);
        var other = new string('b', 32);
        await store.AddContactAsync(SidebandContact.Create("Other", other, trusted: true, unread: true),
            TestContext.Current.CancellationToken);
        await store.AddMessageAsync(Message(incoming: true), TestContext.Current.CancellationToken);
        await store.SetConversationTrustAsync(Peer.ToUpperInvariant(), true, TestContext.Current.CancellationToken);
        await store.SetConversationReadAsync(Peer, true, TestContext.Current.CancellationToken);
        var contact = Assert.Single((await profile.ReloadAsync()).Contacts, item => item.DeliveryHash == Peer);
        Assert.True(contact.Trusted);
        Assert.False(contact.Unread);
        await store.AddContactAsync(SidebandContact.Create("Named peer", Peer), TestContext.Current.CancellationToken);
        await store.AddMessageAsync(Message(incoming: true), TestContext.Current.CancellationToken);
        contact = Assert.Single((await profile.ReloadAsync()).Contacts, item => item.DeliveryHash == Peer);
        Assert.Equal("Named peer", contact.DisplayName);
        Assert.True(contact.Trusted);
        Assert.True(contact.Unread);
        await store.SetConversationTrustAsync(Peer, false, TestContext.Current.CancellationToken);
        contact = Assert.Single((await profile.ReloadAsync()).Contacts, item => item.DeliveryHash == Peer);
        Assert.False(contact.Trusted);
        Assert.True(contact.Unread);
        Assert.Equal(new SidebandContact(other, "Other", true, true, NameIsUserDefined: true),
            Assert.Single((await profile.ReloadAsync()).Contacts, item => item.DeliveryHash == other));
    }

    [Fact]
    public async Task NativeTransitionsMatchExecutedUpstreamPersistenceObservations()
    {
        using var profile = new Profile();
        var token = TestContext.Current.CancellationToken;
        var store = new SidebandStateStore(profile.Root);
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-profile.json"), token));
        foreach (var observation in fixture.RootElement.GetProperty("conversationTransitions").EnumerateArray())
        {
            var operation = observation.GetProperty("operation").GetString();
            switch (operation)
            {
                case "created_trusted": await store.AddContactAsync(SidebandContact.Create("Reference contact", Peer, trusted: true), token); break;
                case "untrusted": await store.SetConversationTrustAsync(Peer, false, token); break;
                case "trusted": await store.SetConversationTrustAsync(Peer, true, token); break;
                case "marked_unread": await store.SetConversationReadAsync(Peer, false, token); break;
                case "renamed_unread": await store.AddContactAsync(SidebandContact.Create("Renamed contact", Peer), token); break;
                case "read":
                    await store.AddContactAsync(SidebandContact.Create("Reference contact", Peer), token);
                    await store.SetConversationReadAsync(Peer, true, token);
                    break;
                case "outgoing_unselected": await store.AddMessageAsync(Message(incoming: false), token); break;
                case "incoming_unselected": await store.AddMessageAsync(Message(incoming: true), token); break;
                case "outgoing_selected":
                case "incoming_selected":
                    // The store has no active-view knowledge. UI acknowledgement is explicit.
                    await store.AddMessageAsync(Message(incoming: operation == "incoming_selected"), token);
                    await store.SetConversationReadAsync(Peer, true, token);
                    break;
                default: Assert.Fail("Unexpected executed reference operation."); break;
            }
            var contact = Assert.Single((await profile.ReloadAsync()).Contacts);
            Assert.Equal(observation.GetProperty("trusted").GetBoolean(), contact.Trusted);
            Assert.Equal(observation.GetProperty("unread").GetBoolean(), contact.Unread);
            Assert.Equal(observation.GetProperty("name").GetString(), contact.DisplayName);
        }
        Assert.False(fixture.RootElement.GetProperty("missingConversationCreated").GetBoolean());
        await store.SetConversationTrustAsync(new string('f', 32), true, token);
        await store.SetConversationReadAsync(new string('f', 32), false, token);
        Assert.Single((await profile.ReloadAsync()).Contacts);
    }

    [Fact]
    public async Task ConcurrentReceiptRenameAndFlagUpdatesDoNotLoseMessagesOrConversationState()
    {
        using var profile = new Profile();
        var store = new SidebandStateStore(profile.Root);
        var token = TestContext.Current.CancellationToken;
        await store.AddContactAsync(SidebandContact.Create("Original", Peer), token);
        var operations = Enumerable.Range(0, 32).SelectMany(index => new Task[]
        {
            store.AddMessageAsync(Message(incoming: index % 2 == 0), token),
            store.AddContactAsync(SidebandContact.Create("Renamed", Peer), token),
            store.SetConversationTrustAsync(Peer, true, token),
            store.SetConversationReadAsync(Peer, false, token),
        });
        await Task.WhenAll(operations);
        await store.SetConversationReadAsync(Peer, true, token);
        var state = await profile.ReloadAsync();
        var contact = Assert.Single(state.Contacts);
        Assert.Equal("Renamed", contact.DisplayName);
        Assert.True(contact.Trusted);
        Assert.False(contact.Unread);
        Assert.Equal(32, state.Messages.Count);
        Assert.Equal(32, state.Messages.Select(message => message.Id).Distinct().Count());
        Assert.Empty(Directory.GetFiles(profile.Root, "*.tmp"));
    }

    [Fact]
    public async Task CancellationAndInvalidInputDoNotChangeContactFlagsOrHistory()
    {
        using var profile = new Profile();
        var store = new SidebandStateStore(profile.Root);
        var token = TestContext.Current.CancellationToken;
        await store.AddContactAsync(SidebandContact.Create("Preserved", Peer, trusted: true), token);
        var before = await File.ReadAllBytesAsync(profile.StatePath, token);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetConversationTrustAsync(Peer, false, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetConversationReadAsync(Peer, false, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AddMessageAsync(Message(true), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AddContactAsync(SidebandContact.Create("Must not rename", Peer), cancelled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetConversationTrustAsync("invalid", false, token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetConversationReadAsync("invalid", false, token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddMessageAsync(Message(true) with { Content = new string('x', 65_537) }, token));
        Assert.Equal(before, await File.ReadAllBytesAsync(profile.StatePath, token));
        Assert.Empty((await profile.ReloadAsync()).Messages);
        Assert.Empty(Directory.GetFiles(profile.Root, "*.tmp"));
    }

    [Fact]
    public async Task ContactCapacityFailureDoesNotPersistAnOrphanMessage()
    {
        using var profile = new Profile();
        await using (var stream = File.Create(profile.StatePath))
        {
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteNumber("SchemaVersion", 1);
            writer.WriteString("ReticulumConfigDirectory", "capacity-rns");
            writer.WriteStartArray("Contacts");
            for (var index = 0; index < 1_000; index++)
            {
                writer.WriteStartObject();
                writer.WriteString("DeliveryHash", index.ToString("x32"));
                writer.WriteString("DisplayName", "Existing");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("Messages");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var store = new SidebandStateStore(profile.Root);
        var token = TestContext.Current.CancellationToken;
        var before = await File.ReadAllBytesAsync(profile.StatePath, token);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.AddMessageAsync(Message(true), token));
        Assert.Equal(before, await File.ReadAllBytesAsync(profile.StatePath, token));
        var state = await profile.ReloadAsync();
        Assert.Equal(1_000, state.Contacts.Count);
        Assert.Empty(state.Messages);
        var existingPeer = new string('0', 32);
        await store.AddMessageAsync(Message(true, existingPeer), token);
        state = await profile.ReloadAsync();
        Assert.Equal(1_000, state.Contacts.Count);
        Assert.Single(state.Messages);
        Assert.True(Assert.Single(state.Contacts, contact => contact.DeliveryHash == existingPeer).Unread);
    }

    private static SidebandChatMessage Message(bool incoming, string peer = Peer) =>
        new(Guid.NewGuid(), peer, incoming, "Title", "Content", DateTimeOffset.FromUnixTimeSeconds(1760000000), incoming ? "Received" : "Sent");

    private sealed class Profile : IDisposable
    {
        internal string Root { get; } = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "conversation-tests-" + Guid.NewGuid().ToString("N"));
        internal string StatePath => Path.Combine(Root, "state.json");
        internal Profile() => Directory.CreateDirectory(Root);
        internal Task<SidebandState> ReloadAsync() =>
            new SidebandStateStore(Root).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
