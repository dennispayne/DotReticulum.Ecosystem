namespace Sideband;

public sealed partial class SidebandClient
{
    public async Task ConfigureMessageFormattingAsync(SidebandMessageFormatting configuration,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.ConfigureMessageFormattingAsync(configuration, cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }
}
