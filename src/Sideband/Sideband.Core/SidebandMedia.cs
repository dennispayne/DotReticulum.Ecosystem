namespace Sideband;

public sealed record SidebandImage(string Format, SidebandAttachment Data);

public sealed record SidebandAudio(int Mode, SidebandAttachment Data);

public enum SidebandImageQuality
{
    LowBandwidth,
    Medium,
    HighResolution,
}

public sealed record SidebandImageMetadata(string Format, int Width, int Height, int EncodedLength);

public sealed record SidebandEncodedImage(string Format, byte[] EncodedBytes, SidebandImageMetadata Metadata);

public sealed record SidebandEncodedAudio(int Mode, byte[] EncodedBytes);
