using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NomadNet;

/// <summary>RRC v1 application envelopes, independently implemented from primary NomadNet observations.</summary>
public sealed record RrcEnvelope(int Type, byte[] Id, long Timestamp, byte[] Source,
    string? Room = null, object? Body = null, string? Nick = null)
{
    public static RrcEnvelope Create(int type, byte[] source, string? room = null, object? body = null, string? nick = null) =>
        new(type, RandomNumberGenerator.GetBytes(8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), source, room, body, nick);

    public byte[] Encode()
    {
        var map = new Dictionary<long, object?> { [0] = 1L, [1] = (long)Type, [2] = Id, [3] = Timestamp, [4] = Source };
        if (Room is not null) map[5] = Room;
        if (Body is not null) map[6] = Body;
        if (!string.IsNullOrEmpty(Nick)) map[7] = Nick;
        return RrcCbor.Encode(map);
    }

    public static RrcEnvelope Decode(ReadOnlyMemory<byte> data)
    {
        if (RrcCbor.Decode(data) is not Dictionary<long, object?> map ||
            map.GetValueOrDefault(0) is not 1L || map.GetValueOrDefault(1) is not long type || type is < 0 or > int.MaxValue ||
            map.GetValueOrDefault(2) is not byte[] { Length: 8 } id ||
            map.GetValueOrDefault(3) is not long timestamp ||
            map.GetValueOrDefault(4) is not byte[] { Length: 16 } source ||
            (map.GetValueOrDefault(5) is { } room && room is not string) ||
            (map.GetValueOrDefault(7) is { } nick && nick is not string))
            throw new InvalidDataException("Malformed RRC v1 envelope.");
        return new((int)type, id, timestamp, source, map.GetValueOrDefault(5) as string,
            map.GetValueOrDefault(6), map.GetValueOrDefault(7) as string);
    }

    public static bool Mentions(string text, string nick)
    {
        if (nick.Length == 0) return false;
        return Regex.IsMatch(text, @"(?<![A-Za-z0-9_])@" + Regex.Escape(nick) + @"(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    public static string NormalizeRoom(string room, int maximumBytes = 64)
    {
        room = room.Trim().ToLowerInvariant();
        if (room.Length == 0 || Encoding.UTF8.GetByteCount(room) > maximumBytes || room.Any(char.IsControl))
            throw new ArgumentException("Room name exceeds the hub's UTF-8 limit or is empty.");
        return room;
    }
}

internal static class RrcCbor
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal static byte[] Encode(object? value)
    {
        var writer = new CborWriter(CborConformanceMode.Lax);
        Write(writer, value, 0);
        if (writer.BytesWritten > MaximumBytes) throw new InvalidDataException("RRC CBOR exceeds the local storage bound.");
        return writer.Encode();
    }

    private static void Write(CborWriter writer, object? value, int depth)
    {
        if (depth > 8) throw new InvalidDataException("RRC CBOR nesting is too deep.");
        switch (value)
        {
            case null: writer.WriteNull(); break;
            case long number: writer.WriteInt64(number); break;
            case int number: writer.WriteInt64(number); break;
            case string text: writer.WriteTextString(text); break;
            case byte[] bytes: writer.WriteByteString(bytes); break;
            case bool boolean: writer.WriteBoolean(boolean); break;
            case Dictionary<long, object?> map:
                writer.WriteStartMap(map.Count);
                foreach (var (key, item) in map) { writer.WriteInt64(key); Write(writer, item, depth + 1); }
                writer.WriteEndMap();
                break;
            case IReadOnlyList<object?> items:
                writer.WriteStartArray(items.Count);
                foreach (var item in items) Write(writer, item, depth + 1);
                writer.WriteEndArray();
                break;
            default: throw new InvalidDataException("Unsupported RRC CBOR value.");
        }
    }

    internal static object? Decode(ReadOnlyMemory<byte> data)
    {
        if (data.Length > MaximumBytes) throw new InvalidDataException("RRC CBOR exceeds the local storage bound.");
        try
        {
            var reader = new CborReader(data, CborConformanceMode.Lax);
            var budget = 20_000;
            var result = Read(reader, 0, ref budget);
            if (reader.BytesRemaining != 0) throw new InvalidDataException("Trailing RRC CBOR data.");
            return result;
        }
        catch (Exception exception) when (exception is CborContentException or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException("Malformed RRC CBOR.", exception);
        }
    }

    private static object? Read(CborReader reader, int depth, ref int budget)
    {
        if (depth > 8 || --budget < 0) throw new InvalidDataException("RRC CBOR structure exceeds the local bound.");
        switch (reader.PeekState())
        {
            case CborReaderState.UnsignedInteger:
            case CborReaderState.NegativeInteger: return reader.ReadInt64();
            case CborReaderState.TextString: return reader.ReadTextString();
            case CborReaderState.ByteString: return reader.ReadByteString();
            case CborReaderState.Boolean: return reader.ReadBoolean();
            case CborReaderState.Null: reader.ReadNull(); return null;
            case CborReaderState.StartMap:
                reader.ReadStartMap();
                var map = new Dictionary<long, object?>();
                while (reader.PeekState() != CborReaderState.EndMap)
                {
                    if (map.Count >= 2048) throw new InvalidDataException("RRC CBOR map is too large.");
                    var key = reader.ReadInt64();
                    if (!map.TryAdd(key, Read(reader, depth + 1, ref budget)))
                        throw new InvalidDataException("Duplicate RRC CBOR key.");
                }
                reader.ReadEndMap();
                return map;
            case CborReaderState.StartArray:
                reader.ReadStartArray();
                var items = new List<object?>();
                while (reader.PeekState() != CborReaderState.EndArray)
                {
                    if (items.Count >= 2048) throw new InvalidDataException("RRC CBOR array is too large.");
                    items.Add(Read(reader, depth + 1, ref budget));
                }
                reader.ReadEndArray();
                return items;
            default: throw new InvalidDataException("Unsupported RRC CBOR value.");
        }
    }
}
