using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Lxmf;

public sealed class LxmRouterOptions
{
    public int MaxDeliveryAttempts { get; init; } = 5;

    public TimeSpan DeliveryRetryWait { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan PathRequestWait { get; init; } = TimeSpan.FromSeconds(7);

    public int MaxPathlessTries { get; init; } = 1;

    public TimeSpan ProcessingInterval { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Largest incoming direct (Resource) message accepted, in bytes (upstream <c>DELIVERY_LIMIT</c>, 1000 KB).</summary>
    public long DeliveryPerTransferLimit { get; init; } = 1_000_000;

    /// <summary>Stamp cost advertised in announces and required of incoming messages; null for none.</summary>
    public int? InboundStampCost { get; init; }

    /// <summary>Drop incoming messages whose stamp does not meet <see cref="InboundStampCost"/> (otherwise they are delivered with <see cref="ReceivedMessage.StampValid"/> false).</summary>
    public bool EnforceStamps { get; init; } = true;

    /// <summary>Optional override of the opportunistic proof wait.</summary>
    public TimeSpan? ReceiptTimeout { get; init; }
}

/// <summary>A received message together with delivery metadata.</summary>
public sealed record ReceivedMessage(LxMessage Message, bool? StampValid);

/// <summary>
/// A lightweight LXMF router (upstream <c>LXMRouter</c>): one <c>lxmf.delivery</c> destination, opportunistic and link-based outbound delivery with
/// retries and an outbound queue, and a receive callback. Propagation nodes are not used.
/// </summary>
public sealed class LxmRouter : IAsyncDisposable
{
    public const string AppName = "lxmf";
    public const string DeliveryAspect = "delivery";

    private readonly TransportRouter _router;
    private readonly Identity _identity;
    private readonly LxmRouterOptions _options;
    private readonly LocalDestination _local;
    private readonly object _gate = new();
    private readonly List<LxMessage> _outbound = [];
    private readonly Dictionary<string, Link> _links = [];
    private readonly HashSet<string> _identified = [];
    private readonly Dictionary<string, double> _seen = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;
    private int _started;

    public LxmRouter(TransportRouter router, Identity identity, string? displayName = null, LxmRouterOptions? options = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _options = options ?? new LxmRouterOptions();
        DisplayName = displayName;
        var destination = Destination.Create(DestinationType.Single, AppName, [DeliveryAspect], identity);
        _local = new LocalDestination(identity, destination)
        {
            AppData = displayName is null ? null : LxmfAppData.Build(displayName, _options.InboundStampCost),
            AcceptLinks = true,
            ProofStrategy = ProofStrategy.All,
            PacketReceived = OnOpportunisticPacket,
            LinkEstablished = OnLinkEstablished,
        };
        router.RegisterLocalDestination(_local);
    }

    public string? DisplayName { get; }

    public byte[] DeliveryHash => _local.Destination.Hash;

    public string DeliveryHashHex => Convert.ToHexStringLower(DeliveryHash);

    /// <summary>Called (off the router thread) for every new valid incoming message. Duplicates are suppressed.</summary>
    public Action<ReceivedMessage>? MessageReceived { get; set; }

    public Action<string>? Log { get; set; }

    public int PendingOutbound
    {
        get { lock (_gate) return _outbound.Count; }
    }

    /// <summary>Starts the outbound processing loop.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
    }

    public ValueTask AnnounceAsync(CancellationToken cancellationToken = default) => _router.AnnounceAsync(_local, cancellationToken: cancellationToken);

    /// <summary>
    /// Packs (generating a stamp when the destination announced a cost) and queues <paramref name="message"/>. Delivery proceeds in the background;
    /// follow <see cref="LxMessage.State"/> / <see cref="LxMessage.Completion"/>.
    /// </summary>
    public async Task SendAsync(LxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        message.Log ??= Log;
        Start();
        if (message.Packed is null)
        {
            byte[]? stamp = null;
            if (LxmfAppData.StampCost(_router.Engine.Known.RecallAppData(message.DestinationHash)) is { } cost)
            {
                var id = message.ComputeHash();
                stamp = await Task.Run(() => LxStamper.Generate(id, cost, cancellationToken), cancellationToken).ConfigureAwait(false);
            }

            message.Pack(stamp);
        }

        message.State = LxmState.Outbound;
        lock (_gate) _outbound.Add(message);
        _wake.Release();
    }

    public void Cancel(LxMessage message)
    {
        message.State = LxmState.Cancelled;
        lock (_gate) _outbound.Remove(message);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(_options.ProcessingInterval, ct).ConfigureAwait(false);
                while (_wake.CurrentCount > 0) await _wake.WaitAsync(0, ct).ConfigureAwait(false);
                await ProcessAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log?.Invoke("LXMF outbound processing error: " + ex.Message);
            }
        }
    }

    private static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private async Task ProcessAsync(CancellationToken ct)
    {
        LxMessage[] pending;
        lock (_gate) pending = [.. _outbound];
        foreach (var message in pending)
        {
            if (message.State is LxmState.Delivered or LxmState.Sent or LxmState.Failed or LxmState.Rejected or LxmState.Cancelled)
            {
                lock (_gate) _outbound.Remove(message);
                continue;
            }

            if (message.State == LxmState.Sending || Now < message.NextAttempt) continue;
            if (message.State == LxmState.Sent && message.Method != LxmMethod.Opportunistic) continue;
            if (message.DeliveryAttempts > _options.MaxDeliveryAttempts)
            {
                Log?.Invoke($"{message} failed after {message.DeliveryAttempts} attempts");
                message.State = LxmState.Failed;
                lock (_gate) _outbound.Remove(message);
                continue;
            }

            try
            {
                if (message.Method == LxmMethod.Opportunistic) await SendOpportunisticAsync(message, ct).ConfigureAwait(false);
                else await SendDirectAsync(message, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log?.Invoke($"{message}: delivery attempt failed: {ex.Message}");
                message.NextAttempt = Now + _options.DeliveryRetryWait.TotalSeconds;
            }
        }
    }

    private async Task<bool> EnsurePathAsync(LxMessage message, CancellationToken ct, bool allowPathless)
    {
        var known = _router.Engine.Known.Recall(message.DestinationHash) is not null;
        if (known && _router.HasPath(message.DestinationHash)) return true;
        if (allowPathless && known && message.PathlessTries < _options.MaxPathlessTries)
        {
            message.PathlessTries++;
            return true;
        }

        Log?.Invoke($"{message}: requesting path to {Convert.ToHexStringLower(message.DestinationHash)}");
        await _router.RequestPathAsync(message.DestinationHash, cancellationToken: ct).ConfigureAwait(false);
        message.DeliveryAttempts++;
        message.NextAttempt = Now + _options.PathRequestWait.TotalSeconds;
        return false;
    }

    private async Task SendOpportunisticAsync(LxMessage message, CancellationToken ct)
    {
        if (!await EnsurePathAsync(message, ct, allowPathless: false).ConfigureAwait(false)) return;
        var identity = _router.Engine.Known.Recall(message.DestinationHash)!;
        var encrypted = _router.Engine.Known.Encrypt(message.DestinationHash, message.Packed.AsSpan(LxMessage.DestinationLength))
            ?? throw new InvalidOperationException("Destination identity is no longer known.");
        var packet = Packet.Create(PacketType.Data, DestinationType.Single, message.DestinationHash, encrypted);
        message.DeliveryAttempts++;
        message.NextAttempt = Now + _options.DeliveryRetryWait.TotalSeconds;
        var receipt = await _router.SendWithReceiptAsync(packet, identity, _options.ReceiptTimeout, ct).ConfigureAwait(false);
        message.State = LxmState.Sent;
        receipt.Delivered = _ => message.State = LxmState.Delivered;
        if (receipt.Status == PacketReceiptStatus.Delivered) message.State = LxmState.Delivered;
    }

    private async Task SendDirectAsync(LxMessage message, CancellationToken ct)
    {
        var key = Convert.ToHexStringLower(message.DestinationHash);
        Link? link;
        lock (_gate)
        {
            _links.TryGetValue(key, out link);
            if (link is { Status: LinkStatus.Closed })
            {
                _links.Remove(key);
                _identified.Remove(key);
                link = null;
            }
        }

        if (link is null)
        {
            if (!await EnsurePathAsync(message, ct, allowPathless: true).ConfigureAwait(false)) return;
            var identity = _router.Engine.Known.Recall(message.DestinationHash)!;
            message.DeliveryAttempts++;
            message.NextAttempt = Now + _options.DeliveryRetryWait.TotalSeconds;
            var created = _router.Links.Connect(identity, message.DestinationHash, _ => _wake.Release(), _ => _wake.Release());
            lock (_gate) _links[key] = created;
            return;
        }

        if (link.Status != LinkStatus.Active) return;
        IdentifyOnce(key, link);
        if (message.IsResource)
        {
            message.State = LxmState.Sending;
            link.SendResource(message.Packed!, null, autoCompress: true, concluded: resource =>
            {
                if (resource.Status == ResourceStatus.Complete)
                {
                    message.State = LxmState.Delivered;
                }
                else if (resource.Status == ResourceStatus.Rejected)
                {
                    message.State = LxmState.Rejected;
                }
                else
                {
                    FailAttempt(message, link);
                }
            });
        }
        else
        {
            message.DeliveryAttempts++;
            var receipt = link.Send(message.Packed!, createReceipt: true);
            if (receipt is null)
            {
                FailAttempt(message, link);
                return;
            }

            message.State = LxmState.Sent;
            receipt.Delivered = _ => message.State = LxmState.Delivered;
            receipt.TimedOut = _ => FailAttempt(message, link);
        }
    }

    private void IdentifyOnce(string key, Link link)
    {
        lock (_gate) if (!_identified.Add(key)) return;
        try { link.Identify(_identity); }
        catch (Exception ex) { Log?.Invoke("Could not identify on delivery link: " + ex.Message); }
    }

    private void FailAttempt(LxMessage message, Link link)
    {
        if (message.State is LxmState.Delivered or LxmState.Cancelled) return;
        try { link.Teardown(); }
        catch (Exception) { /* already closed */ }
        message.NextAttempt = Now + _options.DeliveryRetryWait.TotalSeconds;
        message.State = LxmState.Outbound;
        _wake.Release();
    }

    private ValueTask OnOpportunisticPacket(ReceivedPacket received)
    {
        var plain = _local.Decrypt(received.Packet.Payload.Span);
        if (plain is null) return ValueTask.CompletedTask;
        Deliver([.. DeliveryHash, .. plain], LxmMethod.Opportunistic);
        return ValueTask.CompletedTask;
    }

    private void OnLinkEstablished(Link link)
    {
        link.ResourceStrategy = ResourceStrategy.AcceptApp;
        link.PacketReceived = (data, _) => Deliver(data, LxmMethod.Direct);
        link.ResourceAdvertised = advertisement => advertisement.DataSize <= _options.DeliveryPerTransferLimit;
        link.ResourceReceived = resource =>
        {
            if (resource.Status == ResourceStatus.Complete && resource.Data is { } data) Deliver(data, LxmMethod.Direct);
        };
    }

    private void Deliver(byte[] lxmfData, LxmMethod method)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var message = LxMessage.Unpack(lxmfData, hash => _router.Engine.Known.Recall(hash), method);
                if (!message.DestinationHash.AsSpan().SequenceEqual(DeliveryHash)) return;
                bool? stampValid = null;
                if (_options.InboundStampCost is { } cost)
                {
                    stampValid = message.Stamp is not null && LxStamper.Validate(message.Hash!, message.Stamp, cost);
                    if (stampValid == false && _options.EnforceStamps)
                    {
                        Log?.Invoke($"Dropping {message} with invalid stamp");
                        return;
                    }
                }

                var key = Convert.ToHexStringLower(message.Hash!);
                lock (_gate)
                {
                    var now = Now;
                    foreach (var stale in _seen.Where(e => now - e.Value > 3600).Select(e => e.Key).ToList()) _seen.Remove(stale);
                    if (!_seen.TryAdd(key, now)) return;
                }

                MessageReceived?.Invoke(new ReceivedMessage(message, stampValid));
            }
            catch (Exception ex)
            {
                Log?.Invoke("Could not process incoming LXMF data: " + ex.Message);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        lock (_gate)
        {
            foreach (var link in _links.Values)
            {
                try { link.Teardown(); }
                catch (Exception) { /* already closed */ }
            }

            _links.Clear();
        }

        _cts.Dispose();
    }
}
