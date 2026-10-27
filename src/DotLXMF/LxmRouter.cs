using DotReticulum.Core;
using DotReticulum.Transport;
using System.Threading.Channels;
using System.Security.Cryptography;

namespace DotLXMF;

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

    /// <summary>Enable upstream delivery ratchets. Memory-only unless <see cref="StoragePath"/> is configured.</summary>
    public bool EnableRatchets { get; init; } = true;

    public bool EnforceRatchets { get; init; }

    /// <summary>Application storage root; signed ratchets are saved under lxmf/ratchets.</summary>
    public string? StoragePath { get; init; }

    public int InboundQueueCapacity { get; init; } = 256;

    public int OutboundQueueCapacity { get; init; } = 256;
}

/// <summary>A received message together with delivery metadata.</summary>
public sealed record ReceivedMessage(LxMessage Message, bool? StampValid);

/// <summary>
/// A lightweight LXMF router (upstream <c>LXMRouter</c>): one <c>lxmf.delivery</c> destination, opportunistic and link-based outbound delivery with
/// retries, bounded queues, tickets, ratchets and optional propagation storage.
/// </summary>
public sealed partial class LxmRouter : IAsyncDisposable
{
    public const string AppName = "lxmf";
    public const string DeliveryAspect = "delivery";

    private readonly TransportRouter _router;
    private readonly Identity _identity;
    private readonly LxmRouterOptions _options;
    private readonly LocalDestination _local;
    private readonly object _gate = new();
    private readonly List<LxMessage> _outbound = [];
    private readonly Dictionary<LxMessage, CancellationTokenSource> _preparing = [];
    private readonly Dictionary<string, Link> _links = [];
    private readonly HashSet<Link> _incomingLinks = [];
    private readonly HashSet<string> _ignored = [];
    private readonly HashSet<string> _identified = [];
    private readonly Dictionary<string, double> _seen = [];
    private readonly Dictionary<string, Resource> _incomingResources = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;
    private Task _inboundLoop = Task.CompletedTask;
    private readonly Channel<InboundWork> _inbound;
    private readonly DestinationRatchets? _ratchets;
    private readonly LxmTicketStore _tickets;
    private int _inboundStampCost;
    private int _started;
    private int _disposed;

    public LxmRouter(TransportRouter router, Identity identity, string? displayName = null, LxmRouterOptions? options = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _options = options ?? new LxmRouterOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.InboundQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.OutboundQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(_options.InboundStampCost ?? 0, 255);
        _inboundStampCost = Math.Max(0, _options.InboundStampCost ?? 0);
        _inbound = System.Threading.Channels.Channel.CreateBounded<InboundWork>(new BoundedChannelOptions(_options.InboundQueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        DisplayName = displayName;
        var destination = Destination.Create(DestinationType.Single, AppName, [DeliveryAspect], identity);
        if (_options.EnableRatchets)
        {
            _ratchets = _options.StoragePath is { } storage
                ? SignedDestinationRatchetFile.Open(Path.Combine(storage, "lxmf", "ratchets", Convert.ToHexStringLower(destination.Hash) + ".ratchets"), identity)
                : new DestinationRatchets();
            _ratchets.Enforce = _options.EnforceRatchets;
        }
        else if (_options.EnforceRatchets) throw new ArgumentException("Ratchet enforcement requires ratchets.", nameof(options));
        try
        {
            _tickets = new LxmTicketStore(_options.StoragePath is { } storage ? Path.Combine(storage, "lxmf", "available_tickets") : null);
            try { LoadDeliveryHistory(); LoadProcessedHistory(); LoadOutboundStampCosts(); }
            catch { _tickets.Dispose(); throw; }
        }
        catch
        {
            _ratchets?.Dispose();
            throw;
        }
        _local = new LocalDestination(identity, destination)
        {
            Ratchets = _ratchets,
            AppData = GetAnnounceAppData(),
            AcceptLinks = true,
            ProofStrategy = ProofStrategy.All,
            PacketReceived = OnOpportunisticPacket,
            LinkEstablished = OnLinkEstablished,
        };
        _local.SetDefaultAppDataProvider(GetAnnounceAppData);
        router.RegisterLocalDestination(_local);
        InitializeDeliveryAnnounces();
    }

    public string? DisplayName { get; }

    public byte[] DeliveryHash => _local.Destination.Hash;

    public string DeliveryHashHex => Convert.ToHexStringLower(DeliveryHash);

    /// <summary>Ticket lifecycle and persistence; lifetime is owned by this router.</summary>
    public LxmTicketStore Tickets => _tickets;

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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        _inboundLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
    }

    public ValueTask AnnounceAsync(CancellationToken cancellationToken = default) => _router.AnnounceAsync(_local, cancellationToken: cancellationToken);

    /// <summary>
    /// Packs (generating a stamp when the destination announced a cost) and queues <paramref name="message"/>. Delivery proceeds in the background;
    /// follow <see cref="LxMessage.State"/> / <see cref="LxMessage.Completion"/>.
    /// </summary>
    public async Task SendAsync(LxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        cancellationToken = preparation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_outbound.Contains(message) || _preparing.ContainsKey(message)) throw new InvalidOperationException("Message is already queued.");
            if (_outbound.Count + _preparing.Count >= _options.OutboundQueueCapacity) throw new InvalidOperationException("LXMF outbound queue is full.");
            _preparing.Add(message, preparation);
        }
        try { await PrepareAndQueueAsync(message, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            message.State = LxmState.Cancelled;
            throw;
        }
        finally { lock (_gate) _preparing.Remove(message); }
    }

    private async Task PrepareAndQueueAsync(LxMessage message, CancellationToken cancellationToken)
    {
        message.Log ??= Report;
        Start();
        if (message.Packed is null)
        {
            if (message.IncludeTicket && _tickets.Generate(message.DestinationHash) is { } issued)
            {
                try { message.Fields[LxmField.Ticket] = issued.ToField(); }
                finally { CryptographicOperations.ZeroMemory(issued.Token); }
            }

            byte[]? stamp = null;
            if (_tickets.GetOutbound(message.DestinationHash) is { } ticket)
            {
                try { stamp = message.CreateTicketStamp(ticket.Token); }
                finally { CryptographicOperations.ZeroMemory(ticket.Token); }
            }
            else if (GetOutboundStampCost(message.DestinationHash) is { } cost)
            {
                var id = message.ComputeHash();
                stamp = await LxStamper.GenerateAsync(id, cost, cancellationToken).ConfigureAwait(false);
            }

            if (message.DesiredMethod == LxmMethod.Propagated)
            {
                var node = OutboundPropagationNode ?? throw new InvalidOperationException("No outbound propagation node configured.");
                var identity = message.DestinationHash.AsSpan().SequenceEqual(DeliveryHash) ? _identity : _router.Engine.Known.Recall(message.DestinationHash)
                    ?? throw new InvalidOperationException("The propagated recipient identity is unknown.");
                if (!LxmfPropagationAnnounce.TryParse(_router.Engine.Known.RecallAppData(node), out var announced))
                    throw new InvalidOperationException("Propagation node has not announced valid limits and stamp costs.");
                message.Pack(stamp, identity, _router.Engine.Known.GetRatchet(message.DestinationHash));
                var propagationStamp = await LxStamper.GenerateAsync(message.TransientId!, announced!.StampCost,
                    cancellationToken, LxStamper.WorkblockExpandRoundsPn).ConfigureAwait(false);
                message.SetPropagationStamp(propagationStamp);
                if (message.PropagationPacked!.LongLength > announced!.TransferLimitKilobytes * 1000)
                    throw new ArgumentException("Message exceeds the propagation node transfer limit.", nameof(message));
                message.PropagationNodeHash = node;
            }
            else message.Pack(stamp);
        }

        if (message.IncludeTicket && message.Fields.ContainsKey(LxmField.Ticket))
            message.StateChanged += MarkTicketDelivered;
        message.DetermineTransportEncryption();
        message.State = LxmState.Outbound;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_outbound.Count >= _options.OutboundQueueCapacity) throw new InvalidOperationException("LXMF outbound queue is full.");
            if (_outbound.Contains(message)) throw new InvalidOperationException("Message is already queued.");
            _preparing.Remove(message);
            _outbound.Add(message);
        }
        _wake.Release();
    }

    private void MarkTicketDelivered(LxMessage message)
    {
        if (message.State != LxmState.Delivered) return;
        message.StateChanged -= MarkTicketDelivered;
        try { _tickets.MarkDelivered(message.DestinationHash); }
        catch (Exception ex) { Report("Could not save LXMF ticket delivery: " + ex.Message); }
    }

    public void Cancel(LxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        message.State = LxmState.Cancelled;
        lock (_gate)
        {
            _outbound.Remove(message);
            if (_preparing.TryGetValue(message, out var preparation)) preparation.Cancel();
        }
        message.ActiveResource?.Cancel();
    }

    public bool HasMessage(ReadOnlySpan<byte> messageHash)
    {
        lock (_gate) return _seen.ContainsKey(Convert.ToHexStringLower(messageHash));
    }

    public void IgnoreDestination(ReadOnlySpan<byte> destinationHash)
    {
        if (destinationHash.Length != 16) throw new ArgumentException("Destination hash must be 16 bytes.", nameof(destinationHash));
        lock (_gate) _ignored.Add(Convert.ToHexStringLower(destinationHash));
    }

    public void UnignoreDestination(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate) _ignored.Remove(Convert.ToHexStringLower(destinationHash));
    }

    public IReadOnlyList<Resource> InboundResources
    {
        get { lock (_gate) return _incomingResources.Values.ToArray(); }
    }

    public bool CancelInbound(ReadOnlySpan<byte> resourceHash)
    {
        Resource? resource;
        lock (_gate) _incomingResources.TryGetValue(Convert.ToHexStringLower(resourceHash), out resource);
        if (resource is null || resource.Status >= ResourceStatus.Complete) return false;
        resource.Cancel();
        return true;
    }

    public int CancelAllInbound()
    {
        var resources = InboundResources;
        foreach (var resource in resources) resource.Cancel();
        return resources.Count;
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
                SaveOutboundStampCosts();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Report("LXMF outbound processing error: " + ex.Message);
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
            if (message.State is LxmState.Delivered or LxmState.Failed or LxmState.Rejected or LxmState.Cancelled ||
                message.State == LxmState.Sent && message.Method == LxmMethod.Propagated)
            {
                lock (_gate) _outbound.Remove(message);
                continue;
            }

            if (message.State == LxmState.Sending || Now < message.NextAttempt) continue;
            if (message.State == LxmState.Sent && message.Method != LxmMethod.Opportunistic) continue;
            if (message.DeliveryAttempts > _options.MaxDeliveryAttempts)
            {
                Report($"{message} failed after {message.DeliveryAttempts} attempts");
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
                Report($"{message}: delivery attempt failed: {ex.Message}");
                message.NextAttempt = Now + _options.DeliveryRetryWait.TotalSeconds;
            }
        }
    }

    private async Task<bool> EnsurePathAsync(LxMessage message, CancellationToken ct, bool allowPathless, byte[]? routeHash = null)
    {
        var destination = routeHash ?? message.DestinationHash;
        var known = _router.Engine.Known.Recall(destination) is not null;
        if (known && _router.HasPath(destination)) return true;
        if (allowPathless && known && message.PathlessTries < _options.MaxPathlessTries)
        {
            message.PathlessTries++;
            return true;
        }

        Report($"{message}: requesting path to {Convert.ToHexStringLower(destination)}");
        await _router.RequestPathAsync(destination, cancellationToken: ct).ConfigureAwait(false);
        message.DeliveryAttempts++;
        message.NextAttempt = Now + _options.PathRequestWait.TotalSeconds;
        return false;
    }

    private async Task SendOpportunisticAsync(LxMessage message, CancellationToken ct)
    {
        if (message.DestinationType == DestinationType.Plain)
        {
            message.DeliveryAttempts++;
            message.NextAttempt = Now + _options.DeliveryRetryWait.TotalSeconds;
            var plain = Packet.Create(PacketType.Data, DestinationType.Plain, message.DestinationHash,
                message.Packed.AsMemory(LxMessage.DestinationLength));
            await _router.SendAsync(plain, ct).ConfigureAwait(false);
            message.State = LxmState.Sent;
            Report("No receipt generated for sent plain opportunistic LXMF message.");
            return;
        }
        if (!await EnsurePathAsync(message, ct, allowPathless: false).ConfigureAwait(false)) return;
        var identity = _router.Engine.Known.Recall(message.DestinationHash)!;
        var encrypted = _router.Engine.Known.Encrypt(message.DestinationHash, message.Packed.AsSpan(LxMessage.DestinationLength))
            ?? throw new InvalidOperationException("Destination identity is no longer known.");
        var packet = Packet.Create(PacketType.Data, DestinationType.Single, message.DestinationHash, encrypted);
        message.DeliveryAttempts++;
        message.NextAttempt = Now + _options.DeliveryRetryWait.TotalSeconds;
        var receipt = await _router.SendWithReceiptAsync(packet, identity, _options.ReceiptTimeout, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("An opportunistic LXMF data packet did not qualify for a delivery receipt.");
        message.State = LxmState.Sent;
        receipt.Delivered = _ => message.State = LxmState.Delivered;
        if (receipt.Status == PacketReceiptStatus.Delivered) message.State = LxmState.Delivered;
    }

    private async Task SendDirectAsync(LxMessage message, CancellationToken ct)
    {
        var propagated = message.Method == LxmMethod.Propagated;
        var destination = propagated ? message.PropagationNodeHash ?? throw new InvalidOperationException("No propagation route selected.") : message.DestinationHash;
        var packed = propagated ? message.PropagationPacked! : message.Packed!;
        var key = Convert.ToHexStringLower(destination);
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
            if (!await EnsurePathAsync(message, ct, allowPathless: true, destination).ConfigureAwait(false)) return;
            var identity = _router.Engine.Known.Recall(destination)!;
            message.DeliveryAttempts++;
            message.NextAttempt = Now + _options.DeliveryRetryWait.TotalSeconds;
            var created = _router.Links.Connect(identity, destination, _ => _wake.Release(), _ => _wake.Release());
            lock (_gate) _links[key] = created;
            return;
        }

        if (link.Status != LinkStatus.Active) return;
        if (!propagated) IdentifyOnce(key, link);
        if (message.IsResource)
        {
            message.State = LxmState.Sending;
            if (message.State == LxmState.Cancelled) return;
            message.ActiveResource = link.SendResource(packed, null,
                autoCompress: propagated || LxmfAppData.CompressionSupported(_router.Engine.Known.RecallAppData(message.DestinationHash)) != false, concluded: resource =>
            {
                message.ActiveResource = null;
                if (resource.Status == ResourceStatus.Complete)
                {
                    message.State = propagated ? LxmState.Sent : LxmState.Delivered;
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
            if (message.State == LxmState.Cancelled) message.ActiveResource?.Cancel();
        }
        else
        {
            message.DeliveryAttempts++;
            var receipt = link.Send(packed, createReceipt: true);
            if (receipt is null)
            {
                FailAttempt(message, link);
                return;
            }

            message.State = propagated ? LxmState.Sending : LxmState.Sent;
            receipt.Delivered = _ => message.State = propagated ? LxmState.Sent : LxmState.Delivered;
            receipt.TimedOut = _ => FailAttempt(message, link);
        }
    }

    private void IdentifyOnce(string key, Link link)
    {
        lock (_gate) if (!_identified.Add(key)) return;
        try { link.Identify(_identity); }
        catch (Exception ex) { Report("Could not identify on delivery link: " + ex.Message); }
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
        lock (_gate)
        {
            if (_disposed == 0) _incomingLinks.Add(link);
        }
        if (Volatile.Read(ref _disposed) != 0) { link.Teardown(); return; }
        link.Closed += closed =>
        {
            lock (_gate) _incomingLinks.Remove(closed);
        };
        link.ResourceStrategy = ResourceStrategy.AcceptApp;
        link.PacketReceived = (data, _) => Deliver(data, LxmMethod.Direct);
        link.ResourceAdvertised = advertisement => advertisement.DataSize <= _options.DeliveryPerTransferLimit;
        link.ResourceStarted = resource =>
        {
            lock (_gate) _incomingResources[Convert.ToHexStringLower(resource.Hash)] = resource;
        };
        link.ResourceReceived = resource =>
        {
            lock (_gate) _incomingResources.Remove(Convert.ToHexStringLower(resource.Hash));
            if (resource.Status == ResourceStatus.Complete && resource.Data is { } data) Deliver(data, LxmMethod.Direct);
        };
    }

    private bool Deliver(byte[] lxmfData, LxmMethod method)
    {
        var accepted = _inbound.Writer.TryWrite(new InboundWork(lxmfData, method));
        if (!accepted && Volatile.Read(ref _disposed) == 0)
            Report("LXMF inbound queue is full; dropping incoming message.");
        return accepted;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var work in _inbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested)
                {
                    work.Completion?.TrySetCanceled(ct);
                    break;
                }
                var result = LxmPaperIngestResult.Rejected;
                try
                {
                    if (work.TransientId is { } transient && !RememberProcessed(transient, work.AllowDuplicate))
                        result = LxmPaperIngestResult.Duplicate;
                    else result = ProcessIncoming(work);
                }
                catch (Exception ex) { Report("Could not process incoming LXMF data: " + ex.Message); }
                finally { work.Completion?.TrySetResult(result); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            while (_inbound.Reader.TryRead(out var pending)) pending.Completion?.TrySetCanceled(ct);
        }
    }

    private LxmPaperIngestResult ProcessIncoming(InboundWork work)
    {
        try
        {
            var message = LxMessage.Unpack(work.Data, hash => hash.AsSpan().SequenceEqual(DeliveryHash) ? _identity : _router.Engine.Known.Recall(hash), work.Method);
            if (!message.DestinationHash.AsSpan().SequenceEqual(work.ExpectedDestination ?? DeliveryHash)) return LxmPaperIngestResult.Rejected;
            lock (_gate)
                if (_ignored.Contains(Convert.ToHexStringLower(message.SourceHash))) return LxmPaperIngestResult.Rejected;
            message.TransportEncrypted = work.DestinationType != DestinationType.Plain;
            message.TransportEncryption = work.DestinationType == DestinationType.Plain ? null : "Curve25519";
            if (message.SignatureValidated && message.Fields.TryGetValue(LxmField.Ticket, out var value) &&
                value is object?[] { Length: >= 2 } entry && entry[1] is byte[] { Length: LxMessage.TicketLength } token &&
                entry[0] is double or float or long or ulong)
            {
                var expires = MsgPack.ToDouble(entry[0]);
                if (double.IsFinite(expires)) _tickets.Remember(message.SourceHash, new LxmTicket(expires, token));
            }

            bool? stampValid = null;
            if (work.DestinationType != DestinationType.Plain && InboundStampCost is { } cost)
            {
                var tickets = _tickets.GetInbound(message.SourceHash);
                try { stampValid = message.ValidateStamp(cost, tickets); }
                finally { foreach (var ticket in tickets) CryptographicOperations.ZeroMemory(ticket); }
                if (stampValid == false && _options.EnforceStamps && !work.IgnoreStamps)
                {
                    Report($"Dropping {message} with invalid stamp");
                    return LxmPaperIngestResult.Rejected;
                }
            }

            var key = Convert.ToHexStringLower(message.Hash!);
            lock (_gate)
            {
                var now = Now;
                foreach (var stale in _seen.Where(e => now - e.Value > DeliveryHistoryRetention.TotalSeconds).Select(e => e.Key).ToList()) _seen.Remove(stale);
                if (_seen.ContainsKey(key) && !work.AllowDuplicate) return LxmPaperIngestResult.Duplicate;
                var transientKey = work.TransientId is { } transient ? Convert.ToHexStringLower(transient) : null;
                var additions = (_seen.ContainsKey(key) ? 0 : 1) +
                    (transientKey is null || _seen.ContainsKey(transientKey) ? 0 : 1);
                if (_seen.Count + additions > 65_536) throw new InvalidOperationException("LXMF duplicate cache capacity reached.");
                var previous = _seen.GetValueOrDefault(key);
                var previousTransient = transientKey is null ? 0 : _seen.GetValueOrDefault(transientKey);
                _seen[key] = now;
                if (transientKey is not null) _seen[transientKey] = now;
                try { SaveDeliveryHistory(); }
                catch
                {
                    if (previous == 0) _seen.Remove(key); else _seen[key] = previous;
                    if (transientKey is not null)
                    {
                        if (previousTransient == 0) _seen.Remove(transientKey); else _seen[transientKey] = previousTransient;
                    }
                    throw;
                }
            }

            if (MessageReceived is { } callbacks)
                foreach (Action<ReceivedMessage> callback in callbacks.GetInvocationList())
                {
                    try { callback(new ReceivedMessage(message, stampValid)); }
                    catch (Exception ex) { Report($"LXMF receive observer failed for {message}: {ex}"); }
                }
            return LxmPaperIngestResult.LocalDelivery;
        }
        catch (Exception ex)
        {
            Report("Could not process incoming LXMF data: " + ex.Message);
            return LxmPaperIngestResult.Rejected;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _inbound.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        await _retrieval.WaitAsync().ConfigureAwait(false);
        _retrieval.Release();
        try { await Task.WhenAll(_loop, _inboundLoop).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        Link[] links;
        lock (_gate)
        {
            links = _links.Values.Concat(_incomingLinks).Distinct().ToArray();
            _links.Clear();
            _incomingLinks.Clear();
            foreach (var message in _outbound) message.State = LxmState.Cancelled;
            _outbound.Clear();
        }
        foreach (var link in links)
        {
            try { link.Teardown(); }
            catch (Exception ex) { Report("Could not close LXMF link: " + ex.Message); }
        }

        _router.Engine.DeregisterHandler(_deliveryAnnounceHandler);
        try { SaveOutboundStampCosts(); }
        catch (Exception ex) { Report("Could not save outbound LXMF stamp costs during disposal: " + ex.Message); }
        _router.DeregisterLocalDestination(_local);
        if (_plain is { } plain) { _router.DeregisterLocalDestination(plain); plain.Dispose(); }
        _ratchets?.Dispose();
        _tickets.Dispose();
        if (PropagationNode is { } propagation) await propagation.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
