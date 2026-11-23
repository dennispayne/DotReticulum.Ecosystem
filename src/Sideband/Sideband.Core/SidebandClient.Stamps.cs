namespace Sideband;

public sealed partial class SidebandClient
{
    public async Task ConfigureStampPolicyAsync(SidebandStampConfiguration configuration,
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
            await _store.ConfigureStampPolicyAsync(configuration, cancellation.Token).ConfigureAwait(false);
            if (_router is { } router)
            {
                if (!router.SetInboundStampCost(configuration.EffectiveCost))
                    throw new InvalidOperationException("The active LXMF router refused the validated stamp cost.");
                if (configuration.RejectInvalidStamps) router.EnforceStamps();
                else router.IgnoreStamps();
                try { await router.AnnounceAsync(cancellation.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Report("Stamp policy was saved and applied, but its delivery announcement failed: " + ex.Message);
                    throw;
                }
            }
            PublishState(await _store.LoadAsync().ConfigureAwait(false));
        }
        finally { _lifecycle.Release(); }
    }
}
