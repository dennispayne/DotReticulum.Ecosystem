namespace DotLXST.Audio;

public sealed class AudioMixer : IAsyncDisposable
{
    private sealed class SourceQueue(int capacity)
    {
        public readonly Queue<AudioFrame> Frames = new(capacity);
        public int Capacity { get; } = capacity;
    }

    private readonly object _gate = new();
    private readonly Dictionary<object, SourceQueue> _sources = [];
    private readonly IAudioSink _output;
    private readonly TimeSpan _frameDuration;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _samplesPerFrame;
    private readonly int _maximumSources;
    private readonly int _framesPerSource;
    private CancellationTokenSource? _stopSource;
    private Task? _stopTask;
    private Task? _disposeTask;
    private Task _completion = Task.CompletedTask;
    private bool _disposed;
    private bool _muted;
    private float _gainDb;

    public AudioMixer(
        IAudioSink output,
        TimeSpan frameDuration,
        int sampleRate,
        int channels,
        int maximumSources = 32,
        int framesPerSource = 8)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (frameDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(frameDuration));
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 32);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSources, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(framesPerSource, 1);
        _output = output;
        _frameDuration = frameDuration;
        _sampleRate = sampleRate;
        _channels = channels;
        _samplesPerFrame = checked((int)Math.Ceiling(frameDuration.TotalSeconds * sampleRate));
        _maximumSources = maximumSources;
        _framesPerSource = framesPerSource;
    }

    public Task Completion
    {
        get { lock (_gate) return _completion; }
    }

    public float GainDb
    {
        get { lock (_gate) return _gainDb; }
        set
        {
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _gainDb = value;
            }
        }
    }

    public bool IsMuted
    {
        get { lock (_gate) return _muted; }
        set
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _muted = value;
            }
        }
    }

    public bool CanAccept(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
            return !_disposed &&
                (_sources.TryGetValue(source, out var queue)
                    ? queue.Frames.Count < queue.Capacity
                    : _sources.Count < _maximumSources);
    }

    public bool TrySubmit(object source, AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SampleRate != _sampleRate)
            throw new ArgumentException("Mixer inputs must use its configured sample rate.", nameof(frame));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var sample in frame.Samples.Span)
                if (!float.IsFinite(sample)) throw new ArgumentException("Audio samples must be finite.", nameof(frame));
            if (!_sources.TryGetValue(source, out var queue))
            {
                if (_sources.Count >= _maximumSources) return false;
                queue = new(_framesPerSource);
                _sources.Add(source, queue);
            }

            if (queue.Frames.Count == queue.Capacity) return false;
            queue.Frames.Enqueue(new(frame.Samples.ToArray(), frame.SampleRate, frame.Channels));
            return true;
        }
    }

    public void SetSourceMaximumFrames(object source, int maximumFrames)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFrames, 1);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sources.TryGetValue(source, out var queue))
            {
                if (_sources.Count >= _maximumSources) throw new InvalidOperationException("The mixer source limit has been reached.");
                _sources.Add(source, new(maximumFrames));
                return;
            }

            var replacement = new SourceQueue(maximumFrames);
            foreach (var frame in queue.Frames.TakeLast(maximumFrames)) replacement.Frames.Enqueue(frame);
            _sources[source] = replacement;
        }
    }

    public void RemoveSource(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sources.Remove(source);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopTask is not null) return;
            if (_stopSource is not null && !_completion.IsCompleted) return;
            _stopSource?.Dispose();
            _stopTask = null;
            _stopSource = new();
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
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_frameDuration);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var mixed = MixQueuedFrames();
            if (mixed is not null)
                await _output.WriteAsync(mixed, cancellationToken).ConfigureAwait(false);
        }
    }

    private AudioFrame? MixQueuedFrames()
    {
        var frames = new List<AudioFrame>();
        float gain;
        lock (_gate)
        {
            gain = _muted ? 0f : _gainDb == 0 ? 1f : MathF.Pow(10f, _gainDb / 10f);
            foreach (var queue in _sources.Values)
                if (queue.Frames.TryDequeue(out var frame)) frames.Add(frame);
        }

        if (frames.Count == 0) return null;
        var output = new float[checked(_samplesPerFrame * _channels)];
        foreach (var frame in frames)
        {
            var converted = Adapt(frame);
            var samples = converted.Samples.Span;
            for (var i = 0; i < samples.Length; i++)
                output[i] += samples[i] * gain;
        }

        for (var i = 0; i < output.Length; i++) output[i] = Math.Clamp(output[i], -1f, 1f);
        return new(output, _sampleRate, _channels);
    }

    private AudioFrame Adapt(AudioFrame frame)
    {
        var samples = new float[checked(_samplesPerFrame * _channels)];
        var input = frame.Samples.Span;
        var framesToCopy = Math.Min(_samplesPerFrame, frame.SamplesPerChannel);
        for (var sample = 0; sample < framesToCopy; sample++)
        {
            for (var channel = 0; channel < _channels; channel++)
                samples[sample * _channels + channel] = input[sample * frame.Channels + Math.Min(channel, frame.Channels - 1)];
        }

        return new(samples, _sampleRate, _channels);
    }
}
