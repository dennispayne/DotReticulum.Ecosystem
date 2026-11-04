using System.Text.Json;
using Sideband;

namespace Sideband.Tests;

// GenerateSidebandMapProfileVectors.py executes primary d069122af14880e43607aed0b71add4c7806fbe0.
// source.py SHA256 d3162e1acd36c02617842f93e2e7b67f96a582136819c25903c7d631c7790986.
[Trait("Category", "Unit")]
public sealed class SidebandMapPresetTests
{
    [Fact]
    public void EveryPresetMatchesExecutedPrimaryWithExplicitHttpsSafetyDifference()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-map-profile.json")));
        var providers = fixture.RootElement.GetProperty("providers").EnumerateArray().ToArray();
        Assert.Equal(13, providers.Length);
        Assert.Equal(SidebandMapPresets.Ids, providers.Select(provider => provider.GetProperty("id").GetString()!));
        foreach (var observation in providers)
        {
            var id = observation.GetProperty("id").GetString()!;
            var preset = SidebandMapPresets.Create(id, id.StartsWith("mt_", StringComparison.Ordinal) ?
                "synthetic-maptiler-key" : "synthetic-stadia-key", "https://maps.example.invalid/{z}/{x}/{y}.png");
            Assert.Equal(observation.GetProperty("name").GetString(), preset.Name);
            Assert.Equal(observation.GetProperty("url").GetString()!.Replace("http://", "https://", StringComparison.Ordinal), preset.Provider.UrlTemplate);
            Assert.Equal(observation.GetProperty("attribution").GetString(), preset.Provider.Attribution);
            Assert.Equal(observation.GetProperty("minimum_zoom").GetInt32(), preset.MinimumZoom);
            Assert.Equal(observation.GetProperty("maximum_zoom").GetInt32(), preset.MaximumZoom);
            var uri = preset.Provider.TileUri(new(2, 1, 0));
            Assert.Equal("https", uri.Scheme);
            if (observation.GetProperty("quad").GetBoolean()) Assert.Contains("a23.jpeg", uri.AbsoluteUri);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad&key=value")]
    [InlineData("bad\nkey")]
    [InlineData("bad#fragment")]
    public void ProvidersNeverUseBundledKeysOrAcceptQueryInjection(string? key)
    {
        Assert.Throws<ArgumentException>(() => SidebandMapPresets.Create("mt_topo", key));
        Assert.Throws<ArgumentException>(() => SidebandMapPresets.Create("st_dark", key));
    }

    [Fact]
    public void InvalidPresetAndInsecureCustomSourceRejectBeforeReturningProvider()
    {
        Assert.Throws<ArgumentException>(() => SidebandMapPresets.Create("missing"));
        Assert.Throws<ArgumentException>(() => SidebandMapPresets.Create("custom_1"));
        Assert.Throws<ArgumentException>(() => SidebandMapPresets.Create("custom_1", customUrl: "http://example.invalid/{z}"));
        Assert.Throws<ArgumentException>(() => SidebandMapPresets.Create("mt_hybrid", new string('a', 513)));
    }
}
