using System.Text.Json;

namespace Sideband;

public sealed record SidebandAnnounceConfiguration(bool AnnounceOnStartup = true)
{
    public static TimeSpan StartupDelay => TimeSpan.FromSeconds(8);

    public static SidebandAnnounceConfiguration FromState(SidebandState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Import(state.ImportedSettings);
    }

    internal static SidebandAnnounceConfiguration Import(IReadOnlyDictionary<string, JsonElement>? settings,
        SidebandAnnounceConfiguration? fallback = null)
    {
        if (settings is null || !settings.TryGetValue("start_announce", out var value)) return fallback ?? new();
        return value.ValueKind switch
        {
            JsonValueKind.True => new(true),
            JsonValueKind.False => new(false),
            _ => throw new InvalidDataException("start_announce must be boolean.")
        };
    }

    internal void Export(IDictionary<string, JsonElement> settings) =>
        settings["start_announce"] = JsonSerializer.SerializeToElement(AnnounceOnStartup, SidebandStateJsonContext.Default.Boolean);
}
