namespace DotLXST.Audio;

public sealed class AudioPipeline : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IAudioSource _source;
    private readonly IAudioSink _sink;
    private readonly AudioFilter? _processor;
    private CancellationTokenSource? _stopSource;
    private Task? _stopTask;
    private Task? _disposeTask;
    private IAudioCodec _codec;
    private Task _completion = Task.CompletedTask;
    private bool _disposed;

    public AudioPipeline(IAudioSource source, IAudioCodec codec, IAudioSink sink, AudioFilter? processor = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(sink);
        _source = source;
        _codec = codec;
        _sink = sink;
        _processor = processor;
    }

    public Task Completion
    {
        get { lock (_gate) return _completion; }
    }

    public bool IsRunning
    {
        get { lock (_gate) return _stopSource is not null && !_completion.IsCompleted; }
    }

    public void ReplaceCodec(IAudioCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(codec, _codec)) return;
            var previous = _codec;
            _codec = codec;
            previous.Dispose();
        }
    }

    public void Start(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopTask is not null || (_stopSource is not null && !_completion.IsCompleted)) return;
            _stopSource?.Dispose();
            _stopSource = null;
            _stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _completion = RunAsync(_stopSource.Token);
        }
    }

    public ValueTask StopAsync()
    {
        lock (_gate)
        {
            if (_stopSource is null) return ValueTask.CompletedTask;
            _stopTask ??= StopCoreAsync(_stopSource, _completion);
            return new(_stopTask);
        }
    }

    private async Task StopCoreAsync(CancellationTokenSource stopSource, Task completion)
    {
        await Task.Yield();
        try
        {
            await stopSource.CancelAsync().ConfigureAwait(false);
            try
            {
                await completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopSource.IsCancellationRequested)
            {
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_stopSource, stopSource))
                {
                    _stopSource = null;
                    _stopTask = null;
                }
            }
            stopSource.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            _disposeTask = DisposeCoreAsync();
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _codec.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var sourceFrame in _source.CaptureAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = _processor?.Process(sourceFrame) ?? sourceFrame;
            AudioFrame decoded;
            lock (_gate)
            {
                var encoded = _codec.Encode(frame);
                decoded = _codec.Decode(encoded);
            }

            await _sink.WriteAsync(decoded, cancellationToken).ConfigureAwait(false);
        }
    }
}
