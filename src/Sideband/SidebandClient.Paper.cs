using System.Text;
using LXMF;

namespace Sideband;

public enum SidebandPaperIngestResult
{
    Rejected,
    NonLocal,
    LocalDelivery,
    Duplicate,
}

public sealed partial class SidebandClient
{
    /// <summary>Maximum combined creations/imports, including callers waiting for the lifecycle gate.</summary>
    public const int MaximumPendingPaperOperations = 8;
    private readonly SemaphoreSlim _paperAdmission = new(MaximumPendingPaperOperations, MaximumPendingPaperOperations);

    /// <summary>Creates an encrypted, signed offline message; does not transmit or assert recipient delivery. Capacity exhaustion fails immediately.</summary>
    public async Task<SidebandChatMessage> CreatePaperMessageAsync(string deliveryHash, string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0 || content.Length > LxMessage.PaperMdu)
            throw new ArgumentException("Paper content must be nonempty and fit the LXMF paper limit.", nameof(content));
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var normalized))
            throw new ArgumentException("An LXMF delivery address must be exactly 32 hexadecimal characters.", nameof(deliveryHash));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        AdmitPaperOperation(cancellationToken);
        try
        {
            using var owned = PaperOperationCancellation(cancellationToken);
            await _lifecycle.WaitAsync(owned.Token).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0) owned.Cancel();
                owned.Token.ThrowIfCancellationRequested();
                _ = _router ?? throw new InvalidOperationException("Connect to Reticulum before creating paper messages.");
                var identity = _identity ?? throw new InvalidOperationException("The local Reticulum identity is unavailable.");
                var node = _node ?? throw new InvalidOperationException("The local Reticulum node is unavailable.");
                var destination = Convert.FromHexString(normalized);
                using var recipient = await node.ResolveIdentityAsync(destination, TimeSpan.FromSeconds(15), owned.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("No announce or path was found for this LXMF delivery address.");
                var state = await _store.LoadAsync(cancellationToken: owned.Token).ConfigureAwait(false);
                var fields = await OrdinaryTelemetryFieldsAsync(state, normalized, owned.Token).ConfigureAwait(false);
                var message = LxMessage.Create(destination, identity, content, title: "", fields: fields, method: LxmMethod.Paper);
                // Primary paper_message sets this flag but bypasses outbound ticket creation.
                message.IncludeTicket = state.Contacts.Any(contact => contact.DeliveryHash == normalized && contact.Trusted);
                owned.Token.ThrowIfCancellationRequested();
                message.Pack(destinationIdentity: recipient, ratchet: node.Router.Engine.Known.GetRatchet(destination));
                var local = new SidebandChatMessage(Guid.NewGuid(), normalized, false, "", content,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)(message.Timestamp * 1000)), "Paper",
                    DeliveryMethod: "Paper", PaperUri: message.AsUri());
                await _store.AddMessageAsync(local, owned.Token).ConfigureAwait(false);
                PublishState(await _store.LoadAsync(cancellationToken: owned.Token).ConfigureAwait(false));
                return local;
            }
            finally { _lifecycle.Release(); }
        }
        finally { _paperAdmission.Release(); }
    }

    /// <summary>Returns the exact persisted export, including while disconnected.</summary>
    public async Task<string> GetPaperMessageUriAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var state = await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return state.Messages.SingleOrDefault(message => message.Id == messageId)?.PaperUri
            ?? throw new InvalidOperationException("The selected message has no saved paper URI.");
    }

    /// <summary>Writes an explicit UTF-8 export without overwriting an existing file.</summary>
    public async Task ExportPaperMessageAsync(Guid messageId, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var uri = await GetPaperMessageUriAsync(messageId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous);
        try
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(uri), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            File.Delete(path);
            throw;
        }
    }

    /// <summary>
    /// Reuses normal LXMF duplicate-aware inbound processing. LocalDelivery means accepted by the router,
    /// with history persisted by the existing inbound worker (drained on disconnect), not a network delivery.
    /// NonLocal identifies only a syntactically valid envelope addressed elsewhere, not authenticated content.
    /// Cancellation stops waiting, not an already accepted delivery.
    /// </summary>
    public async Task<SidebandPaperIngestResult> IngestPaperUriAsync(string uri, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] destination;
        try { destination = LxMessage.GetPaperDestinationHash(uri); }
        catch (FormatException) { return SidebandPaperIngestResult.Rejected; }
        catch (ArgumentNullException) { return SidebandPaperIngestResult.Rejected; }
        AdmitPaperOperation(cancellationToken);
        try
        {
            using var owned = PaperOperationCancellation(cancellationToken);
            await _lifecycle.WaitAsync(owned.Token).ConfigureAwait(false);
            try
            {
                // Disposal cancels admitted work even before linked cancellation callbacks finish.
                if (Volatile.Read(ref _disposed) != 0) owned.Cancel();
                owned.Token.ThrowIfCancellationRequested();
                var router = _router ?? throw new InvalidOperationException("Connect to Reticulum before importing paper messages.");
                if (!destination.AsSpan().SequenceEqual(router.DeliveryHash)) return SidebandPaperIngestResult.NonLocal;
                return await router.IngestPaperUriAsync(uri, false, owned.Token).ConfigureAwait(false) switch
                {
                    LxmPaperIngestResult.LocalDelivery => SidebandPaperIngestResult.LocalDelivery,
                    LxmPaperIngestResult.Duplicate => SidebandPaperIngestResult.Duplicate,
                    _ => SidebandPaperIngestResult.Rejected,
                };
            }
            finally { _lifecycle.Release(); }
        }
        finally { _paperAdmission.Release(); }
    }

    private void AdmitPaperOperation(CancellationToken token)
    {
        if (!_paperAdmission.Wait(0, token))
            throw new InvalidOperationException($"Paper operation capacity reached ({MaximumPendingPaperOperations} pending).");
    }

    private CancellationTokenSource PaperOperationCancellation(CancellationToken token)
    {
        lock (_cancellationGate)
            return _runCancellation is { } run
                ? CancellationTokenSource.CreateLinkedTokenSource(token, run.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(token);
    }
}
