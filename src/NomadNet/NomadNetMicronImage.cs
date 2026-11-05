using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NomadNet;

public sealed partial record NomadNetPageMedia
{
    public string? Width { get; init; }
    public string? Height { get; init; }
    public string Alignment { get; init; } = "center";

    internal static NomadNetPageMedia Parse(string[] parts)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in parts.Skip(1).SkipLast(1))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2) properties[pair[0].Trim()] = pair[1].Trim();
        }
        return new(NomadNetTerminalText.Safe(parts[0].Trim()), NomadNetTerminalText.Safe(parts[^1].Trim()))
        {
            Width = Size(properties.GetValueOrDefault("w")),
            Height = Size(properties.GetValueOrDefault("h")),
            Alignment = properties.GetValueOrDefault("a") switch
            {
                "l" or "left" or "<" => "left",
                "r" or "right" or ">" => "right",
                _ => "center",
            },
        };
    }

    private static string? Size(string? value)
    {
        if (value is null) return null;
        if (value.Equals("n", StringComparison.OrdinalIgnoreCase)) return "n";
        if (value.Length > 32) throw new InvalidDataException("Micron image size specification exceeds 32 characters.");
        var normalized = Number(value);
        var percent = PercentSize().IsMatch(normalized);
        var digits = normalized.Length > 0 && normalized.All(char.IsAsciiDigit);
        if (!percent && !digits) return null;
        if (!double.TryParse(percent ? normalized[..^1] : normalized, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var size) || !double.IsFinite(size) || size <= 0) return null;
        if (size > (percent ? 1000 : 4096))
            throw new InvalidDataException("Micron image size exceeds 4096 cells or 1000 percent.");
        return digits ? ((int)size).ToString(CultureInfo.InvariantCulture) : value;
    }

    private static string Number(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber)
                result.Append((char)('0' + (int)Rune.GetNumericValue(rune)));
            else result.Append(rune.ToString());
        return result.ToString();
    }

    public (int Width, int Height) Layout(int imageWidth, int imageHeight, int columns = 80,
        int terminalRows = 32, int maximumRows = 120)
    {
        if (imageWidth is < 1 or > 16000000 || imageHeight is < 1 or > 16000000 ||
            (long)imageWidth * imageHeight > 16000000 || columns is < 1 or > 160 ||
            terminalRows is < 1 or > 4096 || maximumRows is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(columns), "Invalid bounded Micron image layout.");
        var width = Resolve(Size(Width), columns, imageWidth) ?? columns;
        var height = Resolve(Size(Height), Math.Max(1, terminalRows - 8), imageHeight / 2d);
        double resultWidth, resultHeight;
        if (Width is not null && height is not null)
        {
            var scale = Math.Min(1, Math.Min(columns / width, maximumRows / height.Value));
            resultWidth = width * scale;
            resultHeight = height.Value * scale;
        }
        else if (height is not null)
        {
            resultHeight = Math.Min(maximumRows, Math.Max(1, Math.Round(height.Value)));
            resultWidth = Math.Min(columns, Math.Round(resultHeight * 2 * imageWidth / imageHeight));
        }
        else
        {
            resultWidth = Math.Min(columns, Math.Max(1, Math.Round(width)));
            resultHeight = Math.Max(1, Math.Round(resultWidth * imageHeight / (2 * imageWidth)));
            if (resultHeight > maximumRows)
            {
                resultHeight = maximumRows;
                resultWidth = Math.Min(columns, resultHeight * 2 * imageWidth / imageHeight);
            }
        }
        return (Math.Max(1, (int)Math.Round(resultWidth)), Math.Max(1, (int)Math.Round(resultHeight)));

        static double? Resolve(string? size, int available, double native) => size switch
        {
            null => null,
            "n" => native,
            _ when size.EndsWith('%') => available * double.Parse(Number(size[..^1]), CultureInfo.InvariantCulture) / 100,
            _ => double.Parse(size, CultureInfo.InvariantCulture),
        };
    }

    [GeneratedRegex(@"^\d+(?:\.\d+)?%$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PercentSize();
}
