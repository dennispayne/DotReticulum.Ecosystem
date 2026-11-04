using System.Text.Json;
using System.Text.Json.Nodes;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandObjectTests
{
    private const string Peer = "00112233445566778899aabbccddeeff";

    [Fact]
    public async Task ClassificationPersistsWithoutLosingConversationPolicyOrHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-objects-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await store.AddContactAsync(SidebandContact.Create("Object", Peer, trusted: true, directPushToTalk: true), token);
            await store.SetConversationTelemetryAsync(Peer, true, true, token);
            await using (var client = new SidebandClient(store))
            {
                await client.SetConversationObjectAsync(Peer.ToUpperInvariant(), true, token);
                await client.AddContactAsync("Renamed", Peer, token);
                await client.SetConversationTrustAsync(Peer, false, token);
                await store.AddMessageAsync(new(Guid.NewGuid(), Peer, true, "", "Object report",
                    DateTimeOffset.UtcNow, "Received"), token);
            }
            var state = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            var contact = Assert.Single(state.Contacts);
            Assert.True(contact.IsObject);
            Assert.True(contact.ConversationKnown);
            Assert.True(contact.SendTelemetry);
            Assert.True(contact.AllowTelemetryRequests);
            Assert.True(contact.DirectPushToTalk);
            Assert.True(contact.Unread);
            Assert.False(contact.Trusted);
            Assert.Equal("Renamed", contact.DisplayName);
            Assert.Equal("Object report", Assert.Single(state.Messages).Content);
            await new SidebandStateStore(root).SetConversationObjectAsync(Peer, false, token);
            Assert.False(Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts).IsObject);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InvalidUnknownCancelledAndLegacyOperationsDoNotInventObjects()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-objects-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await store.AddContactAsync(SidebandContact.Create("Legacy", Peer), token);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetConversationObjectAsync("bad", true, token));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => store.SetConversationObjectAsync(new string('e', 32), true, token));
            using var stop = new CancellationTokenSource();
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetConversationObjectAsync(Peer, true, stop.Token));
            var json = JsonNode.Parse(await File.ReadAllTextAsync(store.StatePath, token))!;
            json["Contacts"]![0]!.AsObject().Remove("IsObject");
            await File.WriteAllTextAsync(store.StatePath, json.ToJsonString(), token);
            Assert.False(Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts).IsObject);
            json["Contacts"]![0]!["IsObject"] = "true";
            await File.WriteAllTextAsync(store.StatePath, json.ToJsonString(), token);
            await Assert.ThrowsAsync<JsonException>(() => new SidebandStateStore(root).LoadAsync(cancellationToken: token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

public sealed partial class SidebandProfileImportTests
{
    // GenerateSidebandObjectVectors.py executes actual primary SQLite methods at
    // d069122af14880e43607aed0b71add4c7806fbe0; source SHA is asserted below.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ActualPrimaryObjectDataActivatesAndPreservesTelemetryPermissions(int index)
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-objects.json"), TestContext.Current.CancellationToken));
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", fixture.RootElement.GetProperty("revision").GetString());
        Assert.Equal("8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25",
            fixture.RootElement.GetProperty("source_sha256").GetString());
        var observation = fixture.RootElement.GetProperty("cases")[index];
        using var profile = new Profile();
        var data = observation.GetProperty("data_hex").GetString();
        WriteSql(Path.Combine(profile.Source, "sideband.db"),
            $"INSERT INTO conv VALUES (X'00112233445566778899aabbccddeeff',10,20,1,1,1,X'4f626a656374',X'{data}')");
        var before = profile.Fingerprint();
        await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        var contact = Assert.Single((await new SidebandStateStore(profile.Target).LoadAsync(
            cancellationToken: TestContext.Current.CancellationToken)).Contacts);
        Assert.Equal(observation.GetProperty("is_object").GetBoolean(), contact.IsObject);
        Assert.Equal(observation.GetProperty("telemetry").GetBoolean(), contact.SendTelemetry);
        Assert.Equal(observation.GetProperty("allow_requests").GetBoolean(), contact.AllowTelemetryRequests);
        Assert.True(contact.Trusted);
        Assert.True(contact.Unread);
        Assert.Equal(before, profile.Fingerprint());
    }

    [Fact]
    public async Task MalformedObjectClassificationRollsBackTheWholeProfile()
    {
        using var profile = new Profile();
        var malformed = Convert.ToHexString(DotReticulum.Core.MsgPack.Pack(
            new Dictionary<object, object?> { ["is_object"] = "true" }));
        WriteSql(Path.Combine(profile.Source, "sideband.db"),
            $"INSERT INTO conv VALUES (X'00112233445566778899aabbccddeeff',0,0,0,1,0,X'4f626a656374',X'{malformed}')");
        var before = profile.Fingerprint();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(profile.Target));
        Assert.Equal(before, profile.Fingerprint());
    }
}
