using System.Text.Json;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband.Tests;

// GenerateMessageTelemetryVectors.py executes gen_copy_telemetry, UI link rounding and
// Telemeter.read_all at d069122af14880e43607aed0b71add4c7806fbe0; Git-blob SHA-256s are in the fixture.
[Trait("Category", "Integration")]
public sealed class SidebandMessageTelemetryVectorTests
{
    [Theory]
    [Trait("TestId", "T830")]
    [MemberData(nameof(Cases))]
    public void ActualPrimarySelectedMessageCopyMatchesReadingsAndText(string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement;
        if (name == "missing-report")
        {
            Assert.Equal("Could not decode telemetry", Assert.Single(item.GetProperty("copied").EnumerateArray()).GetString());
            Assert.Equal(1, item.GetProperty("error_count").GetInt32());
            Assert.Throws<ArgumentNullException>(() => SidebandMessageTelemetry.CopyText(null!));
            return;
        }
        var readings = V.Decode(Convert.FromHexString(item.GetProperty("readings_hex").GetString()!));
        Assert.Equal(Assert.Single(item.GetProperty("copied").EnumerateArray()).GetString(),
            SidebandMessageTelemetry.Format(readings));
        if (item.GetProperty("extra_link").EnumerateObject().Any())
        {
            var link = item.GetProperty("extra_link");
            var report = SidebandMessageTelemetry.WithLink(
                SidebandTelemetryCodec.Decode(Convert.FromHexString(item.GetProperty("wire_hex").GetString()!)),
                Metric("rssi"), Metric("snr"), Metric("quality"));
            Assert.Equal(Assert.Single(item.GetProperty("copied").EnumerateArray()).GetString(),
                SidebandMessageTelemetry.CopyText(Convert.ToHexString(SidebandTelemetryCodec.Encode(report))));
            return;

            double? Metric(string key)
            {
                var raw = item.GetProperty("raw_link");
                if (raw.EnumerateObject().Any()) return raw.GetProperty(key == "quality" ? "q" : key).GetDouble();
                return link.TryGetProperty(key, out var value) ? value.GetDouble() : null;
            }
        }
        Assert.Equal(Assert.Single(item.GetProperty("copied").EnumerateArray()).GetString(),
            SidebandMessageTelemetry.CopyText(item.GetProperty("wire_hex").GetString()!));
        Assert.Equal(0, item.GetProperty("error_count").GetInt32());
    }

    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-message-telemetry.json")));
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", document.RootElement.GetProperty("revision").GetString());
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("name").GetString()!, item.GetRawText()];
    }
}

[Trait("Category", "Unit")]
public sealed class SidebandMessageTelemetryInputTests
{
    [Fact]
    [Trait("TestId", "T831")]
    public void MalformedOversizedAndUnknownReportsDoNotBecomeAnotherSourcesLatestData()
    {
        Assert.Throws<InvalidDataException>(() => SidebandMessageTelemetry.CopyText(new string('0', SidebandTelemetryCodec.MaxEncodedBytes * 2 + 1)));
        Assert.Throws<FormatException>(() => SidebandMessageTelemetry.CopyText("not-hex"));
        Assert.Throws<FormatException>(() => SidebandMessageTelemetry.CopyText("90"));
        var unknown = V.Map(KeyValuePair.Create(V.From(222L), V.From("Unrecognised sensor"))).Encode();
        Assert.Equal("{}", SidebandMessageTelemetry.CopyText(Convert.ToHexString(unknown)));
    }

    [Fact]
    [Trait("TestId", "T832")]
    public void CopyTextBudgetNeverReturnsTruncatedOrAmbiguousData()
    {
        var binary = V.From(new byte[SidebandTelemetryCodec.MaxValueBytes]);
        var maximal = V.Array(binary, binary, binary);
        Assert.Equal(786_447, SidebandMessageTelemetry.Format(maximal).Length);
        Assert.Throws<ArgumentException>(() => V.Array(binary, binary, binary, binary));
        Assert.Equal(SidebandMessageTelemetry.MaximumCopyCharacters, SidebandMessageTelemetry.Format(Boundary(0)).Length);
        Assert.Throws<InvalidDataException>(() => SidebandMessageTelemetry.Format(Boundary(1)));
        Assert.Equal("{1: None, '1': False}", SidebandMessageTelemetry.Format(
            V.Map(KeyValuePair.Create(V.From(1L), V.Nil), KeyValuePair.Create(V.From("1"), V.From(false)))));

        static V Boundary(int extra)
        {
            var nils = V.Array(Enumerable.Repeat(V.Nil, 1020).ToArray());
            return V.Array(Enumerable.Range(0, 4)
                .Select(index => V.From(new byte[59_412 + (index == 3 ? 3 + extra : 0)]))
                .Concat(Enumerable.Repeat(nils, 16)).ToArray());
        }
    }
}
