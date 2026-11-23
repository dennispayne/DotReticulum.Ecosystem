using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using V = Sideband.SidebandTelemetryValue;
using K = Sideband.SidebandTelemetryValueKind;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband;

public enum SidebandMapOverlayKind { Peer, Interface, Connection, Discovery }
public readonly record struct SidebandMapColor(double Red, double Green, double Blue, double Alpha = 1);
public sealed record SidebandMapOverlayAppearance(string Icon, SidebandMapColor Foreground, SidebandMapColor Background)
{
    public static SidebandMapOverlayAppearance FromTelemetry(SidebandTelemetryAppearance appearance)
    {
        SidebandTelemetryConfiguration.ValidateAppearance(appearance);
        static SidebandMapColor Color(string hex)
        {
            var bytes = Convert.FromHexString(hex);
            return new(bytes[0] / 255d, bytes[1] / 255d, bytes[2] / 255d);
        }
        return new(appearance.Icon, Color(appearance.Foreground), Color(appearance.Background));
    }
}

public sealed record SidebandMapOverlayMarker(string Key, string SourceHash, SidebandGeoCoordinate Coordinate,
    SidebandMapOverlayAppearance Appearance, SidebandMapOverlayKind Kind = SidebandMapOverlayKind.Peer,
    string? ReportingSourceHash = null, string? Name = null, bool Small = false, bool Inspectable = true);
public sealed record SidebandMapOverlayOptions(bool IncludeInterfaces = true, bool IncludeConnections = false,
    bool HideBlocked = false, bool OnlyBlocked = false);
public sealed record SidebandMapOverlayError(string Scope, string Message);
public sealed record SidebandMapOverlayProjection(IReadOnlyList<SidebandMapOverlayMarker> Markers,
    IReadOnlyList<SidebandMapOverlayError> Errors);
public sealed record SidebandMapOverlayCluster(SidebandGeoCoordinate Coordinate, SidebandMapBounds Bounds,
    IReadOnlyList<SidebandMapOverlayMarker> Members)
{
    public int Count => Members.Count;
    public bool IsCluster => Count > 1;
}
public readonly record struct SidebandMapOverlayView(double Latitude, double Longitude, double Zoom);

/// <summary>Owned, hardware-free projections and bounded original geometry. Reported coordinates are not authenticated positions.</summary>
public static class SidebandMapOverlays
{
    public const int MaximumMarkers = 256;
    public const int MaximumInputEntries = 1024;
    public const int ClusterMaximumZoom = 18;
    public const double ClusterRadius = 100;
    public const double ClusterExtent = 512;
    public const int InterfaceUpdateIntervalSeconds = 60;
    public const int HistoryLimitSeconds = 604_800;

    public static SidebandMapOverlayAppearance InterfaceAppearance(string interfaceType)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);
        if (interfaceType.Length > 80 || interfaceType.Any(char.IsControl))
            throw new ArgumentException("Interface type is oversized or contains control characters.", nameof(interfaceType));
        var icon = interfaceType switch
        {
            "RNodeInterface" => "access-point-network",
            "WeaveInterface" => "access-point",
            "KISSInterface" => "radio-tower",
            "BackboneInterface" => "wan",
            "TCPServerInterface" => "lan-connect",
            "I2PInterface" => "lock-pattern",
            "connection" => "transit-connection-variant",
            "blocked" => "close-octagon",
            _ => "lan-connect"
        };
        var signal = interfaceType.StartsWith("signal-", StringComparison.Ordinal);
        if (signal)
        {
            icon = int.TryParse(interfaceType.AsSpan(7), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value switch
                {
                    > 80 => "wifi-strength-4", > 60 => "wifi-strength-3", > 40 => "wifi-strength-2",
                    > 20 => "wifi-strength-1", _ => "wifi-strength-outline"
                }
                : "wifi-strength-off-outline";
        }
        var factor = signal || interfaceType is "connection" or "blocked" ? 0.95 : 0.8;
        return new(icon, new(1, 253d / 255, 231d / 255),
            new(75d / 255 * factor, 97d / 255 * factor, 108d / 255 * factor));
    }

    /// <summary>
    /// Projects one already-admitted report. The caller owns trust, latest-report selection and history limits.
    /// Unlike primary, disabled interfaces are absent, zero coordinates are valid and no stale mutable marker cache is retained.
    /// </summary>
    public static SidebandMapOverlayProjection ProjectTelemetry(string sourceHash, SidebandTelemetryReport report,
        SidebandMapOverlayAppearance? sourceAppearance = null, SidebandMapOverlayOptions? options = null)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(sourceHash, out var source))
            throw new ArgumentException("Reporting source must be an LXMF delivery hash.", nameof(sourceHash));
        ArgumentNullException.ThrowIfNull(report);
        if (sourceAppearance is not null) ValidateAppearance(sourceAppearance);
        options ??= new();
        var markers = new List<SidebandMapOverlayMarker>();
        var errors = new List<SidebandMapOverlayError>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var visited = 0;
        var exhausted = false;
        bool AdmitEntry()
        {
            if (exhausted) return false;
            if (++visited <= MaximumInputEntries && markers.Count < MaximumMarkers) return true;
            exhausted = true;
            if (errors.Count < MaximumMarkers)
                errors.Add(new("report", "Overlay input or marker limit exceeded; remaining entries were not projected."));
            return false;
        }
        void Error(string scope, string message)
        {
            if (errors.Count < MaximumMarkers) errors.Add(new(scope, message));
        }
        void Add(SidebandMapOverlayMarker marker)
        {
            if (!keys.Add(marker.Key)) Error("marker", "Duplicate marker identity was ignored.");
            else markers.Add(marker);
        }
        SidebandMapOverlayAppearance Appearance(string type)
        {
            var appearance = InterfaceAppearance(type);
            return sourceAppearance is null ? appearance :
                appearance with { Foreground = sourceAppearance.Foreground, Background = sourceAppearance.Background };
        }

        var interfaces = report.Get(S.Interfaces);
        if (options.IncludeInterfaces && interfaces is { Status: not SidebandTelemetryStatus.Unavailable })
        {
            var entries = interfaces.Data.Get("interfaces");
            if (interfaces.Status == SidebandTelemetryStatus.Malformed || entries?.Kind != K.Map)
                Error("interfaces", "Connectable-interface data is malformed.");
            else
                foreach (var entry in entries.Members)
                {
                    if (!AdmitEntry()) break;
                    var name = entry.Key.Text;
                    var data = entry.Value;
                    if (name is null || !Printable(name, 256) || data.Kind != K.Map ||
                        data.Get("type")?.Text is not { } type || !Printable(type, 80))
                    {
                        Error("interface", "Interface name, type or data is invalid.");
                        continue;
                    }
                    var latitude = data.Get("lat");
                    var longitude = data.Get("lon");
                    if (latitude is null or { IsNil: true } || longitude is null or { IsNil: true }) continue;
                    if (!Coordinate(latitude, longitude, data.Get("height"), out var coordinate))
                    {
                        Error("interface", "Interface coordinates are invalid.");
                        continue;
                    }
                    var nameBytes = Encoding.UTF8.GetBytes(name);
                    var identity = new byte[16 + nameBytes.Length];
                    Convert.FromHexString(source).CopyTo(identity, 0);
                    nameBytes.CopyTo(identity, 16);
                    var hash = Convert.ToHexStringLower(SHA256.HashData(identity));
                    Add(new(hash, hash, coordinate with { Altitude = 0 }, Appearance(type), SidebandMapOverlayKind.Interface,
                        source, name));
                }
        }
        var connections = report.Get(S.ConnectionMap);
        if (options.IncludeConnections && connections is { Status: not SidebandTelemetryStatus.Unavailable })
        {
            // Connection maps are self-describing. Preserve valid points even when an unknown quality value failed the general sensor projection.
            var maps = connections.WireValue.Get("maps");
            if (maps?.Kind != K.Map)
                Error("connections", "Connection-map data is malformed.");
            else
                foreach (var map in maps.Members)
                {
                    if (exhausted) break;
                    var points = map.Value.Get("points");
                    if (points?.Kind != K.Map)
                    {
                        Error("connections", "Connection map has no valid point collection.");
                        continue;
                    }
                    foreach (var point in points.Members)
                    {
                        if (!AdmitEntry()) break;
                        var hashBytes = point.Key.GetBinary();
                        var data = point.Value;
                        if (hashBytes is not { Length: 16 } || data.Kind != K.Map)
                        {
                            Error("connection", "Connection point identity or data is invalid.");
                            continue;
                        }
                        var blocked = data.Get("type_label")?.Text == "blocked";
                        if (blocked && options.HideBlocked || options.OnlyBlocked && !blocked) continue;
                        if (!Coordinate(data.Get("latitude"), data.Get("longitude"), data.Get("altitude"), out var coordinate,
                            requireAltitude: true))
                        {
                            Error("connection", "Connection point coordinates are invalid.");
                            continue;
                        }
                        var type = blocked ? "blocked" : "connection";
                        var quality = data.Get("signal")?.Get("q");
                        if (!blocked && quality is { IsNil: false })
                        {
                            var value = quality.Number;
                            if (value is not null && value >= int.MinValue && value <= int.MaxValue)
                                type = "signal-" + ((int)value).ToString(CultureInfo.InvariantCulture);
                            else if (quality.Text is { Length: <= 80 } text &&
                                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                                type = "signal-" + parsed.ToString(CultureInfo.InvariantCulture);
                            else type = "signal-invalid";
                        }
                        var name = data.Get("name")?.Text;
                        if (name is not null && !Printable(name, 256))
                        {
                            Error("connection", "Connection point name is invalid.");
                            continue;
                        }
                        Add(new(Convert.ToHexStringLower(hashBytes), source, coordinate, Appearance(type),
                            SidebandMapOverlayKind.Connection, source, name, Small: true));
                    }
                }
        }
        return new(Array.AsReadOnly(markers.ToArray()), Array.AsReadOnly(errors.ToArray()));
    }

    /// <summary>Stable input-order hierarchical grouping; bounded exhaustive neighborhoods avoid a mutable spatial index.</summary>
    public static IReadOnlyList<SidebandMapOverlayCluster> Cluster(IReadOnlyList<SidebandMapOverlayMarker> markers,
        int zoom, SidebandMapBounds? viewport = null)
    {
        markers = SnapshotMarkers(markers);
        SidebandMapProjection.ValidateZoom(zoom);
        viewport ??= new(-180, -90, 180, 90);
        ValidateBounds(viewport);
        var groups = markers.Select(marker => new Group(ToUnit(marker.Coordinate), [marker],
            new(marker.Coordinate.Longitude, marker.Coordinate.Latitude, marker.Coordinate.Longitude, marker.Coordinate.Latitude))).ToList();
        if (zoom <= ClusterMaximumZoom)
            for (var level = ClusterMaximumZoom; level >= zoom; level--)
            {
                var radius = Math.ScaleB(ClusterRadius / ClusterExtent, -level);
                var used = new bool[groups.Count];
                var next = new List<Group>();
                for (var seed = 0; seed < groups.Count; seed++)
                {
                    if (used[seed]) continue;
                    var neighbors = new List<Group> { groups[seed] };
                    used[seed] = true;
                    for (var candidate = seed + 1; candidate < groups.Count; candidate++)
                        if (!used[candidate] && DistanceSquared(groups[seed].Point, groups[candidate].Point) <= radius * radius)
                        {
                            used[candidate] = true;
                            neighbors.Add(groups[candidate]);
                        }
                    if (neighbors.Count == 1) next.Add(neighbors[0]);
                    else
                    {
                        var members = neighbors.SelectMany(group => group.Members).ToArray();
                        var point = new SidebandMapPoint(
                            neighbors.Sum(group => group.Point.X * group.Members.Length) / members.Length,
                            neighbors.Sum(group => group.Point.Y * group.Members.Length) / members.Length);
                        var bounds = new SidebandMapBounds(neighbors.Min(group => group.Bounds.West), neighbors.Min(group => group.Bounds.South),
                            neighbors.Max(group => group.Bounds.East), neighbors.Max(group => group.Bounds.North));
                        next.Add(new(point, members, bounds));
                    }
                }
                groups = next;
            }
        return Array.AsReadOnly(groups.Select(group => new SidebandMapOverlayCluster(
                group.Members.Length == 1 ? group.Members[0].Coordinate : FromUnit(group.Point), group.Bounds,
                Array.AsReadOnly(group.Members)))
            .Where(group => group.Coordinate.Longitude >= viewport.West && group.Coordinate.Longitude <= viewport.East &&
                group.Coordinate.Latitude >= viewport.South && group.Coordinate.Latitude <= viewport.North).ToArray());
    }

    /// <summary>Returns screen-pixel offsets in stable input order; the caller supplies unshifted viewport positions.</summary>
    public static IReadOnlyList<SidebandMapPoint> Scatter(IReadOnlyList<SidebandMapOverlayMarker> markers,
        IReadOnlyList<SidebandMapPoint> windowPositions, int zoom)
    {
        markers = SnapshotMarkers(markers);
        ArgumentNullException.ThrowIfNull(windowPositions);
        SidebandMapProjection.ValidateZoom(zoom);
        if (windowPositions.Count != markers.Count)
            throw new ArgumentException("Supply one screen position per marker.", nameof(windowPositions));
        windowPositions = Enumerable.Range(0, markers.Count).Select(index => windowPositions[index]).ToArray();
        if (windowPositions.Any(point =>
            !double.IsFinite(point.X) || !double.IsFinite(point.Y) || Math.Abs(point.X) > 1e12 || Math.Abs(point.Y) > 1e12))
            throw new ArgumentException("Supply one bounded finite screen position per marker.", nameof(windowPositions));
        var offsets = new SidebandMapPoint[markers.Count];
        if (zoom < 19) return Array.AsReadOnly(offsets);
        var locations = markers.Select((marker, index) => (marker, index)).GroupBy(item =>
            (Math.Round(item.marker.Coordinate.Latitude, 7), Math.Round(item.marker.Coordinate.Longitude, 7))).ToArray();
        foreach (var location in locations.Where(group => group.Count() > 1))
        {
            var ring = 1;
            var start = 1;
            var ordered = location.ToArray();
            for (var slot = 1; slot < ordered.Length; slot++)
            {
                while (slot >= start + 6 * ring) { start += 6 * ring; ring++; }
                var angle = Math.Tau * (slot - start) / (6 * ring);
                offsets[ordered[slot].index] = new(60 * ring * Math.Cos(angle), 60 * ring * Math.Sin(angle));
            }
        }
        var singles = locations.Where(group => group.Count() == 1).Select(group => group.Single().index).ToArray();
        var processed = new HashSet<int>();
        foreach (var seed in singles)
        {
            if (processed.Contains(seed)) continue;
            var neighbors = singles.Where(candidate => candidate >= seed && !processed.Contains(candidate) &&
                DistanceSquared(windowPositions[seed], windowPositions[candidate]) < 144).ToArray();
            if (neighbors.Length < 2) continue;
            var center = new SidebandMapPoint(neighbors.Average(index => windowPositions[index].X),
                neighbors.Average(index => windowPositions[index].Y));
            foreach (var index in neighbors)
            {
                var distance = Math.Sqrt(DistanceSquared(windowPositions[index], center));
                if (distance > 0) offsets[index] = new(6 * (windowPositions[index].X - center.X) / distance,
                    6 * (windowPositions[index].Y - center.Y) / distance);
                processed.Add(index);
            }
        }
        return Array.AsReadOnly(offsets);
    }

    public static SidebandMapOverlayView FitBounds(SidebandMapBounds bounds, double width, double height,
        int minimumZoom = 0, int maximumZoom = 19, double padding = 0.85, bool fractionalZoom = false)
    {
        ValidateBounds(bounds);
        SidebandMapProjection.ValidateZoom(minimumZoom);
        SidebandMapProjection.ValidateZoom(maximumZoom);
        if (minimumZoom > maximumZoom || !double.IsFinite(width) || !double.IsFinite(height) ||
            width is <= 0 or > 16_384 || height is <= 0 or > 16_384 ||
            !double.IsFinite(padding) || padding is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(width), "Bounds fitting requires a positive bounded viewport and ordered zoom limits.");
        var first = SidebandMapProjection.ToWorld(0, SidebandMapProjection.ClipDisplayLatitude(bounds.South), bounds.West);
        var last = SidebandMapProjection.ToWorld(0, SidebandMapProjection.ClipDisplayLatitude(bounds.North), bounds.East);
        var horizontal = last.X == first.X ? maximumZoom : Math.Log2(width * padding / Math.Abs(last.X - first.X));
        var vertical = last.Y == first.Y ? maximumZoom : Math.Log2(height * padding / Math.Abs(last.Y - first.Y));
        var zoom = Math.Clamp(Math.Min(horizontal, vertical), minimumZoom, maximumZoom);
        return new((bounds.North + bounds.South) / 2, (bounds.East + bounds.West) / 2,
            fractionalZoom ? zoom : Math.Floor(zoom));
    }

    private sealed record Group(SidebandMapPoint Point, SidebandMapOverlayMarker[] Members, SidebandMapBounds Bounds);
    private static double DistanceSquared(SidebandMapPoint first, SidebandMapPoint second) =>
        (first.X - second.X) * (first.X - second.X) + (first.Y - second.Y) * (first.Y - second.Y);
    private static SidebandMapPoint ToUnit(SidebandGeoCoordinate coordinate) =>
        new(coordinate.Longitude / 360 + 0.5,
            coordinate.Latitude == 90 ? 0 : coordinate.Latitude == -90 ? 1 :
                Math.Clamp(0.5 - Math.Asinh(Math.Tan(coordinate.Latitude * Math.PI / 180)) / Math.Tau, 0, 1));
    private static SidebandGeoCoordinate FromUnit(SidebandMapPoint point) =>
        new(Math.Atan(Math.Sinh(Math.PI * (1 - 2 * point.Y))) * 180 / Math.PI, (point.X - 0.5) * 360);
    private static bool Printable(string? value, int maximum)
    {
        if (value is not { Length: > 0 } || value.Length > maximum) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index]) || char.IsLowSurrogate(value[index])) return false;
            if (char.IsHighSurrogate(value[index]) &&
                (++index == value.Length || !char.IsLowSurrogate(value[index]))) return false;
        }
        return true;
    }
    private static bool Coordinate(V? latitude, V? longitude, V? altitude, out SidebandGeoCoordinate coordinate,
        bool requireAltitude = false)
    {
        coordinate = default;
        var lat = latitude?.Number;
        var lon = longitude?.Number;
        var alt = altitude?.Number;
        if (lat is null || lon is null || lat < -90 || lat > 90 || lon < -180 || lon > 180 ||
            requireAltitude && alt is null || altitude is { IsNil: false } && alt is null)
            return false;
        coordinate = new(lat.Value, lon.Value, alt ?? 0);
        return double.IsFinite(coordinate.Latitude) && double.IsFinite(coordinate.Longitude) && double.IsFinite(coordinate.Altitude);
    }
    private static void ValidateAppearance(SidebandMapOverlayAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        if (!Printable(appearance.Icon, 80)) throw new ArgumentException("Invalid bounded overlay icon.");
        foreach (var color in new[] { appearance.Foreground, appearance.Background })
            if (!double.IsFinite(color.Red) || !double.IsFinite(color.Green) || !double.IsFinite(color.Blue) ||
                !double.IsFinite(color.Alpha) || color.Red is < 0 or > 1 || color.Green is < 0 or > 1 ||
                color.Blue is < 0 or > 1 || color.Alpha is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(appearance), "Overlay colors must be finite unit-range RGBA values.");
    }
    private static SidebandMapOverlayMarker[] SnapshotMarkers(IReadOnlyList<SidebandMapOverlayMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        if (markers.Count > MaximumMarkers) throw new ArgumentOutOfRangeException(nameof(markers));
        var snapshot = Enumerable.Range(0, markers.Count).Select(index => markers[index]).ToArray();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var marker in snapshot)
        {
            if (marker is null || marker.Key is null || !Printable(marker.Key, 128) || !identities.Add(marker.Key) ||
                marker.SourceHash is not { Length: 32 or 64 } || !marker.SourceHash.All(Uri.IsHexDigit) ||
                marker.Name is not null && !Printable(marker.Name, 256) || !Enum.IsDefined(marker.Kind) ||
                marker.ReportingSourceHash is not null &&
                !SidebandContact.TryNormalizeDeliveryHash(marker.ReportingSourceHash, out _))
                throw new ArgumentException("Markers require unique bounded keys, valid source hashes and printable names.", nameof(markers));
            if (!double.IsFinite(marker.Coordinate.Latitude) || !double.IsFinite(marker.Coordinate.Longitude) ||
                !double.IsFinite(marker.Coordinate.Altitude) || marker.Coordinate.Latitude is < -90 or > 90 ||
                marker.Coordinate.Longitude is < -180 or > 180)
                throw new ArgumentOutOfRangeException(nameof(markers));
            ValidateAppearance(marker.Appearance);
        }
        return snapshot;
    }
    private static void ValidateBounds(SidebandMapBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (!double.IsFinite(bounds.West) || !double.IsFinite(bounds.East) || !double.IsFinite(bounds.South) ||
            !double.IsFinite(bounds.North) || bounds.West < -180 || bounds.East > 180 ||
            bounds.South < -90 || bounds.North > 90 || bounds.West > bounds.East || bounds.South > bounds.North)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Overlay bounds must be ordered finite geographic coordinates.");
    }
}
