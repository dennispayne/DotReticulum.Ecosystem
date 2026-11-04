using DotReticulum.Core;

namespace LXMF;

public sealed partial class LxmPeer
{
    public const int MaximumPersistedBytes = 8 * 1024 * 1024;
    public const int MaximumMessageIds = 65_536;
    private static readonly HashSet<string> PersistedKeys =
    [
        "peering_timebase", "alive", "metadata", "last_heard", "sync_strategy", "peering_key",
        "destination_hash", "link_establishment_rate", "sync_transfer_rate", "propagation_transfer_limit",
        "propagation_sync_limit", "propagation_stamp_cost", "propagation_stamp_cost_flexibility",
        "peering_cost", "last_sync_attempt", "offered", "outgoing", "incoming", "rx_bytes", "tx_bytes",
        "handled_ids", "unhandled_ids",
    ];

    private static Dictionary<object, object?> ReadPersistedMap(ReadOnlySpan<byte> bytes)
    {
        var limits = new MsgPackLimits
        {
            MaxEncodedBytes = MaximumPersistedBytes, MaxValueBytes = MaximumPersistedBytes,
            MaxContainerEntries = MaximumMessageIds, MaxValues = 150_000, MaxDepth = 10,
        };
        if (MsgPack.UnpackApplicationValue(bytes, limits) is not Dictionary<object, object?> map ||
            map.Keys.Any(key => key is not string text || !PersistedKeys.Contains(text)) ||
            map.GetValueOrDefault("destination_hash") is not byte[] { Length: 16 } ||
            map.GetValueOrDefault("alive") is not bool ||
            map.GetValueOrDefault("peering_timebase") is null || map.GetValueOrDefault("last_heard") is null)
            throw new FormatException("Invalid or unsupported persisted propagation peer.");
        foreach (var name in new[] { "peering_timebase", "last_heard", "last_sync_attempt",
            "link_establishment_rate", "sync_transfer_rate", "propagation_transfer_limit", "propagation_sync_limit" })
        {
            if (!map.TryGetValue(name, out var value)) continue;
            if (value is null && name is ("propagation_transfer_limit" or "propagation_sync_limit")) continue;
            if (value is not (long or ulong or float or double) ||
                !double.IsFinite(MsgPack.ToDouble(value)) || MsgPack.ToDouble(value) < 0 ||
                MsgPack.ToDouble(value) >= long.MaxValue)
                throw new FormatException("Invalid persisted propagation peer number.");
        }
        foreach (var name in new[] { "offered", "outgoing", "incoming", "rx_bytes", "tx_bytes" })
            if (map.TryGetValue(name, out var value) &&
                (value is not (long and >= 0) && value is not (ulong and <= long.MaxValue)))
                throw new FormatException("Invalid persisted propagation peer counter.");
        foreach (var name in new[] { "propagation_stamp_cost", "propagation_stamp_cost_flexibility", "peering_cost" })
            if (map.TryGetValue(name, out var value) && value is not null &&
                value is not (long and >= 0 and <= 256) && value is not (ulong and <= 256))
                throw new FormatException("Invalid persisted propagation peer cost.");
        if (map.TryGetValue("sync_strategy", out var strategy) &&
            strategy is not (long and (1 or 2)) && strategy is not (ulong and (1 or 2)))
            throw new FormatException("Invalid persisted propagation peer strategy.");
        if (map.GetValueOrDefault("metadata") is { } metadata &&
            (metadata is not Dictionary<object, object?> || MsgPack.Pack(metadata).Length > 65_536))
            throw new FormatException("Invalid or oversized persisted propagation peer metadata.");
        if (map.GetValueOrDefault("peering_key") is { } proof &&
            (proof is not object?[] { Length: 2 } pair || pair[0] is not byte[] { Length: 32 } ||
             pair[1] is not (long and >= 0 and <= 256) && pair[1] is not (ulong and <= 256)))
            throw new FormatException("Invalid persisted propagation peering proof.");
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "handled_ids", "unhandled_ids" })
        {
            if (map.GetValueOrDefault(name) is not object?[] ids || ids.Length > MaximumMessageIds)
                throw new FormatException("Invalid or oversized persisted propagation peer index.");
            foreach (var value in ids)
                if (value is not byte[] { Length: 32 } id || !all.Add(Convert.ToHexStringLower(id)))
                    throw new FormatException("Duplicate, overlapping or invalid persisted propagation peer message ID.");
        }
        return map;
    }
}
