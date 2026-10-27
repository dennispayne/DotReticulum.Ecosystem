namespace DotLXST;

/// <summary>Device-independent playback; the caller owns the output sink.</summary>
public sealed class FilePlayer : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IAudioSink _sink;
    private readonly string? _libraryPath;
    private string? _path;
    private bool _loop;
    private bool _playing;
    private bool _disposed;
    private CancellationTokenSource? _stop;
    private Task _completion = Task.CompletedTask;
    private Task? _disposal;

    public FilePlayer(IAudioSink sink, string? path = null, bool loop = false, string? libraryPath = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _libraryPath = libraryPath;
        _loop = loop;
        if (path is not null) SetSource(path);
    }

    public bool IsPlaying { get { lock (_gate) return _playing; } }
    public Task Completion { get { lock (_gate) return _completion; } }
    public event Action<FilePlayer>? Finished;
    public Action<string, Exception?>? Error { get; set; }

    public void SetSource(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Playback source does not exist.", fullPath);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_completion.IsCompleted) throw new InvalidOperationException("Stop playback before replacing its source.");
            _path = fullPath;
        }
    }

    public void SetLoop(bool loop)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loop = loop;
        }
    }

    public void Play(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_completion.IsCompleted || _path is null) return;
            _stop?.Dispose();
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _playing = true;
            var path = _path;
            var token = _stop.Token;
            _completion = Task.Run(() => RunAsync(path, token));
        }
    }

    private async Task RunAsync(string path, CancellationToken stop)
    {
        try
        {
            do
            {
                await using var source = new OpusFileSource(path, timed: true, libraryPath: _libraryPath);
                await foreach (var frame in source.CaptureAsync(stop).ConfigureAwait(false))
                    await _sink.WriteAsync(frame, stop).ConfigureAwait(false);
                lock (_gate) if (!_loop) break;
            } while (!stop.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            lock (_gate) _playing = false;
            if (Finished is { } finished)
                foreach (Action<FilePlayer> observer in finished.GetInvocationList())
                    try { observer(this); }
                    catch (Exception ex) { Report("File playback completion observer failed.", ex); }
        }
    }

    private void Report(string context, Exception exception)
    {
        try
        {
            if (Error is { } report) report(context, exception);
            else System.Diagnostics.Trace.TraceError($"{context} {exception}");
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"{context} {exception}; error observer: {ex}"); }
    }

    public async ValueTask StopAsync()
    {
        Task completion;
        lock (_gate)
        {
            _stop?.Cancel();
            completion = _completion;
        }
        await completion.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            return new(_disposal = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally { lock (_gate) { _stop?.Dispose(); _stop = null; } }
    }
}

/// <summary>Records a caller-owned source; each run finalizes its own native writer.</summary>
public sealed class FileRecorder : IAsyncDisposable
{
    private readonly object _gate = new();
    private IAudioSource _source;
    private string _path;
    private readonly OpusProfile _profile;
    private readonly string? _libraryPath;
    private CancellationTokenSource? _stop;
    private Task _completion = Task.CompletedTask;
    private Task? _disposal;
    private bool _disposed;

    public FileRecorder(IAudioSource source, string path, OpusProfile profile = OpusProfile.AudioMaximum,
        string? libraryPath = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = Path.GetFullPath(path);
        _profile = profile;
        _libraryPath = libraryPath;
    }

    public bool IsRecording { get { lock (_gate) return !_completion.IsCompleted; } }
    public Task Completion { get { lock (_gate) return _completion; } }

    public void SetSource(IAudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate) { EnsureStopped(); _source = source; }
    }

    public void SetOutputPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_gate) { EnsureStopped(); _path = Path.GetFullPath(path); }
    }

    private void EnsureStopped()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_completion.IsCompleted) throw new InvalidOperationException("Stop recording before reconfiguring it.");
    }

    public void Record(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_completion.IsCompleted) return;
            _stop?.Dispose();
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var source = _source;
            var path = _path;
            var token = _stop.Token;
            _completion = Task.Run(async () =>
            {
                await using var sink = new OpusFileSink(path, _profile, _libraryPath);
                try
                {
                    await foreach (var frame in source.CaptureAsync(token).ConfigureAwait(false))
                        await sink.WriteAsync(frame, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            });
        }
    }

    public async ValueTask StopAsync()
    {
        Task completion;
        lock (_gate) { _stop?.Cancel(); completion = _completion; }
        await completion.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            return new(_disposal = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally { lock (_gate) { _stop?.Dispose(); _stop = null; } }
    }
}
