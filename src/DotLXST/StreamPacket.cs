using DotReticulum.Core;

namespace DotLXST;

public enum CodecKind : byte
{
    Raw = 0,
    Opus = 1,
    Codec2 = 2,
}

public sealed record EncodedFrame(CodecKind Codec, ReadOnlyMemory<byte> Data);

/// <summary>Bounded LXST envelope codec. This does not add reliability or fragmentation.</summary>
public sealed class StreamPacket
{
    public const int MaximumEncodedBytes = 65_535;
    private static readonly MsgPackLimits Limits = new()
    {
        MaxEncodedBytes = MaximumEncodedBytes,
        MaxValueBytes = MaximumEncodedBytes,
        MaxContainerEntries = 256,
        MaxValues = 1024,
        MaxDepth = 8,
    };

    public IReadOnlyList<EncodedFrame> Frames { get; init; } = [];
    public IReadOnlyList<long> Signals { get; init; } = [];

    public byte[] Encode()
    {
        if (Frames.Count > 256 || Signals.Count > 256)
            throw new ArgumentException("Too many frames or signals.");
        var fields = new Dictionary<object, object?>();
        if (Signals.Count != 0) fields[0L] = Signals.Cast<object?>().ToArray();
        if (Frames.Count != 0)
        {
            var frames = Frames.Select(frame =>
            {
                if (!Enum.IsDefined(frame.Codec)) throw new ArgumentException("Unknown codec.");
                if (frame.Data.Length >= MaximumEncodedBytes) throw new ArgumentException("Frame is too large.");
                var bytes = new byte[frame.Data.Length + 1];
                bytes[0] = (byte)frame.Codec;
                frame.Data.Span.CopyTo(bytes.AsSpan(1));
                return bytes;
            }).ToArray();
            fields[1L] = frames.Length == 1 ? frames[0] : frames.Cast<object?>().ToArray();
        }
        return MsgPack.Pack(fields, Limits);
    }

    public static StreamPacket Decode(ReadOnlySpan<byte> data)
    {
        if (MsgPack.Unpack(data, Limits) is not Dictionary<object, object?> fields)
            throw new FormatException("LXST envelope must be a map.");
        var frames = new List<EncodedFrame>();
        var signals = new List<long>();
        if (fields.TryGetValue(1L, out var frameValue))
        {
            var values = frameValue is object?[] items ? items : [frameValue];
            foreach (var value in values)
            {
                if (value is not byte[] { Length: > 0 } bytes || bytes[0] > (byte)CodecKind.Codec2)
                    throw new FormatException("Invalid LXST frame or unknown codec.");
                frames.Add(new((CodecKind)bytes[0], bytes.AsMemory(1)));
            }
        }
        if (fields.TryGetValue(0L, out var signalValue))
        {
            var values = signalValue is object?[] items ? items : [signalValue];
            foreach (var value in values)
            {
                if (value is long number) signals.Add(number);
                else if (value is ulong unsigned && unsigned <= long.MaxValue) signals.Add((long)unsigned);
                else throw new FormatException("Telephone signals must be integers.");
            }
        }
        return new() { Frames = frames, Signals = signals };
    }
}
