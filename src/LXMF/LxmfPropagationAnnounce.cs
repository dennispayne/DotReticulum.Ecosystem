using DotReticulum.Core;

namespace LXMF;

/// <summary>Upstream's seven-element propagation-node announce data.</summary>
public sealed record LxmfPropagationAnnounce(long Timebase, bool Enabled, double TransferLimitKilobytes,
    double SyncLimitKilobytes, int StampCost, int StampFlexibility, int PeeringCost, Dictionary<object, object?> Metadata)
{
    public string? Name => Metadata.TryGetValue(1L, out var value) && value is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;

    public byte[] Pack() => MsgPack.Pack(new object?[]
    {
        false, Timebase, Enabled, TransferLimitKilobytes, SyncLimitKilobytes,
        new object?[] { (long)StampCost, (long)StampFlexibility, (long)PeeringCost }, Metadata,
    });

    public static bool TryParse(byte[]? appData, out LxmfPropagationAnnounce? announce)
    {
        announce = null;
        try
        {
            if (appData is null || MsgPack.UnpackApplicationValue(appData) is not object?[] { Length: >= 7 } values ||
                values[2] is not bool enabled || values[5] is not object?[] { Length: >= 3 } costs ||
                values[6] is not Dictionary<object, object?> metadata) return false;
            var transfer = MsgPack.ToDouble(values[3]);
            var sync = MsgPack.ToDouble(values[4]);
            var cost = checked((int)MsgPack.ToInt64(costs[0]));
            var flexibility = checked((int)MsgPack.ToInt64(costs[1]));
            var peering = checked((int)MsgPack.ToInt64(costs[2]));
            if (!double.IsFinite(transfer) || !double.IsFinite(sync) || transfer <= 0 || sync <= 0 ||
                cost is < 0 or > 256 || flexibility < 0 || peering is < 0 or > 256) return false;
            announce = new LxmfPropagationAnnounce(MsgPack.ToInt64(values[1]), enabled, transfer, sync, cost, flexibility, peering, metadata);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) { return false; }
    }
}
