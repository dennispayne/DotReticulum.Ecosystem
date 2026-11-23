using System.Text.Json;

namespace Sideband;

public sealed record SidebandDeliveryConfiguration(bool LimitIncomingToOneMegabyte = true)
{
    public long MaximumTransferBytes => LimitIncomingToOneMegabyte ? 1_000_000 : 128_000_000;

    public static SidebandDeliveryConfiguration FromState(SidebandState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Import(state.ImportedSettings);
    }

    internal static SidebandDeliveryConfiguration Import(IReadOnlyDictionary<string, JsonElement>? settings,
        SidebandDeliveryConfiguration? fallback = null)
    {
        if (settings is null || !settings.TryGetValue("lxm_limit_1mb", out var value)) return fallback ?? new();
        return value.ValueKind switch
        {
            JsonValueKind.True => new(true),
            JsonValueKind.False => new(false),
            _ => throw new InvalidDataException("lxm_limit_1mb must be boolean.")
        };
    }

    internal void Export(IDictionary<string, JsonElement> settings) =>
        settings["lxm_limit_1mb"] = JsonSerializer.SerializeToElement(LimitIncomingToOneMegabyte,
            SidebandStateJsonContext.Default.Boolean);
}
