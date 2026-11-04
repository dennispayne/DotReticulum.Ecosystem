using System.Text.Json;
using Sideband;

namespace Sideband.Tests;

// GenerateSidebandDiscoveryVectors.py executes primary d069122af14880e43607aed0b71add4c7806fbe0
// and real RNS a29bfb51c468945c96a1ee22cae0114e993e2391 passive cache listing.
// The fixture records canonical source hashes and synthetic cache/UI seams, not live announcements.
[Trait("Category", "Unit")]
public sealed class SidebandMapDiscoveryTests
{
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-discovery.json")));

    internal static SidebandDiscoveredInterface Interface(JsonElement value)
    {
        var latitude = value.GetProperty("latitude");
        var longitude = value.GetProperty("longitude");
        return new(value.GetProperty("discovery_hash").GetString()!,
            value.GetProperty("type").GetString()!, value.GetProperty("name").GetString()!,
            value.GetProperty("transport").GetBoolean(), value.GetProperty("network_id").GetString()!,
            value.GetProperty("transport_id").GetString()!,
            latitude.ValueKind == JsonValueKind.Null || longitude.ValueKind == JsonValueKind.Null
                ? null : new(latitude.GetDouble(), longitude.GetDouble()),
            value.GetProperty("status").GetString()!, value.GetProperty("value").GetInt32(),
            2, value.GetProperty("last_heard").GetDouble(),
            value.GetProperty("reachable_on").GetString(), value.GetProperty("port").GetInt32());
    }

    [Fact]
    public void ActualPrimaryPassiveListingAndProjectionMatchWithExplicitZeroCoordinateSafetyDifference()
    {
        using var fixture = Fixture();
        var root = fixture.RootElement;
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", root.GetProperty("revision").GetString());
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", root.GetProperty("rns_revision").GetString());
        Assert.Equal("3c7833ccd1a682c7313208874e1a66ff9495280d01827c67b0405d7402d4a89d",
            root.GetProperty("discovery_source_sha256").GetString());
        Assert.True(root.GetProperty("initially_empty").GetBoolean());
        Assert.True(root.GetProperty("passive_instance_reused").GetBoolean());
        var interfaces = root.GetProperty("listed").EnumerateArray().Select(Interface).ToArray();
        Assert.Equal(7, interfaces.Length);
        Assert.Equal(7, root.GetProperty("cache_files_after_listing").GetArrayLength());
        Assert.DoesNotContain(interfaces, item => item.Name == "expired");
        var markers = SidebandMapDiscovery.Project(interfaces);
        Assert.Equal(5, markers.Count);
        foreach (var expected in root.GetProperty("primary_markers").EnumerateArray())
        {
            var marker = Assert.Single(markers, item => item.SourceHash == expected.GetProperty("key").GetString());
            Assert.Equal(expected.GetProperty("latitude").GetDouble(), marker.Coordinate.Latitude);
            Assert.Equal(expected.GetProperty("longitude").GetDouble(), marker.Coordinate.Longitude);
            Assert.Equal(expected.GetProperty("appearance")[0].GetString(), marker.Appearance.Icon);
            Assert.Equal(SidebandMapOverlayKind.Discovery, marker.Kind);
            Assert.Equal("discovery:" + marker.SourceHash, marker.Key);
            Assert.Null(marker.ReportingSourceHash);
            Assert.Equal(64, marker.SourceHash.Length);
        }
        Assert.Equal(3, root.GetProperty("primary_markers").GetArrayLength());
        Assert.Contains(markers, marker => marker.Coordinate.Latitude == 0);
        Assert.Contains(markers, marker => marker.Coordinate.Longitude == 0);
        Assert.DoesNotContain(markers, marker => marker.Name is "not-transport" or "no-location");
        Assert.Contains(markers, marker => marker.Name == "unknown");
        Assert.Contains(markers, marker => marker.Name == "stale");
    }

    [Fact]
    public void ProjectionReplacesMovedAndDisappearedRecordsAndRejectsAmbiguousOrUnboundedInput()
    {
        using var fixture = Fixture();
        var item = fixture.RootElement.GetProperty("listed").EnumerateArray().Select(Interface)
            .First(entry => entry.Name == "recent");
        Assert.Single(SidebandMapDiscovery.Project([item]));
        var moved = Assert.Single(SidebandMapDiscovery.Project([item with { Location = new(0, 0) }]));
        Assert.Equal(new SidebandGeoCoordinate(0, 0), moved.Coordinate);
        Assert.Empty(SidebandMapDiscovery.Project([]));
        Assert.Throws<ArgumentException>(() => SidebandMapDiscovery.Project([item, item]));
        Assert.Throws<ArgumentException>(() => SidebandMapDiscovery.Project([item with { DiscoveryHash = "invalid" }]));
        Assert.Throws<ArgumentException>(() => SidebandMapDiscovery.Project([item with { Location = new(double.NaN, 0) }]));
        Assert.Throws<ArgumentException>(() => SidebandMapDiscovery.Project(
            Enumerable.Repeat(item, SidebandMapDiscovery.MaximumRecords + 1).ToArray()));
    }
}
