using System.Runtime.CompilerServices;
using System.Threading.Channels;

[assembly: InternalsVisibleTo("LXST.Tests")]

namespace LXST.Devices;

/// <summary>Original Windows PCM16 device adapter. This is not the reference's WASAPI backend.</summary>
public sealed class WindowsWaveAudioBackend : IAudioDeviceBackend
{
    private readonly IWindowsWaveDriver _driver;

    public WindowsWaveAudioBackend() : this(new WindowsWaveDriver()) { }
    internal WindowsWaveAudioBackend(IWindowsWaveDriver driver) => _driver = driver;

    public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListInputsAsync(CancellationToken cancellationToken = default) =>
        ListAsync(true, cancellationToken);

    public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListOutputsAsync(CancellationToken cancellationToken = default) =>
        ListAsync(false, cancellationToken);

    private ValueTask<IReadOnlyList<AudioDeviceInfo>> ListAsync(bool capture, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_driver.List(capture));
    }

    public ValueTask<IAudioCapture> OpenCaptureAsync(string? device, AudioDeviceFormat format,
        CancellationToken cancellationToken = default)
    {
        Validate(format, cancellationToken);
        return ValueTask.FromResult<IAudioCapture>(new Capture(_driver, device, format));
    }

    public ValueTask<IAudioPlayback> OpenPlaybackAsync(string? device, AudioDeviceFormat format,
        CancellationToken cancellationToken = default)
    {
        Validate(format, cancellationToken);
        return ValueTask.FromResult<IAudioPlayback>(new Playback(_driver, device, format));
    }

    private static void Validate(AudioDeviceFormat format, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(format);
        token.ThrowIfCancellationRequested();
        format.Validate();
        if (format.Channels > 2 || format.SampleRate > 192000)
            throw new ArgumentOutOfRangeException(nameof(format), "Windows waveform PCM supports one or two channels up to 192 kHz.");
    }

    private sealed class Capture(IWindowsWaveDriver driver, string? device, AudioDeviceFormat format) : IAudioCapture
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<AudioFrame> _frames = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(4)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        private Task? _worker;
        private Task? _disposal;
        private bool _reading;

        public async IAsyncEnumerable<AudioFrame> CaptureAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancellationToken stopToken;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposal is not null, this);
                if (_reading) throw new InvalidOperationException("Capture supports one enumeration.");
                _reading = true;
                stopToken = _stop.Token;
                _worker = Task.Run(Run);
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(stopToken, cancellationToken);
            try
            {
                await foreach (var frame in _frames.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                {
                    stop.Token.ThrowIfCancellationRequested();
                    yield return frame;
                }
            }
            finally { await DisposeAsync().ConfigureAwait(false); }
        }

        private async Task Run()
        {
            Exception? failure = null;
            try
            {
                _stop.Token.ThrowIfCancellationRequested();
                using var session = driver.Open(true, device, format);
                while (true)
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    var samples = session.Read(_stop.Token);
                    await _frames.Writer.WriteAsync(new(samples, format.SampleRate, format.Channels), _stop.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception exception) { failure = exception; throw; }
            finally { _frames.Writer.TryComplete(failure); }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_disposal is not null) return new(_disposal);
                _stop.Cancel();
                return new(_disposal = FinishAsync(_worker ?? Task.CompletedTask));
            }
        }

        private async Task FinishAsync(Task worker)
        {
            await worker.ConfigureAwait(false);
            _frames.Writer.TryComplete();
            _stop.Dispose();
        }
    }

    private sealed class Playback(IWindowsWaveDriver driver, string? device, AudioDeviceFormat format) : IAudioPlayback
    {
        private readonly object _lifetime = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private IWindowsWaveSession? _session;
        private Task? _disposal;

        public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(frame);
            if (frame.SampleRate != format.SampleRate || frame.Channels != format.Channels ||
                frame.SamplesPerChannel == 0 || frame.SamplesPerChannel > format.SampleRate)
                throw new ArgumentException("Playback requires a nonempty frame in its selected format, at most one second long.", nameof(frame));
            foreach (var sample in frame.Samples.Span)
                if (!float.IsFinite(sample)) throw new ArgumentException("Device samples must be finite.", nameof(frame));
            CancellationToken stopToken;
            lock (_lifetime)
            {
                ObjectDisposedException.ThrowIf(_disposal is not null, this);
                stopToken = _stop.Token;
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(stopToken, cancellationToken);
            await _gate.WaitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                var samples = frame.Samples.ToArray();
                await Task.Run(() =>
                {
                    _session ??= driver.Open(false, device, format);
                    _session.Write(samples, stop.Token);
                }, stop.Token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            lock (_lifetime)
            {
                if (_disposal is not null) return new(_disposal);
                _stop.Cancel();
                return new(_disposal = FinishAsync());
            }
        }

        private async Task FinishAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _session?.Dispose();
                _session = null;
            }
            finally
            {
                _gate.Release();
                _stop.Dispose();
            }
        }
    }
}

internal interface IWindowsWaveDriver
{
    IReadOnlyList<AudioDeviceInfo> List(bool capture);
    IWindowsWaveSession Open(bool capture, string? device, AudioDeviceFormat format);
}

internal interface IWindowsWaveSession : IDisposable
{
    float[] Read(CancellationToken cancellationToken);
    void Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken);
}
