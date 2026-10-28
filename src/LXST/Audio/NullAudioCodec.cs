namespace LXST.Audio;

/// <summary>Identity processing for in-memory frames; it is not a wire codec or packet discriminator.</summary>
public sealed class NullAudioCodec
{
    public T Encode<T>(T frame) => frame;

    public T Decode<T>(T frame) => frame;
}
