using System.Text.Json;
using Sideband;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband.Tests;

// These assert independently executed reference observations, NOT an implemented remote-command or privacy service.
[Trait("Category", "Unit")]
public sealed class SidebandTelemetryStoreContractTests
{
    [Fact]
    public void ActualPrimaryPrivacyDefaultsAndCollectorExclusionsAreRecorded()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-telemetry-store-geo.json")));
        var root = fixture.RootElement;
        Assert.Equal("af416ae7a1c087cc0c636e994ff58d5c6f7edf9f85379765d7fda9d130e780c7",
            root.GetProperty("source_sha256").GetProperty("geo").GetString());
        var defaults = root.GetProperty("defaults");
        foreach (var name in new[] { "telemetry_enabled", "telemetry_send_to_trusted", "telemetry_send_to_collector",
            "telemetry_allow_requests_from_anyone", "telemetry_allow_requests_from_trusted",
            "telemetry_receive_trusted_only", "telemetry_display_trusted_only", "telemetry_collector_enabled" })
            Assert.False(defaults.GetProperty(name).GetBoolean(), name);
        Assert.True(defaults.GetProperty("telemetry_requests_only_send_latest").GetBoolean());
        foreach (var item in root.GetProperty("privacy").EnumerateArray())
        {
            if (!item.GetProperty("enabled").GetBoolean()) Assert.False(item.GetProperty("send").GetBoolean());
            if (item.GetProperty("trust").GetInt32() == 2)
            {
                Assert.False(item.GetProperty("allow_request").GetBoolean());
                if (item.GetProperty("enabled").GetBoolean())
                    Assert.Equal(item.GetProperty("per_conversation").GetBoolean(), item.GetProperty("send").GetBoolean());
            }
        }
        foreach (var item in root.GetProperty("streams").EnumerateArray())
        {
            var result = V.Decode(Convert.FromHexString(item.GetProperty("result_hex").GetString()!));
            var stream = result.Get("stream")!;
            Assert.Equal(item.GetProperty("latest_only").GetBoolean() ? 2 : 3, stream.Items.Count);
            foreach (var entry in stream.Items)
            {
                Assert.Equal(4, entry.Items.Count);
                var source = entry.Items[0].GetBinary()!;
                Assert.Equal(16, source.Length);
                Assert.NotEqual(3, source[0]); // Explicit collector exclusion.
                Assert.NotEqual(4, source[0]); // Requester is never echoed its own source.
                Assert.Equal(entry.Items[1].Number,
                    SidebandTelemetryCodec.Decode(entry.Items[2].GetBinary()!).TimestampUtcSeconds);
                Assert.True(entry.Items[3].IsNil); // Controlled appearance callback, not an appearance-service claim.
            }
        }
    }
}
