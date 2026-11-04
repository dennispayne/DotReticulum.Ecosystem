namespace Sideband;

public sealed partial class SidebandClient
{
    public async Task ConfigureDeliveryAsync(SidebandDeliveryConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var cancellation = RuntimeOperationCancellation(cancellationToken);
        await _lifecycle.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _store.ConfigureDeliveryAsync(configuration, cancellation.Token).ConfigureAwait(false);
            if (_router is { } router) router.DeliveryPerTransferLimit = configuration.MaximumTransferBytes;
            PublishState(await _store.LoadAsync().ConfigureAwait(false));
        }
        finally { _lifecycle.Release(); }
    }
}
