using DotReticulum.Transport;

namespace LXMF;

public sealed partial class LxmRouter
{
    private Link? _retrievalLink;
    private byte[]? _retrievalDestination;
    private CancellationTokenSource? _activeRetrieval;
    public double PropagationTransferProgress { get; private set; }
    public long? PropagationTransferSize { get; private set; }
    public int? PropagationTransferLastResult { get; private set; }
    public int PropagationTransferLastDuplicates { get; private set; }
    public int PropagationTransferMaxMessages { get; private set; }
    public byte[]? PropagationLinkId { get { lock (_gate) return _retrievalLink?.LinkId.ToArray(); } }
    public Action<LxmRouter>? PropagationProgressChanged { get; set; }

    public void AcknowledgeSyncCompletion(bool resetState = false)
    {
        lock (_gate)
        {
            PropagationTransferLastResult = null;
            if (resetState || PropagationTransferState <= LxmPropagationTransferState.Complete)
                PropagationTransferState = LxmPropagationTransferState.Idle;
            PropagationTransferProgress = 0;
            PropagationTransferSize = null;
        }
        NotifyPropagationProgress();
    }

    public void CancelPropagationNodeRequests()
    {
        Link? link;
        CancellationTokenSource? request;
        lock (_gate)
        {
            link = _retrievalLink;
            request = _activeRetrieval;
            _retrievalLink = null;
            _retrievalDestination = null;
        }
        try { request?.Cancel(); }
        catch (ObjectDisposedException) { }
        link?.Teardown();
        AcknowledgeSyncCompletion(resetState: true);
    }

    private void NotifyPropagationProgress()
    {
        if (PropagationProgressChanged is not { } callbacks) return;
        foreach (Action<LxmRouter> observer in callbacks.GetInvocationList())
            try { observer(this); }
            catch (Exception ex) { Report("LXMF propagation progress observer failed: " + ex.Message); }
    }

    private void UpdateRetrievalProgress(RequestReceipt receipt, CancellationTokenSource request)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activeRetrieval, request) || request.IsCancellationRequested ||
                PropagationTransferState >= LxmPropagationTransferState.Complete) return;
            PropagationTransferState = LxmPropagationTransferState.Receiving;
            PropagationTransferProgress = receipt.Progress;
            if (receipt.ResponseSize is > 0) PropagationTransferSize = receipt.ResponseSize;
        }
        NotifyPropagationProgress();
    }

    private void ForgetRetrievalLink(Link link)
    {
        lock (_gate)
        {
            _incomingLinks.Remove(link);
            if (!ReferenceEquals(_retrievalLink, link)) return;
            _retrievalLink = null;
            _retrievalDestination = null;
        }
    }

    private async Task<Link> GetRetrievalLinkAsync(byte[] node, CancellationToken ct)
    {
        Link? previous;
        lock (_gate)
        {
            if (_retrievalLink is { Status: LinkStatus.Active } existing &&
                _retrievalDestination!.AsSpan().SequenceEqual(node)) return existing;
            previous = _retrievalLink;
            _retrievalLink = null;
            _retrievalDestination = null;
        }
        previous?.Teardown();
        if (!_router.HasPath(node))
        {
            PropagationTransferState = LxmPropagationTransferState.PathRequested;
            await RequestPathAsync(node, ct).ConfigureAwait(false);
            using var pathDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pathDeadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, PathRequestWait)));
            try
            {
                while (!_router.HasPath(node))
                    await Task.Delay(TimeSpan.FromMilliseconds(100), pathDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                PropagationTransferState = LxmPropagationTransferState.NoPath;
                throw new TimeoutException("Propagation node path request timed out.");
            }
        }
        PropagationTransferState = LxmPropagationTransferState.LinkEstablishing;
        var identity = _router.Engine.Known.Recall(node) ?? throw new IOException("Propagation node identity is unavailable.");
        var established = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = _router.Links.Connect(identity, node, link => established.TrySetResult(link),
            link => established.TrySetException(new IOException("Propagation link closed before establishment.")));
        try
        {
            var link = await established.Task.WaitAsync(TimeSpan.FromSeconds(created.EstablishmentTimeout), ct).ConfigureAwait(false);
            link.Closed += ForgetRetrievalLink;
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                if (link.Status != LinkStatus.Active) throw new IOException("Propagation link closed.");
                _retrievalLink = link;
                _retrievalDestination = node.ToArray();
                _incomingLinks.Add(link);
            }
            return link;
        }
        catch
        {
            created.Teardown();
            if (!ct.IsCancellationRequested) PropagationTransferState = LxmPropagationTransferState.LinkFailed;
            throw;
        }
    }
}
