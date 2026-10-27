using System.Runtime.CompilerServices;

namespace DotLXST;

/// <summary>Explicit synthetic-silence capture and discard output; never opens an operating-system audio device.</summary>
public sealed class HeadlessAudioDeviceBackend : IAudioDeviceBackend
{
    public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListInputsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AudioDeviceInfo>>([new("synthetic", "Synthetic silence (no microphone)", true)]);
    }

    public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListOutputsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AudioDeviceInfo>>([new("discard", "Discard output (no speaker)", true)]);
    }

    public ValueTask<IAudioCapture> OpenCaptureAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        format.Validate();
        return ValueTask.FromResult<IAudioCapture>(new Capture(format));
    }

    public ValueTask<IAudioPlayback> OpenPlaybackAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        format.Validate();
        return ValueTask.FromResult<IAudioPlayback>(new Playback(format));
    }

    private sealed class Capture(AudioDeviceFormat format) : IAudioCapture
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private TaskCompletionSource? _active;
        private Task? _disposal;
        private bool _disposed;
        public async IAsyncEnumerable<AudioFrame> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            TaskCompletionSource complete;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_active is not null) throw new InvalidOperationException("Synthetic capture already has an active reader.");
                complete = _active = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
            try
            {
                var count = checked((int)(format.SampleRate * format.FrameDuration.TotalSeconds) * format.Channels);
                while (true)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    yield return new(new float[count], format.SampleRate, format.Channels);
                    await Task.Delay(format.FrameDuration, stop.Token).ConfigureAwait(false);
                }
            }
            finally { lock (_gate) { _active = null; complete.TrySetResult(); } }
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
        private async Task FinishAsync(Task active) { await active.ConfigureAwait(false); _stop.Dispose(); }
    }

    private sealed class Playback(AudioDeviceFormat format) : IAudioPlayback
    {
        private int _disposed;
        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (frame.SampleRate != format.SampleRate || frame.Channels != format.Channels)
                throw new ArgumentException("Headless output frame does not match the selected format.", nameof(frame));
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref _disposed, 1); return ValueTask.CompletedTask; }
    }
}
