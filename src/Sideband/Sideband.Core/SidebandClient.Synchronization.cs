namespace Sideband;

public sealed partial class SidebandClient
{
    public async Task ConfigureSynchronizationAsync(SidebandSynchronizationConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var cancellation = RuntimeOperationCancellation(cancellationToken);
        await _lifecycle.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _store.ConfigureSynchronizationAsync(configuration, cancellation.Token).ConfigureAwait(false);
            PublishState(await _store.LoadAsync().ConfigureAwait(false));
        }
        finally { _lifecycle.Release(); }
    }

    public Task<int> SynchronizeConfiguredAsync(CancellationToken cancellationToken = default) =>
        SynchronizeCoreAsync(null, cancellationToken);
}
