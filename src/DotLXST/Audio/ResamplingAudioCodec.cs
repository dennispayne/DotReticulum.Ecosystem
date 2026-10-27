using System.Buffers.Binary;

namespace DotLXST.Audio;

/// <summary>Adapts source and playback rates around an owned audio codec.</summary>
public sealed class ResamplingAudioCodec : IAudioCodec
{
    private readonly object _gate = new();
    private readonly IAudioCodec _codec;
    private readonly int _codecSampleRate;
    private readonly int? _outputSampleRate;
    private bool _disposed;

    public ResamplingAudioCodec(IAudioCodec codec, int codecSampleRate, int? outputSampleRate = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentOutOfRangeException.ThrowIfLessThan(codecSampleRate, 1);
        if (outputSampleRate is < 1) throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
        _codec = codec;
        _codecSampleRate = codecSampleRate;
        _outputSampleRate = outputSampleRate;
    }

    public CodecKind Kind => _codec.Kind;

    public byte[] Encode(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _codec.Encode(frame.SampleRate == _codecSampleRate
                ? frame
                : ResampleForCodec(frame));
        }
    }

    public AudioFrame Decode(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var decoded = _codec.Decode(data);
            return _outputSampleRate is { } outputRate && outputRate != decoded.SampleRate
                ? AudioResampler.Resample(decoded, outputRate)
                : decoded;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _codec.Dispose();
        }
    }

    private AudioFrame ResampleForCodec(AudioFrame frame)
    {
        var channels = _codec.Kind == CodecKind.Codec2 && frame.Channels > 1 ? 1 : frame.Channels;
        var pcmBytes = new byte[checked(frame.Samples.Length * sizeof(short) / frame.Channels * channels)];
        var input = frame.Samples.Span;
        for (var sample = 0; sample < frame.SamplesPerChannel; sample++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var inputChannel = channels == 1 && frame.Channels > 1 ? 1 : channel;
                var pcm = NativeOpusCodec.ToPcm(input[sample * frame.Channels + inputChannel]);
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcmBytes.AsSpan((sample * channels + channel) * sizeof(short)),
                    pcm);
            }
        }

        var resampledBytes = AudioResampler.Resample(
            pcmBytes,
            bitDepth: 16,
            channels,
            frame.SampleRate,
            _codecSampleRate);
        var samples = new float[resampledBytes.Length / sizeof(short)];
        for (var i = 0; i < samples.Length; i++)
        {
            var pcm = BinaryPrimitives.ReadInt16LittleEndian(resampledBytes.AsSpan(i * sizeof(short), sizeof(short)));
            samples[i] = pcm / (float)short.MaxValue;
        }

        return new(samples, _codecSampleRate, channels);
    }
}
