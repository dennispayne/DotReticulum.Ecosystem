using System.Globalization;
using Sideband;

namespace Sideband.Desktop;

internal static class SidebandConsoleMapConfiguration
{
    internal static IReadOnlyList<string> Aliases { get; } =
        ["history_limit", "lat", "lon", "zoom", "use_offline", "layer", "cluster", "interfaces"];

    internal static async Task<string> ExecuteAsync(string directory, string? alias, string? value, CancellationToken token)
    {
        await using var maps = await SidebandMapStore.OpenAsync(Path.Combine(directory, "maps"), cancellationToken: token).ConfigureAwait(false);
        var view = maps.ViewSettings;
        if (alias is null) return string.Join('\n', Aliases.Select(a => $"map {a} = {Read(a, view)}"));
        if (!Aliases.Contains(alias)) throw new NotSupportedException("This map alias is not activated. Offline/external paths and online consent are never followed by config.");
        if (value is not null)
        {
            var next = alias switch
            {
                "lat" => view with { Latitude = Number(value) },
                "lon" => view with { Longitude = Number(value) },
                "zoom" => view with { Zoom = Integer(value) },
                "history_limit" => view with { HistoryLimitSeconds = Integer(value) },
                "use_offline" => view with { UseOffline = SidebandConsoleConfiguration.Boolean(value) },
                "cluster" => view with { ClusterMarkers = SidebandConsoleConfiguration.Boolean(value) },
                "interfaces" => view with { IncludeInterfaces = SidebandConsoleConfiguration.Boolean(value) },
                "layer" => Layer(view, value),
                _ => throw new NotSupportedException("Unsupported map alias.")
            };
            await maps.SaveViewAsync(next, token).ConfigureAwait(false);
            view = maps.ViewSettings;
        }
        return $"map {alias} = {Read(alias, view)}; persisted; no online consent granted";
    }

    private static SidebandMapViewSettings Layer(SidebandMapViewSettings view, string value)
    {
        var preset = SidebandMapPresets.Create(value);
        return view with { Provider = preset.Provider, MinimumZoom = preset.MinimumZoom, MaximumZoom = preset.MaximumZoom,
            Zoom = Math.Clamp(view.Zoom, preset.MinimumZoom, preset.MaximumZoom) };
    }

    private static string Read(string alias, SidebandMapViewSettings view) => alias switch
    {
        "lat" => view.Latitude.ToString("R", CultureInfo.InvariantCulture),
        "lon" => view.Longitude.ToString("R", CultureInfo.InvariantCulture),
        "zoom" => view.Zoom.ToString(CultureInfo.InvariantCulture),
        "history_limit" => view.HistoryLimitSeconds.ToString(CultureInfo.InvariantCulture),
        "use_offline" => view.UseOffline ? "true" : "false",
        "cluster" => view.ClusterMarkers ? "true" : "false",
        "interfaces" => view.IncludeInterfaces ? "true" : "false",
        "layer" => new[] { "osm", "opentopo", "virtualearth", "atlas", "googlemaps" }
            .FirstOrDefault(id => SidebandMapPresets.Create(id).Provider == view.Provider) ?? "custom or keyed provider (credentials hidden)",
        _ => throw new NotSupportedException("Unsupported map alias.")
    };

    private static int Integer(string value) => SidebandConsoleConfiguration.NullableInteger(value) ??
        throw new ArgumentException("This map value cannot be null.");
    private static double Number(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
        double.IsFinite(number) ? number : throw new ArgumentException("A finite number is required.");
}
