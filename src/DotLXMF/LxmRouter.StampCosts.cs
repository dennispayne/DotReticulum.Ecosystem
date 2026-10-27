using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotLXMF;

public sealed partial class LxmRouter
{
    private const int StampCostExpirySeconds = 45 * 24 * 60 * 60;
    private readonly Dictionary<string, (double Updated, int? Cost)> _outboundStampCosts = [];
    private IAnnounceHandler _deliveryAnnounceHandler = null!;
    private bool _stampCostsDirty;

    public int? InboundStampCost => Volatile.Read(ref _inboundStampCost) is var cost && cost > 0 ? cost : null;

    public byte[] GetAnnounceAppData() => LxmfAppData.Build(DisplayName, InboundStampCost);

    public bool SetInboundStampCost(int? cost)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (cost >= 255) return false;
        Volatile.Write(ref _inboundStampCost, Math.Max(0, cost ?? 0));
        return true;
    }

    private void InitializeDeliveryAnnounces()
    {
        _deliveryAnnounceHandler = new CallbackAnnounceHandler("lxmf.delivery", received =>
        {
            using var identity = received.Identity;
            try
            {
                if (Volatile.Read(ref _disposed) == 0)
                    UpdateStampCost(received.DestinationHash, LxmfAppData.StampCost(received.AppData));
            }
            catch (Exception ex) { Report("Could not update announced LXMF stamp cost: " + ex.Message); }
        }, receivePathResponses: true);
        _router.Engine.RegisterHandler(_deliveryAnnounceHandler);
    }

    public int? GetOutboundStampCost(ReadOnlySpan<byte> destination)
    {
        if (destination.Length != LxMessage.DestinationLength) throw new ArgumentException("Destination must be 16 bytes.", nameof(destination));
        lock (_gate)
        {
            if (_outboundStampCosts.TryGetValue(Convert.ToHexStringLower(destination), out var entry))
                return Now - entry.Updated <= StampCostExpirySeconds ? entry.Cost : null;
        }
        return LxmfAppData.StampCost(_router.Engine.Known.RecallAppData(destination));
    }

    /// <summary>Updates the bounded cache. The worker and graceful disposal persist dirty entries; callers can explicitly flush.</summary>
    public void UpdateStampCost(ReadOnlySpan<byte> destination, int? cost)
    {
        if (destination.Length != LxMessage.DestinationLength) throw new ArgumentException("Destination must be 16 bytes.", nameof(destination));
        if (cost is <= 0 or >= 255) throw new ArgumentOutOfRangeException(nameof(cost));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            CleanOutboundStampCosts();
            var key = Convert.ToHexStringLower(destination);
            if (_outboundStampCosts.Count >= 65_536 && !_outboundStampCosts.ContainsKey(key))
                throw new InvalidOperationException("LXMF stamp-cost cache capacity reached.");
            _outboundStampCosts[key] = (Now, cost);
            _stampCostsDirty = true;
        }
        _wake.Release();
    }

    private void CleanOutboundStampCosts()
    {
        foreach (var key in _outboundStampCosts.Where(pair => Now - pair.Value.Updated > StampCostExpirySeconds).Select(pair => pair.Key).ToList())
        {
            _outboundStampCosts.Remove(key);
            _stampCostsDirty = true;
        }
    }

    private void LoadOutboundStampCosts()
    {
        if (_options.StoragePath is not { } storage) return;
        var path = Path.Combine(storage, "lxmf", "outbound_stamp_costs");
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 8 * 1024 * 1024 ||
            MsgPack.UnpackApplicationValue(File.ReadAllBytes(path)) is not Dictionary<object, object?> map || map.Count > 65_536)
            throw new InvalidDataException("Invalid LXMF outbound stamp-cost cache.");
        foreach (var (key, value) in map)
        {
            if (key is not byte[] { Length: 16 } hash || value is not object?[] { Length: 2 } entry)
                throw new InvalidDataException("Invalid LXMF outbound stamp-cost entry.");
            var updated = MsgPack.ToDouble(entry[0]);
            if (!double.IsFinite(updated) || entry[1] is not null && entry[1] is not long and not ulong)
                throw new InvalidDataException("Invalid LXMF stamp-cost timestamp or cost.");
            var cost = entry[1] is null ? (int?)null : checked((int)MsgPack.ToDouble(entry[1]));
            if (cost is <= 0 or >= 255) throw new InvalidDataException("Invalid LXMF stamp cost.");
            if (Now - updated <= StampCostExpirySeconds) _outboundStampCosts.Add(Convert.ToHexStringLower(hash), (updated, cost));
        }
    }

    public void SaveOutboundStampCosts()
    {
        lock (_gate)
        {
            CleanOutboundStampCosts();
            if (!_stampCostsDirty || _options.StoragePath is not { } storage) return;
            var path = Path.Combine(storage, "lxmf", "outbound_stamp_costs");
            var output = path + "." + Guid.NewGuid().ToString("N") + ".out";
            var map = new Dictionary<object, object?>();
            foreach (var (key, entry) in _outboundStampCosts) map.Add(Convert.FromHexString(key), new object?[] { entry.Updated, entry.Cost });
            try
            {
                using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(MsgPack.Pack(map));
                    stream.Flush(flushToDisk: true);
                }
                File.Move(output, path, overwrite: true);
                _stampCostsDirty = false;
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }
    }
}
