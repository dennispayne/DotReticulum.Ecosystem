using DotReticulum.Core;
using System.Globalization;

namespace LXMF;

public sealed partial class LxmPropagationNode
{
    private static void WritePrivatePersistedFile(string path, ReadOnlySpan<byte> bytes)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public sealed record PersistedMessage(byte[] TransientId, byte[] DestinationHash, double Received,
        int StampValue, long Size, bool Expired);
    public const int MaximumPersistedPeers = 65_536;
    public const int MaximumPersistedPeerBytes = 16 * 1024 * 1024;
    private readonly Dictionary<string, LxmPeer> _unresolvedPeers = [];

    /// <summary>Validated persisted records awaiting their destination's public identity, never scheduled for synchronization.</summary>
    public IReadOnlyList<LxmPeer> UnresolvedPeers { get { lock (_gate) return _unresolvedPeers.Values.ToArray(); } }

    /// <summary>
    /// Activates retained records only when the existing known-destination store contains a matching
    /// propagation identity. This does not import identities or declare messages synchronized.
    /// </summary>
    public int ResolvePersistedPeers()
    {
        List<string> activated = [];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var (key, peer) in _unresolvedPeers.ToArray())
            {
                using var identity = _router.Engine.Known.Recall(peer.DestinationHash);
                if (identity is null || !PropagationHash(identity).AsSpan().SequenceEqual(peer.DestinationHash)) continue;
                _unresolvedPeers.Remove(key);
                _peers.Add(key, peer);
                activated.Add(key);
            }
        }
        foreach (var key in activated)
            Report("Activated preserved propagation peer after matching identity discovery: " + key);
        return activated.Count;
    }

    private IEnumerable<LxmPeer> PersistedPeers => _peers.Values.Concat(_unresolvedPeers.Values);

    private void CheckPeerCapacity()
    {
        if (_peers.Count + _unresolvedPeers.Count >= MaximumPersistedPeers)
            throw new InvalidOperationException("Propagation persisted peer capacity reached.");
    }

    private static object?[] ReadPeerArray(byte[] bytes)
    {
        if (MsgPack.UnpackApplicationValue(bytes, new MsgPackLimits
        {
            MaxEncodedBytes = MaximumPersistedPeerBytes, MaxValueBytes = LxmPeer.MaximumPersistedBytes,
            MaxContainerEntries = MaximumPersistedPeers, MaxValues = MaximumPersistedPeers + 1, MaxDepth = 2,
        }) is not object?[] values || values.Length > MaximumPersistedPeers)
            throw new InvalidDataException("Invalid propagation peer list.");
        return values;
    }

    /// <summary>Strict offline inspection of the existing primary store format; never decrypts or authenticates message authorship.</summary>
    public static PersistedMessage InspectPersistedMessage(string fileName, ReadOnlySpan<byte> stamped,
        long maximumBytes, double now)
    {
        var parts = fileName.Split('_');
        if (parts.Length != 3 || parts[0].Length != 64 || parts[0].Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var received) ||
            !double.IsFinite(received) || received <= 0 ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 256 ||
            !double.IsFinite(now) || maximumBytes <= 0 ||
            stamped.Length <= LxMessage.Overhead + LxStamper.StampSize || stamped.Length > maximumBytes)
            throw new InvalidDataException("Invalid or oversized persisted propagation message.");
        var payload = stamped[..^LxStamper.StampSize];
        var id = Identity.FullHash(payload);
        if (Convert.ToHexStringLower(id) != parts[0])
            throw new InvalidDataException("Persisted propagation message transient ID does not match its payload.");
        var block = LxStamper.Workblock(id, LxStamper.WorkblockExpandRoundsPn);
        if (LxStamper.Value(block, stamped[^LxStamper.StampSize..]) != value)
            throw new InvalidDataException("Persisted propagation message stamp value does not match its stamp.");
        return new(id, payload[..16].ToArray(), received, value, stamped.Length,
            now > received + TimeSpan.FromDays(30).TotalSeconds);
    }

    /// <summary>Validates all four primary persisted node counters without applying fallback/reset behavior.</summary>
    public static void ValidatePersistedStatistics(ReadOnlySpan<byte> bytes) => ReadNodeStatistics(bytes);

    private static Dictionary<object, object?> ReadNodeStatistics(ReadOnlySpan<byte> bytes)
    {
        string[] names = ["client_propagation_messages_received", "client_propagation_messages_served",
            "unpeered_propagation_incoming", "unpeered_propagation_rx_bytes"];
        if (MsgPack.UnpackApplicationValue(bytes, new MsgPackLimits
        {
            MaxEncodedBytes = 4096, MaxValueBytes = 4096, MaxContainerEntries = 4, MaxValues = 9, MaxDepth = 2,
        }) is not Dictionary<object, object?> map || map.Count != names.Length ||
            names.Any(name => !map.TryGetValue(name, out var value) ||
                value is not (long and >= 0) && value is not (ulong and <= long.MaxValue)))
            throw new InvalidDataException("Invalid persisted propagation node statistics; refusing to reset counters.");
        return map;
    }
}
