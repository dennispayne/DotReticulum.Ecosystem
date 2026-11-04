using System.Text.Json;

namespace Sideband.Tests;

/// <summary>Executed primary d069122 SQLite/providers; exact source digests and seams recorded by GenerateSidebandAnnounceVectors.py.</summary>
[Trait("Category", "Unit")]
public sealed class SidebandAnnounceReferenceTests
{
    [Fact]
    public void ActualPrimaryNamesCostsNodeMetadataOrderingReplacementAndDeletionMatch()
    {
        using var fixture = Fixture();
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", fixture.RootElement.GetProperty("revision").GetString());
        var operations = fixture.RootElement.GetProperty("operations").EnumerateArray().ToArray();
        var snapshots = fixture.RootElement.GetProperty("snapshots").EnumerateArray().ToArray();
        IReadOnlyList<SidebandAnnounce> history = [];
        Check(history, snapshots[0]);
        for (var index = 0; index < 4; index++)
        {
            var item = Decode(operations[index]);
            if (index == 0) item = item with { Signal = new(-77, 12.5, 94) };
            history = SidebandAnnounce.Observe(history, item);
        }
        Check(history, snapshots[1]);
        history = SidebandAnnounce.Observe(history, Decode(operations[4]));
        Check(history, snapshots[2]);
        history = history.Select(item => item.DestinationHash == operations[4].GetProperty("address").GetString()
            ? item with { StampCost = 23 } : item).ToArray();
        Check(history, snapshots[3]);
        history = history.Where(item => item.DestinationHash != operations[4].GetProperty("address").GetString()).ToArray();
        Check(history, snapshots[4]);
    }

    [Fact]
    public void PrimaryPreinsertOffByOneIsExplicitlyCorrectedTo48AfterEveryObservation()
    {
        using var fixture = Fixture();
        IReadOnlyList<SidebandAnnounce> history = [];
        foreach (var operation in fixture.RootElement.GetProperty("operations").EnumerateArray().Skip(5).SkipLast(1))
        {
            history = SidebandAnnounce.Observe(history, Decode(operation));
            Assert.InRange(history.Count, 1, 48);
        }
        var snapshots = fixture.RootElement.GetProperty("snapshots").EnumerateArray().ToArray();
        Assert.Equal(49, snapshots[^2].GetProperty("records").GetArrayLength());
        Check(history, snapshots[^2], skipOldest: 1);
        history = SidebandAnnounce.Observe(history, Decode(fixture.RootElement.GetProperty("operations").EnumerateArray().Last()));
        Check(history, snapshots[^1]);
    }

    [Fact]
    public void MalformedUnboundedNonfiniteAndInvalidMetadataAreRejected()
    {
        var valid = new SidebandAnnounce(new string('1', 32), SidebandAnnounce.Delivery, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => SidebandAnnounce.FromAppData("bad", SidebandAnnounce.Delivery, DateTimeOffset.UtcNow, null));
        Assert.Throws<ArgumentException>(() => SidebandAnnounce.FromAppData(valid.DestinationHash, "unknown", valid.ReceivedAt, null));
        Assert.Throws<InvalidDataException>(() => SidebandAnnounce.FromAppData(valid.DestinationHash, SidebandAnnounce.Propagation, valid.ReceivedAt, [0]));
        Assert.Throws<InvalidDataException>(() => SidebandAnnounce.FromAppData(valid.DestinationHash, SidebandAnnounce.Delivery, valid.ReceivedAt, new byte[4097]));
        foreach (var invalid in new[]
        {
            valid with { StampCost = 0 }, valid with { StampCost = 255 }, valid with { StampCost = -1 },
            valid with { NodeEnabled = true }, valid with { Name = new string('a', 1025) },
            valid with { Name = "\ud800" }, valid with { Signal = new(double.NaN) },
            valid with { Signal = new(Snr: double.PositiveInfinity) }, valid with { Signal = new(Quality: 101) }
        })
            Assert.Throws<InvalidDataException>(() => SidebandAnnounce.Observe([], invalid));
        Assert.Single(SidebandAnnounce.Observe([], valid with { Name = "Untrusted\nname" }));
    }

    internal static SidebandAnnounce Decode(JsonElement item) => SidebandAnnounce.FromAppData(
        item.GetProperty("address").GetString()!, item.GetProperty("type").GetString()!,
        DateTimeOffset.FromUnixTimeSeconds(item.GetProperty("received").GetInt64()),
        Convert.FromHexString(item.GetProperty("app_data").GetString()!),
        item.GetProperty("cached_cost") is { ValueKind: JsonValueKind.Number } cost ? cost.GetInt32() : null);

    private static void Check(IReadOnlyList<SidebandAnnounce> history, JsonElement snapshot, int skipOldest = 0)
    {
        var expected = snapshot.GetProperty("records").EnumerateArray().Skip(skipOldest).ToArray();
        Assert.Equal(expected.Length, history.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            var item = expected[index];
            var actual = history[index];
            Assert.Equal(item.GetProperty("dest").GetString(), actual.DestinationHash);
            Assert.Equal(item.GetProperty("name").GetString(), actual.Name);
            Assert.Equal(item.GetProperty("type").GetString(), actual.DestinationType);
            Assert.Equal(item.GetProperty("pn_ver").GetString(), actual.NodeVersion);
            Assert.Equal(item.GetProperty("time").GetDouble(), actual.ReceivedAt.ToUnixTimeMilliseconds() / 1000d);
            Assert.Equal(item.GetProperty("cost") is { ValueKind: JsonValueKind.Number } cost ? cost.GetInt32() : (int?)null, actual.StampCost);
            if (item.GetProperty("extras").GetProperty("link_stats") is { ValueKind: JsonValueKind.Object } signal)
            {
                Assert.NotNull(actual.Signal);
                Assert.Equal(signal.GetProperty("rssi").GetDouble(), actual.Signal.Rssi);
                Assert.Equal(signal.GetProperty("snr").GetDouble(), actual.Signal.Snr);
                Assert.Equal(signal.GetProperty("q").GetDouble(), actual.Signal.Quality);
            }
        }
    }

    private static JsonDocument Fixture() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-announces.json")));
}
