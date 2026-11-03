using System.Text;

namespace NomadNet;

/// <summary>One page's bounded, cancellable partial updates and composed form state.</summary>
public sealed class NomadNetMicronBrowser : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly NomadNetRenderedPage _root;
    private readonly Func<NomadNetPagePartial, Dictionary<object, object?>?, CancellationToken, Task<byte[]>> _fetch;
    private readonly TimeProvider _clock;
    private readonly Entry[] _entries;
    private readonly SemaphoreSlim _requests = new(4, 4);
    private readonly CancellationTokenSource _stop = new();
    private NomadNetRenderedPage _page;
    private Task? _runner;
    private bool _disposed;

    public NomadNetMicronBrowser(NomadNetRenderedPage root,
        Func<NomadNetPagePartial, Dictionary<object, object?>?, CancellationToken, Task<byte[]>> fetch,
        TimeProvider? clock = null)
    {
        _root = root;
        _page = root;
        _fetch = fetch;
        _clock = clock ?? TimeProvider.System;
        _entries = root.Partials.Select(partial => new Entry(partial)).ToArray();
    }

    public event Action<NomadNetRenderedPage>? Changed;
    public Action<string>? Log { get; set; }
    public NomadNetRenderedPage Current { get { lock (_gate) return _page; } }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.Length > 0) _runner ??= Task.Run(RunAsync);
        }
    }

    public static bool IsDue(DateTimeOffset now, DateTimeOffset? updated, double? seconds) =>
        updated is null || seconds is { } interval && (now - updated.Value).TotalSeconds > interval;

    public void SetField(int number, string value)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _page.SetField(number, value); }
    }

    public string ToggleFold(int number)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _page.ToggleFold(number);
            return _page.DisplayText;
        }
    }

    public string AtAnchor(string name)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _page.AtAnchor(name); }
    }

    public Dictionary<object, object?>? Submission(string selection)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _page.Submission(selection); }
    }

    public Task RefreshDueAsync(CancellationToken cancellationToken = default)
    {
        Task[] tasks;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _clock.GetUtcNow();
            tasks = _entries.Where(entry => entry.Pending is not { IsCompleted: false } &&
                IsDue(now, entry.Updated, entry.Partial.RefreshSeconds))
                .Select(entry => Launch(entry, cancellationToken)).ToArray();
        }
        return Task.WhenAll(tasks);
    }

    public async Task RefreshAsync(int number, CancellationToken cancellationToken = default)
    {
        Entry entry;
        Task? pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (number < 1 || number > _entries.Length) throw new ArgumentOutOfRangeException(nameof(number));
            entry = _entries[number - 1];
            pending = entry.Pending;
        }
        if (pending is not null) await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task refresh;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            refresh = entry.Pending is { IsCompleted: false } current ? current : Launch(entry, cancellationToken);
        }
        await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task Launch(Entry entry, CancellationToken cancellationToken) =>
        entry.Pending = Task.Run(() => UpdateAsync(entry, cancellationToken));

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await RefreshDueAsync(_stop.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), _clock, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task UpdateAsync(Entry entry, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        await _requests.WaitAsync(linked.Token).ConfigureAwait(false);
        linked.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var requested = _clock.GetUtcNow();
            try
            {
                Dictionary<object, object?>? data;
                lock (_gate) data = _page.Submission(entry.Partial.Fields) ?? [];
                var bytes = await _fetch(entry.Partial, data, linked.Token).ConfigureAwait(false);
                var child = MicronText.Render(bytes);
                Publish(entry, child, requested);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested || cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                var context = entry.Partial.Target + ": " + exception.Message;
                var message = "Could not load partial " + context[..Math.Min(context.Length, 4096)];
                Report(message);
                var child = MicronText.Render(Encoding.UTF8.GetBytes("`=\n" + NomadNetTerminalText.Safe(message)));
                Publish(entry, child, _clock.GetUtcNow());
            }
        }
        finally { _requests.Release(); }
    }

    private void Publish(Entry entry, NomadNetRenderedPage child, DateTimeOffset updated)
    {
        NomadNetRenderedPage page;
        lock (_gate)
        {
            if (_disposed || _stop.IsCancellationRequested) return;
            var children = _entries.Where(item => item.Content is not null).ToDictionary(item => item.Partial, item => item.Content!);
            children[entry.Partial] = child;
            page = NomadNetMicronComposition.Compose(_root, children);
            entry.Content = child;
            entry.Updated = updated;
            _page = page;
        }
        foreach (var observer in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action<NomadNetRenderedPage>)observer)(page); }
            catch (Exception exception) { Report("Micron partial observer failed: " + exception.Message); }
        }
    }

    private void Report(string message)
    {
        try { Log?.Invoke(message); }
        catch { /* Observers must not stop page processing. */ }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            tasks = _entries.Select(entry => entry.Pending).Append(_runner).OfType<Task>().ToArray();
        }
        _stop.Cancel();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { _stop.Dispose(); _requests.Dispose(); }
    }

    private sealed class Entry(NomadNetPagePartial partial)
    {
        public NomadNetPagePartial Partial { get; } = partial;
        public NomadNetRenderedPage? Content { get; set; }
        public DateTimeOffset? Updated { get; set; }
        public Task? Pending { get; set; }
    }
}
