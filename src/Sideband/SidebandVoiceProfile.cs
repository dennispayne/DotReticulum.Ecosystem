using DotReticulum.Core;

namespace Sideband;

internal static class SidebandVoiceProfile
{
    internal const int MaximumEncodedBytes = 256 * 1024;
    internal const int MaximumEntries = 1_000;
    private static readonly MsgPackLimits Limits = new()
    {
        MaxEncodedBytes = MaximumEncodedBytes, MaxValueBytes = 64,
        MaxContainerEntries = MaximumEntries, MaxValues = 8_001, MaxDepth = 4,
    };

    internal static IReadOnlyList<SidebandVoiceCallEvent> Read(ReadOnlySpan<byte> packed, double now)
    {
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        object? decoded;
        try { decoded = MsgPack.UnpackApplicationValue(packed, Limits); }
        catch (FormatException error) { throw new InvalidDataException("The primary voice log is malformed or exceeds its safety limits.", error); }
        if (decoded is not object?[] entries)
            throw new InvalidDataException("The primary voice log must be a MessagePack array.");
        var result = new List<SidebandVoiceCallEvent>(entries.Length);
        foreach (var entry in entries)
        {
            if (entry is not Dictionary<object, object?> map ||
                !map.TryGetValue("time", out var rawTime) ||
                !map.TryGetValue("event", out var rawEvent) || rawEvent is not string { Length: > 0 and <= 32 } name ||
                name.Any(char.IsControl) ||
                !map.TryGetValue("identity", out var rawIdentity) || rawIdentity is not byte[] { Length: 16 } identity)
                throw new InvalidDataException("The primary voice log contains an invalid call event.");
            var time = rawTime switch { long value => value, ulong value => value, double value => value, _ => double.NaN };
            if (!double.IsFinite(time) || time < -62_135_596_800d || time >= 253_402_300_800d || time > now + 240)
                throw new InvalidDataException("A primary voice call event has an invalid or excessive future timestamp.");
            if (now - time < SidebandVoiceService.CallLogRetention.TotalSeconds)
                result.Add(new(DateTimeOffset.UnixEpoch.AddSeconds(time), name, Convert.ToHexStringLower(identity), Imported: true));
        }
        return result;
    }
}
