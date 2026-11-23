using System.Text.Json;

namespace Sideband;

public sealed record SidebandSynchronizationConfiguration(bool? LimitManualFetch = null, int MaximumMessages = 3)
{
    public int ManualMaximumMessages => LimitManualFetch == true ? MaximumMessages : 0;

    public void Validate()
    {
        if (MaximumMessages <= 0) throw new InvalidDataException("lxmf_sync_max must be a positive whole number.");
    }

    public static SidebandSynchronizationConfiguration FromState(SidebandState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Import(state.ImportedSettings);
    }

    internal static SidebandSynchronizationConfiguration Import(IReadOnlyDictionary<string, JsonElement>? settings,
        SidebandSynchronizationConfiguration? fallback = null)
    {
        var current = fallback ?? new();
        var limit = current.LimitManualFetch;
        var maximum = current.MaximumMessages;
        if (settings is not null)
        {
            if (settings.TryGetValue("lxmf_sync_limit", out var value))
                limit = value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new InvalidDataException("lxmf_sync_limit must be boolean or null.")
                };
            if (settings.TryGetValue("lxmf_sync_max", out value) &&
                (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out maximum)))
                throw new InvalidDataException("lxmf_sync_max must be a positive whole number.");
        }
        var result = new SidebandSynchronizationConfiguration(limit, maximum);
        result.Validate();
        return result;
    }

    internal void Export(IDictionary<string, JsonElement> settings)
    {
        Validate();
        settings["lxmf_sync_limit"] = LimitManualFetch is { } limited
            ? JsonSerializer.SerializeToElement(limited, SidebandStateJsonContext.Default.Boolean)
            : JsonSerializer.SerializeToElement((string?)null, SidebandStateJsonContext.Default.String);
        settings["lxmf_sync_max"] = JsonSerializer.SerializeToElement(MaximumMessages, SidebandStateJsonContext.Default.Int32);
    }
}
