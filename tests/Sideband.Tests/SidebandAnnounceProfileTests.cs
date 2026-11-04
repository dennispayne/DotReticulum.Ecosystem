using System.Globalization;
using System.Text.Json;

namespace Sideband.Tests;

public sealed partial class SidebandProfileImportTests
{
    public static bool AnnouncementProfileIsUnavailable =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_ANNOUNCE_PROFILE"));

    [Fact(SkipWhen = nameof(AnnouncementProfileIsUnavailable), Skip = "Set DOTRETICULUM_SIDEBAND_ANNOUNCE_PROFILE to an actual GenerateSidebandProfile.py --announce-profile output.")]
    public async Task ActualUpstreamAnnouncementProfileWalActivatesWithoutSourceModification()
    {
        using var profile = new Profile();
        var reference = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_ANNOUNCE_PROFILE")!;
        foreach (var file in Directory.GetFiles(Path.Combine(reference, "app_storage")))
            File.Copy(file, Path.Combine(profile.Source, Path.GetFileName(file)), overwrite: true);
        Assert.True(new FileInfo(Path.Combine(profile.Source, "sideband.db-wal")).Length > 0);
        var before = profile.Fingerprint();
        var result = await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        using var observations = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(reference, "observations.json"), TestContext.Current.CancellationToken));
        var state = await new SidebandStateStore(profile.Target).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(observations.RootElement.GetProperty("ownDeliveryHash").GetString(), state.OwnDeliveryHash);
        Assert.Equal(1, result.Conversations);
        Assert.Equal(2, result.Messages);
        Assert.Equal(4, state.Announces!.Count);
        foreach (var item in observations.RootElement.GetProperty("announces").EnumerateArray())
        {
            var actual = Assert.Single(state.Announces, entry => entry.DestinationHash == item.GetProperty("dest").GetString());
            Assert.Equal(item.GetProperty("name").GetString(), actual.Name);
            Assert.Equal(item.GetProperty("type").GetString(), actual.DestinationType);
            Assert.Equal(item.GetProperty("pn_ver").GetString(), actual.NodeVersion);
            Assert.InRange(Math.Abs(item.GetProperty("time").GetDouble() -
                (actual.ReceivedAt - DateTimeOffset.UnixEpoch).TotalSeconds), 0, 0.000001);
        }
        Assert.Equal(before, profile.Fingerprint());
        Assert.True(Assert.Single(state.Contacts).Trusted);
        Assert.True(Assert.Single(state.Contacts).Unread);
    }

    [Fact]
    public async Task ActualPrimaryAnnouncementProfileActivatesObservedRowsWithoutInventingRouterCostsOrContacts()
    {
        using var profile = new Profile();
        using var fixture = AnnouncementProfileFixture();
        AddAnnouncementProfileRows(profile, fixture.RootElement);
        var before = profile.Fingerprint();
        var result = await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        var state = await new SidebandStateStore(profile.Target).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        var expected = fixture.RootElement.GetProperty("listed_with_router_cache").EnumerateArray().ToArray();
        Assert.Equal(4, state.Announces!.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            var actual = state.Announces[index];
            var item = expected[index];
            Assert.Equal(item.GetProperty("dest").GetString(), actual.DestinationHash);
            Assert.Equal(item.GetProperty("name").GetString(), actual.Name);
            Assert.Equal(item.GetProperty("pn_ver").GetString(), actual.NodeVersion);
            Assert.Equal(item.GetProperty("time").GetDouble(), actual.ReceivedAt.ToUnixTimeMilliseconds() / 1000d);
            if (actual.DestinationType == SidebandAnnounce.Delivery)
            {
                Assert.Null(actual.StampCost);
                Assert.Equal(new SidebandAnnounceSignal(-77, 12.5, 94), actual.Signal);
            }
            else
            {
                Assert.Equal(16, actual.StampCost);
                Assert.True(actual.NodeEnabled);
                Assert.Null(actual.Signal);
            }
        }
        Assert.Empty(state.Contacts);
        Assert.Empty(state.Messages);
        Assert.Equal(before, profile.Fingerprint());
        Assert.Contains(result.Notices, notice => notice.StartsWith("Activated 4 newest unique supported announcements", StringComparison.Ordinal));
        Assert.Contains(result.Notices, notice => notice.Contains("costs are unknown", StringComparison.Ordinal));
        await using var client = new SidebandClient(new(profile.Target));
        Assert.Equal(state.Announces, await client.GetAnnouncesAsync(TestContext.Current.CancellationToken));
        await client.DeleteAnnounceAsync(state.Announces[0].DestinationHash, TestContext.Current.CancellationToken);
        Assert.Equal(3, (await new SidebandStateStore(profile.Target).LoadAsync(cancellationToken: TestContext.Current.CancellationToken)).Announces!.Count);
        Assert.Equal(before, profile.Fingerprint());
    }

    [Fact]
    public async Task AnnouncementProfileBoundsDeduplicateNewestRowsAndArchiveUnsupportedTypes()
    {
        using var profile = new Profile();
        using var fixture = AnnouncementProfileFixture();
        AddAnnouncementProfileRows(profile, fixture.RootElement);
        var data = fixture.RootElement.GetProperty("rows")[0].GetProperty("data").GetString()!;
        var statements = new List<string>();
        for (var index = 100; index < 170; index++)
            statements.Add($"INSERT INTO announce(id,received,source,data,dest_type) VALUES({index},{1700000100 + index},X'{index:x32}',X'{data}','lxmf.delivery')");
        statements.Add($"INSERT INTO announce(id,received,source,data,dest_type) VALUES(999,1700000999,X'{169:x32}',X'{data}','lxmf.delivery')");
        statements.Add($"INSERT INTO announce(id,received,source,data,dest_type) VALUES(1000,1700001000,X'{999:x32}',X'{data}','unsupported.application')");
        WriteSql(Path.Combine(profile.Source, "sideband.db"), string.Join(";", statements));
        var result = await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        var state = await new SidebandStateStore(profile.Target).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(48, state.Announces!.Count);
        Assert.Equal(122.ToString("x32"), state.Announces[0].DestinationHash);
        Assert.Equal(169.ToString("x32"), state.Announces[^1].DestinationHash);
        Assert.Equal(1700000999, state.Announces[^1].ReceivedAt.ToUnixTimeSeconds());
        Assert.DoesNotContain(state.Announces, item => item.DestinationHash == 999.ToString("x32"));
        Assert.Contains(result.Notices, notice => notice.Contains("older/duplicate/other destination types", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HistoricalFiveColumnAnnouncementProfileImportsWithoutInventingSignalObservations()
    {
        using var profile = new Profile();
        using var fixture = AnnouncementProfileFixture();
        var row = fixture.RootElement.GetProperty("rows")[0];
        WriteSql(Path.Combine(profile.Source, "sideband.db"),
            $"INSERT INTO announce(id,received,source,data,dest_type) VALUES(1,1700000001," +
            $"X'{row.GetProperty("source").GetString()}',X'{row.GetProperty("data").GetString()}','lxmf.delivery')");
        await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        var state = await new SidebandStateStore(profile.Target).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        var announce = Assert.Single(state.Announces!);
        Assert.Equal("Synthetic peer", announce.Name);
        Assert.Null(announce.StampCost);
        Assert.Null(announce.Signal);
    }

    [Theory]
    [InlineData("UPDATE announce SET source=X'00'")]
    [InlineData("UPDATE announce SET received='not a timestamp'")]
    [InlineData("UPDATE announce SET received=1e999")]
    [InlineData("UPDATE announce SET received=1e20")]
    [InlineData("UPDATE announce SET data=zeroblob(4097)")]
    [InlineData("UPDATE announce SET extra=zeroblob(4097)")]
    [InlineData("UPDATE announce SET extra=X'C1'")]
    [InlineData("UPDATE announce SET extra=X'81AA6C696E6B5F737461747383A472737369C0A3736E72C0A171CD0065'")]
    [InlineData("ALTER TABLE announce RENAME COLUMN source TO unknown_source")]
    [InlineData("ALTER TABLE announce RENAME TO original_announce;CREATE VIEW announce AS SELECT * FROM original_announce")]
    [InlineData("WITH RECURSIVE numbers(n) AS (SELECT 10 UNION ALL SELECT n+1 FROM numbers WHERE n<65546) INSERT INTO announce(id,received,source,data,dest_type) SELECT n,0,X'00',X'00','unknown' FROM numbers")]
    public async Task MalformedAnnouncementProfileRollsBackAtomicallyAndPreservesSource(string mutation)
    {
        using var profile = new Profile();
        using var fixture = AnnouncementProfileFixture();
        AddAnnouncementProfileRows(profile, fixture.RootElement);
        WriteSql(Path.Combine(profile.Source, "sideband.db"), mutation);
        var before = profile.Fingerprint();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(profile.Target));
        Assert.Empty(Directory.GetDirectories(profile.Root, "*.import-*"));
        Assert.Equal(before, profile.Fingerprint());
    }

    private static JsonDocument AnnouncementProfileFixture() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-announce-profile.json")));

    private static void AddAnnouncementProfileRows(Profile profile, JsonElement fixture)
    {
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", fixture.GetProperty("revision").GetString());
        var statements = new List<string> { "DROP TABLE announce", fixture.GetProperty("schema").GetString()! };
        var index = 0;
        foreach (var row in fixture.GetProperty("rows").EnumerateArray())
        {
            var received = row.GetProperty("received").GetDouble().ToString("R", CultureInfo.InvariantCulture);
            var extra = row.GetProperty("extra") is { ValueKind: JsonValueKind.String } value ? $"X'{value.GetString()}'" : "NULL";
            statements.Add($"INSERT INTO announce(id,received,source,data,dest_type,extra) VALUES({++index},{received}," +
                $"X'{row.GetProperty("source").GetString()}',X'{row.GetProperty("data").GetString()}'," +
                $"'{row.GetProperty("type").GetString()}',{extra})");
        }
        WriteSql(Path.Combine(profile.Source, "sideband.db"), string.Join(";", statements));
    }
}
