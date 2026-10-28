namespace LXST.Audio;

/// <summary>Bounded, headless playback buffering around a caller-owned sink.</summary>
public sealed class BufferedAudioSink : IAudioSink, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IAudioSink _output;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly bool _autoStart;
    private readonly TimeProvider _clock;
    private readonly Queue<AudioFrame> _frames = new();
    private readonly SemaphoreSlim _available = new(0, 1);
    private CancellationTokenSource? _stop;
    private Task _completion = Task.CompletedTask;
    private Task? _stopping;
    private Task? _disposing;
    private TimeSpan? _frameTime;
    private bool _disposed;
    private bool _running;
    private long _dropped;

    public BufferedAudioSink(IAudioSink output, int sampleRate, int channels, bool autoStart = true,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 32);
        _output = output;
        _sampleRate = sampleRate;
        _channels = channels;
        _autoStart = autoStart;
        _clock = timeProvider ?? TimeProvider.System;
    }

    public Action<string, Exception?>? Error { get; set; }
    public int QueuedFrames { get { lock (_gate) return _frames.Count; } }
    public bool CanReceive { get { lock (_gate) return !_disposed && _frames.Count < 3; } }
    public bool IsRunning { get { lock (_gate) return _running; } }
    public Task Completion { get { lock (_gate) return _completion; } }
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public TimeSpan OutputLatency { get; private set; }
    public TimeSpan MaximumLatency { get; private set; }

    public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.SampleRate != _sampleRate || frame.SamplesPerChannel == 0)
            throw new ArgumentException("Supply non-empty frames at the playback sample rate.", nameof(frame));
        foreach (var sample in frame.Samples.Span)
            if (!float.IsFinite(sample)) throw new ArgumentException("Audio samples must be finite.", nameof(frame));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var duration = TimeSpan.FromSeconds((double)frame.SamplesPerChannel / _sampleRate);
            if (duration < TimeSpan.FromTicks(1) || duration.TotalMilliseconds > (uint.MaxValue - 1) / 8.0)
                throw new ArgumentException("Frame duration is outside supported timer limits.", nameof(frame));
            _frameTime ??= duration;
            if (_frames.Count == 6)
            {
                _frames.Dequeue();
                Interlocked.Increment(ref _dropped);
            }
            _frames.Enqueue(new(frame.Samples.ToArray(), frame.SampleRate, frame.Channels));
            if (_available.CurrentCount == 0) _available.Release();
            if (_autoStart) Start();
        }
        return ValueTask.CompletedTask;
    }

    public void Start(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping is not null || _running) return;
            if (_frameTime is null) throw new InvalidOperationException("Queue an audio frame before starting playback.");
            _stop?.Dispose();
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _running = true;
            var token = _stop.Token;
            _completion = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    /// <summary>No hardware latency capability is inferred from an audio-only sink.</summary>
    public bool RequestLowLatency()
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        Report("Run-time low latency is not supported by this headless playback adapter.", null);
        return false;
    }

    public ValueTask StopAsync()
    {
        lock (_gate)
        {
            if (_stop is null) return ValueTask.CompletedTask;
            _stopping ??= StopCoreAsync(_stop, _completion);
            return new(_stopping);
        }
    }

    private async Task StopCoreAsync(CancellationTokenSource stop, Task completion)
    {
        await Task.Yield();
        try
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await completion.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_stop, stop)) _stop = null;
                _stopping = null;
            }
            stop.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AudioFrame? frame;
                lock (_gate)
                {
                    if (_frames.Count != 0)
                    {
                        OutputLatency = _frameTime!.Value * _frames.Count;
                        MaximumLatency = _frameTime.Value * 3;
                        _available.Wait(0);
                    }
                    frame = _frames.Count == 0 ? null : _frames.Dequeue();
                }
                if (frame is null)
                {
                    using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var ready = _available.WaitAsync(wake.Token);
                    var expired = Task.Delay(_frameTime!.Value * 8, _clock, wake.Token);
                    var winner = await Task.WhenAny(ready, expired).ConfigureAwait(false);
                    await wake.CancelAsync().ConfigureAwait(false);
                    try { await ready.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (wake.IsCancellationRequested) { }
                    try { await expired.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (wake.IsCancellationRequested) { }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ReferenceEquals(winner, expired))
                    {
                        lock (_gate)
                        {
                            if (_frames.Count != 0) continue;
                            _running = false;
                            return;
                        }
                    }
                    continue;
                }

                if (frame.Channels > _channels)
                {
                    var samples = new float[frame.SamplesPerChannel * _channels];
                    for (var i = 0; i < frame.SamplesPerChannel; i++)
                        frame.Samples.Span.Slice(i * frame.Channels, _channels).CopyTo(samples.AsSpan(i * _channels));
                    frame = new(samples, frame.SampleRate, _channels);
                }
                try { await _output.WriteAsync(frame, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { Report("Buffered playback sink failed; continuing with the next frame.", ex); }
                lock (_gate)
                {
                    if (_frames.Count > 3)
                    {
                        _frames.Dequeue();
                        Interlocked.Increment(ref _dropped);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
                if (_stop is { } stop && stop.Token == cancellationToken) _running = false;
        }
    }

    private void Report(string context, Exception? error)
    {
        if (Error is not { } observer) System.Diagnostics.Trace.TraceError($"{context} {error}");
        else
        {
            try { observer(context, error); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"{context} {error}; playback error observer failed: {ex}"); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposing is not null) return new(_disposing);
            _disposed = true;
            _disposing = DisposeCoreAsync();
            return new(_disposing);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        try { await StopAsync().ConfigureAwait(false); }
        finally { lock (_gate) _frames.Clear(); }
    }
}
