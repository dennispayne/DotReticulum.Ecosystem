using LXMF;

namespace Sideband;

public sealed partial class SidebandClient
{
    private Task? _startupAnnounceTask;

    public async Task ConfigureAnnouncementAsync(SidebandAnnounceConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var cancellation = RuntimeOperationCancellation(cancellationToken);
        await _lifecycle.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _store.ConfigureAnnouncementAsync(configuration, cancellation.Token).ConfigureAwait(false);
            PublishState(await _store.LoadAsync().ConfigureAwait(false));
        }
        finally { _lifecycle.Release(); }
    }

    private async Task AnnounceCoreAsync(LxmRouter expectedRouter, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var cancellation = RuntimeOperationCancellation(cancellationToken);
        await _lifecycle.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var router = _router ?? throw new InvalidOperationException("Connect to Reticulum before announcing.");
            if (!ReferenceEquals(router, expectedRouter)) return;
            var state = await _store.LoadAsync(cancellationToken: cancellation.Token).ConfigureAwait(false);
            if (!SidebandAnnounceConfiguration.FromState(state).AnnounceOnStartup) return;
            await router.AnnounceAsync(cancellation.Token).ConfigureAwait(false);
            if (_voice is { } voice) await voice.AnnounceAsync(cancellation.Token).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task AnnounceAfterStartupAsync(LxmRouter router, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SidebandAnnounceConfiguration.StartupDelay, cancellationToken).ConfigureAwait(false);
            await AnnounceCoreAsync(router, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { Report("Startup announcement failed: " + ex.Message); }
    }
}
