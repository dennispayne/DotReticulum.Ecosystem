namespace Sideband.Desktop;

/// <summary>Four owned operations, each with a two-minute deadline. No unobserved background tasks.</summary>
internal sealed class SidebandConsoleOperations : IAsyncDisposable
{
    private sealed record Work(string Name, CancellationTokenSource Stop, Task Task);
    private readonly List<Work> _work = [];
    private readonly Action<string> _output;
    private readonly Action<string> _error;
    private readonly CancellationToken _lifetime;

    public SidebandConsoleOperations(Action<string> output, Action<string> error, CancellationToken lifetime)
    { _output = output; _error = error; _lifetime = lifetime; }

    public int Count => _work.Count(w => !w.Task.IsCompleted);

    public void Start(string name, Func<CancellationToken, Task<string>> action, bool unique = false)
    {
        Reap();
        if (_work.Count >= 4) throw new InvalidOperationException("The console has four active operations. Use cancel or wait.");
        if (unique && _work.Any(w => w.Name == name)) throw new InvalidOperationException(name + " is already in progress.");
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        stop.CancelAfter(TimeSpan.FromMinutes(2));
        var task = Task.Run(async () =>
        {
            try { _output(await action(stop.Token).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { _output("CANCELLED " + name); }
            catch (Exception exception) { _error(name + ": " + SidebandConsoleSyntax.Safe(exception.Message)); }
        }, CancellationToken.None);
        _work.Add(new(name, stop, task));
        _output("QUEUED " + name);
    }

    public async Task CancelAsync(string? name = null)
    {
        var work = _work.Where(w => name is null || w.Name == name).ToArray();
        foreach (var item in work) await item.Stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(work.Select(w => w.Task)).ConfigureAwait(false);
        Reap();
    }

    private void Reap()
    {
        foreach (var item in _work.Where(w => w.Task.IsCompleted).ToArray())
        { item.Stop.Dispose(); _work.Remove(item); }
    }

    public async ValueTask DisposeAsync() => await CancelAsync().ConfigureAwait(false);
}
