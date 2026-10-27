using System.Globalization;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotLXMF;

public sealed class LxmPropagationOptions
{
    public string? Name { get; init; }
    public long TransferLimit { get; init; } = 256_000;
    public long SyncLimit { get; init; } = 10_240_000;
    public long StorageLimit { get; init; } = 500_000_000;
    public int StampCost { get; init; } = 16;
    public int StampFlexibility { get; init; } = 3;
    public int PeeringCost { get; init; } = 18;
    public bool RequireAuthentication { get; init; }
    public IReadOnlyCollection<byte[]> AllowedIdentities { get; init; } = [];
    public bool FromStaticOnly { get; init; }
    public IReadOnlyCollection<byte[]> StaticPeers { get; init; } = [];
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxPeers { get; init; } = 20;
    public bool AutoPeer { get; init; } = true;
    public int AutoPeerMaxDepth { get; init; } = 4;
    public int MaxPeeringCost { get; init; } = 26;
    public LxmPeerSyncStrategy SyncStrategy { get; init; } = LxmPeerSyncStrategy.Persistent;
    public IReadOnlyCollection<byte[]> ControlAllowedIdentities { get; init; } = [];
    public IReadOnlyCollection<byte[]> PrioritisedDestinations { get; init; } = [];
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public int MaxInboundSyncs { get; init; } = 3;
    public bool SequentialValidation { get; init; } = true;
    public bool StaticPeerSequential { get; init; }
}

/// <summary>Encrypted propagation storage, authenticated client retrieval and explicit peer synchronization.</summary>
public sealed partial class LxmPropagationNode : IAsyncDisposable
{
    private sealed record Entry(byte[] Id, byte[] Destination, string Path, long Size, double Received, int StampValue);
    private readonly TransportRouter _router;
    private readonly Identity _identity;
    private readonly LocalDestination _local;
    private readonly LxmPropagationOptions _options;
    private readonly string _directory;
    private readonly Func<byte[], bool>? _localDelivery;
    private readonly long _deliveryLimit;
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly HashSet<string> _validatedLinks = [];
    private readonly HashSet<string> _transferringLinks = [];
    private readonly HashSet<string> _validatingLinks = [];
    private readonly Dictionary<string, double> _throttledPeers = [];
    private readonly HashSet<Link> _links = [];
    private readonly Dictionary<string, LxmPeer> _peers = [];
    private readonly HashSet<string> _prioritised = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly FileStream _storeLease;
    private long _storedBytes;
    private bool _disposed;

    public LxmPropagationNode(TransportRouter router, Identity identity, string storagePath, LxmPropagationOptions? options = null,
        Func<byte[], bool>? localDelivery = null, long deliveryLimit = 1_000_000)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        _options = options ?? new LxmPropagationOptions();
        _started = Now;
        _deliveryLimit = deliveryLimit;
        if (_options.StampCost > 256 || _options.StampFlexibility < 0 || _options.PeeringCost is < 0 or > 256 ||
            _options.TransferLimit <= 0 || _options.StorageLimit <= 0 ||
            _options.RequestTimeout <= TimeSpan.Zero || _options.MaxPeers < 0 || _options.AutoPeerMaxDepth < 0 ||
            _options.MaxPeeringCost is < 0 or > 256 || _options.MaxInboundSyncs < 0 || deliveryLimit <= 0 ||
            _options.SyncStrategy is not (LxmPeerSyncStrategy.Lazy or LxmPeerSyncStrategy.Persistent) ||
            _options.StaticPeers.Select(Convert.ToHexStringLower).Distinct().Count() > _options.MaxPeers)
            throw new ArgumentException("Invalid propagation limits or costs.", nameof(options));
        if (_options.AllowedIdentities.Concat(_options.StaticPeers).Concat(_options.ControlAllowedIdentities)
            .Concat(_options.PrioritisedDestinations).Any(hash => hash.Length != 16))
            throw new ArgumentException("Identity and destination hashes must be 16 bytes.", nameof(options));
        InitializePolicy();
        foreach (var destination in _options.PrioritisedDestinations) _prioritised.Add(Convert.ToHexStringLower(destination));
        _localDelivery = localDelivery;
        _directory = Path.Combine(storagePath, "lxmf", "messagestore");
        Directory.CreateDirectory(_directory);
        _storeLease = new FileStream(_directory + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            LoadStore();
            LoadPeers();
            LoadNodeStats();
            if (_peers.Keys.Concat(_options.StaticPeers.Select(Convert.ToHexStringLower)).Distinct().Count() > _options.MaxPeers)
                throw new InvalidDataException("Persisted and static peers exceed MaxPeers.");
        }
        catch { _storeLease.Dispose(); throw; }
        _local = new LocalDestination(identity, Destination.Create(DestinationType.Single, LxmRouter.AppName, ["propagation"], identity))
        {
            AcceptLinks = true,
            ProofStrategy = ProofStrategy.App,
            ProofRequested = ProvePropagation,
            LinkEstablished = ConfigureLink,
        };
        _local.RegisterRequestHandler("/get", GetMessages, RequestPolicy.All, useApplicationValues: true);
        _local.RegisterRequestHandler("/offer", Offer, RequestPolicy.All, useApplicationValues: true);
        _local.SetDefaultAppDataProvider(GetAnnounceAppData);
        router.RegisterLocalDestination(_local);
        InitializeMaintenance();
    }

    public byte[] DestinationHash => _local.Destination.Hash;
    public int MessageCount { get { lock (_gate) return _entries.Count; } }
    public long StoredBytes { get { lock (_gate) return _storedBytes; } }
    public Action<string>? Log { get; set; }
    private void Report(string message)
    {
        if (Log is not { } callbacks) return;
        foreach (Action<string> callback in callbacks.GetInvocationList())
        {
            try { callback(message); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("LXMF propagation log observer failed: {0}", ex.Message); }
        }
    }
    public IReadOnlyList<LxmPeer> Peers { get { lock (_gate) return _peers.Values.ToArray(); } }
    private double Now => _options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
    private long SyncLimit => Math.Max(_options.SyncLimit, _options.TransferLimit);

    public static double CalculateStorageWeight(long size, double received, double now, bool prioritised) =>
        (prioritised ? 0.1 : 1) * Math.Max(1, (now - received) / (86400 * 4)) * size;

    public double GetWeight(ReadOnlySpan<byte> transientId)
    {
        lock (_gate)
        {
            var entry = _entries[Convert.ToHexStringLower(transientId)];
            return CalculateStorageWeight(entry.Size, entry.Received, Now, _prioritised.Contains(Convert.ToHexStringLower(entry.Destination)));
        }
    }

    public long? GetSize(ReadOnlySpan<byte> transientId)
    {
        lock (_gate) return _entries.TryGetValue(Convert.ToHexStringLower(transientId), out var entry) ? entry.Size : null;
    }

    public int? GetStampValue(ReadOnlySpan<byte> transientId)
    {
        lock (_gate) return _entries.TryGetValue(Convert.ToHexStringLower(transientId), out var entry) ? entry.StampValue : null;
    }

    public byte[] GetAnnounceAppData()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var metadata = new Dictionary<object, object?> { [0L] = "1.2.1", [0xfeL] = "DotLXMF" };
        if (_options.Name is { } name) metadata[1L] = System.Text.Encoding.UTF8.GetBytes(name);
        var announce = new LxmfPropagationAnnounce((long)Now, Enabled && !_options.FromStaticOnly,
            _options.TransferLimit / 1000.0, SyncLimit / 1000.0, StampCost, _options.StampFlexibility,
            _options.PeeringCost, metadata);
        return announce.Pack();
    }

    public ValueTask AnnounceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _router.AnnounceAsync(_local, cancellationToken: cancellationToken);
    }

    private bool ProvePropagation(Packet packet)
    {
        var link = _router.Links.Find(packet.DestinationHash.Span);
        if (link is null || link.Decrypt(packet.Payload.Span) is not { } plain) return false;
        var valid = ProcessEnvelope(plain, link, resource: false);
        if (!valid)
        {
            link.Send(MsgPack.Pack(new object?[] { 0xf5L }), createReceipt: false);
            link.Teardown();
        }

        return valid;
    }

    private void ConfigureLink(Link link)
    {
        lock (_gate)
        {
            if (_disposed) { link.Teardown(); return; }
            _links.Add(link);
        }

        link.ResourceStrategy = ResourceStrategy.AcceptApp;
        link.ResourceAdvertised = advertisement =>
        {
            lock (_gate)
            {
                if (_disposed || advertisement.DataSize > SyncLimit || !StaticPeerAllowed(link.RemoteIdentity) ||
                    !BypassSequential(link.RemoteIdentity) && _options.MaxInboundSyncs > 0 &&
                    _transferringLinks.Count >= _options.MaxInboundSyncs) return false;
                _transferringLinks.Add(link.LinkIdHex);
                return true;
            }
        };
        link.ResourceReceived = resource =>
        {
            try
            {
                if (resource.Status != ResourceStatus.Complete || resource.Data is not { } bytes) return;
                lock (_gate) _validatingLinks.Add(link.LinkIdHex);
                if (!ProcessEnvelope(bytes, link, resource: true, out var invalidStamps))
                {
                    if (invalidStamps && link.RemoteIdentity is { } remote)
                    {
                        lock (_gate)
                        {
                            foreach (var key in _throttledPeers.Where(entry => entry.Value <= Now).Select(entry => entry.Key).ToArray())
                                _throttledPeers.Remove(key);
                            if (_throttledPeers.Count < 65_536) _throttledPeers[Convert.ToHexStringLower(PropagationHash(remote))] = Now + 180;
                        }
                    }
                    link.Teardown();
                }
            }
            finally
            {
                lock (_gate) { _transferringLinks.Remove(link.LinkIdHex); _validatingLinks.Remove(link.LinkIdHex); }
            }
        };
        link.Closed += closed =>
        {
            lock (_gate)
            {
                _links.Remove(closed);
                _validatedLinks.Remove(closed.LinkIdHex);
                _transferringLinks.Remove(closed.LinkIdHex);
                _validatingLinks.Remove(closed.LinkIdHex);
            }
        };
    }

    private bool BypassSequential(Identity? identity) => !_options.StaticPeerSequential && identity is not null &&
        _options.StaticPeers.Any(hash => hash.AsSpan().SequenceEqual(PropagationHash(identity)));

    private bool StaticPeerAllowed(Identity? identity) => !_options.FromStaticOnly || identity is not null &&
        _options.StaticPeers.Any(hash => hash.AsSpan().SequenceEqual(PropagationHash(identity)));

    private bool ProcessEnvelope(byte[] bytes, Link link, bool resource) => ProcessEnvelope(bytes, link, resource, out _);

    private bool ProcessEnvelope(byte[] bytes, Link link, bool resource, out bool invalidStamps)
    {
        invalidStamps = false;
        try
        {
            if (bytes.LongLength > SyncLimit || !StaticPeerAllowed(link.RemoteIdentity) ||
                MsgPack.UnpackApplicationValue(bytes) is not object?[] { Length: 2 } envelope ||
                envelope[1] is not object?[] messages) return false;
            _ = MsgPack.ToDouble(envelope[0]);
            lock (_gate)
                if (_disposed || resource && messages.Length > 1 && !_validatedLinks.Contains(link.LinkIdHex)) return false;
            if (messages.Length == 1 && bytes.LongLength > _options.TransferLimit) return false;
            var valid = true;
            foreach (var value in messages)
            {
                if (value is not byte[] stamped || !ValidateStamped(stamped, out var data, out var stampValue))
                {
                    valid = false;
                    invalidStamps = true;
                    continue;
                }

                if (_localDelivery?.Invoke(data) != true) Store(stamped, stampValue);
                lock (_gate)
                {
                    if (resource && link.RemoteIdentity is { } remote &&
                        _peers.TryGetValue(Convert.ToHexStringLower(PropagationHash(remote)), out var peer))
                    {
                        peer.Incoming++;
                        peer.RxBytes += data.Length;
                        peer.MarkHandled(Identity.FullHash(data));
                    }
                    else
                    {
                        if (resource && link.RemoteIdentity is not null) { _unpeeredIncoming++; _unpeeredRxBytes += data.Length; }
                        else _clientMessagesReceived++;
                    }
                }
            }

            return valid;
        }
        catch (Exception ex)
        {
            Report("LXMF propagation transfer rejected: " + ex.Message);
            return false;
        }
    }

    private bool ValidateStamped(byte[] stamped, out byte[] data, out int value)
    {
        data = [];
        value = 0;
        if (stamped.Length <= LxMessage.Overhead + LxStamper.StampSize) return false;
        data = stamped[..^LxStamper.StampSize];
        var id = Identity.FullHash(data);
        var block = LxStamper.Workblock(id, LxStamper.WorkblockExpandRoundsPn);
        var stamp = stamped.AsSpan(stamped.Length - LxStamper.StampSize);
        if (!LxStamper.IsValid(stamp, Math.Max(0, StampCost - _options.StampFlexibility), block)) return false;
        value = LxStamper.Value(block, stamp);
        return true;
    }

    private void Store(byte[] stamped, int stampValue)
    {
        var data = stamped.AsSpan(0, stamped.Length - LxStamper.StampSize);
        var id = Identity.FullHash(data);
        var key = Convert.ToHexStringLower(id);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_enabled) return;
            CleanStore();
            if (_entries.ContainsKey(key)) return;
            if (stamped.LongLength > StorageLimit) return;
            var received = Now;
            var pending = new Entry(id, data[..16].ToArray(), "", stamped.LongLength, received, stampValue);
            var excess = _storedBytes + stamped.LongLength - StorageLimit;
            var countExcess = _entries.Count + 1 - 65_536;
            if (excess > 0 || countExcess > 0)
            {
                var victims = new List<Entry>();
                foreach (var entry in _entries.Values.Append(pending).OrderByDescending(entry =>
                    CalculateStorageWeight(entry.Size, entry.Received, received, _prioritised.Contains(Convert.ToHexStringLower(entry.Destination)))))
                {
                    if (excess <= 0 && countExcess <= 0) break;
                    victims.Add(entry);
                    excess -= entry.Size;
                    countExcess--;
                }
                if (victims.Contains(pending)) return;
                foreach (var entry in victims) Remove(entry);
            }
            var path = Path.Combine(_directory, key + "_" + received.ToString("R", CultureInfo.InvariantCulture) + "_" + stampValue);
            var output = path + ".out";
            try
            {
                using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(stamped);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(output, path);
                _entries.Add(key, new Entry(id, data[..16].ToArray(), path, stamped.LongLength, received, stampValue));
                _storedBytes += stamped.LongLength;
                foreach (var peer in _peers.Values) peer.AddUnhandled(id);
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }
    }

    private object GetMessages(RequestContext request)
    {
        if (request.RemoteIdentity is not { } identity) return 0xf0L;
        if (!IdentityAllowed(identity)) return 0xf1L;
        if (request.Data is not object?[] { Length: >= 2 } data) return 0xf4L;
        var destination = LxMessage.DeliveryDestinationHash(identity);
        lock (_gate)
        {
            if (_disposed) return 0xf6L;
            CleanStore();
            var available = _entries.Values.Where(entry => entry.Destination.AsSpan().SequenceEqual(destination)).ToArray();
            if (data[0] is null && data[1] is null) return available.OrderBy(entry => entry.Size).Select(entry => (object?)entry.Id).ToArray();
            if (data[1] is object?[] haves)
            {
                foreach (var have in haves.OfType<byte[]>())
                    if (_entries.TryGetValue(Convert.ToHexStringLower(have), out var entry) && entry.Destination.AsSpan().SequenceEqual(destination))
                        Remove(entry);
            }

            var response = new List<object?>();
            var limit = data.Length >= 3 ? Math.Min(SyncLimit, MsgPack.ToDouble(data[2]) * 1000) : SyncLimit;
            if (!double.IsFinite(limit) || limit < 0) return 0xf4L;
            long total = 24;
            if (data[0] is object?[] wants)
                foreach (var wanted in wants.OfType<byte[]>())
                    if (_entries.TryGetValue(Convert.ToHexStringLower(wanted), out var entry) && entry.Destination.AsSpan().SequenceEqual(destination) &&
                        entry.Size + 16 <= limit - total)
                    {
                        response.Add(File.ReadAllBytes(entry.Path)[..^LxStamper.StampSize]);
                        total += entry.Size + 16;
                        _clientMessagesServed++;
                    }

            return response.ToArray();
        }
    }

    private object Offer(RequestContext request)
    {
        if (request.RemoteIdentity is not { } identity) return 0xf0L;
        lock (_gate)
        {
            if (!BypassSequential(identity) &&
                (_options.SequentialValidation && _validatingLinks.Count > 0 ||
                 _options.MaxInboundSyncs > 0 && _transferringLinks.Count >= _options.MaxInboundSyncs)) return 0xf6L;
            var remote = Convert.ToHexStringLower(PropagationHash(identity));
            if (_throttledPeers.TryGetValue(remote, out var expires))
            {
                if (Now < expires) return 0xf6L;
                _throttledPeers.Remove(remote);
            }
        }
        if (!StaticPeerAllowed(identity)) return 0xf1L;
        if (request.Data is not object?[] { Length: >= 2 } offer ||
            offer[0] is not byte[] { Length: LxStamper.StampSize } key || offer[1] is not object?[] ids ||
            ids.Length > 65_536 || ids.Any(id => id is not byte[] { Length: 32 })) return 0xf4L;
        var peeringId = _identity.Hash.Concat(identity.Hash).ToArray();
        if (!LxStamper.IsValid(key, _options.PeeringCost, LxStamper.Workblock(peeringId, LxStamper.WorkblockExpandRoundsPeering))) return 0xf3L;
        lock (_gate)
        {
            if (_disposed) return 0xf6L;
            _validatedLinks.Add(Convert.ToHexStringLower(request.LinkId));
            var wanted = ids.Cast<byte[]>().Where(id => !_entries.ContainsKey(Convert.ToHexStringLower(id))).Cast<object?>().ToArray();
            return wanted.Length == 0 ? false : wanted.Length == ids.Length ? true : wanted;
        }
    }

    public void SetPeerSyncStrategy(ReadOnlySpan<byte> destinationHash, LxmPeerSyncStrategy strategy)
    {
        if (destinationHash.Length != 16) throw new ArgumentException("Peer destination must be 16 bytes.", nameof(destinationHash));
        if (strategy is not (LxmPeerSyncStrategy.Lazy or LxmPeerSyncStrategy.Persistent))
            throw new ArgumentOutOfRangeException(nameof(strategy));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var name = Convert.ToHexStringLower(destinationHash);
            if (!_peers.TryGetValue(name, out var peer))
            {
                if (_peers.Count >= _options.MaxPeers) throw new InvalidOperationException("Propagation peer capacity reached.");
                peer = new LxmPeer(destinationHash.ToArray()) { LastHeard = Now };
                foreach (var entry in _entries.Values) peer.AddUnhandled(entry.Id);
                _peers.Add(name, peer);
            }
            peer.SyncStrategy = (int)strategy;
            SavePeers();
        }
    }

    public async Task<int> SynchronizePeerAsync(byte[] destinationHash, CancellationToken cancellationToken = default)
    {
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        if (destinationHash.Length != 16) throw new ArgumentException("Peer destination must be 16 bytes.", nameof(destinationHash));
        await _sync.WaitAsync(owned.Token).ConfigureAwait(false);
        Link? link = null;
        LxmPeer? peer = null;
        var transferred = 0;
        try
        {
            lock (_gate)
            {
                var name = Convert.ToHexStringLower(destinationHash);
                if (!_peers.TryGetValue(name, out peer))
                {
                    if (_peers.Count >= _options.MaxPeers) throw new InvalidOperationException("Propagation peer capacity reached.");
                    peer = new LxmPeer(destinationHash) { LastHeard = Now, SyncStrategy = (int)_options.SyncStrategy };
                    foreach (var entry in _entries.Values) peer.AddUnhandled(entry.Id);
                    _peers.Add(name, peer);
                }
                peer.LastSyncAttempt = Now;
            }
            while (true)
            {
                owned.Token.ThrowIfCancellationRequested();
                peer.State = 1;
                link = await LxmPropagationTransport.ConnectAsync(_router, destinationHash, _options.RequestTimeout, owned.Token).ConfigureAwait(false);
                peer.State = 2;
                peer.Alive = true;
                peer.LastHeard = Now;
                peer.SyncBackoff = 0;
                peer.NextSyncAttempt = 0;
                peer.LinkEstablishmentRate = link.EstablishmentRate ?? 0;
                Report("Propagation peer link established.");
                link.Identify(_identity);
                var remote = _router.Engine.Known.Recall(destinationHash)!;
                if (!LxmfPropagationAnnounce.TryParse(_router.Engine.Known.RecallAppData(destinationHash), out var config))
                    throw new InvalidOperationException("Peer has not announced valid propagation configuration.");
                if (config!.PeeringCost > _options.MaxPeeringCost)
                {
                    lock (_gate) _peers.Remove(Convert.ToHexStringLower(destinationHash));
                    throw new InvalidOperationException("Peer peering cost exceeds the configured maximum.");
                }
                var peeringId = remote.Hash.Concat(_identity.Hash).ToArray();
                peer.TransferLimitKilobytes = config!.TransferLimitKilobytes;
                peer.SyncLimitKilobytes = config.SyncLimitKilobytes;
                peer.StampCost = config.StampCost;
                peer.StampFlexibility = config.StampFlexibility;
                peer.PeeringCost = config.PeeringCost;
                peer.Metadata = config.Metadata;
                peer.PeeringTimebase = config.Timebase;
                if (peer.PeeringKey is null || peer.PeeringKeyValue < config.PeeringCost)
                {
                    Report($"Generating propagation peering key at cost {config.PeeringCost}.");
                    peer.PeeringKey = await LxStamper.GenerateAsync(peeringId, config.PeeringCost, owned.Token,
                        LxStamper.WorkblockExpandRoundsPeering).ConfigureAwait(false);
                    peer.PeeringKeyValue = LxStamper.Value(LxStamper.Workblock(peeringId, LxStamper.WorkblockExpandRoundsPeering), peer.PeeringKey);
                }
                Entry[] entries;
                lock (_gate)
                {
                    var ids = peer.PrepareOffer(id =>
                    {
                        if (!_entries.TryGetValue(Convert.ToHexStringLower(id), out var entry)) return null;
                        return new LxmPeerOfferEntry(entry.Id, CalculateStorageWeight(entry.Size, entry.Received, Now,
                            _prioritised.Contains(Convert.ToHexStringLower(entry.Destination))), entry.Size, entry.StampValue);
                    }, Math.Max(0, config.StampCost - config.StampFlexibility), config.TransferLimitKilobytes * 1000,
                        Math.Min(SyncLimit, config.SyncLimitKilobytes * 1000));
                    entries = ids.Select(id => _entries[Convert.ToHexStringLower(id)]).ToArray();
                }
                if (entries.Length == 0) return transferred;
                peer.State = 3;
                var response = await LxmPropagationTransport.RequestAsync(link, "/offer",
                    new object?[] { peer.PeeringKey, entries.Select(entry => (object?)entry.Id).ToArray() }, _options.RequestTimeout, SyncLimit, owned.Token).ConfigureAwait(false);
                peer.State = 4;
                if (response is long error)
                {
                    if (error == 0xf0)
                    {
                        link.Identify(_identity);
                        response = await LxmPropagationTransport.RequestAsync(link, "/offer",
                            new object?[] { peer.PeeringKey, entries.Select(entry => (object?)entry.Id).ToArray() },
                            _options.RequestTimeout, SyncLimit, owned.Token).ConfigureAwait(false);
                    }
                    else if (error == 0xf1) Unpeer(destinationHash);
                    else if (error == 0xf6)
                    {
                        peer.NextSyncAttempt = Now + 180;
                        return transferred;
                    }
                }
                Report($"Propagation peer offer response received for {entries.Length} messages.");
                byte[][] selected;
                lock (_gate) selected = peer.SelectWanted(entries.Select(entry => entry.Id).ToArray(), response);
                if (selected.Length == 0)
                {
                    lock (_gate) peer.Offered += entries.Length;
                    return transferred;
                }
                var payloads = new List<object?>();
                var offeredEntries = entries.ToDictionary(entry => Convert.ToHexStringLower(entry.Id));
                foreach (var id in selected)
                {
                    var entry = offeredEntries[Convert.ToHexStringLower(id)];
                    payloads.Add(File.ReadAllBytes(entry.Path));
                }

                var envelope = MsgPack.Pack(new object?[] { Now, payloads.ToArray() });
                var transferStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                peer.State = 5;
                var resource = await LxmPropagationTransport.SendResourceAsync(link, envelope, _options.RequestTimeout, owned.Token).ConfigureAwait(false);
                Report($"Propagation peer transfer completed with {payloads.Count} messages.");
                lock (_gate)
                {
                    peer.RecordCompletedTransfer(selected, entries.Length, resource.TotalSize, resource.Size,
                        System.Diagnostics.Stopwatch.GetElapsedTime(transferStarted).TotalSeconds, Now);
                }
                transferred += payloads.Count;
                link.Teardown();
                link = null;
                lock (_gate)
                    if (!peer.ContinueAfterTransfer) return transferred;
            }
        }
        catch (OperationCanceledException) when (owned.IsCancellationRequested) { throw; }
        catch
        {
            if (peer is not null)
            {
                peer.Alive = false;
                peer.SyncBackoff += 720;
                peer.NextSyncAttempt = Now + peer.SyncBackoff;
            }
            throw;
        }
        finally
        {
            link?.Teardown();
            if (peer is not null) peer.State = 0;
            try { lock (_gate) SavePeers(); }
            finally { _sync.Release(); }
        }
    }

    private void LoadStore()
    {
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            var parts = Path.GetFileName(path).Split('_');
            if (parts.Length != 3 || parts[0].Length != 64 || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var received) ||
                !double.IsFinite(received) || !int.TryParse(parts[2], out var value)) continue;
            if (Now > received + TimeSpan.FromDays(30).TotalSeconds) { File.Delete(path); continue; }
            var size = new FileInfo(path).Length;
            if (size <= LxMessage.Overhead + LxStamper.StampSize || size > SyncLimit || _entries.Count >= 65_536)
                throw new InvalidDataException("Invalid or oversized propagation store entry.");
            var stamped = File.ReadAllBytes(path);
            var id = Identity.FullHash(stamped.AsSpan(0, stamped.Length - LxStamper.StampSize));
            if (!string.Equals(Convert.ToHexStringLower(id), parts[0], StringComparison.Ordinal)) throw new InvalidDataException("Propagation store hash mismatch.");
            if (!_entries.TryAdd(parts[0], new Entry(id, stamped[..16], path, size, received, value))) throw new InvalidDataException("Duplicate propagation store entry.");
            _storedBytes += size;
        }
        CleanStore();
    }

    private void CleanStore()
    {
        var now = Now;
        foreach (var entry in _entries.Values.Where(entry => now > entry.Received + TimeSpan.FromDays(30).TotalSeconds).ToArray()) Remove(entry);
        foreach (var entry in _entries.Values.OrderByDescending(entry =>
            CalculateStorageWeight(entry.Size, entry.Received, now, _prioritised.Contains(Convert.ToHexStringLower(entry.Destination)))).ToArray())
        {
            if (_storedBytes <= StorageLimit) break;
            Remove(entry);
        }
    }

    private void Remove(Entry entry)
    {
        File.Delete(entry.Path);
        _entries.Remove(Convert.ToHexStringLower(entry.Id));
        _storedBytes -= entry.Size;
        foreach (var peer in _peers.Values) peer.Forget(entry.Id);
    }

    private void LoadPeers()
    {
        var path = Path.Combine(Path.GetDirectoryName(_directory)!, "peers");
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Propagation peer file is oversized.");
        if (MsgPack.UnpackApplicationValue(File.ReadAllBytes(path)) is not object?[] entries || entries.Length > _options.MaxPeers)
            throw new InvalidDataException("Invalid propagation peer list.");
        foreach (var value in entries)
        {
            if (value is not byte[] bytes) throw new InvalidDataException("Invalid serialized propagation peer.");
            var peer = LxmPeer.FromBytes(bytes, id => _entries.ContainsKey(Convert.ToHexStringLower(id)));
            _peers.Add(Convert.ToHexStringLower(peer.DestinationHash), peer);
        }
    }

    private void SavePeers()
    {
        var path = Path.Combine(Path.GetDirectoryName(_directory)!, "peers");
        var output = path + "." + Guid.NewGuid().ToString("N") + ".out";
        try
        {
            File.WriteAllBytes(output, MsgPack.Pack(_peers.Values.Select(peer => (object?)peer.ToBytes()).ToArray()));
            File.Move(output, path, overwrite: true);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    private static byte[] PropagationHash(Identity identity) => Destination.ComputeHash(identity.Hash, LxmRouter.AppName, ["propagation"]);

    public async ValueTask DisposeAsync()
    {
        Link[] links;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            links = _links.ToArray();
            _links.Clear();
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await StopMaintenanceAsync().ConfigureAwait(false);
            foreach (var link in links) link.Teardown();
            await _sync.WaitAsync().ConfigureAwait(false);
            _sync.Release();
            lock (_gate) { SavePeers(); SaveNodeStats(); }
        }
        finally
        {
            _router.Engine.DeregisterHandler(_announceHandler);
            _router.DeregisterLocalDestination(_local);
            _router.DeregisterLocalDestination(_control);
            _storeLease.Dispose();
            _cts.Dispose();
        }
    }
}

internal static class LxmPropagationTransport
{
    internal static async Task<Link> ConnectAsync(TransportRouter router, byte[] destination, TimeSpan timeout, CancellationToken ct)
    {
        if (destination.Length != 16) throw new ArgumentException("Destination hash must be 16 bytes.", nameof(destination));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        if (!router.HasPath(destination)) await router.RequestPathAsync(destination, cancellationToken: deadline.Token).ConfigureAwait(false);
        while (!router.HasPath(destination) || router.Engine.Known.Recall(destination) is null)
            await Task.Delay(50, deadline.Token).ConfigureAwait(false);
        var connected = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = router.Links.Connect(router.Engine.Known.Recall(destination)!, destination,
            established => connected.TrySetResult(established), _ => connected.TrySetException(new IOException("Propagation link closed.")));
        try { return await connected.Task.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch { link.Teardown(); throw; }
    }

    internal static async Task<object?> RequestAsync(Link link, string path, object? data, TimeSpan timeout, long maxResponse, CancellationToken ct,
        Action<RequestReceipt>? progress = null)
    {
        var receipt = link.Request(path, data, timeout: timeout.TotalSeconds, useApplicationValues: true,
            maxResponseSize: maxResponse, cancellationToken: ct, progressCallback: progress)
            ?? throw new IOException("Propagation request could not be sent.");
        try
        {
            var response = await receipt.ResponseTask.WaitAsync(timeout, ct).ConfigureAwait(false);
            progress?.Invoke(receipt);
            return response;
        }
        catch { receipt.Cancel(); throw; }
    }

    internal static async Task<Resource> SendResourceAsync(Link link, byte[] data, TimeSpan timeout, CancellationToken ct)
    {
        var done = new TaskCompletionSource<Resource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = link.SendResource(data, null, concluded: completed =>
        {
            if (completed.Status == ResourceStatus.Complete) done.TrySetResult(completed);
            else done.TrySetException(new IOException("Propagation resource failed: " + completed.Status));
        });
        using var registration = ct.Register(() => resource.Cancel());
        try { return await done.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch { resource.Cancel(); throw; }
    }
}
