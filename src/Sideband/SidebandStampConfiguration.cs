using System.Text.Json;

namespace Sideband;

public sealed record SidebandStampConfiguration(bool RequireStamps = false, int? InboundStampCost = null,
    bool RejectInvalidStamps = true)
{
    public int? EffectiveCost => RequireStamps && InboundStampCost is > 0 ? InboundStampCost : null;

    public SidebandStampConfiguration Validate()
    {
        if (InboundStampCost >= 255)
            throw new InvalidDataException("The inbound stamp cost must be below 255; null or a value below one clears the cost.");
        return this;
    }

    public static SidebandStampConfiguration FromState(SidebandState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Import(state.ImportedSettings);
    }

    internal static bool IsSupported(string key) =>
        key is "lxmf_require_stamps" or "lxmf_inbound_stamp_cost" or "lxmf_ignore_invalid_stamps";

    internal static SidebandStampConfiguration Import(IReadOnlyDictionary<string, JsonElement>? settings,
        SidebandStampConfiguration? fallback = null)
    {
        var current = fallback ?? new();
        if (settings is null) return current.Validate();
        var cost = current.InboundStampCost;
        if (settings.TryGetValue("lxmf_inbound_stamp_cost", out var value))
        {
            if (value.ValueKind == JsonValueKind.Null) cost = null;
            else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) cost = number;
            else throw new InvalidDataException("lxmf_inbound_stamp_cost must be null or a whole 32-bit integer.");
        }
        return new SidebandStampConfiguration(
            Boolean("lxmf_require_stamps", current.RequireStamps), cost,
            Boolean("lxmf_ignore_invalid_stamps", current.RejectInvalidStamps)).Validate();

        bool Boolean(string key, bool previous) => !settings.TryGetValue(key, out var item) ? previous : item.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"{key} must be boolean.")
        };
    }

    internal void Export(IDictionary<string, JsonElement> settings)
    {
        Validate();
        settings["lxmf_require_stamps"] = JsonSerializer.SerializeToElement(RequireStamps, SidebandStateJsonContext.Default.Boolean);
        settings["lxmf_inbound_stamp_cost"] = JsonSerializer.SerializeToElement(InboundStampCost, SidebandStateJsonContext.Default.NullableInt32);
        settings["lxmf_ignore_invalid_stamps"] = JsonSerializer.SerializeToElement(RejectInvalidStamps, SidebandStateJsonContext.Default.Boolean);
    }
}
