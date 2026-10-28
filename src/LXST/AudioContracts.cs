namespace LXST;

/// <summary>Interleaved normalized samples, with explicit sample rate and channel count.</summary>
public sealed class AudioFrame
{
    public AudioFrame(ReadOnlyMemory<float> samples, int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 64);
        if (samples.Length % channels != 0) throw new ArgumentException("Incomplete interleaved sample.");
        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public ReadOnlyMemory<float> Samples { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int SamplesPerChannel => Samples.Length / Channels;
}

public interface IAudioCodec : IDisposable
{
    CodecKind Kind { get; }
    byte[] Encode(AudioFrame frame);
    AudioFrame Decode(ReadOnlySpan<byte> data);
}

public interface IAudioSource
{
    IAsyncEnumerable<AudioFrame> CaptureAsync(CancellationToken cancellationToken = default);
}

public interface IAudioSink
{
    ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default);
}

/// <summary>Original codec factory boundary: no device or native codec is required for framing.</summary>
public interface IAudioCodecFactory
{
    IAudioCodec Create(CodecKind kind);
}
