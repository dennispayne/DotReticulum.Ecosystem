namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class NomadNetStoreTests
{
    [Fact]
    public async Task Store_RestoresContactsMessagesAndBookmarks()
    {
        var directory = CreateTestDirectory();
        try
        {
            var settings = await NomadNetSettings.OpenAsync(directory);
            Assert.Equal("NomadNet user", settings.DisplayName);
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"),
                """{"schemaVersion":1,"displayName":"Alice from config"}""");
            settings = await NomadNetSettings.OpenAsync(directory);
            Assert.Equal("Alice from config", settings.DisplayName);

            var store = await NomadNetStore.OpenAsync(directory);
            var hash = new string('A', 32);
            await store.AddContactAsync(hash, "Alice");
            await store.SaveMessageAsync(new NomadNetMessage("message-1", hash, "outbound", DateTimeOffset.UtcNow,
                "hello", "world", "queued"));
            await store.AddBookmarkAsync(new NomadNetBookmark(hash, "/page/index.mu", "Home"));

            var reloaded = await NomadNetStore.OpenAsync(directory);
            Assert.Equal("alice", Assert.Single(await reloaded.ContactsAsync()).Name.ToLowerInvariant());
            Assert.Equal("world", Assert.Single(await reloaded.MessagesAsync(hash.ToLowerInvariant())).Content);
            Assert.Equal("Home", Assert.Single(await reloaded.BookmarksAsync()).Title);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Store_BoundsStoredMessageAndRejectsTraversalPeerHashes()
    {
        var directory = CreateTestDirectory();
        try
        {
            var store = await NomadNetStore.OpenAsync(directory);
            await Assert.ThrowsAsync<ArgumentException>(() => store.AddContactAsync("../outside", "bad"));
            var hash = new string('b', 32);
            var body = new string('x', NomadNetStore.MaximumMessageCharacters + 100);
            await store.SaveMessageAsync(new NomadNetMessage("bounded", hash, "inbound", DateTimeOffset.UtcNow, "title", body, "received"));
            Assert.Equal(NomadNetStore.MaximumMessageCharacters,
                Assert.Single(await store.MessagesAsync(hash)).Content.Length);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "nomadnet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
