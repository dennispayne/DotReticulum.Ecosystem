using System.Text.Json;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband.Tests;

// Actual pinned core d069122af14880e43607aed0b71add4c7806fbe0, Telemeter and SQLite;
// all three source SHA-256 values and dependency disclosures are recorded in the generated fixture.
[Trait("Category", "Unit")]
public sealed class SidebandTelemetryObservationsTests
{
    public static IEnumerable<object[]> Observations()
    {
        using var root = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-telemetry-observations.json")));
        foreach (var item in root.RootElement.GetProperty("observations").EnumerateArray()) yield return [item.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Observations))]
    public void ActualPrimaryEnrichmentMatchesExceptDisclosedUnknownPreservation(string json)
    {
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement;
        var input = item.GetProperty("input");
        var packed = Convert.FromHexString(item.GetProperty("input_hex").GetString()!);
        var report = SidebandTelemetryCodec.Decode(packed);
        SidebandTelemetryLocation? own = null;
        if (input.TryGetProperty("own", out var location))
            own = new(location[0].GetDouble(), location[1].GetDouble(), location[2].GetDouble(), 0, 0, 0, 0);
        var physical = input.GetProperty("physical");
        double? Stat(int index) => physical[index].ValueKind == JsonValueKind.Null ? null : physical[index].GetDouble();
        var actual = SidebandTelemetryObservations.Apply(report, input.GetProperty("receiver").GetString()!,
            input.GetProperty("sender").GetString()!, input.GetProperty("forwarded").GetBoolean(), own, Stat(0), Stat(1), Stat(2));
        var expected = SidebandTelemetryCodec.Decode(Convert.FromHexString(item.GetProperty("output_hex").GetString()!));
        foreach (var sensor in expected.Sensors)
        {
            var observed = actual.Get(sensor.Id)!;
            if (sensor.Id == (byte)SidebandTelemetrySensorId.Received)
            {
                for (var index = 0; index < 4; index++)
                {
                    if (index < 2 || sensor.WireValue.Items[index].IsNil)
                        Assert.Equal(sensor.WireValue.Items[index].Encode(), observed.WireValue.Items[index].Encode());
                    else
                        Assert.InRange(Math.Abs(sensor.WireValue.Items[index].Number!.Value - observed.WireValue.Items[index].Number!.Value), 0, 0.001);
                }
            }
            else if (sensor.Id == (byte)SidebandTelemetrySensorId.PhysicalLink)
                for (var index = 0; index < 3; index++)
                    Assert.Equal(sensor.WireValue.Items[index].Number, observed.WireValue.Items[index].Number);
            else Assert.Equal(sensor.WireValue.Encode(), observed.WireValue.Encode());
        }
        Assert.Equal(packed, SidebandTelemetryCodec.Encode(report));
        Assert.Same(report.TimestampValue, actual.TimestampValue);
        if (input.TryGetProperty("unknown", out _))
        {
            Assert.NotNull(actual.Get(0x80));
            Assert.Null(expected.Get(0x80));
        }
    }

    [Fact]
    public void InvalidContextAndUnboundedNumbersFailExplicitly()
    {
        var report = SidebandTelemetryCodec.Create(1L);
        var hash = new string('a', 32);
        Assert.Throws<ArgumentException>(() => SidebandTelemetryObservations.Apply(report, "bad", hash, false));
        Assert.Throws<ArgumentException>(() => SidebandTelemetryObservations.Apply(report, hash, "bad", true));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandTelemetryObservations.Apply(report, hash, hash, false, rssi: double.NaN));
        Assert.Throws<FormatException>(() => SidebandTelemetryObservations.Apply(SidebandTelemetryCodec.FromSensors(), hash, hash, false));
    }
}
