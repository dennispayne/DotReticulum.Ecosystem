using System.Globalization;

namespace NomadNet;

public sealed record MicronStyle(string? Foreground = null, string? Background = null,
    bool Bold = false, bool Underline = false, bool Italic = false, bool Heading = false)
{
    public static string? Rgb(string? color)
    {
        if (color is { Length: 6 } && color.All(char.IsAsciiHexDigit)) return color;
        if (color is { Length: 3 } && color.All(char.IsAsciiHexDigit))
            return string.Concat(color.SelectMany(character => new[] { character, character }));
        if (color is { Length: 3 } && color[0] == 'g' &&
            int.TryParse(color.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var gray))
        {
            var value = (int)Math.Round(gray * 255d / 100);
            return string.Concat(Enumerable.Repeat(value.ToString("x2", CultureInfo.InvariantCulture), 3));
        }
        return null;
    }

    public string Apply(string text, NomadNetSettings settings, bool ansi)
    {
        if (!ansi || settings.ColorMode == "monochrome" || text.Length == 0) return text;
        var foreground = Rgb(Foreground);
        var background = Rgb(Background);
        if (Heading)
            foreground ??= NomadNetTerminalControls.HeadingColor(settings);
        var codes = new List<string>();
        if (Bold) codes.Add("1");
        if (Underline) codes.Add("4");
        if (Italic) codes.Add("3");
        if (foreground is not null) codes.Add(NomadNetTerminalControls.ColorCode(foreground, settings, false));
        if (background is not null) codes.Add(NomadNetTerminalControls.ColorCode(background, settings, true));
        return codes.Count == 0 ? text : "\u001b[" + string.Join(';', codes) + "m" + text + "\u001b[0m";
    }

}

public sealed record MicronStyleSpan(int Row, int Column, int Length, MicronStyle Style);
public sealed record MicronRowStyle(int Row, string Alignment, int HeadingDepth = 0);

internal sealed class MicronStyleState
{
    internal MicronStyle Current { get; set; } = new();
    internal string Alignment { get; set; } = "left";
    internal List<MicronStyleSpan> Spans { get; } = [];
    internal List<MicronRowStyle> Rows { get; } = [];
    internal void Add(int row, int column, int length)
    {
        if (length == 0) return;
        if (Spans.Count >= 65536) throw new InvalidDataException("Micron page exceeds 65536 style spans.");
        Spans.Add(new(row, column, length, Current));
    }
}
