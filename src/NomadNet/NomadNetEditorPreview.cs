using System.Diagnostics;
using System.Threading.Channels;

namespace NomadNet;

public sealed record NomadNetEditorPreviewResult(long Revision, string Text, string? Error = null);

/// <summary>One owned worker and one pending snapshot; observers never own rendering or terminal output.</summary>
public sealed class NomadNetEditorPreview : IAsyncDisposable, IDisposable
{
    [ThreadStatic] private static NomadNetEditorPreview? _callbackOwner;
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);
    private readonly object _gate = new();
    private readonly Channel<(long Revision, string Text)> _changes = Channel.CreateBounded<(long, string)>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeProvider _clock;
    private readonly Func<string, string> _render;
    private readonly Task _worker;
    private long _revision;
    private bool _disposed;
    private bool _resourcesDisposed;
    private NomadNetEditorPreviewResult? _current;
    public event Action<NomadNetEditorPreviewResult>? Rendered;
    public Action<string>? Log { get; set; }
    public NomadNetEditorPreviewResult? Current { get { lock (_gate) return _current; } }
    public long Revision { get { lock (_gate) return _revision; } }

    public NomadNetEditorPreview(Func<string, string> render, TimeProvider? clock = null)
    {
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _clock = clock ?? TimeProvider.System;
        _worker = RunAsync();
    }

    public void Change(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > NomadNetEditor.MaximumBytes) throw new InvalidDataException("Preview snapshot exceeds the editor bound.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _revision++;
            _changes.Writer.TryWrite((_revision, text));
        }
    }

    public void Select(string? immediate)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _revision++;
            _current = immediate is null ? null : new(_revision, immediate);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _changes.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                if (!_changes.Reader.TryRead(out var snapshot)) continue;
                while (true)
                {
                    using var cycle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    var delay = Task.Delay(Delay, _clock, cycle.Token);
                    var changed = _changes.Reader.WaitToReadAsync(cycle.Token).AsTask();
                    var winner = await Task.WhenAny(delay, changed).ConfigureAwait(false);
                    var elapsed = winner == delay;
                    await cycle.CancelAsync().ConfigureAwait(false);
                    try { await delay.ConfigureAwait(false); } catch (OperationCanceledException) when (cycle.IsCancellationRequested) { }
                    try { await changed.ConfigureAwait(false); } catch (OperationCanceledException) when (cycle.IsCancellationRequested) { }
                    _stop.Token.ThrowIfCancellationRequested();
                    if (_changes.Reader.TryRead(out var next)) { snapshot = next; continue; }
                    if (!elapsed) continue;
                    break;
                }
                lock (_gate) { if (snapshot.Revision != _revision || _disposed) continue; }
                NomadNetEditorPreviewResult result;
                try { result = new(snapshot.Revision, InvokeCallback(() => _render(snapshot.Text))); }
                catch (Exception error)
                {
                    Report("Editor preview render failed: " + error.Message);
                    result = new(snapshot.Revision, "Preview failed; editing remains available.", error.Message);
                }
                lock (_gate)
                {
                    if (_disposed || snapshot.Revision != _revision) continue;
                    _current = result;
                }
                foreach (var observer in Rendered?.GetInvocationList() ?? [])
                {
                    try { InvokeCallback(() => { ((Action<NomadNetEditorPreviewResult>)observer)(result); return true; }); }
                    catch (Exception error) { Report("Editor preview observer failed: " + error.Message); }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void Report(string message)
    {
        try { if (Log is { } log) InvokeCallback(() => { log(message); return true; }); else Trace.TraceError(message); }
        catch (Exception error) { Trace.TraceError("Editor preview logger failed: " + error.Message); }
    }

    private T InvokeCallback<T>(Func<T> callback)
    {
        var previous = _callbackOwner;
        _callbackOwner = this;
        try { return callback(); }
        finally { _callbackOwner = previous; }
    }

    public async ValueTask DisposeAsync()
    {
        if (ReferenceEquals(_callbackOwner, this))
            throw new InvalidOperationException("An editor preview callback cannot join its own worker; dispose from the owning session.");
        lock (_gate)
        {
            if (!_disposed) { _disposed = true; _revision++; _changes.Writer.TryComplete(); _stop.Cancel(); }
        }
        await _worker.ConfigureAwait(false);
        lock (_gate)
        {
            _current = null;
            Rendered = null;
            if (!_resourcesDisposed) { _stop.Dispose(); _resourcesDisposed = true; }
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
