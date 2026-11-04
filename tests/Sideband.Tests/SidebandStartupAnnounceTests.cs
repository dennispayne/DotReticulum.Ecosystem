using System.Security.Cryptography;
using System.Text.Json;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandStartupAnnounceTests
{
    [Fact]
    public void ExecutedPrimaryStandaloneDelayAndDeliveryVoiceOrderingMatch()
    {
        // GenerateSidebandStartupAnnounce.py executes the exact d069122 deferred-start method.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-startup-announce.json");
        Assert.Equal("e0b4fb6145e9bbba6e3350e70617f2017f739a754b802f19406642fafbcd2fd4",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", fixture.RootElement.GetProperty("upstream").GetString());
        foreach (var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
        {
            var value = row.GetProperty("configured");
            var state = SidebandState.Empty("") with
            {
                ImportedSettings = new Dictionary<string, JsonElement> { ["start_announce"] = value.Clone() }
            };
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                Assert.Throws<InvalidDataException>(() => SidebandAnnounceConfiguration.FromState(state));
                continue;
            }
            var events = row.GetProperty("events").EnumerateArray().ToArray();
            var expected = SidebandAnnounceConfiguration.FromState(state).AnnounceOnStartup;
            Assert.Equal(expected, events.Any(item => item.GetProperty("kind").GetString() == "delivery"));
            if (!expected) { Assert.Single(events); continue; }
            Assert.Equal(SidebandAnnounceConfiguration.StartupDelay.TotalSeconds, events[0].GetProperty("seconds").GetDouble());
            Assert.Equal("delivery", events[1].GetProperty("kind").GetString());
            Assert.Equal(row.GetProperty("voice").GetBoolean(), events.Any(item => item.GetProperty("kind").GetString() == "voice"));
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("\"false\"")]
    [InlineData("[]")]
    public void PermissivePrimaryTruthinessIsRejected(string json)
    {
        using var value = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => SidebandAnnounceConfiguration.FromState(SidebandState.Empty("") with
        {
            ImportedSettings = new Dictionary<string, JsonElement> { ["start_announce"] = value.RootElement.Clone() }
        }));
    }
}
