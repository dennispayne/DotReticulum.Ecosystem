using System.Text.Json;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandMapProjectionTests
{
    // GenerateSidebandMapVectors.py executes primary d069122 mapview methods; exact blob hashes are in the fixture.
    [Fact]
    public void ProjectionAndInverseMatchExecutedPrimaryMethods()
    {
        using var fixture = Fixture();
        foreach (var item in fixture.RootElement.GetProperty("projection").EnumerateArray())
        {
            var zoom = item.GetProperty("zoom").GetInt32();
            var size = item.GetProperty("tile_size").GetInt32();
            var actual = SidebandMapProjection.ToWorld(zoom, item.GetProperty("latitude").GetDouble(),
                item.GetProperty("longitude").GetDouble(), size);
            Close(item.GetProperty("x").GetDouble(), actual.X);
            Close(item.GetProperty("y").GetDouble(), actual.Y);
            var inverse = SidebandMapProjection.FromWorld(zoom, actual, size);
            Close(item.GetProperty("inverse_latitude").GetDouble(), inverse.Latitude);
            Close(item.GetProperty("inverse_longitude").GetDouble(), inverse.Longitude);
            Assert.Equal(item.GetProperty("rows").GetInt32(), 1 << zoom);
        }
    }

    [Fact]
    public void TileUrlsMatchActualXyzRowInversionAndQuadKeys()
    {
        using var fixture = Fixture();
        foreach (var item in fixture.RootElement.GetProperty("urls").EnumerateArray())
        {
            var quad = item.GetProperty("quad").GetBoolean();
            var provider = new SidebandMapProvider(quad ? "https://{s}.example.invalid/{q}.png" :
                "https://{s}.example.invalid/{z}/{x}/{y}.png", "Synthetic");
            var tile = new SidebandMapTile(item.GetProperty("zoom").GetInt32(), item.GetProperty("x").GetInt32(),
                item.GetProperty("tms_y").GetInt32());
            Assert.Equal(item.GetProperty("uri").GetString(), provider.TileUri(tile).AbsoluteUri);
        }
    }

    [Fact]
    public void RasterMbTilesMetadataMatchesPrimaryBoundsCenterAndCartesianRules()
    {
        using var fixture = Fixture();
        foreach (var item in fixture.RootElement.GetProperty("mbtiles").EnumerateArray())
        {
            var metadata = item.GetProperty("metadata").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString()!);
            if (item.TryGetProperty("exception", out _))
            {
                Assert.Throws<NotSupportedException>(() => SidebandMbTilesMetadata.Parse(metadata));
                continue;
            }
            var actual = SidebandMbTilesMetadata.Parse(metadata);
            Assert.Equal(item.GetProperty("min_zoom").GetInt32(), actual.MinimumZoom);
            Assert.Equal(item.GetProperty("max_zoom").GetInt32(), actual.MaximumZoom);
            Assert.Equal(item.GetProperty("stored_max_zoom").GetInt32(), actual.StoredMaximumZoom);
            Close(item.GetProperty("latitude").GetDouble(), actual.Latitude);
            Close(item.GetProperty("longitude").GetDouble(), actual.Longitude);
            Assert.Equal(item.GetProperty("zoom").GetInt32(), actual.Zoom);
            Assert.Equal(item.GetProperty("xy").GetBoolean(), actual.IsCartesian);
            Assert.Equal(item.GetProperty("attribution").GetString(), actual.Attribution);
        }
    }

    [Fact]
    public void InvalidCoordinatesTemplatesAndMetadataFailExplicitly()
    {
        foreach (var latitude in new[] { -90d, 90d, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => SidebandMapProjection.ToWorld(0, latitude, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandMapProjection.WorldSize(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandMapProjection.WorldSize(25));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SidebandMapTile(1, 2, 0).Validate());
        Assert.Equal(SidebandMapProjection.DisplayLatitudeLimit, SidebandMapProjection.ClipDisplayLatitude(90));
        foreach (var template in new[] { "http://example.invalid/{z}", "file:///private", "https://user:password@example.invalid/{x}", "https://example.invalid/{unknown}", "https://example.invalid/#fragment" })
            Assert.Throws<ArgumentException>(() => new SidebandMapProvider(template, "Synthetic").TileUri(new(0, 0, 0)));
        var invalid = new Dictionary<string, string> { ["format"] = "png", ["minzoom"] = "1", ["maxzoom"] = "0" };
        Assert.Throws<InvalidDataException>(() => SidebandMbTilesMetadata.Parse(invalid));
        invalid["maxzoom"] = "19";
        invalid["center"] = "NaN,0,5";
        Assert.Throws<InvalidDataException>(() => SidebandMbTilesMetadata.Parse(invalid));
    }

    [Fact]
    public void InitialViewUsesActualPrimaryCoordinatesAndZoomButDoesNotPersistOnlineConsent()
    {
        using var fixture = Fixture();
        var defaults = fixture.RootElement.GetProperty("primary_defaults");
        var native = new SidebandMapViewSettings();
        Assert.Equal(defaults.GetProperty("map_lat").GetDouble(), native.Latitude);
        Assert.Equal(defaults.GetProperty("map_lon").GetDouble(), native.Longitude);
        Assert.Equal(defaults.GetProperty("map_zoom").GetInt32(), native.Zoom);
        Assert.Equal(defaults.GetProperty("map_use_offline").GetBoolean(), native.UseOffline);
        Assert.True(defaults.GetProperty("map_use_online").GetBoolean());
        Assert.Null(typeof(SidebandMapViewSettings).GetProperty("AllowOnline"));
    }

    private static JsonDocument Fixture() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-maps.json")));

    private static void Close(double expected, double actual) =>
        Assert.InRange(Math.Abs(expected - actual), 0, Math.Max(1e-8, Math.Abs(expected) * 1e-12));
}
