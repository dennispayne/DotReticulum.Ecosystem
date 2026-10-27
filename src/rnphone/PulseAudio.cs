using System.Buffers.Binary;
using System.Diagnostics;
using DotLXST;
using DotLXST.Audio;

namespace Rnphone;

internal sealed class ResamplingPlaybackSink(IAudioSink sink) : IAudioSink
{
    public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default) =>
        sink.WriteAsync(frame.SampleRate == 48000 ? frame : AudioResampler.Resample(frame, 48000), cancellationToken);
}

/// <summary>Linux device I/O is isolated in cancellable processes, outside the platform-independent library.</summary>
internal static class PulseAudio
{
    public static Process Start(string program, IEnumerable<string> arguments, bool input, bool output)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Audio device I/O currently requires Linux PulseAudio utilities; Windows/macOS devices are unimplemented.");
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = input,
            RedirectStandardOutput = output,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException($"Could not start {program}.");
    }

    public static async Task<string> DrainErrorsAsync(Process process)
    {
        var tail = "";
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            tail += line + "\n";
            if (tail.Length > 8192) tail = tail[^8192..];
        }
        return tail;
    }

    public static async Task StopAsync(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public static async Task CaptureAsync(TelephoneEndpoint telephone, Func<string?> deviceProvider, CancellationToken stop, Action? firstFrame = null)
    {
        var firstRun = true;
        while (!stop.IsCancellationRequested && telephone.State == CallState.Established)
        {
            var reported = false;
            var profile = telephone.ActiveProfile ?? TelephoneProfile.MediumQuality;
            var device = deviceProvider();
            var (rate, channels, duration) = Format(profile);
            var arguments = new List<string> { "--raw", "--format=float32le", "--rate=" + rate, "--channels=" + channels, "--latency-msec=20" };
            if (device is not null) arguments.Add("--device=" + device);
            using var process = Start("parec", arguments, input: false, output: true);
            var errors = DrainErrorsAsync(process);
            var clock = Stopwatch.StartNew();
            var envelope = new AudioCaptureEnvelope(TimeSpan.FromSeconds(.075),
                TimeSpan.FromSeconds(firstRun ? .225 : 0));
            firstRun = false;
            try
            {
                var bytes = new byte[rate * channels * duration / 1000 * sizeof(float)];
                while (!stop.IsCancellationRequested && telephone.State == CallState.Established &&
                    telephone.ActiveProfile == profile && deviceProvider() == device)
                {
                    await process.StandardOutput.BaseStream.ReadExactlyAsync(bytes, stop).ConfigureAwait(false);
                    var samples = new float[bytes.Length / sizeof(float)];
                    for (var i = 0; i < samples.Length; i++)
                        samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)));
                    var frame = envelope.Process(new(samples, rate, channels), clock.Elapsed);
                    if (frame is null) continue;
                    try { await telephone.SendAudioAsync(frame, stop).ConfigureAwait(false); }
                    catch (ArgumentException) when (telephone.ActiveProfile != profile) { break; }
                    if (!reported) { reported = true; firstFrame?.Invoke(); }
                }
            }
            finally
            {
                await StopAsync(process).ConfigureAwait(false);
                var diagnostic = await errors.ConfigureAwait(false);
                if (!stop.IsCancellationRequested && process.ExitCode != 0 && diagnostic.Length != 0)
                    Trace.TraceError($"rnphone capture: {diagnostic}");
            }
        }
    }

    private static (int Rate, int Channels, int Duration) Format(TelephoneProfile profile) => profile switch
    {
        TelephoneProfile.UltraLowBandwidth => (8000, 1, 400),
        TelephoneProfile.VeryLowBandwidth => (8000, 1, 320),
        TelephoneProfile.LowBandwidth => (8000, 1, 200),
        TelephoneProfile.HighQuality => (48000, 1, 60),
        TelephoneProfile.MaximumQuality => (48000, 2, 60),
        TelephoneProfile.LowLatency => (24000, 1, 20),
        TelephoneProfile.UltraLowLatency => (24000, 1, 10),
        _ => (24000, 1, 60),
    };
}

internal sealed class PulseAudioSink(string? device, Action? firstFrame = null) : IAudioSink, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private Task<string>? _errors;
    private (int Rate, int Channels) _format;
    private bool _disposed;
    private bool _reported;
    private string? _device = device;
    private string? _activeDevice;

    public void SetDevice(string? device) => Volatile.Write(ref _device, device);

    public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var currentDevice = Volatile.Read(ref _device);
            if (_process is null || _format != (frame.SampleRate, frame.Channels) || currentDevice != _activeDevice)
            {
                await CloseProcessAsync().ConfigureAwait(false);
                var arguments = new List<string> { "--playback", "--raw", "--format=float32le",
                    "--rate=" + frame.SampleRate, "--channels=" + frame.Channels, "--latency-msec=20" };
                if (currentDevice is not null) arguments.Add("--device=" + currentDevice);
                _process = PulseAudio.Start("pacat", arguments, input: true, output: false);
                _errors = PulseAudio.DrainErrorsAsync(_process);
                _format = (frame.SampleRate, frame.Channels);
                _activeDevice = currentDevice;
            }
            var bytes = new byte[frame.Samples.Length * sizeof(float)];
            for (var i = 0; i < frame.Samples.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), frame.Samples.Span[i]);
            await _process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (!_reported) { _reported = true; firstFrame?.Invoke(); }
        }
        finally { _gate.Release(); }
    }

    private async Task CloseProcessAsync()
    {
        if (_process is null) return;
        await PulseAudio.StopAsync(_process).ConfigureAwait(false);
        if (_errors is not null)
        {
            var errors = await _errors.ConfigureAwait(false);
            if (errors.Length != 0) Trace.TraceError("rnphone playback: " + errors);
        }
        _process.Dispose();
        _process = null;
    }

    public async ValueTask StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CloseProcessAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await CloseProcessAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
