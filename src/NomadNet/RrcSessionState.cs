using System.Security.Cryptography;
using System.Text;

namespace NomadNet;

public sealed record RrcChatMessage(string Kind, string? Room, string Source, string? Nick, string Text, long Timestamp, bool Mention)
{
    public bool ImportedHistory { get; init; }
}

/// <summary>Hardware- and transport-independent RRC room, limit, history and resource processing.</summary>
public sealed class RrcSessionState(byte[] identityHash)
{
    public const int MaximumHistory = 1000;
    public const int MaximumResourceBytes = 262144;
    private readonly byte[] _identityHash = identityHash.Length == 16 ? identityHash.ToArray() :
        throw new ArgumentException("Expected a Reticulum identity hash.", nameof(identityHash));
    private readonly Queue<string> _sentIds = new();
    private readonly HashSet<string> _pendingJoins = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingParts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResourceExpectation> _resources = new(StringComparer.Ordinal);
    private readonly List<RrcChatMessage> _history = [];
    private int _historyBytes;
    private long _roomFragmentExpires;
    private int _silentLists;
    private readonly HashSet<string> _silentWho = new(StringComparer.Ordinal);
    private readonly Queue<RrcEnvelope> _automatic = new();

    public string Nick { get; set; } = "";
    public bool Welcomed { get; private set; }
    public string HubName { get; private set; } = "";
    public string HubVersion { get; private set; } = "";
    public int MaximumNickBytes { get; private set; } = 32;
    public int MaximumRoomBytes { get; private set; } = 64;
    public int MaximumMessageBytes { get; private set; } = 350;
    public int MaximumRooms { get; private set; } = 32;
    public int RatePerMinute { get; private set; } = 240;
    public HashSet<string> Rooms { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> Members { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Nicks { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string?> AvailableRooms { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<RrcChatMessage> History => _history;
    public string? ActiveRoom { get; private set; }
    public HashSet<string> UnreadRooms { get; } = new(StringComparer.Ordinal);
    public HashSet<string> MentionRooms { get; } = new(StringComparer.Ordinal);
    public int HistoryPerRoomCap { get; private set; }
    public bool AutoList { get; set; }
    public bool AutoWho { get; set; }

    public IEnumerable<RrcEnvelope> DrainAutomaticCommands()
    {
        while (_automatic.TryDequeue(out var command)) yield return command;
    }

    private void QueueAutomatic(string? room, string text)
    {
        if (_automatic.Count >= 256) throw new InvalidDataException("Too many pending RRC automatic commands.");
        _automatic.Enqueue(MakeMessage(room, text));
    }

    public void SetActiveRoom(string? room)
    {
        ActiveRoom = room is null ? null : RrcEnvelope.NormalizeRoom(room, MaximumRoomBytes);
        if (ActiveRoom is not null) MarkRead(ActiveRoom);
    }

    public void MarkRead(string room)
    {
        room = RrcEnvelope.NormalizeRoom(room, MaximumRoomBytes);
        UnreadRooms.Remove(room);
        MentionRooms.Remove(room);
    }

    public void ClearMessages(string room)
    {
        room = RrcEnvelope.NormalizeRoom(room, MaximumRoomBytes);
        _history.RemoveAll(message => message.Room == room);
        _historyBytes = _history.Sum(message => Encoding.UTF8.GetByteCount(message.Text));
        MarkRead(room);
    }

    public void SetHistoryPerRoomCap(int cap)
    {
        if (cap is < 0 or > MaximumHistory) throw new ArgumentOutOfRangeException(nameof(cap));
        HistoryPerRoomCap = cap;
        if (cap > 0)
            foreach (var room in _history.Select(message => message.Room).Distinct(StringComparer.Ordinal).ToArray())
                TrimRoom(room);
    }

    public RrcEnvelope MakeHello() => RrcEnvelope.Create(1, _identityHash, body: new Dictionary<long, object?>
    {
        [0] = "nomadnet", [1] = "0.1", [2] = new Dictionary<long, object?> { [0] = true, [1] = true },
    }, nick: Nick);

    public RrcEnvelope MakeJoin(string room, string? key = null)
    {
        room = RrcEnvelope.NormalizeRoom(room, MaximumRoomBytes);
        if (!Rooms.Contains(room) && Rooms.Count >= MaximumRooms) throw new InvalidOperationException("Hub room limit reached.");
        ValidateNick();
        return RrcEnvelope.Create(10, _identityHash, room, key, Nick);
    }

    public RrcEnvelope MakeMessage(string? room, string text, bool action = false)
    {
        if (room is not null) room = RrcEnvelope.NormalizeRoom(room, MaximumRoomBytes);
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > MaximumMessageBytes)
            throw new ArgumentException("Message is empty or exceeds the hub's UTF-8 byte limit.");
        ValidateNick();
        return RrcEnvelope.Create(action ? 22 : 20, _identityHash, room, text, Nick);
    }

    public RrcEnvelope MakePart(string room) => RrcEnvelope.Create(12, _identityHash,
        RrcEnvelope.NormalizeRoom(room, MaximumRoomBytes), nick: Nick);

    public void Sent(RrcEnvelope envelope)
    {
        if (envelope.Type == 10 && envelope.Room is { } join) { Rooms.Add(join); _pendingJoins.Add(join); }
        if (envelope.Type == 12 && envelope.Room is { } part) _pendingParts.Add(part);
        if (envelope.Type is 20 or 22 && envelope.Body is string text && !text.StartsWith('/'))
        {
            _sentIds.Enqueue(Convert.ToHexStringLower(envelope.Id));
            while (_sentIds.Count > 256) _sentIds.Dequeue();
            Record(new(envelope.Type == 22 ? "action" : "message", envelope.Room,
                Convert.ToHexStringLower(_identityHash), Nick, text, envelope.Timestamp, false));
        }
    }

    public void ResetConnection()
    {
        Welcomed = false;
        Members.Clear();
        _pendingJoins.Clear();
        _pendingParts.Clear();
        _resources.Clear();
        _silentLists = 0;
        _silentWho.Clear();
        _automatic.Clear();
    }

    public RrcEnvelope? Apply(RrcEnvelope envelope)
    {
        var source = Convert.ToHexStringLower(envelope.Source);
        var room = envelope.Room is null ? null : RrcEnvelope.NormalizeRoom(envelope.Room, MaximumRoomBytes);
        switch (envelope.Type)
        {
            case 30: return RrcEnvelope.Create(31, _identityHash, body: envelope.Body);
            case 2:
                if (envelope.Body is not Dictionary<long, object?> welcome) throw new InvalidDataException("Malformed RRC WELCOME.");
                HubName = welcome.GetValueOrDefault(0) as string ?? "";
                HubVersion = welcome.GetValueOrDefault(1) as string ?? "";
                if (welcome.GetValueOrDefault(3) is Dictionary<long, object?> limits)
                {
                    MaximumNickBytes = Limit(limits, 0, 32, 4096);
                    MaximumRoomBytes = Limit(limits, 1, 64, 4096);
                    MaximumMessageBytes = Limit(limits, 2, 350, MaximumResourceBytes);
                    MaximumRooms = Limit(limits, 3, 32, 256);
                    RatePerMinute = Limit(limits, 4, 240, 1_000_000);
                }
                Welcomed = true;
                if (AutoList)
                {
                    QueueAutomatic(null, "/list");
                    _silentLists = Math.Min(256, _silentLists + 1);
                }
                break;
            case 11 when room is not null:
                if (!Rooms.Contains(room) && Rooms.Count >= 256) throw new InvalidDataException("RRC room state exceeds the local bound.");
                Rooms.Add(room);
                if (!Members.TryGetValue(room, out var members)) Members[room] = members = new(StringComparer.Ordinal);
                foreach (var member in Hashes(envelope.Body))
                {
                    if (members.Count >= 4096 && !members.Contains(member)) throw new InvalidDataException("RRC member state exceeds the local bound.");
                    members.Add(member);
                }
                members.Add(Convert.ToHexStringLower(_identityHash));
                if (_pendingJoins.Remove(room) && AutoWho)
                {
                    QueueAutomatic(room, "/who " + room);
                    _silentWho.Add(room);
                }
                LearnNick(envelope);
                break;
            case 13 when room is not null:
                if (_pendingParts.Remove(room))
                {
                    Rooms.Remove(room);
                    Members.Remove(room);
                    MarkRead(room);
                    if (ActiveRoom == room) ActiveRoom = null;
                }
                else if (Members.TryGetValue(room, out var present))
                    foreach (var member in Hashes(envelope.Body)) present.Remove(member);
                break;
            case 20:
            case 22:
                if (source == Convert.ToHexStringLower(_identityHash) && _sentIds.Contains(Convert.ToHexStringLower(envelope.Id))) break;
                if (envelope.Body is not string text) break;
                if (!string.IsNullOrEmpty(envelope.Nick) && (Nicks.Count < 4096 || Nicks.ContainsKey(source))) Nicks[source] = envelope.Nick;
                PromotePrefix(room, source);
                Record(new(envelope.Type == 22 ? "action" : "message", room, source, envelope.Nick, text,
                    envelope.Timestamp, RrcEnvelope.Mentions(text, Nick)), incoming: true);
                break;
            case 21:
            case 40:
                if (envelope.Type == 40 && room is not null)
                {
                    if (_pendingJoins.Remove(room)) Rooms.Remove(room);
                    _pendingParts.Remove(room);
                }
                if (envelope.Body is string notice)
                {
                    if (envelope.Type == 21 && ProcessNotice(notice)) break;
                    Record(new(envelope.Type == 40 ? "error" : "notice", room, source, null, notice, envelope.Timestamp, false),
                        incoming: true);
                }
                break;
            case 50:
                ExpectResource(envelope, room);
                break;
        }
        return null;
    }

    private void LearnNick(RrcEnvelope envelope)
    {
        if (string.IsNullOrEmpty(envelope.Nick)) return;
        var hashes = Hashes(envelope.Body).ToArray();
        if (hashes.Length == 1 && (Nicks.Count < 4096 || Nicks.ContainsKey(hashes[0]))) Nicks[hashes[0]] = envelope.Nick;
        foreach (var hash in hashes) PromotePrefix(envelope.Room, hash);
    }

    private void PromotePrefix(string? room, string fullHash)
    {
        if (room is null || !Members.TryGetValue(room, out var members)) return;
        foreach (var prefix in members.Where(member => member.Length == 12 && fullHash.StartsWith(member, StringComparison.Ordinal)).ToArray())
        {
            members.Remove(prefix);
            members.Add(fullHash);
            if (Nicks.Remove(prefix, out var nick) && (Nicks.Count < 4096 || Nicks.ContainsKey(fullHash))) Nicks[fullHash] = nick;
        }
    }

    private bool ProcessNotice(string text)
    {
        if (RrcDirectory.ParseWho(text) is { } who)
        {
            _ = RrcEnvelope.NormalizeRoom(who.Room, MaximumRoomBytes);
            if (!Members.ContainsKey(who.Room) && Members.Count >= 256) throw new InvalidDataException("RRC room state exceeds the local bound.");
            var known = (Members.GetValueOrDefault(who.Room) ?? []).Concat(Nicks.Keys)
                .Append(Convert.ToHexStringLower(_identityHash)).Where(hash => hash.Length > 12).ToArray();
            Members[who.Room] = new(who.Entries.Select(entry => entry.Hash), StringComparer.Ordinal);
            foreach (var entry in who.Entries)
            {
                if (entry.Nick is not null && Encoding.UTF8.GetByteCount(entry.Nick) > MaximumNickBytes)
                    throw new InvalidDataException("RRC membership nickname exceeds the hub limit.");
                if (entry.Nick is not null && (Nicks.Count < 4096 || Nicks.ContainsKey(entry.Hash))) Nicks[entry.Hash] = entry.Nick;
                foreach (var full in known.Where(hash => hash.StartsWith(entry.Hash, StringComparison.Ordinal)))
                    PromotePrefix(who.Room, full);
            }
            return _silentWho.Remove(who.Room);
        }
        else if (RrcDirectory.ParseRooms(text) is { } rooms)
        {
            AvailableRooms.Clear();
            foreach (var (name, topic) in rooms)
            {
                _ = RrcEnvelope.NormalizeRoom(name, MaximumRoomBytes);
                if (topic is not null && Encoding.UTF8.GetByteCount(topic) > 4096)
                    throw new InvalidDataException("RRC room topic exceeds the local bound.");
                AvailableRooms[name] = topic;
            }
            _roomFragmentExpires = Environment.TickCount64 + 60000;
            if (_silentLists > 0) { _silentLists--; return true; }
        }
        else if (Environment.TickCount64 < _roomFragmentExpires && RrcDirectory.ParseRoomFragment(text) is { } fragment)
        {
            if (AvailableRooms.Count >= 4096 && !AvailableRooms.ContainsKey(fragment.Name))
                throw new InvalidDataException("RRC room directory exceeds the local bound.");
            _ = RrcEnvelope.NormalizeRoom(fragment.Name, MaximumRoomBytes);
            if (fragment.Topic is not null && Encoding.UTF8.GetByteCount(fragment.Topic) > 4096)
                throw new InvalidDataException("RRC room topic exceeds the local bound.");
            AvailableRooms[fragment.Name] = fragment.Topic;
            _roomFragmentExpires = Environment.TickCount64 + 60000;
            return true;
        }
        return false;
    }

    private static IEnumerable<string> Hashes(object? body) =>
        body is IReadOnlyList<object?> items ? items.OfType<byte[]>().Where(hash => hash.Length == 16)
            .Select(Convert.ToHexStringLower) : [];

    private static int Limit(Dictionary<long, object?> limits, long key, int fallback, int bound)
    {
        if (!limits.TryGetValue(key, out var value)) return fallback;
        if (value is not long number || number < 1 || number > bound)
            throw new InvalidDataException("Hub advertises an invalid or unsupported local limit.");
        return (int)number;
    }

    private void ValidateNick()
    {
        if (Encoding.UTF8.GetByteCount(Nick) > MaximumNickBytes || Nick.Any(char.IsControl))
            throw new ArgumentException("Nickname exceeds the hub's UTF-8 limit or contains terminal controls.");
    }

    private void ExpectResource(RrcEnvelope envelope, string? room)
    {
        PruneResources();
        if (envelope.Body is not Dictionary<long, object?> body || body.GetValueOrDefault(0) is not byte[] id ||
            id.Length is < 1 or > 64 || body.GetValueOrDefault(1) is not string kind ||
            body.GetValueOrDefault(2) is not long size || size is < 1 or > MaximumResourceBytes ||
            (body.GetValueOrDefault(3) is { } hash && hash is not byte[] { Length: 32 }) ||
            (body.GetValueOrDefault(4) is { } encoding && encoding is not string))
            throw new InvalidDataException("Invalid RRC resource expectation.");
        if (_resources.Count >= 8) throw new InvalidDataException("Too many pending RRC resources.");
        _resources[Convert.ToHexStringLower(id)] = new(kind, (int)size, body.GetValueOrDefault(3) as byte[],
            body.GetValueOrDefault(4) as string ?? "utf-8", room, Environment.TickCount64 + 30_000);
    }

    private void PruneResources()
    {
        foreach (var key in _resources.Where(item => item.Value.Expires < Environment.TickCount64).Select(item => item.Key).ToArray())
            _resources.Remove(key);
    }

    public void ApplyResource(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaximumResourceBytes) throw new InvalidDataException("RRC resource exceeds 256 KiB.");
        PruneResources();
        var length = data.Length;
        var match = _resources.FirstOrDefault(item => item.Value.Size == length);
        if (match.Key is null) return;
        _resources.Remove(match.Key);
        var expected = match.Value;
        if (expected.Hash is not null && !CryptographicOperations.FixedTimeEquals(SHA256.HashData(data), expected.Hash))
            throw new InvalidDataException("RRC resource SHA-256 mismatch.");
        if (expected.Kind is not ("notice" or "motd")) return;
        var encoding = expected.Encoding.ToLowerInvariant() switch
        {
            "utf-8" or "utf8" => Encoding.UTF8,
            "ascii" or "us-ascii" => Encoding.ASCII,
            "latin-1" or "latin1" or "iso-8859-1" => Encoding.Latin1,
            _ => throw new InvalidDataException("Unsupported RRC notice resource text encoding."),
        };
        var text = encoding.GetString(data);
        if (expected.Kind == "notice" && ProcessNotice(text)) return;
        Record(new("notice", expected.Room, "", null, text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false),
            incoming: true);
    }

    internal void Record(RrcChatMessage message, bool incoming = false)
    {
        if (Encoding.UTF8.GetByteCount(message.Text) > MaximumResourceBytes)
            throw new InvalidDataException("RRC history item exceeds 256 KiB.");
        if (incoming && message.Room is null && message.Kind is "notice" or "error" && ActiveRoom is not null)
            message = message with { Room = ActiveRoom };
        if (incoming && message.Room is { } room && room != ActiveRoom)
        {
            if (UnreadRooms.Count >= 256 && !UnreadRooms.Contains(room))
                throw new InvalidDataException("RRC unread room state exceeds the local bound.");
            UnreadRooms.Add(room);
            if (message.Mention) MentionRooms.Add(room);
        }
        _history.Add(message);
        _historyBytes += Encoding.UTF8.GetByteCount(message.Text);
        if (HistoryPerRoomCap > 0) TrimRoom(message.Room);
        while (_history.Count > MaximumHistory || _historyBytes > 2 * 1024 * 1024)
        {
            _historyBytes -= Encoding.UTF8.GetByteCount(_history[0].Text);
            _history.RemoveAt(0);
        }
    }

    private void TrimRoom(string? room)
    {
        var excess = _history.Count(message => message.Room == room) - HistoryPerRoomCap;
        for (var index = 0; excess > 0 && index < _history.Count;)
        {
            if (_history[index].Room != room) { index++; continue; }
            _historyBytes -= Encoding.UTF8.GetByteCount(_history[index].Text);
            _history.RemoveAt(index);
            excess--;
        }
    }

    private sealed record ResourceExpectation(string Kind, int Size, byte[]? Hash, string Encoding, string? Room, long Expires);
}
