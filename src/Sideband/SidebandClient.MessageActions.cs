namespace Sideband;

public sealed partial class SidebandClient
{
    public const int MaximumPendingMessageRetries = 8;
    private readonly SemaphoreSlim _messageRetryAdmission = new(MaximumPendingMessageRetries, MaximumPendingMessageRetries);

    public async Task<SidebandChatMessage> RetryMessageTextAsync(string deliveryHash, Guid id,
        CancellationToken cancellationToken = default)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var normalized))
            throw new ArgumentException("An LXMF delivery address must be exactly 32 hexadecimal characters.", nameof(deliveryHash));
        if (id == Guid.Empty) throw new ArgumentException("A saved message ID is required.", nameof(id));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_messageRetryAdmission.Wait(0))
            throw new InvalidOperationException("The pending message retry limit has been reached.");
        try
        {
            using var owned = RuntimeOperationCancellation(cancellationToken);
            await _lifecycle.WaitAsync(owned.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var state = await LoadAsync(owned.Token).ConfigureAwait(false);
                var selected = state.Messages.FirstOrDefault(message => message.Id == id && message.PeerDeliveryHash == normalized);
                if (selected is null || !SidebandMessageActions.CanRetryText(selected))
                    throw new InvalidOperationException("Select an outgoing failed non-paper message with nonempty bounded text.");
                return await SendMessageCoreAsync(normalized, selected.Content, null, null, owned.Token,
                    replacedFailedMessageId: id).ConfigureAwait(false);
            }
            finally { _lifecycle.Release(); }
        }
        finally { _messageRetryAdmission.Release(); }
    }
}
