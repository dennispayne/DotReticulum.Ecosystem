using System.Buffers.Binary;

namespace DotLXST;

/// <summary>Portable little-endian raw floating-point samples. Float128 is deliberately unsupported.</summary>
public sealed class RawAudioCodec : IAudioCodec
{
    private readonly int _bitDepth;
    private readonly int? _channels;
    private readonly int _sampleRate;

    public RawAudioCodec(int bitDepth = 16, int? channels = null, int sampleRate = 48_000)
    {
        if (bitDepth is not (16 or 32 or 64)) throw new ArgumentOutOfRangeException(nameof(bitDepth));
        if (channels is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(channels));
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        _bitDepth = bitDepth;
        _channels = channels;
        _sampleRate = sampleRate;
    }

    public CodecKind Kind => CodecKind.Raw;

    public byte[] Encode(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SampleRate != _sampleRate) throw new ArgumentException("Raw codec does not resample.");
        var channels = _channels ?? frame.Channels;
        var width = _bitDepth / 8;
        var length = checked(frame.SamplesPerChannel * channels * width + 1);
        if (length > StreamPacket.MaximumEncodedBytes) throw new ArgumentException("Raw frame is too large.");
        var result = new byte[length];
        result[0] = (byte)((_bitDepth switch { 16 => 0, 32 => 1, _ => 2 }) << 6 | (channels - 1));
        var input = frame.Samples.Span;
        for (var i = 0; i < frame.SamplesPerChannel; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                var sample = input[i * frame.Channels + Math.Min(c, frame.Channels - 1)];
                var target = result.AsSpan(1 + (i * channels + c) * width, width);
                switch (_bitDepth)
                {
                    case 16: BinaryPrimitives.WriteHalfLittleEndian(target, (Half)sample); break;
                    case 32: BinaryPrimitives.WriteSingleLittleEndian(target, sample); break;
                    case 64: BinaryPrimitives.WriteDoubleLittleEndian(target, sample); break;
                }
            }
        }
        return result;
    }

    public AudioFrame Decode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.Length > StreamPacket.MaximumEncodedBytes) throw new FormatException("Invalid raw frame length.");
        var channels = (data[0] & 63) + 1;
        var depth = data[0] >> 6;
        if (channels > 32) throw new FormatException("Raw frame exceeds supported channel count.");
        if (depth == 3) throw new NotSupportedException("Native NumPy float128 is not a portable IEEE interchange format.");
        var width = 2 << depth;
        if ((data.Length - 1) % (width * channels) != 0) throw new FormatException("Truncated raw samples.");
        var output = new float[(data.Length - 1) / width];
        for (var i = 0; i < output.Length; i++)
        {
            var value = data.Slice(1 + i * width, width);
            output[i] = depth switch
            {
                0 => (float)BinaryPrimitives.ReadHalfLittleEndian(value),
                1 => BinaryPrimitives.ReadSingleLittleEndian(value),
                _ => (float)BinaryPrimitives.ReadDoubleLittleEndian(value),
            };
        }
        return new(output, _sampleRate, channels);
    }

    public void Dispose() { }
}
