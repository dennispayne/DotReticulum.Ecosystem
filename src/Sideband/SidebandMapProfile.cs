using System.Text.Json;

namespace Sideband;

internal static class SidebandMapProfile
{
    private static readonly IReadOnlyDictionary<string, string> LayerFlags = new Dictionary<string, string>
    {
        ["osm"] = "map_use_osm", ["mt_topo"] = "map_use_topo", ["mt_outdoor"] = "map_use_outdoor",
        ["mt_hybrid"] = "map_use_hybrid", ["virtualearth"] = "map_use_virtualearth",
        ["opentopo"] = "map_use_opentopo", ["atlas"] = "map_use_atlas", ["googlemaps"] = "map_use_googlemaps",
        ["st_terrain"] = "map_use_st_terrain", ["st_dark"] = "map_use_st_dark",
        ["st_bright"] = "map_use_st_bright", ["st_sat"] = "map_use_st_sat", ["custom_1"] = "map_use_custom_1",
    };

    internal static bool IsSupported(string key) =>
        LayerFlags.Values.Contains(key) || key is "map_layer" or "map_lat" or "map_lon" or "map_zoom" or
            "map_mt_api_key" or "map_st_api_key" or "map_custom_1_url" or
            "map_cluster" or "map_interfaces" or "map_connection_maps" or "map_hide_blocked" or "map_history_limit";

    internal static async Task<IReadOnlyList<string>> ImportAsync(
        IReadOnlyDictionary<string, JsonElement>? config, string staging, CancellationToken cancellationToken)
    {
        if (config is null || !config.Keys.Any(IsSupported)) return [];
        var notices = new List<string>();
        var selected = Text("map_layer") ?? "osm";
        if (!LayerFlags.TryGetValue(selected, out var flag) ||
            !Flag(flag, selected is "osm" or "virtualearth" or "opentopo"))
        {
            selected = "osm";
            notices.Add("Map layer is unavailable or disabled; the primary OpenStreetMap fallback is activated.");
        }
        var key = selected.StartsWith("mt_", StringComparison.Ordinal) ? Text("map_mt_api_key") :
            selected.StartsWith("st_", StringComparison.Ordinal) ? Text("map_st_api_key") : null;
        SidebandMapPreset preset;
        if ((selected.StartsWith("mt_", StringComparison.Ordinal) || selected.StartsWith("st_", StringComparison.Ordinal)) &&
            string.IsNullOrEmpty(key))
        {
            preset = SidebandMapPresets.Create("osm");
            notices.Add("Selected map needs your own API key. Bundled primary credentials are not copied; OpenStreetMap is activated without online consent.");
        }
        else preset = SidebandMapPresets.Create(selected, key, Text("map_custom_1_url"));
        var zoom = Number("map_zoom", 3);
        if (zoom != Math.Truncate(zoom)) throw new InvalidDataException("Imported map zoom must be a whole number.");
        var view = new SidebandMapViewSettings
        {
            Latitude = Math.Clamp(Number("map_lat", 0), -89, 89),
            Longitude = Math.Clamp(Number("map_lon", 0), -179, 179),
            Zoom = (int)Math.Clamp(zoom, preset.MinimumZoom, preset.MaximumZoom),
            MinimumZoom = preset.MinimumZoom, MaximumZoom = preset.MaximumZoom,
            Provider = preset.Provider,
            ClusterMarkers = Flag("map_cluster", true),
            IncludeInterfaces = Flag("map_interfaces", true),
            IncludeConnections = Flag("map_connection_maps", false),
            HideBlocked = Flag("map_hide_blocked", false),
            HistoryLimitSeconds = History(),
        };
        await using var maps = await SidebandMapStore.OpenAsync(Path.Combine(staging, "maps"), cancellationToken: cancellationToken).ConfigureAwait(false);
        await maps.SaveViewAsync(view, cancellationToken).ConfigureAwait(false);
        notices.Add("Map coordinates, selected available provider and zoom limits are activated. Online consent is not imported. External/offline map paths are never followed; explicitly import the raster MBTiles snapshot in Map.");
        return notices;

        int History()
        {
            var history = Number("map_history_limit", SidebandMapOverlays.HistoryLimitSeconds);
            if (history != Math.Truncate(history) || history is < 1 or > SidebandTelemetryRetention.PrimaryKeepSeconds)
                throw new InvalidDataException("Imported map history must contain whole seconds between 1 and the seven-day retention limit.");
            return (int)history;
        }

        string? Text(string name)
        {
            if (!config.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"Map setting '{name}' must be text or null.");
            return value.GetString();
        }
        bool Flag(string name, bool fallback)
        {
            if (!config.TryGetValue(name, out var value)) return fallback;
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"Map setting '{name}' must be boolean.");
            return value.GetBoolean();
        }
        double Number(string name, double fallback)
        {
            if (!config.TryGetValue(name, out var value)) return fallback;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                throw new InvalidDataException($"Map setting '{name}' must be finite numeric data.");
            return number;
        }
    }
}
