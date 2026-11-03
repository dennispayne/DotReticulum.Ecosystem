using System.Text.Json;
using LXMF;

namespace NomadNet.Tests;

// Executed primary 1.4.4 f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2.
// GenerateNomadNetConversationVectors.py records source hashes and its persistence observation seam.
[Trait("Category", "Integration")]
public sealed class NomadNetConversationTests
{
    [Fact]
    public async Task DirectoryDefaultsTrustCollisionsAndPoliciesMatchExecutedPrimaryAndSurviveRestart()
    {
        var directory = CreateDirectory();
        try
        {
            using var fixture = Fixture();
            var store = await NomadNetStore.OpenAsync(directory);
            var peer = new string('1', 32);
            var other = new string('2', 32);
            await store.AddContactAsync(peer, "Alice");
            await store.SaveContactAsync(new NomadNetContact(other, "Trusted Alice") { TrustLevel = 255 });
            var contact = (await store.FindContactAsync(peer))!;
            var defaults = fixture.RootElement.GetProperty("defaults");
            Assert.Equal(defaults.GetProperty("trust").GetInt32(), contact.TrustLevel);
            Assert.Equal(defaults.GetProperty("delivery").GetInt32(), contact.PreferredDelivery);
            Assert.Equal(defaults.GetProperty("identify").GetBoolean(), contact.IdentifyOnConnect);
            Assert.Equal(defaults.GetProperty("notes").GetString(), contact.Notes);
            foreach (var observation in fixture.RootElement.GetProperty("trust").EnumerateArray())
            {
                var saved = observation.GetProperty("saved");
                if (saved.ValueKind != JsonValueKind.Null)
                    await store.SaveContactAsync(contact with { TrustLevel = saved.GetInt32() });
                Assert.Equal(observation.GetProperty("actual").GetInt32(),
                    await store.TrustLevelAsync(saved.ValueKind == JsonValueKind.Null ? new string('0', 32) : peer,
                        observation.GetProperty("announced").GetString()));
            }
            await store.SaveContactAsync(contact with { TrustLevel = 255, Ignored = true, PreferredDelivery = 3,
                HostsNode = true, IdentifyOnConnect = true, SortRank = -10, Notes = "local\nnotes" });
            await store.AddContactAsync(peer, "Renamed");
            store = await NomadNetStore.OpenAsync(directory);
            var restored = (await store.FindContactAsync(peer))!;
            Assert.Equal("Renamed", restored.Name);
            Assert.Equal(255, restored.TrustLevel);
            Assert.True(restored.Ignored);
            Assert.Equal(3, restored.PreferredDelivery);
            Assert.Equal("local\nnotes", restored.Notes);
            Assert.Equal(-10, restored.SortRank);
            await store.RemoveContactAsync(peer);
            Assert.True(await store.IsIgnoredAsync(peer));
            Assert.True(await (await NomadNetStore.OpenAsync(directory)).IsIgnoredAsync(peer));
            await store.SaveContactAsync(contact with { Ignored = false });
            Assert.False(await store.IsIgnoredAsync(peer));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveContactAsync(contact with { TrustLevel = 3 }));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveContactAsync(contact with { PreferredDelivery = 0 }));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveContactAsync(contact with { Notes = "\u001b[0m" }));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task UnreadFailuresReadAndDeletionFollowExecutedPrimaryWithoutDuplicateInflation()
    {
        var directory = CreateDirectory();
        try
        {
            using var fixture = Fixture();
            var store = await NomadNetStore.OpenAsync(directory);
            var peer = new string('1', 32);
            var index = 0;
            foreach (var observation in fixture.RootElement.GetProperty("sequence").EnumerateArray())
            {
                var message = new NomadNetMessage((index++).ToString(), peer,
                    observation.GetProperty("direction").GetString()!, DateTimeOffset.UnixEpoch.AddSeconds(index), "title", "body",
                    observation.GetProperty("failed").GetBoolean() ? "failed" : "received");
                await store.SaveMessageAsync(message);
                await store.SaveMessageAsync(message);
                var conversation = Assert.Single(await store.ConversationsAsync());
                Assert.Equal(observation.GetProperty("unread").GetInt32(), conversation.Unread);
                Assert.Equal(observation.GetProperty("failures").GetInt32(), conversation.Failures);
            }
            store = await NomadNetStore.OpenAsync(directory);
            Assert.Equal(2, Assert.Single(await store.ConversationsAsync()).Unread);
            await store.MarkReadAsync(peer);
            Assert.Equal(fixture.RootElement.GetProperty("read").GetProperty("unread").GetInt32(),
                Assert.Single(await store.ConversationsAsync()).Unread);
            Assert.Equal(0, Assert.Single(await store.ConversationsAsync()).Failures);
            Assert.Equal(4, (await store.DeleteConversationAsync(peer)).Count);
            Assert.Empty(await store.ConversationsAsync());
            Assert.True(fixture.RootElement.GetProperty("deleted").GetBoolean());
            Assert.Empty(await (await NomadNetStore.OpenAsync(directory)).ConversationsAsync());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ActiveStateIsEphemeralFailuresAreCountedOnceAndPurgePreservesReceivedAndDelivered()
    {
        var directory = CreateDirectory();
        try
        {
            var store = await NomadNetStore.OpenAsync(directory);
            var peer = new string('1', 32);
            var message = new NomadNetMessage("received", peer, "inbound", DateTimeOffset.UnixEpoch, "", "received", "received");
            await store.SetActiveConversationAsync(peer);
            await store.SaveMessageAsync(message);
            Assert.Equal(0, Assert.Single(await store.ConversationsAsync()).Unread);
            store = await NomadNetStore.OpenAsync(directory);
            Assert.Null(await store.ActiveConversationAsync());
            await store.SaveMessageAsync(message with { Id = "received-2" });
            await store.SaveMessageAsync(message with { Id = "failed", Direction = "outbound", Status = "queued" });
            await store.UpdateMessageStatusAsync("failed", "failed");
            await store.UpdateMessageStatusAsync("failed", "failed");
            await store.SaveMessageAsync(message with { Id = "delivered", Direction = "outbound", Status = "delivered" });
            await store.UpdateMessageStatusAsync("delivered", "failed");
            var conversation = Assert.Single(await store.ConversationsAsync());
            Assert.Equal(1, conversation.Unread);
            Assert.Equal(1, conversation.Failures);
            Assert.Single(await store.DeleteConversationAsync(peer, failedOnly: true));
            conversation = Assert.Single(await store.ConversationsAsync());
            Assert.Equal(1, conversation.Unread);
            Assert.Equal(0, conversation.Failures);
            Assert.Equal(3, conversation.MessageCount);
            var export = await store.ExportConversationAsync(peer);
            Assert.Contains("received-2", export);
            Assert.Contains("delivered", export);
            Assert.DoesNotContain("failed", export);
            await store.SetActiveConversationAsync(peer);
            Assert.Equal(0, Assert.Single(await store.ConversationsAsync()).Unread);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DeleteRemovesOnlySelectedAttachmentRootsAndCancellationDoesNotMutateState()
    {
        var directory = CreateDirectory();
        try
        {
            var peer = new string('1', 32);
            var messageId = new string('a', 64);
            var other = new string('b', 64);
            var fields = new Dictionary<object, object?> { [LxmField.FileAttachments] = new object?[] { new object?[] { "public.txt", "hello"u8.ToArray() } } };
            await NomadNetAttachments.SaveAsync(directory, messageId, fields);
            await NomadNetAttachments.SaveAsync(directory, other, fields);
            var store = await NomadNetStore.OpenAsync(directory);
            await store.SaveMessageAsync(new NomadNetMessage(messageId, peer, "inbound", DateTimeOffset.UnixEpoch, "", "", "received"));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteConversationAsync(peer, cancellationToken: canceled.Token));
            Assert.Single(await store.MessagesAsync(peer));
            await NomadNetAttachments.DeleteAsync(directory, await store.DeleteConversationAsync(peer));
            Assert.False(Directory.Exists(Path.Combine(directory, "attachments", messageId)));
            Assert.True(Directory.Exists(Path.Combine(directory, "attachments", other)));
            await NomadNetAttachments.DeleteAsync(directory, ["..", "invalid"]);
            await Assert.ThrowsAsync<InvalidDataException>(() => NomadNetAttachments.DeleteAsync(directory,
                Enumerable.Range(0, NomadNetStore.MaximumMessages + 1).Select(value => value.ToString("x64"))));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ConcurrentDistinctReceivesAreCountedAndOldStateHasNoInferredUnread()
    {
        var directory = CreateDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "state.json"),
                """{"schemaVersion":1,"contacts":[],"messages":[],"bookmarks":[]}""");
            var store = await NomadNetStore.OpenAsync(directory);
            var peer = new string('1', 32);
            await Task.WhenAll(Enumerable.Range(0, 40).Select(index => store.SaveMessageAsync(
                new NomadNetMessage(index.ToString(), peer, "inbound", DateTimeOffset.UnixEpoch, "", "", "received"))));
            Assert.Equal(40, Assert.Single(await store.ConversationsAsync()).Unread);
            await store.MarkReadAsync(peer);
            Assert.Equal(0, Assert.Single(await (await NomadNetStore.OpenAsync(directory)).ConversationsAsync()).Unread);
        }
        finally { Directory.Delete(directory, true); }
    }

    [NomadNetFunctionalTests.LinuxFact]
    public async Task AttachmentCleanupRejectsLinkedStorageWithoutDeletingItsTarget()
    {
        var directory = CreateDirectory();
        try
        {
            var actual = Path.Combine(directory, "actual");
            Directory.CreateDirectory(actual);
            var messageId = new string('a', 64);
            Directory.CreateDirectory(Path.Combine(actual, messageId));
            var keep = Path.Combine(actual, messageId, "0.bin");
            await File.WriteAllTextAsync(keep, "public");
            Directory.CreateSymbolicLink(Path.Combine(directory, "attachments"), actual);
            await Assert.ThrowsAsync<InvalidDataException>(() => NomadNetAttachments.DeleteAsync(directory, [messageId]));
            Assert.Equal("public", await File.ReadAllTextAsync(keep));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task OversizedContactPolicyDoesNotEvictHistoryOrLeaveAnInMemoryBlacklistChange()
    {
        var directory = CreateDirectory();
        try
        {
            var state = new NomadNetState
            {
                Contacts = Enumerable.Range(0, 1150).Select(index =>
                    new NomadNetContact(index.ToString("x32"), "Public") { Notes = new string('x', 4096) }).ToList(),
            };
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            byte[] data;
            while (true)
            {
                data = JsonSerializer.SerializeToUtf8Bytes(state, options);
                var room = NomadNetStore.MaximumStateBytes - data.Length - 1024;
                if (room < 512) break;
                state.Messages.Add(new NomadNetMessage(state.Messages.Count.ToString(), new string('1', 32), "inbound",
                    DateTimeOffset.UnixEpoch, "", new string('x', Math.Min(room, NomadNetStore.MaximumMessageCharacters)), "received"));
            }
            Assert.InRange(data.Length, NomadNetStore.MaximumStateBytes - 4095, NomadNetStore.MaximumStateBytes);
            await File.WriteAllBytesAsync(Path.Combine(directory, "state.json"), data);
            var store = await NomadNetStore.OpenAsync(directory);
            var peer = new string('f', 32);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveContactAsync(
                new NomadNetContact(peer, "Overflow") { Ignored = true, Notes = new string('x', 4096) }));
            Assert.False(await store.IsIgnoredAsync(peer));
            Assert.Null(await store.FindContactAsync(peer));
            Assert.Equal(state.Messages.Count, (await store.MessagesAsync()).Count);
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(directory, "state.json")));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static JsonDocument Fixture() => JsonDocument.Parse(typeof(NomadNetConversationTests).Assembly
        .GetManifestResourceStream("NomadNet.Tests.NomadNetConversationVectors.json")!);

    private static string CreateDirectory()
    {
        var path = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomadnet-conversations-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        return path;
    }
}
