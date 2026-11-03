using System.Diagnostics;
using LXST;
using LXST.Audio;

namespace Sideband;

/// <summary>Explicit, per-operation device leases. The backend remains caller-owned; nothing opens automatically.</summary>
public static class SidebandMediaDevices
{
    private const int DeviceRate = 48000;
    private const int CompositionRate = 12000;
    private const int PlaybackSamples = 960;
    private static readonly TimeSpan ChunkDuration = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Captures 1–10 seconds, selecting the first channel and returning bounded mono 12 kHz PCM.
    /// Providers must honor cancellation; a stalled provider receives a deadline two seconds beyond the requested capture.
    /// </summary>
    public static async Task<AudioFrame> RecordAsync(IAudioDeviceBackend backend, string? device, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        cancellationToken.ThrowIfCancellationRequested();
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(duration), "Recording duration must be between 1 and 10 seconds.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration + TimeSpan.FromSeconds(2));
        await using var source = new DeviceAudioSource(backend, new(DeviceRate, 1, ChunkDuration), device);
        var samples = new float[checked((int)Math.Ceiling(DeviceRate * duration.TotalSeconds))];
        var count = 0;
        var rate = 0;
        var target = 0;
        try
        {
            await foreach (var frame in source.CaptureAsync(deadline.Token).ConfigureAwait(false))
            {
                deadline.Token.ThrowIfCancellationRequested();
                ValidateFrame(frame);
                if (frame.SamplesPerChannel > frame.SampleRate)
                    throw new InvalidDataException("A Sideband capture provider returned more than one second in a frame.");
                if (rate == 0)
                {
                    rate = frame.SampleRate;
                    target = checked((int)Math.Ceiling(rate * duration.TotalSeconds));
                }
                else if (frame.SampleRate != rate)
                    throw new InvalidDataException("The Sideband capture provider changed its sample rate during recording.");
                var take = Math.Min(frame.SamplesPerChannel, target - count);
                for (var index = 0; index < take; index++)
                    samples[count + index] = Math.Clamp(frame.Samples.Span[index * frame.Channels], -1, 1);
                count += take;
                if (count == target)
                    return Resample(new(samples.AsMemory(0, count), rate, 1), CompositionRate,
                        checked((int)Math.Ceiling(CompositionRate * duration.TotalSeconds)));
            }
            throw new EndOfStreamException("The Sideband capture provider stopped before the requested recording duration.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The Sideband capture provider did not complete within its recording deadline.", exception);
        }
    }

    /// <summary>
    /// Plays positive PCM of at most 10 seconds and 2 MiB, selecting the first channel.
    /// Output is mono 48 kHz, paced in 20 ms frames; the final frame is zero-padded by less than 20 ms.
    /// Cancellation or failure closes only this operation's lease, permitting subsequent reuse of the backend.
    /// </summary>
    public static async Task PlayAsync(IAudioDeviceBackend backend, string? device, AudioFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFrame(frame);
        if (frame.SamplesPerChannel > frame.SampleRate * 10)
            throw new ArgumentException("Sideband playback is limited to 10 seconds.", nameof(frame));
        var mono = new float[frame.SamplesPerChannel];
        for (var index = 0; index < mono.Length; index++)
            mono[index] = Math.Clamp(frame.Samples.Span[index * frame.Channels], -1, 1);
        var output = Resample(new(mono, frame.SampleRate, 1), DeviceRate,
            checked((int)Math.Ceiling((double)mono.Length * DeviceRate / frame.SampleRate)));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds((double)output.Samples.Length / DeviceRate) + TimeSpan.FromSeconds(2));
        await using var sink = new DeviceAudioSink(backend, device);
        try
        {
            for (var offset = 0; offset < output.Samples.Length; offset += PlaybackSamples)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var chunk = new float[PlaybackSamples];
                output.Samples.Span.Slice(offset, Math.Min(chunk.Length, output.Samples.Length - offset)).CopyTo(chunk);
                var started = Stopwatch.GetTimestamp();
                await sink.WriteAsync(new(chunk, DeviceRate, 1), deadline.Token).ConfigureAwait(false);
                var remaining = ChunkDuration - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, deadline.Token).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The Sideband playback provider did not complete within its playback deadline.", exception);
        }
    }

    private static AudioFrame Resample(AudioFrame frame, int rate, int count)
    {
        var converted = AudioResampler.Resample(frame, rate);
        if (converted.Samples.Length == count) return converted;
        var fitted = new float[count];
        converted.Samples.Span[..Math.Min(count, converted.Samples.Length)].CopyTo(fitted);
        if (converted.Samples.Length < count)
            fitted.AsSpan(converted.Samples.Length).Fill(converted.Samples.IsEmpty
                ? frame.Samples.Span[^1] : converted.Samples.Span[^1]);
        return new(fitted, rate, 1);
    }

    private static void ValidateFrame(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SampleRate is < 8000 or > DeviceRate || frame.Channels is < 1 or > 2 ||
            frame.Samples.IsEmpty || frame.Samples.Length > SidebandMediaCodec.MaximumEncodedBytes / sizeof(float))
            throw new InvalidDataException("Sideband devices require nonempty mono/stereo PCM at 8–48 kHz, bounded to 2 MiB.");
        foreach (var sample in frame.Samples.Span)
            if (!float.IsFinite(sample))
                throw new InvalidDataException("Sideband device PCM contains a nonfinite sample.");
    }
}
