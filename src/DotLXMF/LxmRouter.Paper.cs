using DotReticulum.Core;

namespace DotLXMF;

public enum LxmPaperIngestResult
{
    Rejected,
    LocalDelivery,
    Duplicate,
}

public sealed partial class LxmRouter
{
    private sealed record InboundWork(byte[] Data, LxmMethod Method, bool AllowDuplicate = false, bool IgnoreStamps = false,
        byte[]? TransientId = null, TaskCompletionSource<LxmPaperIngestResult>? Completion = null,
        byte[]? ExpectedDestination = null, DestinationType DestinationType = DestinationType.Single,
        byte[]? RatchetId = null, PacketPhysicalLayer PhysicalLayer = default);

    /// <summary>
    /// Ingests a paper URI addressed to this router. As upstream, paper ingestion bypasses stamp enforcement and
    /// delivers with the propagated method. Cancellation stops waiting, not an already accepted delivery.
    /// </summary>
    public async Task<LxmPaperIngestResult> IngestPaperUriAsync(string uri, bool allowDuplicate = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        LxMessage message;
        byte[]? ratchetId = null;
        try
        {
            message = LxMessage.FromPaperUri(uri, _identity, hash => _router.Engine.Known.Recall(hash), encrypted =>
            {
                var result = _local.DecryptWithMetadata(encrypted);
                ratchetId = result?.RatchetId;
                return result?.Plaintext;
            });
        }
        catch (FormatException) { return LxmPaperIngestResult.Rejected; }
        Start();
        var completion = new TaskCompletionSource<LxmPaperIngestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new InboundWork(message.Packed!, LxmMethod.Propagated, allowDuplicate, IgnoreStamps: true,
            Identity.FullHash(message.PaperPacked!), completion, RatchetId: ratchetId);
        if (!_inbound.Writer.TryWrite(work))
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            throw new InvalidOperationException("LXMF inbound queue is full.");
        }
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Report(string message)
    {
        if (Log is not { } callbacks) return;
        foreach (Action<string> callback in callbacks.GetInvocationList())
        {
            try { callback(message); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("LXMF log observer failed: {0}", ex.Message); }
        }
    }
}
