namespace DotLXST;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

public sealed record AudioDeviceFormat(int SampleRate, int Channels, TimeSpan FrameDuration)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(SampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SampleRate, 384000);
        ArgumentOutOfRangeException.ThrowIfLessThan(Channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Channels, 64);
        if (FrameDuration <= TimeSpan.Zero || FrameDuration > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(FrameDuration));
        if (SampleRate * FrameDuration.TotalSeconds < 1)
            throw new ArgumentException("The selected format must contain at least one sample.");
    }
}

public interface IAudioCapture : IAudioSource, IAsyncDisposable;
public interface IAudioPlayback : IAudioSink, IAsyncDisposable;

public interface IAudioDeviceBackend
{
    ValueTask<IReadOnlyList<AudioDeviceInfo>> ListInputsAsync(CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<AudioDeviceInfo>> ListOutputsAsync(CancellationToken cancellationToken = default);
    ValueTask<IAudioCapture> OpenCaptureAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default);
    ValueTask<IAudioPlayback> OpenPlaybackAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default);
}

/// <summary>Device-independent capture ownership and gain; the backend remains caller-owned.</summary>
public sealed class DeviceAudioSource(IAudioDeviceBackend backend, AudioDeviceFormat format, string? device = null)
    : IAudioSource, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource? _active;
    private Task? _disposal;
    private bool _disposed;
    private float _gain = 1;
    public float LinearGain
    {
        get { lock (_gate) return _gain; }
        set
        {
            if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _gain = value; }
        }
    }

    public async IAsyncEnumerable<AudioFrame> CaptureAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        format.Validate();
        TaskCompletionSource completion;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null) throw new InvalidOperationException("Capture is already active.");
            completion = _active = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        try
        {
            await using var capture = await backend.OpenCaptureAsync(device, format, stop.Token).ConfigureAwait(false);
            await foreach (var frame in capture.CaptureAsync(stop.Token).ConfigureAwait(false))
            {
                var gain = LinearGain;
                var samples = frame.Samples.ToArray();
                for (var index = 0; index < samples.Length; index++) samples[index] *= gain;
                yield return new(samples, frame.SampleRate, frame.Channels);
            }
        }
        finally
        {
            lock (_gate) { _active = null; completion.TrySetResult(); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            _stop.Cancel();
            return new(_disposal = FinishAsync(_active?.Task ?? Task.CompletedTask));
        }
    }

    private async Task FinishAsync(Task active)
    {
        await active.ConfigureAwait(false);
        _stop.Dispose();
    }
}

/// <summary>Serializes output and replaces a device lease when its format or identifier changes.</summary>
public sealed class DeviceAudioSink(IAudioDeviceBackend backend, string? device = null, Action? firstFrame = null) : IAudioSink, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAudioPlayback? _playback;
    private AudioDeviceFormat? _format;
    private string? _device = device;
    private string? _activeDevice;
    private bool _disposed;
    private bool _reported;
    public void SetDevice(string? device) => Volatile.Write(ref _device, device);

    public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SamplesPerChannel == 0) throw new ArgumentException("A device frame cannot be empty.", nameof(frame));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var format = new AudioDeviceFormat(frame.SampleRate, frame.Channels, TimeSpan.FromSeconds((double)frame.SamplesPerChannel / frame.SampleRate));
            format.Validate();
            var selected = Volatile.Read(ref _device);
            if (_playback is null || _format != format || selected != _activeDevice)
            {
                if (_playback is not null) { await _playback.DisposeAsync().ConfigureAwait(false); _playback = null; }
                _playback = await backend.OpenPlaybackAsync(selected, format, cancellationToken).ConfigureAwait(false);
                _format = format;
                _activeDevice = selected;
            }
            try { await _playback.WriteAsync(frame, cancellationToken).ConfigureAwait(false); }
            catch
            {
                var failed = _playback;
                _playback = null;
                await failed.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            if (!_reported)
            {
                _reported = true;
                try { firstFrame?.Invoke(); }
                catch (Exception exception) { System.Diagnostics.Trace.TraceError("Device output observer failed: " + exception); }
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_playback is not null) { await _playback.DisposeAsync().ConfigureAwait(false); _playback = null; }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_playback is not null) { await _playback.DisposeAsync().ConfigureAwait(false); _playback = null; }
        }
        finally { _gate.Release(); }
    }
}
