using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NomadNet;

/// <summary>Strict offline conversion of primary string-key CBOR, never a network identity claim.</summary>
internal static partial class NomadNetRrcImport
{
    internal const int MaximumHubs = 128;
    internal const int MaximumBytes = 4 * 1024 * 1024;

    internal static async Task ImportAsync(string source, string staging, IReadOnlyDictionary<string, string> snapshot,
        string fallbackNick, string config, HashSet<string> activated, List<string> warnings, CancellationToken token)
    {
        if (!snapshot.ContainsKey("storage/rrc_hubs")) return;
        var data = await ReadAsync(source, "storage/rrc_hubs", token).ConfigureAwait(false);
        var roots = Decode(data);
        if (roots.Count != 1 || roots[0] is not Dictionary<string, object?> root ||
            root.GetValueOrDefault("hubs") is not List<object?> hubs || hubs.Count > MaximumHubs)
            throw new InvalidDataException("Invalid primary RRC hub collection.");
        var (cap, filter) = NomadNetConfigImport.RrcImportHistoryPolicy(config);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in hubs)
        {
            token.ThrowIfCancellationRequested();
            if (value is not Dictionary<string, object?> hub || hub.GetValueOrDefault("hash") is not byte[] { Length: 16 } hash)
                throw new InvalidDataException("Invalid primary RRC hub hash.");
            foreach (var key in hub.Keys.Where(key => key is not ("hash" or "dest_name" or "name" or "nick" or "rooms" or
                         "parted_rooms" or "auto_reconnect" or "auto_list" or "auto_who")))
                warnings.Add("Unsupported RRC hub key archived, not activated: " + key);
            var hex = Convert.ToHexStringLower(hash);
            if (!hashes.Add(hex)) throw new InvalidDataException("Multiple RRC profiles for one destination are unsupported; nothing was overwritten.");
            var aspect = Text(hub, "dest_name", "rrc.hub", 256);
            ValidateAspect(aspect);
            var name = Text(hub, "name", "<" + hex + ">", 4096);
            var nick = Text(hub, "nick", fallbackNick, 32);
            var rooms = Rooms(hub, "rooms");
            var parted = Rooms(hub, "parted_rooms");
            if (rooms.Intersect(parted, StringComparer.Ordinal).Any() || rooms.Count + parted.Count > 256)
                throw new InvalidDataException("Overlapping or excessive RRC joined/parted rooms.");
            var history = new List<object?>();
            var historyBytes = 0;
            foreach (var room in rooms.Concat(parted))
            {
                var suffix = aspect == "rrc.hub" ? "" : "__" + Digest(aspect);
                var sanitized = RoomFilename().Replace(room, "_");
                if (sanitized.Length > 64) sanitized = sanitized[..64];
                var path = $"storage/rrc_history/{hex}{suffix}/{sanitized}_{Digest(room)}.log";
                if (!paths.Add(path)) throw new InvalidDataException("Colliding primary RRC history filenames.");
                if (!snapshot.ContainsKey(path)) continue;
                var retained = new List<object?>();
                foreach (var item in Decode(await ReadAsync(source, path, token).ConfigureAwait(false)))
                {
                    if (item is not Dictionary<string, object?> entry || entry.GetValueOrDefault("k") is not string kind ||
                        kind is not ("msg" or "action" or "notice" or "system" or "error") ||
                        entry.GetValueOrDefault("t") is not string text || Encoding.UTF8.GetByteCount(text) > RrcSessionState.MaximumResourceBytes ||
                        entry.GetValueOrDefault("ts") is not long timestamp || timestamp < 0 ||
                        timestamp > 253402300799999 || entry.GetValueOrDefault("m") is not bool mention ||
                        (entry.GetValueOrDefault("s") is not null && entry.GetValueOrDefault("s") is not byte[] { Length: 16 }) ||
                        (entry.GetValueOrDefault("n") is not null && entry.GetValueOrDefault("n") is not string))
                        throw new InvalidDataException("Invalid primary RRC historical record; no defaults or timestamps were invented.");
                    if (entry.GetValueOrDefault("n") is string historicalNick && Encoding.UTF8.GetByteCount(historicalNick) > 4096)
                        throw new InvalidDataException("Historical RRC nickname exceeds bounds.");
                    if (filter && kind is "notice" or "system") continue;
                    retained.Add(new object?[] { kind == "msg" ? "message" : kind, room,
                        entry.GetValueOrDefault("s") is byte[] sender ? Convert.ToHexStringLower(sender) : "",
                        entry.GetValueOrDefault("n"), text, timestamp, mention, true });
                }
                if (cap > 0 && retained.Count > cap) retained.RemoveRange(0, retained.Count - cap);
                history.AddRange(retained);
                historyBytes += retained.Cast<object?[]>().Sum(record => Encoding.UTF8.GetByteCount((string)record[4]!));
                if (history.Count > RrcSessionState.MaximumHistory || historyBytes > 2 * 1024 * 1024)
                    throw new InvalidDataException("Retained primary RRC history exceeds native bounds; nothing was evicted.");
                activated.Add(path);
            }
            var native = RrcCbor.Encode(new Dictionary<long, object?>
            {
                [0] = 1L, [1] = nick, [2] = rooms.Cast<object?>().ToArray(), [3] = history.ToArray(),
                [4] = Flag(hub, "auto_reconnect"), [5] = Flag(hub, "auto_list"), [6] = Flag(hub, "auto_who"),
                [7] = (long)cap, [8] = name, [9] = aspect, [10] = parted.Cast<object?>().ToArray(),
            });
            var directory = Path.Combine(staging, "rrc");
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, hex + ".cbor"), native, token).ConfigureAwait(false);
        }
        activated.Add("storage/rrc_hubs");
        warnings.Add("Imported RRC source/nick/mention values are unauthenticated historical claims; no membership, unread/active state, hub authorization or startup connection was inferred. Primary load filtering and retention were applied. Unreferenced history files remain not imported. RRC history config keys affect this migration only, not complete ConfigObj parity.");
    }

    internal static void ValidateAspect(string aspect)
    {
        var parts = aspect.Split('.');
        if (parts.Length < 2 || parts.Any(part => part.Length == 0 || part.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '_')))
            throw new InvalidDataException("Unsupported RRC destination aspect.");
    }

    private static List<string> Rooms(Dictionary<string, object?> hub, string key)
    {
        if (!hub.TryGetValue(key, out var value)) return [];
        if (value is not List<object?> items || items.Count > 256) throw new InvalidDataException("Invalid RRC room list.");
        var rooms = new List<string>();
        foreach (var item in items)
        {
            if (item is not string room || room != room.Trim().ToLowerInvariant() || room == "*" ||
                RrcEnvelope.NormalizeRoom(room) != room || rooms.Contains(room, StringComparer.Ordinal))
                throw new InvalidDataException("Unsupported or duplicate persisted primary RRC room.");
            rooms.Add(room);
        }
        return rooms;
    }

    private static bool Flag(Dictionary<string, object?> map, string key) =>
        !map.TryGetValue(key, out var value) ? false : value is bool flag ? flag :
            throw new InvalidDataException("Invalid primary RRC policy: " + key);

    private static string Text(Dictionary<string, object?> map, string key, string fallback, int bound)
    {
        if (!map.TryGetValue(key, out var value) || value is null || value is "") value = fallback;
        if (value is not string text || Encoding.UTF8.GetByteCount(text) > bound || text.Any(char.IsControl))
            throw new InvalidDataException("Invalid primary RRC text: " + key);
        return text;
    }

    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];

    private static Task<byte[]> ReadAsync(string root, string relative, CancellationToken token) =>
        NomadNetProfileImport.ReadAsync(root, relative, MaximumBytes, token);

    internal static List<object?> Decode(byte[] data)
    {
        try
        {
            var reader = new CborReader(data, CborConformanceMode.Strict, allowMultipleRootLevelValues: true);
            var result = new List<object?>();
            var budget = 100_000;
            while (reader.BytesRemaining > 0) result.Add(Read(reader, 0, ref budget));
            return result;
        }
        catch (Exception error) when (error is CborContentException or InvalidOperationException or OverflowException)
        { throw new InvalidDataException("Corrupt primary RRC CBOR, including incomplete tails; import rolled back.", error); }
    }

    private static object? Read(CborReader reader, int depth, ref int budget)
    {
        if (depth > 8 || --budget < 0) throw new InvalidDataException("Primary RRC CBOR exceeds structural bounds.");
        switch (reader.PeekState())
        {
            case CborReaderState.TextString: return reader.ReadTextString();
            case CborReaderState.ByteString: return reader.ReadByteString();
            case CborReaderState.UnsignedInteger:
            case CborReaderState.NegativeInteger: return reader.ReadInt64();
            case CborReaderState.Boolean: return reader.ReadBoolean();
            case CborReaderState.Null: reader.ReadNull(); return null;
            case CborReaderState.StartArray:
                reader.ReadStartArray();
                var list = new List<object?>();
                while (reader.PeekState() != CborReaderState.EndArray) list.Add(Read(reader, depth + 1, ref budget));
                reader.ReadEndArray();
                return list;
            case CborReaderState.StartMap:
                reader.ReadStartMap();
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                while (reader.PeekState() != CborReaderState.EndMap)
                    if (!map.TryAdd(reader.ReadTextString(), Read(reader, depth + 1, ref budget)))
                        throw new InvalidDataException("Duplicate primary RRC CBOR key.");
                reader.ReadEndMap();
                return map;
            default: throw new InvalidDataException("Unsupported primary RRC CBOR type.");
        }
    }

    [GeneratedRegex("[^a-z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex RoomFilename();
}
