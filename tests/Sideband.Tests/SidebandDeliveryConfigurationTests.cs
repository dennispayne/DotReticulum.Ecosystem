using System.Text.Json;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandDeliveryConfigurationTests
{
    // Executed primary d069122af14880e43607aed0b71add4c7806fbe0,
    // core.py SHA256 8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25.
    // GenerateSidebandDeliveryLimits.py runs the real constructor/startup/update methods.
    [Fact]
    public void ExecutedPrimaryLimitsUseDecimalKilobytesAndRejectAccidentalTruthiness()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-delivery-limits.json")));
        var current = new Dictionary<string, JsonElement>();
        var count = 0;
        foreach (var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
        {
            foreach (var property in row.GetProperty("changes").EnumerateObject()) current[property.Name] = property.Value.Clone();
            var state = SidebandState.Empty("unused") with { ImportedSettings = current };
            if (row.GetProperty("observed").GetProperty("configured").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                Assert.Throws<InvalidDataException>(() => SidebandDeliveryConfiguration.FromState(state));
            else
                Assert.Equal(row.GetProperty("observed").GetProperty("limit_kilobytes").GetInt64() * 1000,
                    SidebandDeliveryConfiguration.FromState(state).MaximumTransferBytes);
            count++;
        }
        Assert.Equal(8, count);
        Assert.Equal(1_000_000, SidebandDeliveryConfiguration.FromState(SidebandState.Empty("unused")).MaximumTransferBytes);
        Assert.Equal(128_000_000, new SidebandDeliveryConfiguration(false).MaximumTransferBytes);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("\"false\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void MalformedFlagsNeverActivate(string value)
    {
        using var json = JsonDocument.Parse(value);
        var state = SidebandState.Empty("unused") with
        {
            ImportedSettings = new Dictionary<string, JsonElement> { ["lxm_limit_1mb"] = json.RootElement.Clone() }
        };
        Assert.Throws<InvalidDataException>(() => SidebandDeliveryConfiguration.FromState(state));
    }
}
