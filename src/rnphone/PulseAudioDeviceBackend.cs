using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DotLXST;

namespace Rnphone;

public sealed class PulseAudioDeviceBackend : IAudioDeviceBackend
{
    public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListInputsAsync(CancellationToken cancellationToken = default) =>
        ListAsync("sources", "get-default-source", cancellationToken);
    public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListOutputsAsync(CancellationToken cancellationToken = default) =>
        ListAsync("sinks", "get-default-sink", cancellationToken);

    private static async ValueTask<IReadOnlyList<AudioDeviceInfo>> ListAsync(string kind, string defaultCommand, CancellationToken stop)
    {
        async Task<string> RunAsync(string[] arguments)
        {
            using var process = PulseAudio.Start("pactl", arguments, input: false, output: true);
            var errors = PulseAudio.DrainErrorsAsync(process);
            try
            {
                var text = new StringBuilder();
                var buffer = new char[4096];
                int count;
                while ((count = await process.StandardOutput.ReadAsync(buffer, stop).ConfigureAwait(false)) != 0)
                {
                    if (text.Length + count > 1024 * 1024) throw new IOException("PulseAudio device listing exceeds one MiB.");
                    text.Append(buffer, 0, count);
                }
                await process.WaitForExitAsync(stop).ConfigureAwait(false);
                if (process.ExitCode != 0) throw new IOException(await errors.ConfigureAwait(false));
                return text.ToString().Trim();
            }
            finally { await PulseAudio.StopAsync(process).ConfigureAwait(false); await errors.ConfigureAwait(false); }
        }
        var defaultId = await RunAsync([defaultCommand]).ConfigureAwait(false);
        using var devices = JsonDocument.Parse(await RunAsync(["--format=json", "list", kind]).ConfigureAwait(false));
        return devices.RootElement.EnumerateArray().Select(device =>
        {
            var id = device.GetProperty("name").GetString() ?? throw new IOException("PulseAudio returned a device without an identifier.");
            var description = device.TryGetProperty("description", out var name) ? name.GetString() : null;
            return new AudioDeviceInfo(id, description ?? id, id == defaultId);
        }).ToArray();
    }

    public async ValueTask<IAudioCapture> OpenCaptureAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default)
    {
        EnsureLinux();
        format.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var selected = device is null ? null : AudioDeviceSelection.Resolve(device,
            await ListInputsAsync(cancellationToken).ConfigureAwait(false)).Id;
        return new Capture(selected, format);
    }

    public async ValueTask<IAudioPlayback> OpenPlaybackAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default)
    {
        EnsureLinux();
        format.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var selected = device is null ? null : AudioDeviceSelection.Resolve(device,
            await ListOutputsAsync(cancellationToken).ConfigureAwait(false)).Id;
        return new Playback(selected, format);
    }

    private static void EnsureLinux()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("PulseAudio devices require Linux.");
    }

    private sealed class Playback(string? device, AudioDeviceFormat format) : IAudioPlayback
    {
        private readonly PulseAudioSink _sink = new(device);
        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            if (frame.SampleRate != format.SampleRate || frame.Channels != format.Channels)
                throw new ArgumentException("The frame does not match the opened playback format.", nameof(frame));
            return _sink.WriteAsync(frame, cancellationToken);
        }
        public ValueTask DisposeAsync() => _sink.DisposeAsync();
    }

    private sealed class Capture(string? device, AudioDeviceFormat format) : IAudioCapture
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
                if (_active is not null) throw new InvalidOperationException("The capture lease is already running.");
                complete = _active = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
            try
            {
                var arguments = new List<string> { "--raw", "--format=float32le", "--rate=" + format.SampleRate,
                    "--channels=" + format.Channels, "--latency-msec=20" };
                if (device is not null) arguments.Add("--device=" + device);
                using var process = PulseAudio.Start("parec", arguments, input: false, output: true);
                var errors = PulseAudio.DrainErrorsAsync(process);
                try
                {
                    var count = checked((int)(format.SampleRate * format.FrameDuration.TotalSeconds) * format.Channels);
                    var bytes = new byte[checked(count * sizeof(float))];
                    while (!stop.IsCancellationRequested)
                    {
                        try { await process.StandardOutput.BaseStream.ReadExactlyAsync(bytes, stop.Token).ConfigureAwait(false); }
                        catch (IOException exception)
                        {
                            await PulseAudio.StopAsync(process).ConfigureAwait(false);
                            throw new IOException("PulseAudio capture terminated: " + await errors.ConfigureAwait(false), exception);
                        }
                        var samples = new float[count];
                        for (var index = 0; index < count; index++)
                            samples[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(index * sizeof(float)));
                        yield return new(samples, format.SampleRate, format.Channels);
                    }
                }
                finally { await PulseAudio.StopAsync(process).ConfigureAwait(false); await errors.ConfigureAwait(false); }
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
}
