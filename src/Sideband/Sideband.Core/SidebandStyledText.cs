using System.Globalization;
using System.Text;

namespace Sideband;

public sealed record SidebandTextStyle(bool Bold = false, bool Italic = false, bool Underline = false,
    bool Strikethrough = false, bool Monospace = false, double Size = 14, string? Color = null, int Script = 0);

public sealed record SidebandTextRun(string Text, SidebandTextStyle Style);

public static class SidebandStyledText
{
    public const int MaximumRuns = 4096;

    public static IReadOnlyList<SidebandTextRun> Parse(string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);
        if (markup.Length > SidebandMessageMarkup.MaximumMarkupLength)
            throw new InvalidDataException("Message markup exceeds the rendering limit.");
        var result = new List<SidebandTextRun>();
        var text = new StringBuilder();
        var stacks = new Dictionary<string, Stack<SidebandTextStyle>>(StringComparer.Ordinal);
        var style = new SidebandTextStyle();
        var nesting = 0;
        var tags = 0;
        for (var index = 0; index < markup.Length;)
        {
            if (markup[index] != '[') { text.Append(markup[index++]); continue; }
            var remaining = markup.AsSpan(index);
            var close = remaining.IndexOfAny(']', '\n');
            if (close < 0) { text.Append(remaining); break; }
            if (remaining[close] != ']')
            {
                text.Append(remaining[..(close + 1)]);
                index += close + 1;
                continue;
            }
            var token = remaining[1..close].ToString();
            if (++tags > 8192) throw new InvalidDataException("Message markup exceeds the tag limit.");
            var closing = token.StartsWith('/');
            var equals = token.IndexOf('=');
            var name = closing ? token[1..] : equals < 0 ? token : token[..equals];
            var value = equals < 0 ? "" : token[(equals + 1)..];
            if (name is not ("b" or "i" or "u" or "s" or "font" or "size" or "color" or "sub" or "sup") ||
                equals >= 0 && name is "b" or "i" or "u" or "s" or "sub" or "sup")
            {
                text.Append(remaining[..(close + 1)]);
                index += close + 1;
                continue;
            }
            Flush();
            if (closing)
            {
                if (stacks.TryGetValue(name, out var saved) && saved.TryPop(out var previous))
                {
                    nesting--;
                    style = name switch
                    {
                        "b" => style with { Bold = previous.Bold },
                        "i" => style with { Italic = previous.Italic },
                        "u" => style with { Underline = previous.Underline },
                        "s" => style with { Strikethrough = previous.Strikethrough },
                        "font" => style with { Monospace = previous.Monospace },
                        "size" => style with { Size = previous.Size },
                        "color" => style with { Color = previous.Color },
                        _ => style with { Size = previous.Size, Script = previous.Script }
                    };
                }
                else throw new InvalidDataException($"Unmatched closing message style: {name}.");
            }
            else
            {
                if (++nesting > 64) throw new InvalidDataException("Message markup exceeds the style nesting limit.");
                if (!stacks.TryGetValue(name, out var saved)) stacks[name] = saved = new();
                saved.Push(style);
                style = name switch
                {
                    "b" => style with { Bold = true },
                    "i" => style with { Italic = true },
                    "u" => style with { Underline = true },
                    "s" => style with { Strikethrough = true },
                    "font" when value is "mono" or "monospace" => style with { Monospace = true },
                    "font" => throw new InvalidDataException("Only local monospace font selection is supported in messages."),
                    "size" => style with { Size = Size(value) },
                    "color" => style with { Color = Color(value) },
                    "sub" => style with { Size = Math.Max(8, style.Size * .5), Script = -1 },
                    _ => style with { Size = Math.Max(8, style.Size * .5), Script = 1 }
                };
            }
            index += close + 1;
        }
        Flush();
        return result;

        void Flush()
        {
            if (text.Length == 0) return;
            if (result.Count == MaximumRuns) throw new InvalidDataException("Message formatting exceeds the styled-run limit.");
            var decoded = text.ToString().Replace("&bl;", "[", StringComparison.Ordinal)
                .Replace("&br;", "]", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);
            result.Add(new(decoded, style));
            text.Clear();
        }
    }

    private static double Size(string value)
    {
        if (value.EndsWith("sp", StringComparison.Ordinal) || value.EndsWith("px", StringComparison.Ordinal))
            value = value[..^2];
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size) ||
            !double.IsFinite(size) || size < 0)
            throw new InvalidDataException("Message font size must be a nonnegative pixel or sp value.");
        return Math.Clamp(size, 8, 48);
    }

    private static string Color(string value)
    {
        var hex = value.StartsWith('#') ? value.AsSpan(1) : value.AsSpan();
        if (hex.Length is 6 or 8 && hex.IndexOfAnyExcept("0123456789abcdefABCDEF") < 0) return "#" + hex.ToString();
        if (value.Length is > 0 and <= 32 && value.AsSpan().IndexOfAnyExceptInRange('a', 'z') < 0) return value;
        throw new InvalidDataException("Message color must be a named color or RGB/RGBA hex value.");
    }
}
