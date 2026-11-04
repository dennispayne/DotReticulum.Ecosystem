using System.Security.Cryptography;
using System.Text.Json;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandSynchronizationConfigurationTests
{
    [Fact]
    public void ExecutedPrimaryManualLimitAndUnlimitedPeriodicRequestsMatch()
    {
        // GenerateSidebandSyncLimits.py executes Sideband d069122 and pinned LXMF real request APIs.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-sync-limits.json");
        Assert.Equal("a691f2d8374c21b7e73265349bdcaff4c3746cbd055d65d2cd4d0b75327e42cf",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", fixture.RootElement.GetProperty("upstream").GetString());
        foreach (var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
        {
            var settings = row.GetProperty("observed").GetProperty("configured").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone());
            var state = SidebandState.Empty("") with { ImportedSettings = settings };
            if (row.GetProperty("name").GetString() is "zero" or "negative" or "string")
            {
                Assert.Throws<InvalidDataException>(() => SidebandSynchronizationConfiguration.FromState(state));
                continue;
            }
            var configuration = SidebandSynchronizationConfiguration.FromState(state);
            Assert.Equal(row.GetProperty("observed").GetProperty("manual").GetInt32(), configuration.ManualMaximumMessages);
            Assert.Equal(0, row.GetProperty("observed").GetProperty("periodic").GetInt32());
            Assert.False(row.GetProperty("persistent").GetProperty("lxmf.syncretrying").GetBoolean());
            Assert.Equal(1700000000, row.GetProperty("persistent").GetProperty("lxmf.lastsync").GetInt64());
        }
    }

    [Theory]
    [InlineData("""{"lxmf_sync_limit":0}""")]
    [InlineData("""{"lxmf_sync_limit":"false"}""")]
    [InlineData("""{"lxmf_sync_max":null}""")]
    [InlineData("""{"lxmf_sync_max":false}""")]
    [InlineData("""{"lxmf_sync_max":1.5}""")]
    [InlineData("""{"lxmf_sync_max":2147483648}""")]
    [InlineData("""{"lxmf_sync_max":0}""")]
    [InlineData("""{"lxmf_sync_max":-1}""")]
    public void MalformedInactiveAndOversizedLimitsAreRejected(string json)
    {
        using var values = JsonDocument.Parse(json);
        var settings = values.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        Assert.Throws<InvalidDataException>(() => SidebandSynchronizationConfiguration.FromState(
            SidebandState.Empty("") with { ImportedSettings = settings }));
    }
}
