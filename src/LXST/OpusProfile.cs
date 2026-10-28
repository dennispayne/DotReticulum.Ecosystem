namespace LXST;

public enum OpusProfile : byte
{
    VoiceLow,
    VoiceMedium,
    VoiceHigh,
    VoiceMaximum,
    AudioMinimum,
    AudioLow,
    AudioMedium,
    AudioHigh,
    AudioMaximum,
}

public sealed record OpusParameters(int SampleRate, int Channels, int BitrateCeiling, bool Voice)
{
    public static OpusParameters For(OpusProfile profile) => profile switch
    {
        OpusProfile.VoiceLow => new(8000, 1, 6000, true),
        OpusProfile.VoiceMedium => new(24000, 1, 8000, true),
        OpusProfile.VoiceHigh => new(48000, 1, 16000, true),
        OpusProfile.VoiceMaximum => new(48000, 2, 32000, true),
        OpusProfile.AudioMinimum => new(8000, 1, 8000, false),
        OpusProfile.AudioLow => new(12000, 1, 14000, false),
        OpusProfile.AudioMedium => new(24000, 2, 28000, false),
        OpusProfile.AudioHigh => new(48000, 2, 56000, false),
        OpusProfile.AudioMaximum => new(48000, 2, 128000, false),
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };
}
