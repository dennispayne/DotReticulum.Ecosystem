using System.Text.Json;
using LXMF;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandPropagationPolicyTests
{
    // GenerateSettingsVectors.py executes primary PropagationNodeDetector at
    // d069122af14880e43607aed0b71add4c7806fbe0; the fixture records the source digest.
    [Fact]
    public void SelectionMatchesExecutedReferenceIncludingItsIgnoredEnabledFlag()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "sideband-propagation.json")));
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            Assert.True(LxmfPropagationAnnounce.TryParse(Convert.FromBase64String(item.GetProperty("announce").GetString()!), out var announce));
            var previous = item.GetProperty("previous_hops");
            var selected = SidebandPropagationPolicy.ShouldSelect(item.GetProperty("manual").GetBoolean(),
                previous.ValueKind == JsonValueKind.Null ? null : previous.GetInt32(),
                item.GetProperty("hops").GetInt32(), announce!.Timebase, item.GetProperty("now").GetDouble());
            Assert.True(item.GetProperty("selected").GetBoolean() == selected, item.GetProperty("name").GetString());
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandPropagationPolicy.ShouldSelect(false, null, -1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandPropagationPolicy.ShouldSelect(false, -1, 1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandPropagationPolicy.ShouldSelect(false, null, 1, 0, double.NaN));
    }
}
