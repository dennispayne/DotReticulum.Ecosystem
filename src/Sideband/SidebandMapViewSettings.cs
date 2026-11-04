using System.Text.Json.Serialization;

namespace Sideband;

public sealed record SidebandMapViewSettings
{
    public int SchemaVersion { get; init; } = 1;
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public int Zoom { get; init; } = 3;
    public int MinimumZoom { get; init; }
    public int MaximumZoom { get; init; } = SidebandMapProjection.MaximumZoom;
    public bool UseOffline { get; init; }
    public bool IsCartesian { get; init; }
    public SidebandMapProvider Provider { get; init; } = SidebandMapProvider.OpenStreetMap;

    internal void Validate()
    {
        if (SchemaVersion != 1 || !double.IsFinite(Latitude) || !double.IsFinite(Longitude) ||
            !IsCartesian && (Latitude is < -90 or > 90 || Longitude is < -180 or > 180))
            throw new InvalidDataException("Map view settings have an unsupported version or invalid coordinates.");
        _ = SidebandMapProjection.WorldSize(Zoom);
        _ = SidebandMapProjection.WorldSize(MinimumZoom);
        _ = SidebandMapProjection.WorldSize(MaximumZoom);
        if (MinimumZoom > MaximumZoom || Zoom < MinimumZoom || Zoom > MaximumZoom)
            throw new InvalidDataException("Map view zoom is outside the configured source limits.");
        ArgumentNullException.ThrowIfNull(Provider);
        _ = Provider.TileUri(new(0, 0, 0));
    }
}

[JsonSerializable(typeof(SidebandMapViewSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal partial class SidebandMapJsonContext : JsonSerializerContext;
