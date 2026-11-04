namespace Sideband;

public sealed record SidebandMapPreset(string Id, string Name, SidebandMapProvider Provider,
    int MinimumZoom = 0, int MaximumZoom = 19)
{
    public override string ToString() => Name;
}

public static class SidebandMapPresets
{
    public static IReadOnlyList<string> Ids { get; } = Array.AsReadOnly<string>(
        ["osm", "mt_topo", "mt_outdoor", "mt_hybrid", "virtualearth", "opentopo", "atlas", "googlemaps",
            "st_terrain", "st_dark", "st_bright", "st_sat", "custom_1"]);

    public static SidebandMapPreset Create(string id, string? apiKey = null, string? customUrl = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        const string osm = "\u00a9 OpenStreetMap contributors";
        const string mapTiler = "\u00a9 MapTiler \u00a9 OpenStreetMap contributors";
        const string stadia = "\u00a9 Stadia \u00a9 OpenMapTiles \u00a9 OpenStreetMap contributors";
        var (name, url, attribution) = id switch
        {
            "osm" => ("OpenStreetMap", "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png", osm),
            "opentopo" => ("OpenTopo", "https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png", osm),
            "virtualearth" => ("VirtualEarth", "https://ecn.t3.tiles.virtualearth.net/tiles/a{q}.jpeg?g=1", "\u00a9 VirtualEarth"),
            "atlas" => ("Atlas", "https://server.arcgisonline.com/ArcGIS/rest/services/NatGeo_World_Map/MapServer/tile/{z}/{y}/{x}", "\u00a9 Esri"),
            "googlemaps" => ("Google Maps", "https://mt1.google.com/vt/lyrs=m&x={x}&y={y}&z={z}", "\u00a9 Google"),
            "mt_topo" => ("Topographic", $"https://api.maptiler.com/maps/topo-v4/256/{{z}}/{{x}}/{{y}}.png?key={Key(apiKey)}", mapTiler),
            "mt_outdoor" => ("Outdoor", $"https://api.maptiler.com/maps/outdoor-v4/256/{{z}}/{{x}}/{{y}}.png?key={Key(apiKey)}", mapTiler),
            "mt_hybrid" => ("Hybrid", $"https://api.maptiler.com/maps/hybrid-v4/256/{{z}}/{{x}}/{{y}}.jpg?key={Key(apiKey)}", mapTiler),
            "st_terrain" => ("Stadia Terrain", $"https://tiles.stadiamaps.com/tiles/stamen_terrain/{{z}}/{{x}}/{{y}}.png?api_key={Key(apiKey)}", stadia),
            "st_dark" => ("Stadia Dark", $"https://tiles.stadiamaps.com/tiles/alidade_smooth_dark/{{z}}/{{x}}/{{y}}.png?api_key={Key(apiKey)}", stadia),
            "st_bright" => ("Stadia Bright", $"https://tiles.stadiamaps.com/tiles/alidade_smooth/{{z}}/{{x}}/{{y}}.png?api_key={Key(apiKey)}", stadia),
            "st_sat" => ("Stadia Satellite", $"https://tiles.stadiamaps.com/tiles/alidade_satellite/{{z}}/{{x}}/{{y}}.jpg?api_key={Key(apiKey)}", stadia),
            "custom_1" => ("Custom 1", customUrl ?? throw new ArgumentException("Enter an explicit HTTPS custom tile template.", nameof(customUrl)), "Custom Map"),
            _ => throw new ArgumentException("Unknown map provider preset.", nameof(id)),
        };
        var provider = new SidebandMapProvider(url, attribution);
        _ = provider.TileUri(new(0, 0, 0));
        return new(id, name, provider);
    }

    private static string Key(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 512 ||
            key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.')))
            throw new ArgumentException("This provider requires your own 1-512 character API key using letters, digits, underscore, hyphen or period.", nameof(key));
        return key;
    }
}
