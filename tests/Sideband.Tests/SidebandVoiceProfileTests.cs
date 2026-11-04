using System.Text.Json;
using DotReticulum.Core;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandVoiceProfileTests
{
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "sideband-voice-profile.json")));

    // GenerateSidebandVoiceProfileVectors.py executes primary persistence and fresh-instance loading.
    // Revision d069122af14880e43607aed0b71add4c7806fbe0; voice.py SHA-256 is recorded in the fixture.
    [Fact]
    public void EventsDuplicatesAndStrictRetentionMatchExecutedPrimary()
    {
        using var fixture = Fixture();
        foreach (var row in fixture.RootElement.GetProperty("observations").EnumerateArray())
        {
            var actual = SidebandVoiceProfile.Read(Convert.FromHexString(row.GetProperty("packed_hex").GetString()!),
                row.GetProperty("now").GetDouble());
            var expected = row.GetProperty("loaded").EnumerateArray().ToArray();
            Assert.Equal(expected.Length, actual.Count);
            for (var index = 0; index < actual.Count; index++)
            {
                Assert.Equal(expected[index].GetProperty("event").GetString(), actual[index].Event);
                Assert.Equal(expected[index].GetProperty("identity").GetString(), actual[index].IdentityHash);
                Assert.True(actual[index].Imported);
                Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(expected[index].GetProperty("time").GetDouble()), actual[index].Time);
            }
        }
    }

    [Theory]
    [InlineData("nil")]
    [InlineData("not-map")]
    [InlineData("missing-time")]
    [InlineData("invalid-time")]
    [InlineData("future")]
    [InlineData("invalid-identity")]
    [InlineData("invalid-event")]
    [InlineData("empty-event")]
    [InlineData("trailing")]
    public void MalformedInputCannotBecomeSuccessShapedEmptyHistory(string mutation)
    {
        var row = new Dictionary<object, object?>
        {
            ["time"] = 1_800_000_000d, ["event"] = "incoming-missed", ["identity"] = new byte[16],
        };
        switch (mutation)
        {
            case "missing-time": row.Remove("time"); break;
            case "invalid-time": row["time"] = "yesterday"; break;
            case "future": row["time"] = 1_800_000_241d; break;
            case "invalid-identity": row["identity"] = new byte[15]; break;
            case "invalid-event": row["event"] = "incoming\nmissed"; break;
            case "empty-event": row["event"] = ""; break;
        }
        var packed = mutation switch
        {
            "nil" => MsgPack.Pack(null),
            "not-map" => MsgPack.Pack(new object?[] { "not an event" }),
            _ => MsgPack.Pack(new object?[] { row }),
        };
        if (mutation == "trailing") packed = [.. packed, 0x90];
        Assert.Throws<InvalidDataException>(() => SidebandVoiceProfile.Read(packed, 1_800_000_000));
    }

    [Fact]
    public void LimitsRejectOversizedLogsRatherThanSilentlyDroppingHistory()
    {
        Assert.Throws<InvalidDataException>(() => SidebandVoiceProfile.Read(new byte[SidebandVoiceProfile.MaximumEncodedBytes + 1], 0));
        Assert.Throws<InvalidDataException>(() => SidebandVoiceProfile.Read(
            MsgPack.Pack(new object?[SidebandVoiceProfile.MaximumEntries + 1]), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandVoiceProfile.Read([0x90], double.NaN));
        const double now = 1_800_000_000;
        var row = new Dictionary<object, object?>
        {
            ["time"] = now + 240, ["event"] = "incoming-missed", ["identity"] = new byte[16],
        };
        Assert.Single(SidebandVoiceProfile.Read(MsgPack.Pack(new object?[] { row }), now));
        row["time"] = Math.BitIncrement(now + 240);
        Assert.Throws<InvalidDataException>(() => SidebandVoiceProfile.Read(MsgPack.Pack(new object?[] { row }), now));
    }
}
