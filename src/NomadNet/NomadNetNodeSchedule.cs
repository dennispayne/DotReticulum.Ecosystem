namespace NomadNet;

public readonly record struct NomadNetNodeJobs(bool Announce, bool Pages, bool Files);

/// <summary>Independent host-maintenance deadlines with explicit completion tracking.</summary>
public sealed class NomadNetNodeSchedule
{
    private readonly TimeSpan _announceInterval;
    private readonly TimeSpan _pagesInterval;
    private readonly TimeSpan _filesInterval;
    private readonly object _gate = new();
    private DateTimeOffset _announced;
    private DateTimeOffset _pages;
    private DateTimeOffset _files;

    public NomadNetNodeSchedule(DateTimeOffset started, TimeSpan announceInterval, TimeSpan pagesInterval, TimeSpan filesInterval)
    {
        if (announceInterval <= TimeSpan.Zero || pagesInterval < TimeSpan.Zero || filesInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(announceInterval), "Announce interval must be positive; refresh intervals cannot be negative.");
        _announceInterval = announceInterval;
        _pagesInterval = pagesInterval;
        _filesInterval = filesInterval;
        _announced = _pages = _files = started;
    }

    public NomadNetNodeJobs Due(DateTimeOffset now)
    {
        lock (_gate) return new(now - _announced > _announceInterval,
            _pagesInterval > TimeSpan.Zero && now - _pages > _pagesInterval,
            _filesInterval > TimeSpan.Zero && now - _files > _filesInterval);
    }

    public void Completed(DateTimeOffset now, NomadNetNodeJobs jobs)
    {
        lock (_gate)
        {
            if (jobs.Announce) _announced = now;
            if (jobs.Pages) _pages = now;
            if (jobs.Files) _files = now;
        }
    }
}
