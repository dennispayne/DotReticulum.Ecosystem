using System.Buffers.Binary;

namespace DotLXST;

/// <summary>Little-endian raw samples; depth 128 explicitly targets pinned Linux x86 NumPy long double.</summary>
public sealed class RawAudioCodec : IAudioCodec
{
    private readonly int _bitDepth;
    private int _channels;
    private readonly int _sampleRate;

    public RawAudioCodec(int bitDepth = 16, int? channels = null, int sampleRate = 48_000)
    {
        if (channels == 0) throw new ArgumentOutOfRangeException(nameof(channels));
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        _bitDepth = bitDepth >= 128 ? 128 : bitDepth >= 64 ? 64 : bitDepth >= 32 ? 32 : 16;
        _channels = channels is { } count ? Math.Clamp(count, 1, 32) : 0;
        _sampleRate = sampleRate;
    }

    public CodecKind Kind => CodecKind.Raw;

    public byte[] Encode(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SampleRate != _sampleRate) throw new ArgumentException("Raw codec does not resample.");
        Interlocked.CompareExchange(ref _channels, frame.Channels, 0);
        var channels = Volatile.Read(ref _channels);
        var width = _bitDepth / 8;
        var length = checked(frame.SamplesPerChannel * channels * width + 1);
        if (length > StreamPacket.MaximumEncodedBytes) throw new ArgumentException("Raw frame is too large.");
        var result = new byte[length];
        result[0] = (byte)((_bitDepth switch { 16 => 0, 32 => 1, 64 => 2, _ => 3 }) << 6 | (channels - 1));
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
                    case 128: RawExtendedFloat.Write(target, sample); break;
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
                2 => (float)BinaryPrimitives.ReadDoubleLittleEndian(value),
                _ => RawExtendedFloat.Read(value),
            };
        }
        Interlocked.CompareExchange(ref _channels, channels, 0);
        return new(output, _sampleRate, channels);
    }

    public void Dispose() { }
}
