namespace Sideband;

public sealed partial class SidebandClient
{
    public async Task ConfigureConversationAppearanceAsync(SidebandConversationAppearance configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var cancellation = RuntimeOperationCancellation(cancellationToken);
        await _lifecycle.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _store.ConfigureConversationAppearanceAsync(configuration, cancellation.Token).ConfigureAwait(false);
            PublishState(await _store.LoadAsync().ConfigureAwait(false));
        }
        finally { _lifecycle.Release(); }
    }
}
