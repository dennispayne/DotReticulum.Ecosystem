using System.Globalization;
using System.Text;

namespace NomadNet;

/// <summary>Original bounded console presentation; not Urwid layout or upstream assets.</summary>
public static class NomadNetTerminalControls
{
    public static void Validate(NomadNetSettings settings)
    {
        if (!double.IsFinite(settings.IntroTime) || settings.IntroTime is < -3600 or > 60 ||
            settings.IntroText is null || settings.IntroText.Length > 256 || settings.IntroText.Any(char.IsControl) ||
            settings.Theme is not ("dark" or "light") ||
            settings.ColorMode is not ("monochrome" or "16" or "88" or "256" or "24bit") ||
            settings.Glyphs is not ("plain" or "unicode" or "nerdfont") ||
            settings.AnimationInterval is < 1 or > 3600)
            throw new InvalidDataException("TextUI requires bounded intro (-3600–60 seconds/256 characters), a documented theme/color/glyph set and animation interval1–3600 seconds.");
    }

    public static string Name(string name, bool sanitize)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length > 65536) throw new InvalidDataException("Display name exceeds64 KiB.");
        if (sanitize) return NomadNetAnnounceStream.SanitizeName(name);
        name = name.Replace("🕵️", " ", StringComparison.Ordinal).Replace("☝", " ", StringComparison.Ordinal);
        var result = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark or UnicodeCategory.Format ||
                rune.Value is >= 0xfe00 and <= 0xfe0f or >= 0xe0100 and <= 0xe01ef or >= 0x1f3fb and <= 0x1f3ff)
                continue;
            // Even with cosmetic sanitation off, no terminal control or multiline name is emitted.
            if (!Rune.IsControl(rune) && rune.Value is not (0x2028 or 0x2029)) result.Append(rune.ToString());
        }
        return result.ToString().Trim();
    }

    public static string Icon(NomadNetSettings settings) => settings.Glyphs switch
    {
        "plain" => "*",
        "unicode" => "◆",
        _ => "\uf192",
    };

    public static string Color(string text, string rgb, NomadNetSettings settings, bool ansi, bool bold = false)
    {
        if (!ansi || settings.ColorMode == "monochrome") return text;
        var bytes = Convert.FromHexString(rgb);
        if (bytes.Length != 3) throw new ArgumentException("Expected an RGB color.", nameof(rgb));
        var code = settings.ColorMode switch
        {
            "24bit" => $"38;2;{bytes[0]};{bytes[1]};{bytes[2]}",
            "256" => "38;5;" + (16 + 36 * Cube(bytes[0], 5) + 6 * Cube(bytes[1], 5) + Cube(bytes[2], 5)),
            "88" => "38;5;" + (16 + 16 * Cube(bytes[0], 3) + 4 * Cube(bytes[1], 3) + Cube(bytes[2], 3)),
            _ => (30 + (bytes[0] >= 128 ? 1 : 0) + (bytes[1] >= 128 ? 2 : 0) +
                (bytes[2] >= 128 ? 4 : 0)).ToString(CultureInfo.InvariantCulture),
        };
        return "\u001b[" + (bold ? "1;" : "") + code + "m" + text + "\u001b[0m";
        static int Cube(byte value, int maximum) => (value * maximum + 127) / 255;
    }

    public static string Heading(string text, NomadNetSettings settings, bool ansi) =>
        Color(text, settings.Theme == "light" ? "184060" : "90d8f8", settings, ansi, true);

    public static IReadOnlyList<(string Label, string Command)> Menu(NomadNetSettings settings)
    {
        var entries = new List<(string, string)>
        {
            ("Conversations", "conversations"), ("Network", "peers"), ("Channels", "rrc hubs"),
        };
        if (settings.EnableUtilities) entries.Add(("Utilities", "utility help"));
        entries.Add(("Config", "config sections"));
        if (!settings.HideGuide) entries.Add(("Guide", "guide"));
        entries.Add(("Quit", "quit"));
        return entries.AsReadOnly();
    }

    public static string MenuText(NomadNetSettings settings) =>
        Icon(settings) + " " + string.Join(" ", Menu(settings).Select((entry, index) => $"[{index + 1}:{entry.Label}]"));

    public static string Select(NomadNetSettings settings, int number)
    {
        var menu = Menu(settings);
        return number < 1 || number > menu.Count ? throw new ArgumentOutOfRangeException(nameof(number)) : menu[number - 1].Command;
    }

    public static string? Mouse(NomadNetSettings settings, string sequence)
    {
        if (!settings.MouseEnabled || sequence.Length > 32 || !sequence.StartsWith("\u001b[<", StringComparison.Ordinal) ||
            !sequence.EndsWith('M')) return null;
        var fields = sequence[3..^1].Split(';');
        if (fields.Length != 3 || !int.TryParse(fields[0], out var button) || button != 0 ||
            !int.TryParse(fields[1], out var column) || column is < 1 or > 4096 ||
            !int.TryParse(fields[2], out var row) || row != 1) return null;
        var offset = 3; // Original one-cell icon and separator, with one-based columns.
        foreach (var (entry, index) in Menu(settings).Select((entry, index) => (entry, index)))
        {
            var length = $"[{index + 1}:{entry.Label}]".Length;
            if (column >= offset && column < offset + length) return entry.Command;
            offset += length + 1;
        }
        return null;
    }

    public static async Task IntroAsync(NomadNetSettings settings, TextWriter output, bool ansi, CancellationToken token)
    {
        Validate(settings);
        token.ThrowIfCancellationRequested();
        if (settings.IntroTime <= 0) return;
        await output.WriteLineAsync(Heading(settings.IntroText, settings, ansi).AsMemory(), token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(settings.IntroTime), token).ConfigureAwait(false);
    }

    public static async Task WatchAsync(NomadNetSettings settings, int frames, Func<string> status,
        TextWriter output, bool ansi, CancellationToken token)
    {
        Validate(settings);
        if (frames is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(frames), "Watch requires1–60 frames.");
        for (var index = 0; index < frames; index++)
        {
            token.ThrowIfCancellationRequested();
            await output.WriteLineAsync(Heading($"{Icon(settings)} {index + 1}/{frames} " +
                NomadNetTerminalText.Safe(status()), settings, ansi).AsMemory(), token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
            if (index + 1 < frames)
                await Task.Delay(TimeSpan.FromSeconds(settings.AnimationInterval), token).ConfigureAwait(false);
        }
    }
}
