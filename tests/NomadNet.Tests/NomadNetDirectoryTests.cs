using System.Text.Json;

namespace NomadNet.Tests;

// Actual pinned Directory.py at f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2.
// GenerateNomadNetDirectoryVectors.py records source hash, actual APIs and isolated persistence.
[Trait("Category", "Unit")]
public sealed class NomadNetDirectoryTests
{
    [Fact]
    public void NodeOrderingMatchesPrimaryIncludingXorDefaultAndUnicodeCodePoints()
    {
        using var fixture = Fixture();
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("commit").GetString());
        var contacts = fixture.RootElement.GetProperty("inputs").EnumerateArray().Select(input =>
            new NomadNetContact(input.GetProperty("hash").GetString()!, input.GetProperty("name").GetString()!)
            {
                HostsNode = input.GetProperty("node").GetBoolean(),
                TrustLevel = input.GetProperty("trust").GetInt32(),
                SortRank = input.GetProperty("rank").ValueKind == JsonValueKind.Null ? null : input.GetProperty("rank").GetInt32(),
            });
        Assert.Equal(fixture.RootElement.GetProperty("ordering").EnumerateArray().Select(item => item.GetString()),
            NomadNetDirectoryPolicy.KnownNodes(contacts).Select(item => item.DestinationHash));
    }

    [Fact]
    public void IdentifyRequiresExplicitSavedConsentAndIsIndependentOfTrust()
    {
        using var fixture = Fixture();
        var entry = new NomadNetContact(new string('1', 32), "Known") { TrustLevel = 255 };
        Assert.Equal(fixture.RootElement.GetProperty("identify").EnumerateArray().Select(item => item.GetBoolean()),
            [NomadNetDirectoryPolicy.ShouldIdentify(null), NomadNetDirectoryPolicy.ShouldIdentify(entry),
                NomadNetDirectoryPolicy.ShouldIdentify(entry with { TrustLevel = 0, IdentifyOnConnect = true })]);
    }

    internal static JsonDocument Fixture() => JsonDocument.Parse(typeof(NomadNetDirectoryTests).Assembly
        .GetManifestResourceStream("NomadNet.Tests.NomadNetDirectoryVectors.json")!);
}

[Trait("Category", "Integration")]
public sealed class NomadNetDirectoryPersistenceTests
{
    [Fact]
    public async Task ExistingAssociatedNodeTrustUpdatesAtomicallyAndPreservesOtherMetadata()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomadnet-directory-" + Guid.NewGuid().ToString("N")));
        try
        {
            using var fixture = NomadNetDirectoryTests.Fixture();
            var store = await NomadNetStore.OpenAsync(directory);
            var node = new NomadNetContact(new string('1', 32), "Saved node") {
                HostsNode = true, SortRank = -2, IdentifyOnConnect = true, Notes = "local" };
            var peer = new NomadNetContact(new string('2', 32), "Peer") { TrustLevel = 255 };
            await store.SaveContactAsync(node);
            await store.SaveContactAsync(peer, associatedNodeHash: node.DestinationHash);
            var restored = await NomadNetStore.OpenAsync(directory);
            Assert.Equal(node with { TrustLevel = fixture.RootElement.GetProperty("associatedTrust").GetInt32() },
                Assert.Single(await restored.KnownNodesAsync()));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restored.SaveContactAsync(
                peer with { TrustLevel = 0 }, canceled.Token, node.DestinationHash));
            Assert.Equal(255, Assert.Single(await restored.KnownNodesAsync()).TrustLevel);
            await restored.SaveContactAsync(peer with { TrustLevel = 1 }, associatedNodeHash: node.DestinationHash);
            Assert.Equal(1, Assert.Single(await restored.KnownNodesAsync()).TrustLevel);
            Assert.True(Assert.Single(await restored.KnownNodesAsync()).IdentifyOnConnect);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AssociationDoesNotInventNodeEntriesAndInvalidAssociationDoesNotChangePeer()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomadnet-directory-" + Guid.NewGuid().ToString("N")));
        try
        {
            var store = await NomadNetStore.OpenAsync(directory);
            var peer = new NomadNetContact(new string('2', 32), "Peer") { TrustLevel = 255 };
            await store.SaveContactAsync(peer, associatedNodeHash: new string('1', 32));
            Assert.Empty(await store.KnownNodesAsync());
            Assert.Single(await store.ContactsAsync());
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveContactAsync(peer with { TrustLevel = 0 },
                associatedNodeHash: "invalid"));
            Assert.Equal(255, (await store.FindContactAsync(peer.DestinationHash))!.TrustLevel);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
