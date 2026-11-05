namespace Sideband;

public sealed partial class SidebandClient
{
    public async Task ConfigurePrintingAsync(bool enabled, string command, bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.ConfigurePrintingAsync(enabled, command, explicitlyApproved, cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public SidebandPrintingService CreatePrintingService(TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new(_store, timeout);
    }
}
