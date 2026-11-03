using System.Text.Json;
using Sideband;

namespace Sideband.Tests;

// Executed primary d069122af14880e43607aed0b71add4c7806fbe0, geo.py SHA-256
// af416ae7a1c087cc0c636e994ff58d5c6f7edf9f85379765d7fda9d130e780c7.
[Trait("Category", "Unit")]
public sealed class SidebandGeoTests
{
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-telemetry-store-geo.json")));

    public static IEnumerable<object[]> Cases()
    {
        using var fixture = Fixture();
        return fixture.RootElement.GetProperty("geo").EnumerateArray()
            .Select(c => new object[] { c.GetProperty("name").GetString()!, c.GetProperty("ellipsoid").GetBoolean() }).ToArray();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ActualPrimaryDistanceAndBearingVectors(string name, bool ellipsoid)
    {
        using var fixture = Fixture();
        var item = fixture.RootElement.GetProperty("geo").EnumerateArray().Single(c =>
            c.GetProperty("name").GetString() == name && c.GetProperty("ellipsoid").GetBoolean() == ellipsoid);
        var first = Coordinate(item.GetProperty("first")); var second = Coordinate(item.GetProperty("second"));
        Near(item.GetProperty("euclidian"), SidebandGeo.EuclideanDistance(first, second, ellipsoid), 1e-6);
        Near(item.GetProperty("orthodromic"), SidebandGeo.OrthodromicDistance(first, second, ellipsoid, TestContext.Current.CancellationToken), 0.001);
        var bearing = SidebandGeo.Bearing(first, second, ellipsoid);
        Near(item.GetProperty("azalt")[0], bearing.AzimuthDegrees, 0.00011);
        Near(item.GetProperty("azalt")[1], bearing.ElevationDegrees, 0.00011);
    }

    [Fact]
    public void ActualPrimaryHorizonVectorsAndStrictWithinRange()
    {
        using var fixture = Fixture();
        foreach (var item in fixture.RootElement.GetProperty("horizons").EnumerateArray())
        {
            var height = item.GetProperty("height").GetDouble();
            Near(item.GetProperty("distance"), SidebandGeo.HorizonDistance(height), 1e-6);
            Near(item.GetProperty("angle"), SidebandGeo.HorizonAngle(height), 1e-9);
            Near(item.GetProperty("radio"), SidebandGeo.RadioHorizon(height), 0.001);
        }
        foreach (var item in fixture.RootElement.GetProperty("shared_horizons").EnumerateArray())
        {
            var actual = SidebandGeo.SharedRadioHorizon(Coordinate(item.GetProperty("first")), Coordinate(item.GetProperty("second")));
            var expected = item.GetProperty("result");
            Near(expected.GetProperty("horizon1"), actual.FirstRange, 0.001);
            Near(expected.GetProperty("horizon2"), actual.SecondRange, 0.001);
            Near(expected.GetProperty("shared"), actual.CombinedRange, 0.002);
            Near(expected.GetProperty("geodesic_distance"), actual.SurfaceDistance, 0.001);
            Near(expected.GetProperty("antenna_distance"), actual.AntennaDistance, 1e-6);
            Assert.Equal(expected.GetProperty("within").GetBoolean(), actual.WithinRange);
        }
        Assert.False(SidebandGeo.SharedRadioHorizon(new(0, 0), new(0, 0)).WithinRange);
        Assert.Equal(0, SidebandGeo.HorizonAngle(-100));
    }

    [Fact]
    public void TypedBoundsCancellationAndPrimaryPlaceholdersAreExplicit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandGeo.ToCartesian(new(91, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandGeo.ToCartesian(new(0, 181)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandGeo.ToCartesian(new(double.NaN, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandGeo.Bearing(new(0, 0), new(0, 0, double.PositiveInfinity)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandGeo.HorizonDistance(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandGeo.HorizonAngle(double.NegativeInfinity));
        Assert.Throws<NotSupportedException>(() => SidebandGeo.HorizonDistance(10, ellipsoid: true));
        Assert.Throws<NotSupportedException>(() => SidebandGeo.RadioHorizon(10, ellipsoid: true));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SidebandGeo.OrthodromicDistance(new(0, 0), new(0, 180),
            cancellationToken: cancellation.Token));
        Assert.Null(SidebandGeo.OrthodromicDistance(new(0.001, 0), new(-0.001, 179.9999),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(SidebandGeo.Bearing(new(0, 0), new(0, 0)).AzimuthDegrees);
        Assert.Null(SidebandGeo.Bearing(new(0, 0), new(0, 0)).ElevationDegrees);
        using var fixture = Fixture();
        var errors = fixture.RootElement.GetProperty("geo_errors");
        Assert.Equal("IndexError", errors.GetProperty("two_coordinate_chord").GetProperty("exception").GetString());
        Assert.Equal("NotImplementedError", errors.GetProperty("ellipsoid_horizon").GetProperty("exception").GetString());
    }

    private static SidebandGeoCoordinate Coordinate(JsonElement value) =>
        new(value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble());
    private static void Near(JsonElement expected, double? actual, double tolerance)
    {
        if (expected.ValueKind == JsonValueKind.Null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        Assert.True(Math.Abs(expected.GetDouble() - actual.Value) <= tolerance,
            $"Expected {expected.GetDouble():R}, actual {actual.Value:R}, tolerance {tolerance:R}");
    }
}
