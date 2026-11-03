using System.Text;

namespace NomadNet;

public sealed record NomadNetNavigationEntry(string DestinationHash, string Path, Dictionary<object, object?>? RequestData);

/// <summary>Session-owned navigation snapshots; does not retain pages or persist submitted fields.</summary>
public sealed class NomadNetNavigationHistory
{
    public const int MaximumEntries = 64;
    private NomadNetNavigationEntry[] _entries = [];
    private int _position = -1;

    public int Position => _position + 1;
    public int Count => _entries.Length;
    public IReadOnlyList<NomadNetNavigationEntry> Snapshot() => _entries.Select(Clone).ToArray();

    public void Visit(string destinationHash, string path, object? requestData = null)
    {
        var entry = new NomadNetNavigationEntry(NomadNetAddress.NormalizeHash(destinationHash),
            NomadNetAddress.NormalizePagePath(path), CopyData(requestData));
        _entries = [.. _entries.Take(_position + 1).TakeLast(MaximumEntries - 1), entry];
        _position = _entries.Length - 1;
    }

    public NomadNetNavigationEntry? Back() => _position > 0 ? Clone(_entries[--_position]) : null;
    public NomadNetNavigationEntry? Forward() => _position + 1 < _entries.Length ? Clone(_entries[++_position]) : null;

    private static NomadNetNavigationEntry Clone(NomadNetNavigationEntry entry) =>
        entry with { RequestData = CopyData(entry.RequestData) };

    private static Dictionary<object, object?>? CopyData(object? data)
    {
        if (data is null) return null;
        if (data is not IReadOnlyDictionary<object, object?> fields || fields.Count > 1024)
            throw new ArgumentException("Browser history requires bounded Micron string fields.", nameof(data));
        var copy = new Dictionary<object, object?>();
        var bytes = 0L;
        foreach (var (key, value) in fields)
        {
            if (key is not string name || value is not string text)
                throw new ArgumentException("Browser history requires Micron string fields.", nameof(data));
            bytes += (long)Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(text);
            if (bytes > 65536) throw new InvalidDataException("Browser history form data exceeds 64 KiB.");
            copy.Add(name, text);
        }
        return copy;
    }
}
