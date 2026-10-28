namespace LXST;

public sealed record TelephoneProfileInfo(TelephoneProfile Profile, string Name, string Abbreviation, int FrameMilliseconds);

public static class TelephoneProfiles
{
    public static IReadOnlyList<TelephoneProfileInfo> Available { get; } = Array.AsReadOnly<TelephoneProfileInfo>(
    [
        new(TelephoneProfile.UltraLowBandwidth, "Ultra Low Bandwidth", "ULBW", 400),
        new(TelephoneProfile.VeryLowBandwidth, "Very Low Bandwidth", "VLBW", 320),
        new(TelephoneProfile.LowBandwidth, "Low Bandwidth", "LBW", 200),
        new(TelephoneProfile.MediumQuality, "Medium Quality", "MQ", 60),
        new(TelephoneProfile.HighQuality, "High Quality", "HQ", 60),
        new(TelephoneProfile.MaximumQuality, "Super High Quality", "SHQ", 60),
        new(TelephoneProfile.LowLatency, "Low Latency", "LL", 20),
        new(TelephoneProfile.UltraLowLatency, "Ultra Low Latency", "ULL", 10),
    ]);

    public static TelephoneProfileInfo Get(TelephoneProfile profile) =>
        Available.FirstOrDefault(value => value.Profile == profile) ?? throw new ArgumentOutOfRangeException(nameof(profile));

    public static TelephoneProfile Next(TelephoneProfile profile)
    {
        for (var index = 0; index < Available.Count; index++)
            if (Available[index].Profile == profile) return Available[(index + 1) % Available.Count].Profile;
        throw new ArgumentOutOfRangeException(nameof(profile));
    }
}
