using System.Diagnostics;

namespace NomadNet;

public sealed class NomadNetClientAnnounces : IAsyncDisposable
{
    public const int MaximumPending = 16;
    private readonly Func<NomadNetSettings> _settings;
    private readonly Func<CancellationToken, ValueTask> _announce;
    private readonly Func<DateTimeOffset, CancellationToken, Task> _save;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _announceGate = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _jobs;
    private Task? _disposal;
    private bool _disposed;
    private int _pending;

    public NomadNetClientAnnounces(Func<NomadNetSettings> settings, Func<CancellationToken, ValueTask> announce,
        Func<DateTimeOffset, CancellationToken, Task> save, TimeProvider? timeProvider = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _announce = announce ?? throw new ArgumentNullException(nameof(announce));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _time = timeProvider ?? TimeProvider.System;
    }

    public Action<string>? Log { get; set; }

    public static bool PeriodicDue(NomadNetSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ClientAnnounceIntervalMinutes is < 30 or > 10080)
            throw new ArgumentOutOfRangeException(nameof(settings), "Client announce interval must be 30–10080 minutes.");
        // A fresh primary profile with startup disabled crashes its jobs on None + interval.
        // Keep servicing other work, but require a first successful announce before periodic ones.
        return settings.LastClientAnnounce is { } last &&
            now - last > TimeSpan.FromMinutes(settings.ClientAnnounceIntervalMinutes);
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_jobs is not null) throw new InvalidOperationException("Client announcements are already started.");
            _jobs = RunAsync(_settings().ClientAnnounceAtStart, _stop.Token);
        }
    }

    public async ValueTask AnnounceNowAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linked;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending >= MaximumPending) throw new InvalidOperationException("Client announcement request limit reached.");
            linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
            _pending++;
        }
        try
        {
            await _announceGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await _announce(linked.Token).ConfigureAwait(false);
                await _save(_time.GetUtcNow(), linked.Token).ConfigureAwait(false);
            }
            finally { _announceGate.Release(); }
        }
        finally
        {
            linked.Dispose();
            lock (_lifecycle)
            {
                _pending--;
                if (_disposed && _pending == 0) _drained.TrySetResult();
            }
        }
    }

    private async Task RunAsync(bool announceAtStart, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _time, cancellationToken).ConfigureAwait(false);
            if (announceAtStart) await TryAnnounceAsync("startup", cancellationToken).ConfigureAwait(false);
            var remaining = TimeSpan.FromSeconds(90) - _time.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, _time, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (PeriodicDue(_settings(), _time.GetUtcNow()))
                    await TryAnnounceAsync("periodic", cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(5), _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { Report("Client announcement service stopped: " + exception.Message); }
    }

    private async Task TryAnnounceAsync(string context, CancellationToken cancellationToken)
    {
        try { await AnnounceNowAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception exception) { Report($"Client {context} announce failed: {exception.Message}"); }
    }

    private void Report(string message)
    {
        Trace.TraceError(message);
        if (Log is not { } callbacks) return;
        foreach (Action<string> callback in callbacks.GetInvocationList())
            try { callback(message); }
            catch (Exception exception) { Trace.TraceError("NomadNet client announce observer: {0}", exception.Message); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            _disposal = DisposeCoreAsync(_pending == 0 ? Task.CompletedTask : _drained.Task);
            return new(_disposal);
        }
    }

    private async Task DisposeCoreAsync(Task drained)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_jobs is not null) await _jobs.ConfigureAwait(false);
        await drained.ConfigureAwait(false);
        _announceGate.Dispose();
        _stop.Dispose();
    }
}
