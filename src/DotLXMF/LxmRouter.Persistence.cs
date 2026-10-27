using DotReticulum.Core;

namespace DotLXMF;

public sealed partial class LxmRouter
{
    private static readonly TimeSpan DeliveryHistoryRetention = TimeSpan.FromDays(180);
    private readonly Dictionary<string, double> _processed = [];

    private void LoadDeliveryHistory() => LoadHistory("local_deliveries", _seen);

    private void LoadProcessedHistory() => LoadHistory("locally_processed", _processed);

    private void LoadHistory(string name, Dictionary<string, double> history)
    {
        if (_options.StoragePath is not { } storage) return;
        var path = Path.Combine(storage, "lxmf", name);
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 8 * 1024 * 1024 ||
            MsgPack.UnpackApplicationValue(File.ReadAllBytes(path)) is not Dictionary<object, object?> map || map.Count > 65_536)
            throw new InvalidDataException("Invalid LXMF delivery history.");
        foreach (var (key, value) in map)
        {
            if (key is not byte[] { Length: 32 } hash) throw new InvalidDataException("Invalid delivered message ID.");
            var timestamp = MsgPack.ToDouble(value);
            if (!double.IsFinite(timestamp)) throw new InvalidDataException("Invalid delivery timestamp.");
            if (Now - timestamp <= DeliveryHistoryRetention.TotalSeconds) history.Add(Convert.ToHexStringLower(hash), timestamp);
        }
    }

    private bool RememberProcessed(byte[] transientId, bool allowDuplicate)
    {
        lock (_gate)
        {
            var now = Now;
            foreach (var stale in _processed.Where(e => now - e.Value > DeliveryHistoryRetention.TotalSeconds).Select(e => e.Key).ToList())
                _processed.Remove(stale);
            var key = Convert.ToHexStringLower(transientId);
            if (_processed.ContainsKey(key) && !allowDuplicate) return false;
            if (_processed.Count >= 65_536 && !_processed.ContainsKey(key)) throw new InvalidOperationException("LXMF processed cache capacity reached.");
            var previous = _processed.GetValueOrDefault(key);
            _processed[key] = now;
            try { SaveHistory("locally_processed", _processed); }
            catch
            {
                if (previous == 0) _processed.Remove(key); else _processed[key] = previous;
                throw;
            }
            return true;
        }
    }

    private void SaveDeliveryHistory() => SaveHistory("local_deliveries", _seen);

    private void SaveHistory(string name, Dictionary<string, double> history)
    {
        if (_options.StoragePath is not { } storage) return;
        var path = Path.Combine(storage, "lxmf", name);
        var output = path + "." + Guid.NewGuid().ToString("N") + ".out";
        var map = new Dictionary<object, object?>();
        foreach (var (key, value) in history) map.Add(Convert.FromHexString(key), value);
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
