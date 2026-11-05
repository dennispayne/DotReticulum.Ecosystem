using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DotReticulum.Applications.Rngit;
using LXMF;

namespace NomadNet;

public static partial class NomadNetPresentation
{
    public const int MarkdownRenderer = 2;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static int? Renderer(IReadOnlyDictionary<object, object?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var item in fields)
            if (item.Key is long key && key == LxmField.Renderer || item.Key is int number && number == LxmField.Renderer)
                return item.Value switch { int value => value, long value when value is >= int.MinValue and <= int.MaxValue => (int)value, _ => null };
        return null;
    }

    public static string Conversation(string text, int? renderer, NomadNetSettings? settings = null, bool ansi = false)
    {
        Validate(text);
        if (renderer != MarkdownRenderer) return NomadNetTerminalText.Safe(text);
        var page = Render(StripNonFormatting(MarkdownMicron.Format(text, urlScope: "", maxWidth: 80)), false);
        return settings is null ? page.Text : page.Present(settings, ansi);
    }

    public static string Rrc(string text, bool markdown = true, bool micron = true, string kind = "msg")
        => RrcDocument(text, markdown, micron, kind).Text;

    internal static NomadNetRenderedPage RrcDocument(string text, bool markdown = true, bool micron = true, string kind = "msg")
    {
        Validate(text);
        if (kind is not ("msg" or "message" or "action")) return new(NomadNetTerminalText.Safe(text), []);
        var first = text.Length > 0 && text[0] is '>' or '#' ? text[..1] : "";
        var remaining = first.Length == 0 ? text : text[1..];
        if (!micron) remaining = EscapedMicron().Replace(remaining, "");
        if (markdown) remaining = MarkdownMicron.Format(remaining, urlScope: "", maxWidth: 80);
        if (micron) remaining = EscapedMicron().Replace(remaining, "`$1");
        var formatted = first + StripNonFormatting(remaining);
        return Render(formatted, true);
    }

    private static void Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (StrictUtf8.GetByteCount(text) > MicronText.MaximumPageBytes)
            throw new InvalidDataException("Message presentation exceeds the one-megabyte bound.");
    }

    private static string StripNonFormatting(string text) => NonFormatting().Replace(text, "");

    private static string Normalize(string text)
    {
        text = text.Replace("🕵️", " ", StringComparison.Ordinal).Replace("☝", " ", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;
            if (Rune.IsControl(rune) && rune.Value is not ('\n' or '\t')) continue;
            result.Append(rune.ToString());
        }
        return result.ToString().Trim();
    }

    private static NomadNetRenderedPage Render(string text, bool prefixed)
    {
        const string prefix = "message: ";
        text = Normalize(prefixed ? prefix + text : text);
        Validate(text);
        var page = MicronText.Render(StrictUtf8.GetBytes(text), dividerWidth: 80);
        var rows = page.Text.Split('\n');
        var cuts = new List<(int Row, int Column, int Length)>();
        foreach (var token in page.Tokens.Where(token => token.Kind == "link").OrderByDescending(token => token.Column))
        {
            if (token.Row >= rows.Length) continue;
            var marker = $"[{token.Index + 1}] ";
            if (token.Column <= rows[token.Row].Length - marker.Length &&
                rows[token.Row].AsSpan(token.Column).StartsWith(marker, StringComparison.Ordinal))
            {
                rows[token.Row] = rows[token.Row].Remove(token.Column, marker.Length);
                cuts.Add((token.Row, token.Column, marker.Length));
            }
        }
        var result = string.Join('\n', rows);
        if (prefixed && result == prefix.TrimEnd()) return new("", []);
        if (prefixed && result.StartsWith(prefix, StringComparison.Ordinal))
        {
            result = result[prefix.Length..];
            cuts.Add((0, 0, prefix.Length));
        }
        var styles = page.Styles.Select(span => span with
        {
            Column = Map(span.Row, span.Column),
            Length = Map(span.Row, span.Column + span.Length) - Map(span.Row, span.Column),
        }).Where(span => span.Length > 0).ToArray();
        return page with { Text = result, Styles = styles, Tokens = [], Links = [] };

        int Map(int row, int column) => column - cuts.Where(cut => cut.Row == row)
            .Sum(cut => Math.Clamp(column - cut.Column, 0, cut.Length));
    }

    [GeneratedRegex("`[<>{rcl]", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex NonFormatting();

    [GeneratedRegex("¦([FB]T[0-9a-fA-F]{6}|[FB][0-9a-fA-F]{3}|[!*_=]|f`b|[fb<>{])",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EscapedMicron();
}
