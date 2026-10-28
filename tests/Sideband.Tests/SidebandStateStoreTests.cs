using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandStateStoreTests
{
    [Fact]
    public async Task StateAndContactsSurviveReloadAndNormalizeHashes()
    {
        var root = CreateOwnedDirectory();
        try
        {
            var store = new SidebandStateStore(root);
            var cancellationToken = TestContext.Current.CancellationToken;
            var state = await store.LoadAsync(Path.Combine(root, "reticulum"), cancellationToken);
            Assert.Equal(SidebandState.CurrentSchemaVersion, state.SchemaVersion);
            await store.AddContactAsync(SidebandContact.Create("  Alice  ", "AABBCCDDEEFF00112233445566778899"), cancellationToken);
            await store.AddMessageAsync(new SidebandChatMessage(
                Guid.NewGuid(), "aabbccddeeff00112233445566778899", false, "", "hello",
                DateTimeOffset.UtcNow, "Queued"), cancellationToken);

            var reloaded = await new SidebandStateStore(root).LoadAsync(cancellationToken: cancellationToken);
            Assert.Equal("Alice", Assert.Single(reloaded.Contacts).DisplayName);
            Assert.Equal("aabbccddeeff00112233445566778899", reloaded.Contacts[0].DeliveryHash);
            Assert.Equal("hello", Assert.Single(reloaded.Messages).Content);
        }
        finally { DeleteOwnedDirectory(root); }
    }

    [Fact]
    public async Task ContactAddressAndMessageBoundsAreValidated()
    {
        var root = CreateOwnedDirectory();
        try
        {
            var store = new SidebandStateStore(root);
            var cancellationToken = TestContext.Current.CancellationToken;
            await Assert.ThrowsAsync<ArgumentException>(() => store.AddContactAsync(new SidebandContact("xyz", "not-an-address"), cancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => store.AddMessageAsync(new SidebandChatMessage(
                Guid.NewGuid(), "bad", false, "", "hello", DateTimeOffset.UtcNow, "Queued"), cancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => store.GetMessagesAsync("bad", cancellationToken));
        }
        finally { DeleteOwnedDirectory(root); }
    }

    [Fact]
    public async Task NullCollectionsInPersistedStateAreReportedAsInvalidData()
    {
        var root = CreateOwnedDirectory();
        try
        {
            var store = new SidebandStateStore(root);
            await File.WriteAllTextAsync(store.StatePath,
                "{\"SchemaVersion\":1,\"ReticulumConfigDirectory\":\"rns\",\"OwnDeliveryHash\":null,\"Contacts\":null,\"Messages\":[]}",
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new SidebandStateStore(root).LoadAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        finally { DeleteOwnedDirectory(root); }
    }

    private static string CreateOwnedDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteOwnedDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
