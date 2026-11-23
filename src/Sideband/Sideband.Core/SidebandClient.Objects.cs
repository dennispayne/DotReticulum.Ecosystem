namespace Sideband;

public sealed partial class SidebandClient
{
    public async Task ImportFixedLocationsAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var locations = await SidebandFixedLocations.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        await _store.ConfigureFixedLocationsAsync(locations, cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public async Task ClearFixedLocationsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.ConfigureFixedLocationsAsync([], cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public async Task SetConversationObjectAsync(string deliveryHash, bool isObject, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.SetConversationObjectAsync(deliveryHash, isObject, cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }
}
