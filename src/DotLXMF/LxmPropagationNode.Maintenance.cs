using System.Threading.Channels;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotLXMF;

public sealed partial class LxmPropagationNode
{
    private LocalDestination _control = null!;
    private IAnnounceHandler _announceHandler = null!;
    private Channel<byte[]> _syncQueue = null!;
    private Task _maintenance = Task.CompletedTask;
    private Task _scheduledSync = Task.CompletedTask;
    private readonly HashSet<string> _queuedPeers = [];
    private readonly double _started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    private long _clientMessagesReceived;
    private long _clientMessagesServed;
    private long _unpeeredIncoming;
    private long _unpeeredRxBytes;
    private int _maintenanceTicks;

    public byte[] ControlDestinationHash => _control.Destination.Hash;

    private void InitializeMaintenance()
    {
        _control = new LocalDestination(_identity,
            Destination.Create(DestinationType.Single, LxmRouter.AppName, ["propagation", "control"], _identity)) { AcceptLinks = true };
        _control.RegisterRequestHandler("/pn/get/stats", request => ControlAllowed(request) ? CompileStats() : ControlError(request),
            RequestPolicy.All, useApplicationValues: true);
        _control.RegisterRequestHandler("/pn/peer/sync", request => ControlPeer(request, sync: true),
            RequestPolicy.All, useApplicationValues: true);
        _control.RegisterRequestHandler("/pn/peer/unpeer", request => ControlPeer(request, sync: false),
            RequestPolicy.All, useApplicationValues: true);
        _router.RegisterLocalDestination(_control);
        _syncQueue = System.Threading.Channels.Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_options.MaxPeers)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        foreach (var destination in _options.StaticPeers)
        {
            var name = Convert.ToHexStringLower(destination);
            if (!_peers.ContainsKey(name))
            {
                if (_peers.Count >= _options.MaxPeers) throw new ArgumentException("Static peers exceed MaxPeers.");
                var peer = new LxmPeer(destination);
                foreach (var entry in _entries.Values) peer.AddUnhandled(entry.Id);
                _peers.Add(name, peer);
            }
        }

        _announceHandler = new CallbackAnnounceHandler("lxmf.propagation", ReceiveNodeAnnounce, receivePathResponses: true);
        _router.Engine.RegisterHandler(_announceHandler);
        _maintenance = MaintainPeersAsync(_cts.Token);
        _scheduledSync = RunScheduledSyncAsync(_cts.Token);
    }

    private bool ControlAllowed(RequestContext request) => request.RemoteIdentity is { } identity &&
        (identity.Hash.AsSpan().SequenceEqual(_identity.Hash) ||
            _options.ControlAllowedIdentities.Any(hash => hash.AsSpan().SequenceEqual(identity.Hash)));

    private static long ControlError(RequestContext request) => request.RemoteIdentity is null ? 0xf0L : 0xf1L;

    private object ControlPeer(RequestContext request, bool sync)
    {
        if (!ControlAllowed(request)) return ControlError(request);
        if (request.Data is not byte[] { Length: 16 } destination) return 0xf4L;
        lock (_gate)
        {
            if (!_peers.ContainsKey(Convert.ToHexStringLower(destination))) return 0xfdL;
            if (sync) return QueuePeer(destination);
            return Unpeer(destination);
        }
    }

    public bool Unpeer(ReadOnlySpan<byte> destination, long? timebase = null)
    {
        lock (_gate)
        {
            var key = Convert.ToHexStringLower(destination);
            if (!_peers.TryGetValue(key, out var peer) || timebase is { } timestamp && timestamp < peer.PeeringTimebase) return false;
            _peers.Remove(key);
            SavePeers();
            return true;
        }
    }

    private void ReceiveNodeAnnounce(AnnounceReceived received)
    {
        using var identity = received.Identity;
        if (!LxmfPropagationAnnounce.TryParse(received.AppData, out var config)) return;
        lock (_gate)
        {
            if (_disposed || received.DestinationHash.AsSpan().SequenceEqual(DestinationHash)) return;
            var key = Convert.ToHexStringLower(received.DestinationHash);
            var isStatic = _options.StaticPeers.Any(hash => hash.AsSpan().SequenceEqual(received.DestinationHash));
            _peers.TryGetValue(key, out var peer);
            if (isStatic && received.IsPathResponse && peer?.LastHeard > 0) return;
            if (!isStatic && (!_options.AutoPeer || received.IsPathResponse)) return;
            if (config!.PeeringCost > _options.MaxPeeringCost ||
                !isStatic && (!config.Enabled || received.Hops > _options.AutoPeerMaxDepth))
            {
                Unpeer(received.DestinationHash, config.Timebase);
                return;
            }

            if (peer is null)
            {
                if (_peers.Count >= _options.MaxPeers) return;
                peer = new LxmPeer(received.DestinationHash);
                foreach (var entry in _entries.Values) peer.AddUnhandled(entry.Id);
                _peers.Add(key, peer);
            }
            else if (config.Timebase <= peer.PeeringTimebase) return;
            peer.Alive = true;
            peer.LastHeard = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            peer.PeeringTimebase = config.Timebase;
            peer.TransferLimitKilobytes = config.TransferLimitKilobytes;
            peer.SyncLimitKilobytes = config.SyncLimitKilobytes;
            peer.StampCost = config.StampCost;
            peer.StampFlexibility = config.StampFlexibility;
            peer.PeeringCost = config.PeeringCost;
            peer.Metadata = config.Metadata;
            peer.SyncBackoff = 0;
            peer.NextSyncAttempt = 0;
        }
    }

    private bool QueuePeer(byte[] destination)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            var key = Convert.ToHexStringLower(destination);
            if (!_queuedPeers.Add(key)) return true;
            if (_syncQueue.Writer.TryWrite(destination.ToArray())) return true;
            _queuedPeers.Remove(key);
            return false;
        }
    }

    private async Task MaintainPeersAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(24));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (_disposed) break;
                    if (++_maintenanceTicks % 56 == 0) RotatePeers();
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    foreach (var peer in _peers.Values.ToArray())
                        if (now > peer.LastHeard + TimeSpan.FromDays(14).TotalSeconds &&
                            !_options.StaticPeers.Any(hash => hash.AsSpan().SequenceEqual(peer.DestinationHash)))
                            _peers.Remove(Convert.ToHexStringLower(peer.DestinationHash));
                    var waiting = _peers.Values.Where(peer => peer.UnhandledCount > 0 && !_queuedPeers.Contains(Convert.ToHexStringLower(peer.DestinationHash))).ToArray();
                    var alive = waiting.Where(peer => peer.Alive).OrderByDescending(peer => peer.SyncTransferRate).Take(2).ToArray();
                    var pool = alive.Length > 0
                        ? alive.Concat(waiting.Where(peer => peer.Alive && peer.SyncTransferRate == 0).Take(alive.Length)).ToArray()
                        : waiting.Where(peer => now >= peer.NextSyncAttempt).ToArray();
                    if (pool.Length > 0) QueuePeer(pool[Random.Shared.Next(pool.Length)].DestinationHash);
                    CleanStore();
                    if (_maintenanceTicks % 20 == 0) { SavePeers(); SaveNodeStats(); }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task RunScheduledSyncAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var destination in _syncQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    lock (_gate)
                        if (!_peers.ContainsKey(Convert.ToHexStringLower(destination))) continue;
                    await SynchronizePeerAsync(destination, ct).ConfigureAwait(false);
                }

                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { Log?.Invoke("Scheduled propagation peer synchronization failed: " + ex.Message); }
                finally { lock (_gate) _queuedPeers.Remove(Convert.ToHexStringLower(destination)); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public int RotatePeers()
    {
        lock (_gate)
        {
            var headroom = Math.Max(1, (int)Math.Floor(_options.MaxPeers * 0.1));
            var required = _peers.Count - (_options.MaxPeers - headroom);
            if (required <= 0 || _peers.Count - required <= 1 ||
                _peers.Values.Count(peer => peer.LastSyncAttempt == 0) >= headroom) return 0;
            var candidates = _peers.Values.ToArray();
            var synced = candidates.Where(peer => peer.UnhandledCount == 0).ToArray();
            if (synced.Length > 0) candidates = synced;
            candidates = candidates.Where(peer => peer.State == 0 &&
                (!peer.Alive || peer.Offered > 0) &&
                !_options.StaticPeers.Any(hash => hash.AsSpan().SequenceEqual(peer.DestinationHash)))
                .OrderBy(peer => peer.AcceptanceRate).Take(required).ToArray();
            var removed = 0;
            foreach (var peer in candidates)
                if (peer.AcceptanceRate < 0.5 && _peers.Remove(Convert.ToHexStringLower(peer.DestinationHash))) removed++;
            if (removed > 0) SavePeers();
            return removed;
        }
    }

    private async Task StopMaintenanceAsync()
    {
        _router.Engine.DeregisterHandler(_announceHandler);
        _syncQueue.Writer.TryComplete();
        await Task.WhenAll(_maintenance, _scheduledSync).ConfigureAwait(false);
    }

    public Dictionary<object, object?> CompileStats()
    {
        lock (_gate)
        {
            var peerStats = new Dictionary<object, object?>();
            foreach (var peer in _peers.Values)
            {
                var isStatic = _options.StaticPeers.Any(hash => hash.AsSpan().SequenceEqual(peer.DestinationHash));
                peerStats[peer.DestinationHash] = new Dictionary<object, object?>
                {
                    ["type"] = isStatic ? "static" : "discovered", ["state"] = (long)peer.State,
                    ["alive"] = peer.Alive, ["name"] = PeerName(peer), ["last_heard"] = (long)peer.LastHeard,
                    ["next_sync_attempt"] = peer.NextSyncAttempt, ["last_sync_attempt"] = peer.LastSyncAttempt,
                    ["sync_backoff"] = peer.SyncBackoff, ["peering_timebase"] = peer.PeeringTimebase,
                    ["ler"] = (long)peer.LinkEstablishmentRate, ["str"] = (long)peer.SyncTransferRate,
                    ["transfer_limit"] = peer.TransferLimitKilobytes, ["sync_limit"] = peer.SyncLimitKilobytes,
                    ["target_stamp_cost"] = peer.StampCost, ["stamp_cost_flexibility"] = peer.StampFlexibility,
                    ["peering_cost"] = peer.PeeringCost, ["peering_key"] = peer.PeeringKeyValue,
                    ["network_distance"] = _router.HopsTo(peer.DestinationHash), ["rx_bytes"] = peer.RxBytes, ["tx_bytes"] = peer.TxBytes,
                    ["acceptance_rate"] = peer.AcceptanceRate,
                    ["messages"] = new Dictionary<object, object?>
                    {
                        ["offered"] = peer.Offered, ["outgoing"] = peer.Outgoing, ["incoming"] = peer.Incoming,
                        ["unhandled"] = (long)peer.UnhandledCount,
                    },
                };
            }

            var staticCount = _peers.Values.Count(peer => _options.StaticPeers.Any(hash => hash.AsSpan().SequenceEqual(peer.DestinationHash)));
            return new Dictionary<object, object?>
            {
                ["identity_hash"] = _identity.Hash, ["destination_hash"] = DestinationHash,
                ["uptime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - _started,
                ["delivery_limit"] = _deliveryLimit / 1000.0, ["propagation_limit"] = _options.TransferLimit / 1000.0,
                ["sync_limit"] = _options.SyncLimit / 1000.0, ["target_stamp_cost"] = (long)_options.StampCost,
                ["stamp_cost_flexibility"] = (long)_options.StampFlexibility, ["peering_cost"] = (long)_options.PeeringCost,
                ["max_peering_cost"] = (long)_options.MaxPeeringCost, ["autopeer_maxdepth"] = (long)_options.AutoPeerMaxDepth,
                ["from_static_only"] = _options.FromStaticOnly,
                ["messagestore"] = new Dictionary<object, object?> { ["count"] = (long)_entries.Count, ["bytes"] = _storedBytes, ["limit"] = _options.StorageLimit },
                ["clients"] = new Dictionary<object, object?>
                {
                    ["client_propagation_messages_received"] = _clientMessagesReceived,
                    ["client_propagation_messages_served"] = _clientMessagesServed,
                },
                ["unpeered_propagation_incoming"] = _unpeeredIncoming, ["unpeered_propagation_rx_bytes"] = _unpeeredRxBytes,
                ["static_peers"] = (long)staticCount, ["discovered_peers"] = (long)(_peers.Count - staticCount),
                ["total_peers"] = (long)_peers.Count, ["max_peers"] = (long)_options.MaxPeers, ["peers"] = peerStats,
            };
        }
    }

    private static string? PeerName(LxmPeer peer) => peer.Metadata?.GetValueOrDefault(1L) switch
    {
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        string value => value,
        _ => null,
    };

    private void LoadNodeStats()
    {
        var path = Path.Combine(Path.GetDirectoryName(_directory)!, "node_stats");
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 4096 ||
            MsgPack.UnpackApplicationValue(File.ReadAllBytes(path)) is not Dictionary<object, object?> map)
            throw new InvalidDataException("Invalid propagation statistics.");
        long Count(string key)
        {
            var value = MsgPack.ToInt64(map.GetValueOrDefault(key) ?? 0L);
            return value >= 0 ? value : throw new InvalidDataException("Invalid propagation counter.");
        }
        _clientMessagesReceived = Count("client_propagation_messages_received");
        _clientMessagesServed = Count("client_propagation_messages_served");
        _unpeeredIncoming = Count("unpeered_propagation_incoming");
        _unpeeredRxBytes = Count("unpeered_propagation_rx_bytes");
    }

    private void SaveNodeStats()
    {
        var path = Path.Combine(Path.GetDirectoryName(_directory)!, "node_stats");
        var output = path + "." + Guid.NewGuid().ToString("N") + ".out";
        try
        {
            File.WriteAllBytes(output, MsgPack.Pack(new Dictionary<object, object?>
            {
                ["client_propagation_messages_received"] = _clientMessagesReceived,
                ["client_propagation_messages_served"] = _clientMessagesServed,
                ["unpeered_propagation_incoming"] = _unpeeredIncoming,
                ["unpeered_propagation_rx_bytes"] = _unpeeredRxBytes,
            }));
            File.Move(output, path, overwrite: true);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }
}
