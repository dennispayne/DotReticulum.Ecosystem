using DotReticulum.Core;
using LXMF;

namespace Sideband;

internal static class SidebandPaperProfile
{
    internal static (byte[] Wire, string Uri) Read(byte[] packed, ReadOnlySpan<byte> destination)
    {
        try
        {
            if (MsgPack.UnpackApplicationValue(packed) is not object?[] { Length: 2 } pair ||
                pair[0] is not byte[] { Length: > 0 } wire || pair[1] is not byte[] envelope)
                throw new InvalidDataException("Invalid stored paper message pair.");
            var uri = LxMessage.PaperUriFromBytes(envelope);
            if (!envelope.AsSpan(0, LxMessage.DestinationLength).SequenceEqual(destination))
                throw new InvalidDataException("The stored paper envelope has a different destination.");
            return (wire, uri);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("Invalid or oversized stored paper envelope.", error);
        }
    }
}
