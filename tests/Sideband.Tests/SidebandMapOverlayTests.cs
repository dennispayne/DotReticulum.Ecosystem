using System.Text.Json;
using Sideband;
using V = Sideband.SidebandTelemetryValue;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband.Tests;

// GenerateSidebandMapOverlayVectors.py executes primary d069122af14880e43607aed0b71add4c7806fbe0.
// map.py SHA256 cfd65489d5f94ddac3990ebd50c6b346e3cd6d0e0c3bfc1e30880ca983e5bf11.
// clustered_marker_layer.py SHA256 783ef113242283d7906e00375e0f5dd1b9e503aad0e0a0cd62912cdb0ec2cca7.
// The fixture discloses AST selection, clocks, stable input-order scatter identity and synthetic UI positions.
[Trait("Category", "Unit")]
public sealed class SidebandMapOverlayTests
{
    private const string Peer = "22222222222222222222222222222222";
    private static readonly SidebandMapOverlayAppearance PeerAppearance = new("account", new(0.2, 0.3, 0.4), new(1, 1, 1));
    private static readonly SidebandMapOverlayAppearance OwnAppearance = new("account", new(0, 0, 0), new(1, 1, 1));
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-map-overlays.json")));

    public static IEnumerable<object[]> AppearanceCases() => Cases("appearance_cases", "type");
    public static IEnumerable<object[]> IdentityCases() => Cases("identity_cases", "source");
    public static IEnumerable<object[]> ConnectionCases() => Cases("connection_marker_cases", "type_label");
    public static IEnumerable<object[]> ClusterCases() => Cases("cluster_cases", "name");
    public static IEnumerable<object[]> ScatterCases() => Cases("scatter_cases", "name");
    public static IEnumerable<object[]> BoundsCases() => Cases("bounds_cases", null);

    private static IEnumerable<object[]> Cases(string property, string? nameProperty)
    {
        using var fixture = Fixture();
        var index = 0;
        foreach (var value in fixture.RootElement.GetProperty(property).EnumerateArray())
            yield return [nameProperty is null ? index++.ToString() : value.GetProperty(nameProperty).GetString()!, value.GetRawText()];
    }

    [Fact]
    public void DefaultsHaveExactExecutedPrimaryProvenance()
    {
        using var fixture = Fixture();
        var root = fixture.RootElement;
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", root.GetProperty("revision").GetString());
        Assert.Equal(9, root.GetProperty("source_sha256").EnumerateObject().Count());
        Assert.Equal("783ef113242283d7906e00375e0f5dd1b9e503aad0e0a0cd62912cdb0ec2cca7",
            root.GetProperty("source_sha256").GetProperty("sbapp/mapview/clustered_marker_layer.py").GetString());
        Assert.Equal(SidebandMapOverlays.HistoryLimitSeconds, root.GetProperty("defaults").GetProperty("map_history_limit").GetInt32());
        Assert.True(root.GetProperty("defaults").GetProperty("map_cluster").GetBoolean());
        Assert.True(root.GetProperty("defaults").GetProperty("map_interfaces").GetBoolean());
        Assert.False(root.GetProperty("defaults").GetProperty("map_connection_maps").GetBoolean());
        Assert.False(root.GetProperty("defaults").GetProperty("map_hide_blocked").GetBoolean());
        var options = root.GetProperty("cluster_options");
        Assert.Equal(SidebandMapOverlays.ClusterMaximumZoom, options.GetProperty("cluster_max_zoom").GetInt32());
        Assert.Equal(SidebandMapOverlays.ClusterRadius, options.GetProperty("cluster_radius").GetDouble());
        Assert.Equal(SidebandMapOverlays.ClusterExtent, options.GetProperty("cluster_extent").GetDouble());
        Assert.Equal(SidebandMapOverlays.InterfaceUpdateIntervalSeconds, root.GetProperty("interface_update_interval").GetInt32());
    }

    [Theory]
    [MemberData(nameof(AppearanceCases))]
    public void AppearanceMatchesExecutedPrimary(string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        AssertAppearance(document.RootElement.GetProperty("appearance"), SidebandMapOverlays.InterfaceAppearance(name));
    }

    [Theory]
    [MemberData(nameof(IdentityCases))]
    public void InterfaceIdentityIncludesReportingSourceAndExactUtf8Name(string source, string json)
    {
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        var report = SidebandTelemetryCodec.Decode(Convert.FromHexString(value.GetProperty("wire_hex").GetString()!));
        var appearance = source == Peer ? PeerAppearance : OwnAppearance;
        var projection = SidebandMapOverlays.ProjectTelemetry(source, report, appearance);
        Assert.Empty(projection.Errors);
        var expected = value.GetProperty("markers").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, projection.Markers.Count);
        foreach (var marker in projection.Markers)
        {
            Assert.Equal(SidebandMapOverlayKind.Interface, marker.Kind);
            Assert.Equal(source, marker.ReportingSourceHash);
            Assert.Equal(64, marker.Key.Length);
            AssertMarker(expected.Single(item => item.GetProperty("key").GetString() == marker.Key), marker);
        }
        Assert.NotEqual(projection.Markers[0].Key, projection.Markers[1].Key);
    }

    [Theory]
    [MemberData(nameof(ConnectionCases))]
    public void ConnectionPointUsesActualHashOwnerFilterAndQualityDispatch(string name, string json)
    {
        Assert.NotEmpty(name);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        var report = SidebandTelemetryCodec.Decode(Convert.FromHexString(value.GetProperty("wire_hex").GetString()!));
        var result = SidebandMapOverlays.ProjectTelemetry(Peer, report, PeerAppearance, new(false, true));
        Assert.Empty(result.Errors);
        var marker = Assert.Single(result.Markers);
        Assert.Equal(SidebandMapOverlayKind.Connection, marker.Kind);
        Assert.Equal(Peer, marker.SourceHash);
        Assert.Equal(Peer, marker.ReportingSourceHash);
        AssertMarker(value.GetProperty("markers")[0], marker);
        var blocked = value.GetProperty("type_label").GetString() == "blocked";
        Assert.Equal(blocked ? 0 : 1,
            SidebandMapOverlays.ProjectTelemetry(Peer, report, options: new(false, true, HideBlocked: true)).Markers.Count);
        Assert.Equal(blocked ? 1 : 0,
            SidebandMapOverlays.ProjectTelemetry(Peer, report, options: new(false, true, OnlyBlocked: true)).Markers.Count);
    }

    [Theory]
    [MemberData(nameof(ClusterCases))]
    public void ClustersAndBoundsMatchExecutedPrimaryWithoutReusingItsIndex(string name, string json)
    {
        Assert.NotEmpty(name);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        var markers = Markers(value.GetProperty("coordinates"));
        var bbox = value.GetProperty("bbox").EnumerateArray().Select(item => item.GetDouble()).ToArray();
        var result = SidebandMapOverlays.Cluster(markers, value.GetProperty("zoom").GetInt32(),
            new(bbox[0], bbox[1], bbox[2], bbox[3]));
        var expected = value.GetProperty("result").EnumerateArray()
            .OrderBy(item => item.GetProperty("longitude").GetDouble()).ThenBy(item => item.GetProperty("latitude").GetDouble()).ToArray();
        var actual = result.OrderBy(item => item.Coordinate.Longitude).ThenBy(item => item.Coordinate.Latitude).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < actual.Length; index++)
        {
            var observed = expected[index];
            var cluster = actual[index];
            Near(observed.GetProperty("latitude").GetDouble(), cluster.Coordinate.Latitude);
            Near(observed.GetProperty("longitude").GetDouble(), cluster.Coordinate.Longitude);
            Assert.Equal(observed.GetProperty("count").GetInt32(), cluster.Count);
            Assert.Equal(cluster.Count > 1, cluster.IsCluster);
            var extents = observed.GetProperty("extents");
            if (extents.ValueKind == JsonValueKind.Null) Assert.Equal(1, cluster.Count);
            else
            {
                Near(extents[0].GetDouble(), cluster.Bounds.South);
                Near(extents[1].GetDouble(), cluster.Bounds.North);
                Near(extents[2].GetDouble(), cluster.Bounds.West);
                Near(extents[3].GetDouble(), cluster.Bounds.East);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ScatterCases))]
    public void ScatterMatchesActualPrimaryWithDisclosedStableInputOrdering(string name, string json)
    {
        Assert.NotEmpty(name);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        var positions = value.GetProperty("window_positions").EnumerateArray()
            .Select(point => new SidebandMapPoint(point[0].GetDouble(), point[1].GetDouble())).ToArray();
        var result = SidebandMapOverlays.Scatter(Markers(value.GetProperty("coordinates")), positions,
            value.GetProperty("zoom").GetInt32());
        var expected = value.GetProperty("result").EnumerateArray()
            .ToDictionary(item => item.GetProperty("index").GetInt32(), item => item.GetProperty("offset"));
        Assert.Equal(positions.Length, result.Count);
        for (var index = 0; index < result.Count; index++)
        {
            Near(expected.TryGetValue(index, out var offset) ? offset[0].GetDouble() : 0, result[index].X);
            Near(expected.TryGetValue(index, out offset) ? offset[1].GetDouble() : 0, result[index].Y);
        }
    }

    [Theory]
    [MemberData(nameof(BoundsCases))]
    public void ViewportBoundsFitMatchesActualPrimary(string name, string json)
    {
        Assert.NotEmpty(name);
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        var extents = value.GetProperty("extents");
        var actual = SidebandMapOverlays.FitBounds(
            new(extents[2].GetDouble(), extents[0].GetDouble(), extents[3].GetDouble(), extents[1].GetDouble()),
            value.GetProperty("size")[0].GetDouble(), value.GetProperty("size")[1].GetDouble(),
            padding: value.GetProperty("padding").GetDouble(), fractionalZoom: value.GetProperty("float_zoom").GetBoolean());
        var expected = value.GetProperty("result");
        Near(expected.GetProperty("center")[0].GetDouble(), actual.Latitude);
        Near(expected.GetProperty("center")[1].GetDouble(), actual.Longitude);
        Near(expected.GetProperty("zoom").GetDouble(), actual.Zoom);
    }

    [Fact]
    public void PrimaryOverlaySelectionMatchesWithoutInventingConnectivityEdges()
    {
        using var fixture = Fixture();
        var root = fixture.RootElement;
        var report = root.GetProperty("reports").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "newest_without_location");
        var decoded = SidebandTelemetryCodec.Decode(Convert.FromHexString(report.GetProperty("wire_hex").GetString()!));
        var actual = SidebandMapOverlays.ProjectTelemetry(Peer, decoded, PeerAppearance, new(true, true));
        var reference = root.GetProperty("overlay_cases").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "initial")
            .GetProperty("markers").EnumerateArray().ToArray();
        Assert.Empty(actual.Errors);
        Assert.Equal(5, actual.Markers.Count);
        foreach (var marker in actual.Markers)
            AssertMarker(reference.Single(item => item.GetProperty("key").GetString() == marker.Key), marker);
        Assert.Empty(SidebandMapOverlays.ProjectTelemetry(Peer, decoded, options: new(false, false)).Markers);
    }

    [Fact]
    public void NativeSafetyDifferencesHaveExplicitNonstaleZeroCoordinateBehavior()
    {
        var reading = SidebandTelemetryCodec.FromWireValue((byte)S.Interfaces, V.Fields(
            ("equator", V.Map(KeyValuePair.Create(V.From(0L), V.From("TCPServerInterface")),
                KeyValuePair.Create(V.From(3L), V.From(0d)), KeyValuePair.Create(V.From(4L), V.From(2d)))),
            ("meridian", V.Map(KeyValuePair.Create(V.From(0L), V.From("TCPServerInterface")),
                KeyValuePair.Create(V.From(3L), V.From(48d)), KeyValuePair.Create(V.From(4L), V.From(0d))))));
        var report = SidebandTelemetryCodec.Create(1700000000, reading);
        var projected = SidebandMapOverlays.ProjectTelemetry(Peer, report);
        Assert.Empty(projected.Errors);
        Assert.Equal(2, projected.Markers.Count);
        Assert.Empty(SidebandMapOverlays.ProjectTelemetry(Peer, report, options: new(false)).Markers);
        Assert.Empty(SidebandMapOverlays.ProjectTelemetry(Peer, SidebandTelemetryCodec.Create(1700000000)).Markers);
        Assert.Equal(2, projected.Markers.Count);
    }

    [Fact]
    public void MalformedEntryReportsErrorsAndDoesNotDisableFollowingValidEntriesOrRequireObserver()
    {
        var bad = V.Fields(("type", V.From("TCPServerInterface")), ("lat", V.From(100d)), ("lon", V.From(2d)));
        var reading = SidebandTelemetryCodec.FromData(S.Interfaces, V.Fields(("interfaces",
            V.Fields(("bad", bad), ("valid", Interface("valid", 48, 2))))));
        var result = SidebandMapOverlays.ProjectTelemetry(Peer, SidebandTelemetryCodec.Create(1700000000, reading));
        Assert.Single(result.Errors);
        Assert.Equal("interface", result.Errors[0].Scope);
        Assert.Equal("valid", Assert.Single(result.Markers).Name);
        var malformed = SidebandTelemetryCodec.FromWireValue((byte)S.Interfaces, V.From("invalid"));
        Assert.NotEmpty(SidebandMapOverlays.ProjectTelemetry(Peer, SidebandTelemetryCodec.Create(1700000000, malformed)).Errors);
    }

    [Fact]
    public void ProjectionAndGeometryHaveStrictResourceBounds()
    {
        var points = Enumerable.Range(0, 257).Select(index =>
        {
            var key = new byte[16];
            BitConverter.TryWriteBytes(key, index);
            return KeyValuePair.Create(V.From(key), V.Fields(("latitude", V.From(48d)),
                ("longitude", V.From(2d)), ("altitude", V.From(0L))));
        }).ToArray();
        var report = SidebandTelemetryCodec.Create(1700000000, SidebandTelemetryCodec.FromData(S.ConnectionMap,
            V.Fields(("maps", V.Fields(("test", V.Fields(("points", V.Map(points)))))))));
        var result = SidebandMapOverlays.ProjectTelemetry(Peer, report, options: new(false, true));
        Assert.Equal(SidebandMapOverlays.MaximumMarkers, result.Markers.Count);
        Assert.Contains(result.Errors, error => error.Scope == "report");
        var tooMany = Enumerable.Range(0, 257).Select(index => Marker(index, 48, 2)).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandMapOverlays.Cluster(tooMany, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandMapOverlays.Scatter(tooMany, new SidebandMapPoint[257], 19));
    }

    [Fact]
    public void InvalidCoordinatesKeysColorsZoomBoundsAndViewportRejectExplicitly()
    {
        var marker = Marker(0, 48, 2);
        Assert.Throws<ArgumentException>(() => SidebandMapOverlays.Cluster([marker, marker], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandMapOverlays.Cluster([marker with { Coordinate = new(double.NaN, 0) }], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandMapOverlays.Cluster([marker with
        { Appearance = new("test", new(2, 0, 0), new(1, 1, 1)) }], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandMapOverlays.Cluster([], 25));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandMapOverlays.Cluster([], 0, new(5, 5, 1, 1)));
        Assert.Throws<ArgumentException>(() => SidebandMapOverlays.Scatter([marker], [], 19));
        Assert.Throws<ArgumentException>(() => SidebandMapOverlays.Scatter([marker], [new(double.PositiveInfinity, 0)], 19));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandMapOverlays.FitBounds(new(1, 1, 2, 2), 0, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandMapOverlays.FitBounds(new(1, 1, 2, 2), 800, 600, padding: 2));
        Assert.Throws<ArgumentException>(() => SidebandMapOverlays.ProjectTelemetry("bad", SidebandTelemetryCodec.Create(0)));
        Assert.Throws<ArgumentException>(() => SidebandMapOverlays.InterfaceAppearance(new string('x', 81)));
        Assert.Throws<ArgumentException>(() => SidebandMapOverlays.Cluster(
            [marker with { Appearance = new(null!, new(0, 0, 0), new(1, 1, 1)) }], 0));
        Assert.Throws<ArgumentException>(() => SidebandMapOverlays.Cluster([marker with { Key = "\ud800" }], 0));
    }

    [Fact]
    public void RejectedEntriesConsumeBudgetAndDiagnosticsCannotGrowWithoutBound()
    {
        var invalid = V.Map(Enumerable.Range(0, SidebandMapOverlays.MaximumInputEntries)
            .Select(index => KeyValuePair.Create(V.From((long)index), V.Nil)).ToArray());
        var valid = V.Map(KeyValuePair.Create(V.From(new byte[16]),
            V.Fields(("latitude", V.From(48d)), ("longitude", V.From(2d)), ("altitude", V.From(0L)))));
        var sensor = SidebandTelemetryCodec.FromWireValue((byte)S.ConnectionMap, V.Fields(("maps", V.Fields(
            ("invalid", V.Fields(("points", invalid))), ("valid", V.Fields(("points", valid)))))));
        var result = SidebandMapOverlays.ProjectTelemetry(Peer, SidebandTelemetryCodec.Create(1700000000, sensor),
            options: new(false, true));
        Assert.Empty(result.Markers);
        Assert.Equal(SidebandMapOverlays.MaximumMarkers, result.Errors.Count);
    }

    [Fact]
    public void OutputsOwnTheirCollectionsAndConversionRetainsAppearance()
    {
        var input = new List<SidebandMapOverlayMarker> { Marker(0, 48, 2), Marker(1, 48, 2) };
        var groups = SidebandMapOverlays.Cluster(input, 0);
        input.Clear();
        Assert.Equal(2, Assert.Single(groups).Count);
        Assert.Throws<NotSupportedException>(() => ((IList<SidebandMapOverlayMarker>)groups[0].Members).Clear());
        var color = SidebandMapOverlayAppearance.FromTelemetry(new("account", "336699", "ffffff"));
        Near(0.2, color.Foreground.Red);
        Near(0.4, color.Foreground.Green);
        Near(0.6, color.Foreground.Blue);
        Assert.Equal("account", color.Icon);
        Assert.Empty(SidebandMapOverlays.Cluster([], 0));
        Assert.Empty(SidebandMapOverlays.Scatter([], [], 19));
    }

    private static V Interface(string name, double latitude, double longitude) =>
        V.Fields(("name", V.From(name)), ("type", V.From("TCPServerInterface")),
            ("lat", V.From(latitude)), ("lon", V.From(longitude)));
    private static SidebandMapOverlayMarker Marker(int index, double latitude, double longitude) =>
        new("marker-" + index, Peer, new(latitude, longitude), PeerAppearance);
    private static SidebandMapOverlayMarker[] Markers(JsonElement coordinates) =>
        coordinates.EnumerateArray().Select((point, index) => Marker(index, point[1].GetDouble(), point[0].GetDouble())).ToArray();
    private static void AssertMarker(JsonElement expected, SidebandMapOverlayMarker marker)
    {
        Assert.Equal(expected.GetProperty("key").GetString(), marker.Key);
        Assert.Equal(expected.GetProperty("source").GetString(), marker.SourceHash);
        Near(expected.GetProperty("latitude").GetDouble(), marker.Coordinate.Latitude);
        Near(expected.GetProperty("longitude").GetDouble(), marker.Coordinate.Longitude);
        Assert.Equal(expected.GetProperty("small").GetBoolean(), marker.Small);
        Assert.Equal(expected.GetProperty("inspectable").GetBoolean(), marker.Inspectable);
        AssertAppearance(expected.GetProperty("appearance"), marker.Appearance);
    }
    private static void AssertAppearance(JsonElement expected, SidebandMapOverlayAppearance appearance)
    {
        Assert.Equal(expected[0].GetString(), appearance.Icon);
        AssertColor(expected[1], appearance.Foreground);
        AssertColor(expected[2], appearance.Background);
    }
    private static void AssertColor(JsonElement expected, SidebandMapColor color)
    {
        Near(expected[0].GetDouble(), color.Red);
        Near(expected[1].GetDouble(), color.Green);
        Near(expected[2].GetDouble(), color.Blue);
        Near(expected[3].GetDouble(), color.Alpha);
    }
    private static void Near(double expected, double actual) =>
        Assert.InRange(Math.Abs(expected - actual), 0, 1e-8);
}
