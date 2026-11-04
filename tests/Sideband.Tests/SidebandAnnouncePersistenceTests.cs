using System.Text.Json.Nodes;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandAnnouncePersistenceTests
{
    [Fact]
    public async Task BoundedConcurrentHistoryRestartsAndDeletionPreserveConversationsAndPermissions()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-announces-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            var contact = SidebandContact.Create("Known", new string('a', 32), trusted: true, unread: true)
                with { NameIsUserDefined = true };
            await store.AddContactAsync(contact, token);
            await store.SetPropagationAsync(new string('b', 32), true, token);
            await Task.WhenAll(Enumerable.Range(1, 100).Select(index => store.ObserveAnnounceAsync(
                new(index.ToString("x32"), SidebandAnnounce.Delivery, DateTimeOffset.UnixEpoch.AddSeconds(index), "Peer"), token)));
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ObserveAnnounceAsync(
                new(new string('c', 32), SidebandAnnounce.Delivery, DateTimeOffset.UtcNow), cancelled.Token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            var state = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.Equal(48, state.Announces!.Count);
            Assert.Equal(53.ToString("x32"), state.Announces[0].DestinationHash);
            Assert.Equal(contact, Assert.Single(state.Contacts));
            Assert.Empty(state.Messages);
            await store.DeleteAnnounceAsync(state.Announces[0].DestinationHash.ToUpperInvariant(), token);
            await store.DeleteAnnounceAsync(state.Announces[0].DestinationHash, token);
            var after = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.Equal(47, after.Announces!.Count);
            Assert.Equal(new string('b', 32), after.PropagationNode);
            Assert.True(after.PropagationByDefault);
            Assert.Equal(contact, Assert.Single(after.Contacts));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedAtomicReplacementDoesNotPublishNewHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-announces-atomic-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await store.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
            File.Delete(store.StatePath);
            Directory.CreateDirectory(store.StatePath);
            var failure = await Record.ExceptionAsync(() => store.ObserveAnnounceAsync(
                new(new string('a', 32), SidebandAnnounce.Delivery, DateTimeOffset.UtcNow), token));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString() ?? "Replacement unexpectedly succeeded.");
            Assert.Empty((await store.LoadAsync(cancellationToken: token)).Announces ?? []);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    [InlineData("unordered")]
    [InlineData("null")]
    public async Task MalformedPersistedHistoryFailsWithoutMutatingSource(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-announces-invalid-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await store.ObserveAnnounceAsync(new(new string('a', 32), SidebandAnnounce.Delivery, DateTimeOffset.UtcNow), token);
            var state = JsonNode.Parse(await File.ReadAllTextAsync(store.StatePath, token))!;
            var entries = state["Announces"]!.AsArray();
            var original = entries[0]!.DeepClone();
            if (mode == "duplicate") entries.Add(original.DeepClone());
            if (mode == "oversized") for (var index = 0; index < 48; index++) entries.Add(original.DeepClone());
            if (mode == "null") entries.Add(null);
            if (mode == "unordered")
            {
                var previous = original.DeepClone();
                previous["DestinationHash"] = new string('b', 32);
                previous["ReceivedAt"] = DateTimeOffset.UnixEpoch;
                entries.Add(previous);
            }
            await File.WriteAllTextAsync(store.StatePath, state.ToJsonString(), token);
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            await Assert.ThrowsAsync<InvalidDataException>(() => new SidebandStateStore(root).LoadAsync(cancellationToken: token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
