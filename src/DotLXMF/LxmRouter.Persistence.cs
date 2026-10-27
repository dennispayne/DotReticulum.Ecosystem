using DotReticulum.Core;

namespace DotLXMF;

public sealed partial class LxmRouter
{
    private static readonly TimeSpan DeliveryHistoryRetention = TimeSpan.FromDays(180);

    private void LoadDeliveryHistory()
    {
        if (_options.StoragePath is not { } storage) return;
        var path = Path.Combine(storage, "lxmf", "local_deliveries");
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 8 * 1024 * 1024 ||
            MsgPack.UnpackApplicationValue(File.ReadAllBytes(path)) is not Dictionary<object, object?> map || map.Count > 65_536)
            throw new InvalidDataException("Invalid LXMF delivery history.");
        foreach (var (key, value) in map)
        {
            if (key is not byte[] { Length: 32 } hash) throw new InvalidDataException("Invalid delivered message ID.");
            var timestamp = MsgPack.ToDouble(value);
            if (!double.IsFinite(timestamp)) throw new InvalidDataException("Invalid delivery timestamp.");
            if (Now - timestamp <= DeliveryHistoryRetention.TotalSeconds) _seen.Add(Convert.ToHexStringLower(hash), timestamp);
        }
    }

    private void SaveDeliveryHistory()
    {
        if (_options.StoragePath is not { } storage) return;
        var path = Path.Combine(storage, "lxmf", "local_deliveries");
        var output = path + "." + Guid.NewGuid().ToString("N") + ".out";
        var map = new Dictionary<object, object?>();
        foreach (var (key, value) in _seen) map.Add(Convert.FromHexString(key), value);
        try
        {
            using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(MsgPack.Pack(map));
                stream.Flush(flushToDisk: true);
            }
            File.Move(output, path, overwrite: true);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }
}
