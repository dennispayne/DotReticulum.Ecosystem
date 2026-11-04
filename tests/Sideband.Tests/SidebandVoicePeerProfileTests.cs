using System.Text.Json;
using DotReticulum.Core;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandVoicePeerProfileTests
{
    // GenerateSidebandProfile.py --voice-peers-profile executes the pinned actual core methods.
    // Primary d069122af14880e43607aed0b71add4c7806fbe0; core SHA-256 is recorded in the fixture.
    [Fact]
    public void NamesTrustAndDeliveryPrecedenceMatchExecutedPrimary()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-voice-peers.json")));
        var root = fixture.RootElement;
        var rows = root.GetProperty("rows").EnumerateArray().ToArray();
        var peers = rows.Select(row => SidebandVoicePeerProfile.Read(
        [
            Convert.FromHexString(row.GetProperty("identity").GetString()!), row.GetProperty("type").GetInt64(),
            Convert.FromHexString(row.GetProperty("name_hex").GetString()!), row.GetProperty("trust").GetInt64(),
            row.GetProperty("unread").GetInt64(), null,
        ])).ToArray();
        var policy = new SidebandVoicePolicy(new() { TrustedOnly = true }, peers,
        [
            new(root.GetProperty("overrideDeliveryHash").GetString()!, false, DisplayName: "Untrusted delivery override"),
        ]);
        for (var index = 0; index < peers.Length; index++)
        {
            Assert.Equal(rows[index].GetProperty("allowed_trusted_only").GetBoolean(), policy.IsAllowed(peers[index].IdentityHash));
            Assert.Equal(rows[index].GetProperty("display_name").GetString(), policy.GetDisplayName(peers[index].IdentityHash));
        }
        Assert.All(peers, peer => Assert.Null(peer.DeliveryTrusted));
        Assert.All(peers, peer => Assert.False(peer.DeliveryPushToTalk));
        Assert.Null(peers[2].DisplayName);
    }

    [Fact]
    public void DiscoveryMetadataCannotOverrideAnExplicitVoicePeerOrBecomeADeliveryAuthorization()
    {
        const string identity = "000102030405060708090a0b0c0d0e0f";
        var destination = Convert.ToHexStringLower(Destination.ComputeHashFromNameAndIdentity("lxmf.delivery",
            Convert.FromHexString(identity)));
        var discovery = SidebandContact.Create("Discovered only", destination) with { ConversationKnown = false };
        var state = SidebandState.Empty("unused") with
        {
            Voice = new() { TrustedOnly = true, DirectPushToTalk = true },
            VoicePeers = [new(identity, VoiceTrusted: true, DisplayName: "Voice-only trusted")],
            Contacts = [discovery],
        };
        var policy = SidebandVoicePolicy.FromState(state);
        Assert.True(policy.IsAllowed(identity));
        Assert.Equal("Voice-only trusted", policy.GetDisplayName(identity));
        Assert.False(policy.AllowsDirectPushToTalk(identity));
        policy = SidebandVoicePolicy.FromState(state with { Contacts = [discovery with { ConversationKnown = true }] });
        Assert.False(policy.IsAllowed(identity));
        Assert.Equal($"Discovered only <{destination}>", policy.GetDisplayName(identity));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("type")]
    [InlineData("trust")]
    [InlineData("name-type")]
    [InlineData("utf8")]
    [InlineData("name-bytes")]
    [InlineData("name-length")]
    [InlineData("name-control")]
    public void MalformedPrimaryVoiceRowsFailExplicitly(string mutation)
    {
        object?[] row = [new byte[16], 4L, "Peer"u8.ToArray(), 1L, 0L, null];
        switch (mutation)
        {
            case "identity": row[0] = new byte[15]; break;
            case "type": row[1] = 1L; break;
            case "trust": row[3] = 2L; break;
            case "name-type": row[2] = 123L; break;
            case "utf8": row[2] = new byte[] { 0xff }; break;
            case "name-bytes": row[2] = new byte[321]; break;
            case "name-length": row[2] = new string('x', 81); break;
            case "name-control": row[2] = "Unsafe\nname"; break;
        }
        Assert.Throws<InvalidDataException>(() => SidebandVoicePeerProfile.Read(row));
    }
}

[Trait("Category", "Integration")]
public sealed class SidebandVoicePeerPersistenceTests
{
    [Fact]
    public async Task NamesTrustDeletionAndUnrelatedUpdatesPersistWithoutEnablingVoiceOrCreatingContacts()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-voice-peers-" + Guid.NewGuid().ToString("N"));
        const string identity = "000102030405060708090a0b0c0d0e0f";
        var token = TestContext.Current.CancellationToken;
        try
        {
            await using (var client = new SidebandClient(new(path)))
            {
                await client.ConfigureVoicePeerAsync(identity.ToUpperInvariant(), "  Saved alias  ", true, token);
                var state = await client.LoadAsync(token);
                Assert.Equal("  Saved alias  ", Assert.Single(state.VoicePeers!).DisplayName);
                Assert.Empty(state.Contacts);
                Assert.False(state.VoiceEnabled);
                Assert.False(client.IsConnected);
                await client.SetVoicePeerTrustAsync(identity, false, token);
                await client.ConfigureVoiceAsync(false, new() { TrustedOnly = true }, token);
                await Assert.ThrowsAsync<ArgumentException>(() => client.ConfigureVoicePeerAsync(identity, "Bad\nname", true, token));
            }

            await using var reopened = new SidebandClient(new(path));
            var peer = Assert.Single((await reopened.LoadAsync(token)).VoicePeers!);
            Assert.Equal(identity, peer.IdentityHash);
            Assert.Equal("  Saved alias  ", peer.DisplayName);
            Assert.False(peer.VoiceTrusted);
            await reopened.DeleteVoicePeerAsync(identity, token);
            Assert.Empty((await reopened.LoadAsync(token)).VoicePeers!);
            Assert.Empty((await new SidebandStateStore(path).LoadAsync(cancellationToken: token)).VoicePeers!);
            Assert.False(reopened.IsConnected);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
