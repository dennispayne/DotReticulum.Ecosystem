using System.Text.Json;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandSynchronizationPolicyTests
{
    // GenerateSettingsVectors.py executes the periodic-sync branch at primary
    // d069122af14880e43607aed0b71add4c7806fbe0; fixture records the core.py digest.
    [Fact]
    public void StrictDeadlineInitializationAndBusyRetryMatchExecutedReference()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "sideband-synchronization.json")));
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var last = item.GetProperty("last");
            var decision = SidebandSynchronizationPolicy.Advance(item.GetProperty("enabled").GetBoolean(),
                item.GetProperty("interval").GetInt32(), last.ValueKind == JsonValueKind.Null ? null : last.GetDouble(),
                item.GetProperty("retrying").GetBoolean(), item.GetProperty("now").GetDouble(), item.GetProperty("accepted").GetBoolean());
            var nextLast = item.GetProperty("next_last");
            Assert.Equal(nextLast.ValueKind == JsonValueKind.Null ? (double?)null : nextLast.GetDouble(), decision.LastSync);
            Assert.Equal(item.GetProperty("next_retrying").GetBoolean(), decision.Retrying);
            Assert.Equal(item.GetProperty("requested").GetBoolean(), decision.Attempted);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandSynchronizationPolicy.Advance(true, 0, null, false, 0, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandSynchronizationPolicy.Advance(true, 1, null, false, double.NaN, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandSynchronizationPolicy.Advance(true, 1, double.PositiveInfinity, false, 0, true));
    }
}
