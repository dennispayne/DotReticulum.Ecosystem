using System.Text;
using System.Text.Json;
using Sideband;
using V = Sideband.SidebandTelemetryValue;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandTelemetryReferenceTests
{
    /// <summary>Set SIDEBAND_TELEMETRY_VECTOR_OUTPUT, then run GenerateTelemetryVectors.py --verify-csharp on that file.</summary>
    [Fact]
    public void ManualDataCanBeExportedForActualReferenceVerification()
    {
        var output = Environment.GetEnvironmentVariable("SIDEBAND_TELEMETRY_VECTOR_OUTPUT");
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-telemetry.json")));
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartArray();
            foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
            {
                var id = item.GetProperty("sid").GetByte();
                var data = V.Decode(Convert.FromHexString(item.GetProperty("unpacked_value_hex").GetString()!));
                var manual = SidebandTelemetryCodec.FromData((S)id, data);
                var sensors = new List<SidebandTelemetryReading>();
                if (id != 1) sensors.Add(SidebandTelemetryCodec.FromData(S.Time, V.Fields(("utc", V.From(1700000000L)))));
                sensors.Add(manual);
                var encoded = SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.FromSensors(sensors.ToArray()));
                Assert.NotEmpty(encoded);
                json.WriteStartObject();
                json.WriteString("name", item.GetProperty("name").GetString());
                json.WriteString("wire_hex", Convert.ToHexStringLower(encoded));
                json.WriteString("expected_readings_hex", item.GetProperty("expected_readings_hex").GetString());
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        if (output is not null) File.WriteAllBytes(output, stream.ToArray());
        Assert.Contains("wire_hex", Encoding.UTF8.GetString(stream.ToArray()));
    }
}
