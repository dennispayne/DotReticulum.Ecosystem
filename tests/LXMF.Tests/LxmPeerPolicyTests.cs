using System.Text.Json;

namespace LXMF.Tests;

[Trait("Category", "Unit")]
public class LxmPeerPolicyTests
{
    // GenerateLxmfContractVectors.py executes LXMPeer.sync, offer_response and
    // resource_concluded at upstream e8ba02483217af43be284a72f1c059c730b7d047.
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-contract-vectors.json")));
    private static byte[] Id(int value) => Enumerable.Repeat((byte)value, 32).ToArray();
    private static string[] Names(IEnumerable<byte[]> ids) => ids.Select(Convert.ToHexStringLower).ToArray();
    private static string[] Expected(JsonElement row, string property) =>
        row.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToArray();

    [Fact]
    public void PrepareOffer_MatchesReferenceWeightLimitsAndQueuePruning()
    {
        using var vectors = Vectors();
        foreach (var row in vectors.RootElement.GetProperty("peer_selection").EnumerateArray())
        {
            var peer = new LxmPeer(new byte[16]);
            var entries = row.GetProperty("entries").EnumerateArray().Select(entry => new LxmPeerOfferEntry(
                Convert.FromHexString(entry.GetProperty("id").GetString()!), entry.GetProperty("weight").GetDouble(),
                entry.GetProperty("size").GetInt64(), entry.GetProperty("stamp_value").GetInt32()))
                .ToDictionary(entry => Convert.ToHexStringLower(entry.Id));
            foreach (var entry in entries.Values) peer.AddUnhandled(entry.Id);
            peer.AddUnhandled(Id(6));
            var offered = peer.PrepareOffer(id => entries.GetValueOrDefault(Convert.ToHexStringLower(id)), 13,
                1000, row.GetProperty("sync_limit").GetDouble());
            Assert.Equal(Expected(row, "offered"), Names(offered));
            foreach (var entry in entries.Values)
                Assert.Equal(Expected(row, "handled").Contains(Convert.ToHexStringLower(entry.Id)), peer.IsHandled(entry.Id));
            Assert.Equal(row.GetProperty("unhandled").GetArrayLength(), peer.UnhandledCount);
        }
    }

    [Fact]
    public void SubsetAndCompletion_MatchReferenceLazyAndPersistentPolicies()
    {
        using var vectors = Vectors();
        foreach (var row in vectors.RootElement.GetProperty("peer_completion").EnumerateArray())
        {
            var peer = new LxmPeer(new byte[16]) { SyncStrategy = row.GetProperty("strategy").GetInt32() };
            foreach (var id in new[] { Id(1), Id(2), Id(3) }) peer.AddUnhandled(id);
            var selected = peer.SelectWanted([Id(1), Id(2)], new object?[] { Id(2) });
            Assert.Equal(new[] { Id(2) }, selected);
            Assert.True(peer.IsHandled(Convert.FromHexString(row.GetProperty("declined_handled")[0].GetString()!)));
            foreach (var id in selected) peer.MarkHandled(id);
            Assert.Equal(row.GetProperty("handled").GetArrayLength(), peer.HandledCount);
            Assert.Equal(row.GetProperty("unhandled").GetArrayLength(), peer.UnhandledCount);
            Assert.Equal(row.GetProperty("continued").GetBoolean(), peer.ContinueAfterTransfer);
            Assert.Empty(peer.SelectWanted([Id(3)], false));
            Assert.False(peer.ContinueAfterTransfer);
        }
    }

    [Fact]
    public void WantedResponse_RejectsUnofferedDuplicateAndMalformedIds()
    {
        var peer = new LxmPeer(new byte[16]);
        peer.AddUnhandled(Id(1));
        Assert.Throws<InvalidDataException>(() => peer.SelectWanted([Id(1)], new object?[] { Id(2) }));
        Assert.Throws<InvalidDataException>(() => peer.SelectWanted([Id(1)], new object?[] { Id(1), Id(1) }));
        Assert.Throws<InvalidOperationException>(() => peer.SelectWanted([Id(1)], new object?[] { new byte[31] }));
        Assert.Equal(1, peer.UnhandledCount);
    }
}
