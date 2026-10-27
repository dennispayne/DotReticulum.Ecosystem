using DotReticulum.Core;

namespace DotLXMF;

public enum LxmPeerSyncStrategy { Lazy = 1, Persistent = 2 }

internal sealed record LxmPeerOfferEntry(byte[] Id, double Weight, long Size, int StampValue);

/// <summary>Persistent propagation peer state. Wire storage matches LXMPeer.to_bytes/from_bytes.</summary>
public sealed class LxmPeer
{
    private readonly HashSet<string> _handled = [];
    private readonly HashSet<string> _unhandled = [];
    public LxmPeer(byte[] destinationHash)
    {
        if (destinationHash.Length != 16) throw new ArgumentException("Peer destination must be 16 bytes.", nameof(destinationHash));
        DestinationHash = destinationHash.ToArray();
    }

    public byte[] DestinationHash { get; }
    public bool Alive { get; internal set; }
    public byte State { get; internal set; }
    public double PeeringTimebase { get; internal set; }
    public double LastHeard { get; internal set; }
    public double LastSyncAttempt { get; internal set; }
    public double NextSyncAttempt { get; internal set; }
    public double SyncBackoff { get; internal set; }
    public double LinkEstablishmentRate { get; internal set; }
    public double SyncTransferRate { get; internal set; }
    public double? TransferLimitKilobytes { get; internal set; }
    public double? SyncLimitKilobytes { get; internal set; }
    public int? StampCost { get; internal set; }
    public int? StampFlexibility { get; internal set; }
    public int? PeeringCost { get; internal set; }
    public int SyncStrategy { get; internal set; } = 2;
    public byte[]? PeeringKey { get; internal set; }
    public int? PeeringKeyValue { get; internal set; }
    public Dictionary<object, object?>? Metadata { get; internal set; }
    public long Offered { get; internal set; }
    public long Outgoing { get; internal set; }
    public long Incoming { get; internal set; }
    public long RxBytes { get; internal set; }
    public long TxBytes { get; internal set; }
    public int HandledCount => _handled.Count;
    public int UnhandledCount => _unhandled.Count;
    public double AcceptanceRate => Offered > 0 ? (double)Outgoing / Offered : 0;
    internal bool IsHandled(byte[] id) => _handled.Contains(Convert.ToHexStringLower(id));
    internal bool ContinueAfterTransfer => SyncStrategy == (int)LxmPeerSyncStrategy.Persistent && UnhandledCount > 0;

    internal void RecordCompletedTransfer(IReadOnlyCollection<byte[]> transferred, int offered, long dataSize,
        long transferSize, double elapsedSeconds, double completedAt)
    {
        foreach (var id in transferred) MarkHandled(id);
        Offered += offered;
        Outgoing += transferred.Count;
        TxBytes += dataSize;
        SyncTransferRate = transferSize * 8.0 / Math.Max(0.0001, elapsedSeconds);
        Alive = true;
        LastHeard = completedAt;
        SyncBackoff = 0;
    }

    internal byte[][] PrepareOffer(Func<byte[], LxmPeerOfferEntry?> lookup, int minimumStampValue,
        double transferLimit, double syncLimit)
    {
        var candidates = new List<LxmPeerOfferEntry>();
        foreach (var key in _unhandled.ToArray())
        {
            var entry = lookup(Convert.FromHexString(key));
            if (entry is null || entry.StampValue < minimumStampValue) _unhandled.Remove(key);
            else candidates.Add(entry);
        }

        var offered = new List<byte[]>();
        long total = 24;
        foreach (var entry in candidates.OrderBy(entry => entry.Weight))
        {
            if (entry.Size + 16 > transferLimit) { MarkHandled(entry.Id); continue; }
            if (total + entry.Size + 16 >= syncLimit) continue;
            offered.Add(entry.Id);
            total += entry.Size + 16;
        }

        return offered.ToArray();
    }

    internal byte[][] SelectWanted(byte[][] offered, object? response)
    {
        if (response is true) return offered;
        var wanted = response is false ? [] : response is object?[] values &&
            values.All(value => value is byte[] { Length: 32 }) ? values.Cast<byte[]>().ToArray() :
            throw new InvalidOperationException("Peer rejected propagation offer: " + response);
        var offeredKeys = offered.Select(Convert.ToHexStringLower).ToHashSet();
        var wantedKeys = wanted.Select(Convert.ToHexStringLower).ToHashSet();
        if (wantedKeys.Count != wanted.Length || !wantedKeys.IsSubsetOf(offeredKeys))
            throw new InvalidDataException("Peer requested an unoffered or duplicate message ID.");
        foreach (var id in offered)
            if (!wantedKeys.Contains(Convert.ToHexStringLower(id))) MarkHandled(id);
        return wanted;
    }
    internal void Forget(byte[] id)
    {
        var key = Convert.ToHexStringLower(id);
        _handled.Remove(key);
        _unhandled.Remove(key);
    }
    internal void AddUnhandled(byte[] id)
    {
        var key = Convert.ToHexStringLower(id);
        if (!_handled.Contains(key))
        {
            if (_unhandled.Count >= 65_536) throw new InvalidOperationException("Peer message queue is full.");
            _unhandled.Add(key);
        }
    }
    internal void MarkHandled(byte[] id)
    {
        var key = Convert.ToHexStringLower(id);
        _unhandled.Remove(key);
        if (_handled.Count >= 65_536) throw new InvalidOperationException("Peer handled cache is full.");
        _handled.Add(key);
    }

    public byte[] ToBytes() => MsgPack.Pack(new Dictionary<object, object?>
    {
        ["peering_timebase"] = PeeringTimebase,
        ["alive"] = Alive,
        ["metadata"] = Metadata,
        ["last_heard"] = LastHeard,
        ["sync_strategy"] = (long)SyncStrategy,
        ["peering_key"] = PeeringKey is null ? null : new object?[] { PeeringKey, (long)(PeeringKeyValue ?? 0) },
        ["destination_hash"] = DestinationHash,
        ["link_establishment_rate"] = LinkEstablishmentRate,
        ["sync_transfer_rate"] = SyncTransferRate,
        ["propagation_transfer_limit"] = TransferLimitKilobytes,
        ["propagation_sync_limit"] = SyncLimitKilobytes,
        ["propagation_stamp_cost"] = StampCost is { } cost ? (long)cost : null,
        ["propagation_stamp_cost_flexibility"] = StampFlexibility is { } flexibility ? (long)flexibility : null,
        ["peering_cost"] = PeeringCost is { } peering ? (long)peering : null,
        ["last_sync_attempt"] = LastSyncAttempt,
        ["offered"] = Offered,
        ["outgoing"] = Outgoing,
        ["incoming"] = Incoming,
        ["rx_bytes"] = RxBytes,
        ["tx_bytes"] = TxBytes,
        ["handled_ids"] = _handled.Select(id => (object?)Convert.FromHexString(id)).ToArray(),
        ["unhandled_ids"] = _unhandled.Select(id => (object?)Convert.FromHexString(id)).ToArray(),
    });

    public static LxmPeer FromBytes(ReadOnlySpan<byte> bytes, Func<byte[], bool> messageExists)
    {
        ArgumentNullException.ThrowIfNull(messageExists);
        if (MsgPack.UnpackApplicationValue(bytes) is not Dictionary<object, object?> map ||
            !map.TryGetValue("destination_hash", out var destination) || destination is not byte[] { Length: 16 } hash)
            throw new FormatException("Invalid propagation peer.");
        double Number(string key, double fallback = 0) => map.TryGetValue(key, out var value) && value is not null ? MsgPack.ToDouble(value) : fallback;
        int? Cost(string key) => map.TryGetValue(key, out var value) && value is not null ? checked((int)MsgPack.ToInt64(value)) : null;
        var peer = new LxmPeer(hash)
        {
            PeeringTimebase = Number("peering_timebase"),
            Alive = map.GetValueOrDefault("alive") is true,
            Metadata = map.GetValueOrDefault("metadata") as Dictionary<object, object?>,
            LastHeard = Number("last_heard"),
            SyncStrategy = Cost("sync_strategy") ?? 2,
            LinkEstablishmentRate = Number("link_establishment_rate"),
            SyncTransferRate = Number("sync_transfer_rate"),
            TransferLimitKilobytes = map.GetValueOrDefault("propagation_transfer_limit") is null ? null : Number("propagation_transfer_limit"),
            SyncLimitKilobytes = map.GetValueOrDefault("propagation_sync_limit") is null ? null : Number("propagation_sync_limit"),
            StampCost = Cost("propagation_stamp_cost"),
            StampFlexibility = Cost("propagation_stamp_cost_flexibility"),
            PeeringCost = Cost("peering_cost"),
            LastSyncAttempt = Number("last_sync_attempt"),
            Offered = checked((long)Number("offered")),
            Outgoing = checked((long)Number("outgoing")),
            Incoming = checked((long)Number("incoming")),
            RxBytes = checked((long)Number("rx_bytes")),
            TxBytes = checked((long)Number("tx_bytes")),
        };
        if (map.GetValueOrDefault("peering_key") is object?[] { Length: 2 } key &&
            key[0] is byte[] { Length: 32 } token)
        {
            peer.PeeringKey = token;
            peer.PeeringKeyValue = checked((int)MsgPack.ToInt64(key[1]));
        }

        foreach (var (name, handled) in new[] { ("handled_ids", true), ("unhandled_ids", false) })
        {
            if (map.GetValueOrDefault(name) is not object?[] ids) throw new FormatException("Invalid peer message index.");
            foreach (var value in ids)
            {
                if (value is not byte[] { Length: 32 } id) throw new FormatException("Invalid peer message ID.");
                if (messageExists(id)) { if (handled) peer.MarkHandled(id); else peer.AddUnhandled(id); }
            }
        }

        return peer;
    }
}
