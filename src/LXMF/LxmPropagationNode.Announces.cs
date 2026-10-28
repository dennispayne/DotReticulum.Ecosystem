using System.Threading.Channels;

namespace LXMF;

public sealed partial class LxmPropagationNode
{
    private readonly Channel<double> _announcements = Channel.CreateBounded<double>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private Task _announcer = Task.CompletedTask;
    private int _pendingAnnouncements;

    /// <summary>Schedules the source-compatible announcement after 20 seconds.</summary>
    public void ScheduleAnnounce()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pendingAnnouncements >= 256 || !_announcements.Writer.TryWrite(Now + 20))
                throw new InvalidOperationException("Propagation announcement capacity reached.");
            _pendingAnnouncements++;
        }
    }

    private async Task RunAnnouncementsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var due in _announcements.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var remaining = due - Now;
                    if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining), _options.TimeProvider, ct).ConfigureAwait(false);
                    await AnnounceAsync(ct).ConfigureAwait(false);
                    Report("Announced LXMF propagation node.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Report("Could not announce propagation node: " + ex.Message); }
                finally { lock (_gate) _pendingAnnouncements--; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
