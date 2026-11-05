namespace NomadNet;

public static class NomadNetRrcPolicy
{
    public static void Validate(NomadNetSettings settings)
    {
        if (settings.RrcHistoryPerRoomCap is < 0 or > RrcSessionState.MaximumHistory)
            throw new InvalidDataException("RRC global history cap requires 0–1000; 0 disables the per-room cap, not aggregate safety bounds.");
        if (!double.IsFinite(settings.RrcEphemeralNoticeMinutes) || settings.RrcEphemeralNoticeMinutes is < 0 or > 43200)
            throw new InvalidDataException("RRC ephemeral notice minutes require a finite value between0 and43200.");
        if (settings.RrcMentionColor is { Length: > 0 } color && !ValidColor(color))
            throw new InvalidDataException("RRC mention color requires six hexadecimal digits.");
        if (settings.RrcNickColorsTheme is null || settings.RrcNickColorsTheme.Count > 128 ||
            settings.RrcNickColorsTheme.Any(color => !ValidColor(color)))
            throw new InvalidDataException("RRC nickname palette requires at most128 six-hexadecimal-digit colors.");
    }

    public static bool ValidColor(string? color) => color is { Length: 6 } && color.All(char.IsAsciiHexDigit);

    internal static int EffectiveCap(int global, int perHub) => global == 0 ? perHub : perHub == 0 ? global : Math.Min(global, perHub);
}
