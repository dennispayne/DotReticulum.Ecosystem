using System.Text;

namespace Sideband;

internal static class SidebandVoicePeerProfile
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static SidebandVoicePeer Read(object?[] row)
    {
        if (row is not { Length: 6 } || row[0] is not byte[] { Length: 16 } identity ||
            row[1] is not 4L || row[3] is not long trust || trust is not (0 or 1))
            throw new InvalidDataException("A primary voice peer has an invalid identity, type or trust flag.");
        string name;
        try
        {
            name = row[2] switch
            {
                byte[] { Length: <= 320 } bytes => Utf8.GetString(bytes),
                string text => text,
                _ => throw new InvalidDataException("A primary voice peer has an invalid or oversized name."),
            };
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("A primary voice peer name is not valid UTF-8.", error);
        }
        if (!SidebandVoicePeer.IsValidDisplayName(name))
            throw new InvalidDataException("A primary voice peer name exceeds its native safety limits.");
        return new(Convert.ToHexStringLower(identity), VoiceTrusted: trust == 1, DisplayName: name.Length == 0 ? null : name);
    }
}
