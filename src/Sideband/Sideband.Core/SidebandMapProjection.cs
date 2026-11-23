using System.Globalization;
using System.Text;

namespace Sideband;

public readonly record struct SidebandMapPoint(double X, double Y);
public readonly record struct SidebandMapTile(int Zoom, int X, int TmsY)
{
    public int XyzY { get { Validate(); return (1 << Zoom) - TmsY - 1; } }

    public void Validate()
    {
        SidebandMapProjection.ValidateZoom(Zoom);
        var count = 1 << Zoom;
        if (X < 0 || X >= count || TmsY < 0 || TmsY >= count)
            throw new ArgumentOutOfRangeException(nameof(SidebandMapTile), "Map tile coordinates are outside this zoom level.");
    }
}

/// <summary>Primary map coordinates use a bottom-origin Y axis; screen and HTTP XYZ rows must invert it.</summary>
public static class SidebandMapProjection
{
    public const int MaximumZoom = 24;
    public const double DisplayLatitudeLimit = 85.0511287798066;

    public static double WorldSize(int zoom, int tileSize = 256)
    {
        ValidateZoom(zoom);
        if (tileSize is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(tileSize));
        return Math.ScaleB(tileSize, zoom);
    }

    public static SidebandMapPoint ToWorld(int zoom, double latitude, double longitude, int tileSize = 256)
    {
        if (!double.IsFinite(latitude) || latitude is <= -90 or >= 90 || !double.IsFinite(longitude))
            throw new ArgumentOutOfRangeException(nameof(latitude), "Map projection requires finite coordinates strictly away from the poles.");
        var world = WorldSize(zoom, tileSize);
        var northing = Math.Asinh(Math.Tan(latitude * Math.PI / 180)) / (2 * Math.PI);
        return new((Math.Clamp(longitude, -180, 180) + 180) / 360 * world, (0.5 + northing) * world);
    }

    public static SidebandGeoCoordinate FromWorld(int zoom, SidebandMapPoint point, int tileSize = 256)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentOutOfRangeException(nameof(point));
        var world = WorldSize(zoom, tileSize);
        var longitude = Math.Clamp(point.X / world * 360 - 180, -180, 180);
        var latitude = Math.Atan(Math.Sinh((point.Y / world * 2 - 1) * Math.PI)) * 180 / Math.PI;
        return new(latitude, longitude);
    }

    public static double ClipDisplayLatitude(double latitude)
    {
        if (!double.IsFinite(latitude)) throw new ArgumentOutOfRangeException(nameof(latitude));
        return Math.Clamp(latitude, -DisplayLatitudeLimit, DisplayLatitudeLimit);
    }

    internal static void ValidateZoom(int zoom)
    {
        if (zoom is < 0 or > MaximumZoom) throw new ArgumentOutOfRangeException(nameof(zoom));
    }
}

public sealed record SidebandMapProvider(string UrlTemplate, string Attribution, string Subdomains = "abc",
    bool AllowInsecureHttp = false)
{
    public static SidebandMapProvider OpenStreetMap { get; } =
        new("https://tile.openstreetmap.org/{z}/{x}/{y}.png", "\u00a9 OpenStreetMap contributors");

    public Uri TileUri(SidebandMapTile tile)
    {
        tile.Validate();
        if (string.IsNullOrWhiteSpace(UrlTemplate) || UrlTemplate.Length > 4096 ||
            Attribution is null || Attribution.Length > 4096 || Subdomains is null || Subdomains.Length is < 1 or > 32 ||
            Subdomains.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("Map provider settings are invalid or oversized.");
        var quad = new StringBuilder(tile.Zoom);
        for (var shift = tile.Zoom - 1; shift >= 0; shift--)
            quad.Append((char)('0' + ((tile.X >> shift) & 1) + 2 * ((tile.XyzY >> shift) & 1)));
        var url = UrlTemplate.Replace("{z}", tile.Zoom.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{x}", tile.X.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{y}", tile.XyzY.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{q}", quad.ToString(), StringComparison.Ordinal)
            .Replace("{s}", Subdomains[..1], StringComparison.Ordinal);
        if (url.Contains('{') || url.Contains('}') || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Scheme != Uri.UriSchemeHttps && !(AllowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp))
            throw new ArgumentException("Map providers require an absolute HTTPS tile template without credentials or fragments; HTTP needs explicit opt-in.");
        return uri;
    }
}

public sealed record SidebandMapBounds(double West, double South, double East, double North);
public sealed record SidebandMbTilesMetadata(string Format, int MinimumZoom, int StoredMaximumZoom,
    double Latitude, double Longitude, int Zoom, SidebandMapBounds? Bounds, bool IsCartesian, string Attribution)
{
    public int MaximumZoom => Math.Max(StoredMaximumZoom, 19);

    public static SidebandMbTilesMetadata Parse(IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!metadata.TryGetValue("format", out var format)) throw new InvalidDataException("MBTiles metadata has no raster format.");
        if (format is not ("png" or "jpg" or "jpeg" or "webp"))
            throw new NotSupportedException("Only raster PNG, JPEG and WebP MBTiles are supported; vector tiles are not raster images.");
        try
        {
            var minimum = int.Parse(metadata["minzoom"], CultureInfo.InvariantCulture);
            var maximum = int.Parse(metadata["maxzoom"], CultureInfo.InvariantCulture);
            SidebandMapProjection.ValidateZoom(minimum);
            SidebandMapProjection.ValidateZoom(maximum);
            if (minimum > maximum) throw new InvalidDataException("MBTiles zoom limits are reversed.");
            var latitude = 0d;
            var longitude = 0d;
            var zoom = 5;
            SidebandMapBounds? bounds = null;
            if (metadata.TryGetValue("bounds", out var rawBounds))
            {
                var values = Numbers(rawBounds, 4);
                bounds = new(values[0], values[1], values[2], values[3]);
                longitude = (values[0] + values[2]) / 2;
                latitude = (values[1] + values[3]) / 2;
                zoom = minimum;
            }
            if (metadata.TryGetValue("center", out var rawCenter))
            {
                var values = Numbers(rawCenter, 3);
                longitude = values[0]; latitude = values[1];
                if (values[2] is < 0 or > SidebandMapProjection.MaximumZoom)
                    throw new InvalidDataException("MBTiles center zoom exceeds native map limits.");
                zoom = (int)values[2];
            }
            var cartesian = metadata.TryGetValue("projection", out var projection) && projection == "xy";
            if (!cartesian && (latitude is < -90 or > 90 || longitude is < -180 or > 180))
                throw new InvalidDataException("MBTiles center is outside geographic coordinates.");
            if (bounds is not null && (bounds.West > bounds.East || bounds.South > bounds.North ||
                !cartesian && (bounds.West < -180 || bounds.East > 180 || bounds.South < -90 || bounds.North > 90)))
                throw new InvalidDataException("MBTiles bounds are reversed or outside geographic coordinates.");
            var attribution = metadata.GetValueOrDefault("attribution", "Local MBTiles");
            if (attribution.Length > 4096 || attribution.Any(char.IsControl))
                throw new InvalidDataException("MBTiles attribution is invalid or oversized.");
            return new(format, minimum, maximum, latitude, longitude, zoom, bounds, cartesian, attribution);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or KeyNotFoundException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("MBTiles metadata is missing, malformed or outside native map limits.", exception);
        }
    }

    private static double[] Numbers(string text, int count)
    {
        if (text.Length > 4096) throw new InvalidDataException("MBTiles coordinates are oversized.");
        var values = text.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != count || values.Any(value => !double.IsFinite(value)))
            throw new InvalidDataException("MBTiles coordinates are malformed.");
        return values;
    }
}
