using System.Text.Json;
using LXMF;

namespace Sideband.Tests;

/// <summary>Actual primary d069122 startup/update/announce observations; generator records exact core/RNS/LXMF source pins.</summary>
[Trait("Category", "Unit")]
public sealed class SidebandStampConfigurationTests
{
    [Fact]
    public void ExecutedPrimaryPolicyAnnouncementAndIndependentRejectionMatch()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-stamp-policy.json")));
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", fixture.RootElement.GetProperty("upstream").GetString());
        var policy = new SidebandStampConfiguration();
        foreach (var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
        {
            var changes = row.GetProperty("changes").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone());
            if (row.GetProperty("name").GetString() is "tooHigh" or "boolean" or "fractional" or "string")
            {
                // Native rejects the invalid update atomically; primary retains its previous effective router cost.
                Assert.Throws<InvalidDataException>(() => SidebandStampConfiguration.Import(changes, policy));
            }
            else policy = SidebandStampConfiguration.Import(changes, policy);
            var expected = row.GetProperty("observed");
            Assert.Equal(expected.GetProperty("enforce").GetBoolean(), policy.RejectInvalidStamps);
            Assert.Equal(expected.GetProperty("effective_cost").ValueKind == JsonValueKind.Null ? null :
                (int?)expected.GetProperty("effective_cost").GetInt32(), policy.EffectiveCost);
            Assert.Equal(Convert.FromHexString(expected.GetProperty("app_data").GetString()!),
                LxmfAppData.Build("Anonymous Peer", policy.EffectiveCost));
        }
    }

    [Theory]
    [InlineData("""{"lxmf_require_stamps":null}""")]
    [InlineData("""{"lxmf_require_stamps":1}""")]
    [InlineData("""{"lxmf_ignore_invalid_stamps":"false"}""")]
    [InlineData("""{"lxmf_ignore_invalid_stamps":null}""")]
    [InlineData("""{"lxmf_inbound_stamp_cost":8.0}""")]
    [InlineData("""{"lxmf_inbound_stamp_cost":2147483648}""")]
    [InlineData("""{"lxmf_inbound_stamp_cost":{}}""")]
    public void MalformedInactiveAndOversizePoliciesAreRejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        var settings = document.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone());
        Assert.Throws<InvalidDataException>(() => SidebandStampConfiguration.Import(settings));
        Assert.Throws<InvalidDataException>(() => new SidebandStampConfiguration(false, 255).Validate());
    }
}
