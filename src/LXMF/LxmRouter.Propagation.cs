using DotReticulum.Core;
using DotReticulum.Transport;

namespace LXMF;

public enum LxmPropagationTransferState : byte
{
    Idle = 0, PathRequested = 1, LinkEstablishing = 2, LinkEstablished = 3, RequestSent = 4,
    Receiving = 5, ResponseReceived = 6, Complete = 7,
    NoPath = 0xf0, LinkFailed = 0xf1, TransferFailed = 0xf2, NoIdentity = 0xf3, NoAccess = 0xf4, Failed = 0xfe,
}

public sealed partial class LxmRouter
{
    private byte[]? _outboundPropagationNode;
    private readonly SemaphoreSlim _retrieval = new(1, 1);
    public LxmPropagationNode? PropagationNode { get; private set; }
    public LxmPropagationTransferState PropagationTransferState { get; private set; }
    public bool RetainNodeMessages { get; set; }
    public byte[]? InboundPropagationNode => OutboundPropagationNode;

    public byte[]? OutboundPropagationNode
    {
        get { lock (_gate) return _outboundPropagationNode?.ToArray(); }
        set
        {
            if (value is not null && value.Length != 16) throw new ArgumentException("Propagation destination must be 16 bytes.", nameof(value));
            Link? retired;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                _outboundPropagationNode = value?.ToArray();
                retired = _retrievalLink is { } link && (_retrievalDestination is null || value is null ||
                    !_retrievalDestination.AsSpan().SequenceEqual(value)) ? link : null;
            }
            if (retired is not null)
            {
                ForgetRetrievalLink(retired);
                try { retired.Teardown(); }
                catch (Exception ex) { Report("Could not retire changed propagation retrieval link: " + ex.Message); }
            }
        }
    }

    public async Task<int?> GetOutboundPropagationCostAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var node = OutboundPropagationNode ?? throw new InvalidOperationException("No propagation node configured.");
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        return (await GetPropagationConfigurationAsync(node, owned.Token).ConfigureAwait(false))?.StampCost;
    }

    private async Task<LxmfPropagationAnnounce?> GetPropagationConfigurationAsync(byte[] node, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (LxmfPropagationAnnounce.TryParse(_router.Engine.Known.RecallAppData(node), out var announced)) return announced;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(PathRequestWait));
        try
        {
            await RequestPathAsync(node, deadline.Token).ConfigureAwait(false);
            while (_router.Engine.Known.RecallAppData(node) is null)
                await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        return LxmfPropagationAnnounce.TryParse(_router.Engine.Known.RecallAppData(node), out announced) ? announced : null;
    }

    /// <summary>Starts a propagation destination on this transport using the router's configured persistent storage root.</summary>
    public LxmPropagationNode EnablePropagationNode(LxmPropagationOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        lock (_gate)
        {
            if (PropagationNode is not null) { PropagationNode.SetEnabled(true); return PropagationNode; }
            if (_options.StoragePath is not { } storage) throw new InvalidOperationException("Propagation nodes require StoragePath.");
            PropagationNode = new LxmPropagationNode(_router, _identity, storage, options, ReceivePropagatedData,
                _options.DeliveryPerTransferLimit)
            { Log = Log };
            return PropagationNode;
        }
    }

    public ValueTask DisablePropagationNodeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (PropagationNode is not { } node) return ValueTask.CompletedTask;
            node.SetEnabled(false);
            return ValueTask.CompletedTask;
        }
    }

    private bool ReceivePropagatedData(byte[] data)
    {
        if (data.Length < LxMessage.DestinationLength || !data.AsSpan(0, LxMessage.DestinationLength).SequenceEqual(DeliveryHash)) return false;
        var decrypted = _local.DecryptWithMetadata(data.AsSpan(LxMessage.DestinationLength));
        if (decrypted is null) return false;
        var plain = decrypted.Plaintext;
        var wire = new byte[DeliveryHash.Length + plain.Length];
        DeliveryHash.CopyTo(wire, 0);
        plain.CopyTo(wire, DeliveryHash.Length);
        try { _ = LxMessage.Unpack(wire, hash => _router.Engine.Known.Recall(hash), LxmMethod.Propagated); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) { return false; }
        var accepted = _inbound.Writer.TryWrite(new InboundWork(wire, LxmMethod.Propagated,
            TransientId: Identity.FullHash(data), RatchetId: decrypted.RatchetId));
        if (!accepted && Volatile.Read(ref _disposed) == 0) Report("LXMF inbound queue is full; dropping propagated message.");
        return accepted;
    }

    /// <summary>Downloads this identity's messages and acknowledges successful decryption to the node unless retain is true.</summary>
    public async Task<int> RequestMessagesFromPropagationNodeAsync(byte[]? destinationHash = null, int maxMessages = 0, bool? retain = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessages);
        var keepOnNode = retain ?? RetainNodeMessages;
        var node = destinationHash?.ToArray() ?? InboundPropagationNode ?? throw new InvalidOperationException("No propagation node configured.");
        if (node.Length != 16) throw new ArgumentException("Propagation destination must be 16 bytes.", nameof(destinationHash));
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        await _retrieval.WaitAsync(owned.Token).ConfigureAwait(false);
        DotReticulum.Transport.Link? link = null;
        var succeeded = false;
        try
        {
            lock (_gate)
            {
                _activeRetrieval = owned;
                PropagationTransferProgress = 0;
                PropagationTransferSize = null;
                PropagationTransferMaxMessages = maxMessages;
            }
            link = await GetRetrievalLinkAsync(node, owned.Token).ConfigureAwait(false);
            PropagationTransferState = LxmPropagationTransferState.LinkEstablished;
            link.Identify(_identity);
            PropagationTransferState = LxmPropagationTransferState.RequestSent;
            var response = await LxmPropagationTransport.RequestAsync(link, "/get", new object?[] { null, null },
                TimeSpan.FromSeconds(30), _options.DeliveryPerTransferLimit, owned.Token).ConfigureAwait(false);
            EnsureRetrievalResponse(response);
            if (response is not object?[] ids || ids.Any(id => id is not byte[] { Length: 32 })) throw new FormatException("Invalid propagation message list.");
            var haves = new List<object?>();
            var wants = new List<object?>();
            lock (_gate)
                foreach (var id in ids.Cast<byte[]>())
                    if (_seen.ContainsKey(Convert.ToHexStringLower(id))) { if (!keepOnNode) haves.Add(id); }
                    else if (maxMessages == 0 || wants.Count < maxMessages) wants.Add(id);
            if (wants.Count == 0 && haves.Count == 0)
            {
                PropagationTransferState = LxmPropagationTransferState.Complete;
                PropagationTransferProgress = 1;
                PropagationTransferLastResult = 0;
                PropagationTransferLastDuplicates = 0;
                succeeded = true;
                NotifyPropagationProgress();
                return 0;
            }

            PropagationTransferState = LxmPropagationTransferState.Receiving;
            response = await LxmPropagationTransport.RequestAsync(link, "/get",
                new object?[] { wants.ToArray(), haves.ToArray(), _options.DeliveryPerTransferLimit / 1000.0 },
                TimeSpan.FromSeconds(60), _options.DeliveryPerTransferLimit, owned.Token,
                receipt => UpdateRetrievalProgress(receipt, owned)).ConfigureAwait(false);
            PropagationTransferState = LxmPropagationTransferState.ResponseReceived;
            EnsureRetrievalResponse(response);
            if (response is not object?[] messages) throw new FormatException("Invalid propagation download.");
            haves.Clear();
            var count = 0;
            var duplicates = 0;
            foreach (var value in messages)
            {
                if (value is not byte[] data) throw new FormatException("Invalid propagated message bytes.");
                var id = Identity.FullHash(data);
                var key = Convert.ToHexStringLower(id);
                bool known;
                lock (_gate) known = _seen.ContainsKey(key);
                if (known) duplicates++;
                if (known || ReceivePropagatedData(data))
                {
                    if (!keepOnNode) haves.Add(id);
                    if (!known)
                    {
                        lock (_gate) _seen[key] = Now;
                        count++;
                    }
                }
            }

            if (haves.Count > 0) await LxmPropagationTransport.RequestAsync(link, "/get", new object?[] { null, haves.ToArray() },
                TimeSpan.FromSeconds(30), _options.DeliveryPerTransferLimit, owned.Token).ConfigureAwait(false);
            PropagationTransferState = LxmPropagationTransferState.Complete;
            PropagationTransferProgress = 1;
            PropagationTransferLastResult = messages.Length;
            PropagationTransferLastDuplicates = duplicates;
            succeeded = true;
            NotifyPropagationProgress();
            return count;
        }
        catch (OperationCanceledException) when (owned.IsCancellationRequested)
        {
            AcknowledgeSyncCompletion(resetState: true);
            throw;
        }
        catch
        {
            if (PropagationTransferState < LxmPropagationTransferState.NoPath)
                PropagationTransferState = LxmPropagationTransferState.TransferFailed;
            throw;
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_activeRetrieval, owned)) _activeRetrieval = null;
            if (!succeeded && link is not null) { ForgetRetrievalLink(link); link.Teardown(); }
            _retrieval.Release();
        }
    }

    private void EnsureRetrievalResponse(object? response)
    {
        if (response is not long error) return;
        PropagationTransferState = error switch
        {
            0xf0 => LxmPropagationTransferState.NoIdentity,
            0xf1 => LxmPropagationTransferState.NoAccess,
            _ => LxmPropagationTransferState.Failed,
        };
        throw new IOException("Propagation node rejected request: 0x" + error.ToString("x"));
    }
}
