using System.Text.Json;

namespace Sideband.Tests;

public sealed partial class SidebandProfileImportTests
{
    [Fact]
    public async Task PrimaryVoicePeerNamesTrustAndDeliveryOverrideActivateWithoutConvertingIdentitiesToContacts()
    {
        using var profile = new Profile();
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-voice-peers.json")));
        var root = fixture.RootElement;
        var database = Path.Combine(profile.Source, "sideband.db");
        foreach (var row in root.GetProperty("rows").EnumerateArray())
            WriteSql(database, $"INSERT INTO conv VALUES(X'{row.GetProperty("identity").GetString()}',0,0,0,4,{row.GetProperty("trust").GetInt64()},X'{row.GetProperty("name_hex").GetString()}',X'C0')");
        var destination = root.GetProperty("overrideDeliveryHash").GetString()!;
        WriteSql(database, $"INSERT INTO conv VALUES(X'{destination}',0,0,0,1,0,X'556e747275737465642064656c6976657279206f76657272696465',X'C0')");
        var before = profile.Fingerprint();
        var token = TestContext.Current.CancellationToken;
        var result = await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, token);
        var state = await new SidebandStateStore(profile.Target).LoadAsync(cancellationToken: token);
        Assert.Equal(3, state.VoicePeers!.Count);
        Assert.Equal(destination, Assert.Single(state.Contacts).DeliveryHash);
        Assert.False(state.VoiceEnabled);
        var policy = SidebandVoicePolicy.FromState(state with { Voice = new() { TrustedOnly = true } });
        foreach (var row in root.GetProperty("rows").EnumerateArray())
        {
            var identity = row.GetProperty("identity").GetString()!;
            Assert.Equal(row.GetProperty("allowed_trusted_only").GetBoolean(), policy.IsAllowed(identity));
            Assert.Equal(row.GetProperty("display_name").GetString(), policy.GetDisplayName(identity));
        }
        Assert.Equal(before, profile.Fingerprint());
        Assert.Contains(result.Notices, text => text.Contains("Activated 3 voice-only", StringComparison.Ordinal));
        await using var reopened = new SidebandClient(new(profile.Target));
        Assert.Equal(state.VoicePeers, (await reopened.LoadAsync(token)).VoicePeers);
        Assert.False(reopened.IsConnected);
    }

    [Theory]
    [InlineData("trust")]
    [InlineData("utf8")]
    [InlineData("oversized-name")]
    [InlineData("identity")]
    public async Task InvalidPrimaryVoicePeersRollBackWholeProfileAndHealthyRetrySucceeds(string mutation)
    {
        using var profile = new Profile();
        var database = Path.Combine(profile.Source, "sideband.db");
        const string identity = "000102030405060708090a0b0c0d0e0f";
        var encodedIdentity = mutation == "identity" ? identity[..30] : identity;
        var name = mutation switch { "utf8" => "FF", "oversized-name" => new string('6', 162), _ => "566F696365" };
        var trust = mutation == "trust" ? 2 : 1;
        var insert = $"INSERT INTO conv VALUES(X'{encodedIdentity}',0,0,0,4,{trust},X'{name}',X'C0')";
        WriteSql(database, insert);
        var before = profile.Fingerprint();
        var token = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidDataException>(() => SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, token));
        Assert.False(Directory.Exists(profile.Target));
        Assert.Empty(Directory.GetDirectories(profile.Root, ".native.import-*"));
        Assert.Equal(before, profile.Fingerprint());
        WriteSql(database, "DELETE FROM conv WHERE type=4");
        WriteSql(database, $"INSERT INTO conv VALUES(X'{identity}',0,0,0,4,1,X'566F696365',X'C0')");
        await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, token);
        var peer = Assert.Single((await new SidebandStateStore(profile.Target).LoadAsync(cancellationToken: token)).VoicePeers!);
        Assert.Equal("Voice", peer.DisplayName);
        Assert.True(peer.VoiceTrusted);
    }
}
