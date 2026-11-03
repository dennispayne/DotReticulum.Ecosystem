using LXMF;

namespace NomadNet;

public sealed record NomadNetPropagationCandidate(string NodeHash, string PropagationHash, int Hops);

/// <summary>Application policy only; transport, stamp work and retrieval belong to LXMF.</summary>
public sealed class NomadNetPropagation : IAsyncDisposable
{
    private readonly string _directory;
    private readonly Func<IEnumerable<NomadNetPropagationCandidate>> _candidates;
    private readonly Action<string?> _select;
    private readonly Func<int, CancellationToken, Task<int>> _retrieve;
    private readonly Action _cancel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _started;
    private CancellationTokenSource? _request;
    private Task _sync = Task.CompletedTask;
    private Task _scheduler = Task.CompletedTask;
    private int _disposed;
    private int _schedulerStarted;
    private int _running;
    private NomadNetSettings _settings;
    private string? _selected;
    private string _status = "Idle";

    public NomadNetPropagation(string directory, NomadNetSettings settings,
        Func<IEnumerable<NomadNetPropagationCandidate>> candidates, Action<string?> select,
        Func<int, CancellationToken, Task<int>> retrieve, Action cancel, TimeProvider? timeProvider = null)
    {
        _directory = directory;
        _settings = settings with { TrustedNodes = [.. settings.TrustedNodes] };
        _candidates = candidates;
        _select = select;
        _retrieve = retrieve;
        _cancel = cancel;
        _time = timeProvider ?? TimeProvider.System;
        _started = _time.GetUtcNow();
    }

    public Action<string>? Log { get; set; }
    public NomadNetSettings Settings
    {
        get
        {
            var current = Volatile.Read(ref _settings);
            return current with { TrustedNodes = [.. current.TrustedNodes] };
        }
    }
    public string? Selected => Volatile.Read(ref _selected);
    public string Status => Volatile.Read(ref _status);
    public bool IsSyncing => Volatile.Read(ref _running) != 0;

    public static string? SelectNode(string? manual, IEnumerable<string> trustedNodes,
        IEnumerable<NomadNetPropagationCandidate> candidates)
    {
        if (manual is not null) return NomadNetAddress.NormalizeHash(manual);
        var trusted = trustedNodes.Select(NomadNetAddress.NormalizeHash).ToHashSet(StringComparer.Ordinal);
        return candidates.Where(candidate => candidate.Hops >= 0 &&
                trusted.Contains(NomadNetAddress.NormalizeHash(candidate.NodeHash)))
            .OrderBy(candidate => candidate.Hops).FirstOrDefault()?.PropagationHash;
    }

    public static bool ShouldFallback(LxmState state, LxmMethod method, bool enabled,
        bool hasNode, bool preparationFailed = false) =>
        state == LxmState.Failed && method is LxmMethod.Direct or LxmMethod.Opportunistic &&
        enabled && hasNode && !preparationFailed;

    public bool IsDue(DateTimeOffset now) =>
        _settings.PeriodicLxmfSync && _settings.UsePropagation &&
        now > _started.AddSeconds(90) &&
        now > (_settings.LastLxmfSync ?? DateTimeOffset.UnixEpoch).AddMinutes(_settings.LxmfSyncIntervalMinutes);

    public void Start()
    {
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (Interlocked.Exchange(ref _schedulerStarted, 1) != 0)
                throw new InvalidOperationException("Propagation scheduler already started.");
            _scheduler = ScheduleAsync();
        }
        finally { _gate.Release(); }
    }

    private async Task ScheduleAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _time);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    await RefreshSelectionAsync(_lifetime.Token).ConfigureAwait(false);
                    if (IsDue(_time.GetUtcNow()) && Selected is not null)
                        await BeginSyncAsync(_settings.LxmfSyncLimit, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Report($"Propagation scheduler: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    public async Task UpdateSettingsAsync(Func<NomadNetSettings, NomadNetSettings> update,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var changed = update(Settings);
            await changed.SaveAsync(_directory, cancellationToken).ConfigureAwait(false);
            _settings = changed;
            RefreshSelection();
        }
        finally { _gate.Release(); }
    }

    public async Task RefreshSelectionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            RefreshSelection();
        }
        finally { _gate.Release(); }
    }

    private void RefreshSelection()
    {
        var selected = _settings.UsePropagation
            ? SelectNode(_settings.PropagationNode, _settings.TrustedNodes, _candidates()) : null;
        if (selected == _selected) return;
        _select(selected);
        Volatile.Write(ref _selected, selected);
        Report($"Propagation node: {selected ?? "none"}");
    }

    public async Task<bool> BeginSyncAsync(int limit = 0, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (IsSyncing) return false;
            RefreshSelection();
            if (Selected is null) throw new InvalidOperationException("No propagation node selected.");
            var changed = _settings with { LastLxmfSync = _time.GetUtcNow() };
            await changed.SaveAsync(_directory, cancellationToken).ConfigureAwait(false);
            _settings = changed;
            _request?.Dispose();
            _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            Volatile.Write(ref _running, 1);
            Volatile.Write(ref _status, "Synchronizing");
            _sync = RetrieveAsync(limit, _request.Token);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task RetrieveAsync(int limit, CancellationToken cancellationToken)
    {
        try
        {
            var count = await _retrieve(limit, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _status, $"Complete: {count} messages");
            Report($"Propagation sync: {count} messages (Complete).");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref _status, "Cancelled");
            Report("Propagation sync cancelled.");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _status, $"Failed: {ex.Message}");
            Report($"Propagation sync failed: {ex.Message}");
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    public async Task CancelSyncAsync(CancellationToken cancellationToken = default)
    {
        Task pending;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsSyncing) return;
            _request?.Cancel();
            _cancel();
            pending = _sync;
        }
        finally { _gate.Release(); }
        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task WaitForSyncAsync(CancellationToken cancellationToken = default) => _sync.WaitAsync(cancellationToken);

    private void Report(string message)
    {
        if (Log is not { } callbacks) return;
        foreach (Action<string> observer in callbacks.GetInvocationList())
            try { observer(message); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"NomadNet propagation observer: {ex}"); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel();
            _request?.Cancel();
            if (IsSyncing) _cancel();
        }
        finally { _gate.Release(); }
        await Task.WhenAll(_scheduler, _sync).ConfigureAwait(false);
        _request?.Dispose();
        _lifetime.Dispose();
    }
}
