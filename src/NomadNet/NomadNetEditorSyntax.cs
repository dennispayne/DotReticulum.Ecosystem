using System.Globalization;
using System.Text;

namespace NomadNet;

public enum NomadNetEditorStyle { Plain, Comment, Heading, Tag, Field, Link }
public sealed record NomadNetEditorSpan(int Start, int Length, NomadNetEditorStyle Style);
public sealed record NomadNetEditorRow(string Text, IReadOnlyList<NomadNetEditorSpan> Spans);

/// <summary>Original source token presentation, independently authored from executed lexical observations.</summary>
public static class NomadNetEditorSyntax
{
    public const int MaximumHighlightCharacters = 50_000;

    public static IReadOnlyList<NomadNetEditorSpan> Highlight(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > NomadNetEditor.MaximumBytes) throw new InvalidDataException("Source exceeds editor bound.");
        if (text.EnumerateRunes().Count() > MaximumHighlightCharacters) return [];
        var result = new List<NomadNetEditorSpan>();
        var start = 0;
        var first = true;
        while (start < text.Length)
        {
            var length = 1;
            var style = NomadNetEditorStyle.Plain;
            var remaining = text.AsSpan(start);
            if (first && remaining[0] == '#')
            {
                var newline = remaining.IndexOf('\n');
                length = newline < 0 ? remaining.Length : newline;
                style = NomadNetEditorStyle.Comment;
            }
            else if (first && (remaining[0] == '>' ||
                remaining.Length >= 3 && remaining[0] == '`' && remaining[1] is '+' or '-' && remaining[2] == '>'))
            {
                length = remaining[0] == '>' ? 0 : 2;
                while (length < remaining.Length && remaining[length] == '>') length++;
                style = NomadNetEditorStyle.Heading;
            }
            else if (remaining[0] == '\\' && remaining.Length > 1)
                length = 1 + Rune.GetRuneAt(text, start + 1).Utf16SequenceLength;
            else if (remaining[0] == '`')
            {
                style = NomadNetEditorStyle.Tag;
                length = ScalarPrefix(remaining, 2);
                if (remaining.Length > 1)
                {
                    var closing = remaining[1] switch { '<' => '>', '[' => ']', '{' => '}', _ => '\0' };
                    if (closing != '\0')
                    {
                        var end = remaining[2..].IndexOf(closing);
                        length = end < 0 ? remaining.Length : end + 3;
                        style = closing == '>' ? NomadNetEditorStyle.Field : NomadNetEditorStyle.Link;
                    }
                    else if (remaining[1] is 'F' or 'B')
                        length = ScalarPrefix(remaining, remaining.Length > 2 && remaining[2] == 'T' ? 9 : 5);
                    else if (remaining[1] == ':')
                    {
                        while (length < remaining.Length)
                        {
                            var rune = Rune.GetRuneAt(text, start + length);
                            if (!Rune.IsLetterOrDigit(rune) && Rune.GetUnicodeCategory(rune) is not
                                (UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber) && rune.Value is not ('_' or '-')) break;
                            length += rune.Utf16SequenceLength;
                        }
                    }
                }
            }
            else length = Rune.GetRuneAt(text, start).Utf16SequenceLength;
            if (result.Count > 0 && result[^1].Style == style)
                result[^1] = result[^1] with { Length = result[^1].Length + length };
            else result.Add(new(start, length, style));
            first = length == 1 && text[start] == '\n';
            start += length;
        }
        return result;
    }

    private static int ScalarPrefix(ReadOnlySpan<char> text, int count)
    {
        var length = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            length += rune.Utf16SequenceLength;
            if (--count == 0) break;
        }
        return length;
    }

    public static IReadOnlyList<NomadNetEditorRow> Rows(string text, int width, int maximumRows = 60, int firstRow = 0)
    {
        if (width is < 1 or > 240 || maximumRows is < 1 or > 60 || firstRow < 0) throw new ArgumentOutOfRangeException(nameof(width));
        var spans = Highlight(text);
        var rows = new List<NomadNetEditorRow>();
        foreach (var (offset, take, prefix) in Layout(text, width).Skip(firstRow).Take(maximumRows))
        {
            var safe = SafeCells(text.Substring(offset, take));
            if (safe.Length > width - prefix.Length) safe = "?";
            var rowSpans = spans.Where(span => span.Start < offset + take && span.Start + span.Length > offset)
                .Select(span => new NomadNetEditorSpan(prefix.Length + Math.Max(0, span.Start - offset),
                    Math.Min(offset + take, span.Start + span.Length) - Math.Max(offset, span.Start), span.Style))
                .Where(span => span.Start + span.Length <= prefix.Length + safe.Length).ToArray();
            rows.Add(new(prefix + safe.PadRight(width - prefix.Length), rowSpans));
        }
        return rows;
    }

    public static int CountRows(string text, int width)
    {
        if (width is < 1 or > 240) throw new ArgumentOutOfRangeException(nameof(width));
        if (text.Length > NomadNetEditor.MaximumBytes) throw new InvalidDataException("Source exceeds editor bound.");
        return Layout(text, width).Count();
    }

    private static IEnumerable<(int Offset, int Length, string Prefix)> Layout(string text, int width)
    {
        var lineCount = text.Count(character => character == '\n') + 1;
        var gutter = Math.Max(3, lineCount.ToString(CultureInfo.InvariantCulture).Length + 2);
        if (width <= gutter) gutter = 0;
        var available = width - gutter;
        var wrapWords = text.EnumerateRunes().Count() <= MaximumHighlightCharacters;
        var offset = 0;
        var number = 1;
        foreach (var line in text.Split('\n'))
        {
            var position = 0;
            do
            {
                if (position > 0 && wrapWords)
                    while (position < line.Length && line[position] == ' ') position++;
                if (position == line.Length && line.Length > 0) break;
                var take = Math.Min(available, line.Length - position);
                if (take > 0 && position + take < line.Length && char.IsHighSurrogate(line[position + take - 1])) take--;
                if (take == 0 && position < line.Length) take = Math.Min(2, line.Length - position);
                // Match observed word wrapping for ordinary source; long words still make progress.
                if (wrapWords && position + take < line.Length && take > 1)
                {
                    var space = line.AsSpan(position, take).LastIndexOf(' ');
                    if (space >= 0)
                    {
                        var nextSpace = line.AsSpan(position + space + 1).IndexOf(' ');
                        var wordLength = nextSpace < 0 ? line.Length - position - space - 1 : nextSpace;
                        if (wordLength <= available) take = space + 1;
                    }
                }
                var prefix = gutter == 0 ? "" : position == 0
                    ? number.ToString(CultureInfo.InvariantCulture).PadLeft(gutter - 1) + " " : new string(' ', gutter);
                yield return (offset + position, take, prefix);
                position += take;
            } while (position < line.Length);
            offset += line.Length + 1;
            number++;
        }
    }

    public static string SafeCells(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            result.Append(Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
                ? new string('?', rune.Utf16SequenceLength) : rune.ToString());
        return result.ToString();
    }

    public static string Color(NomadNetEditorRow row, NomadNetSettings settings, bool ansi)
    {
        if (!ansi || settings.ColorMode == "monochrome") return row.Text;
        var result = new StringBuilder();
        var position = 0;
        foreach (var span in row.Spans)
        {
            if (span.Start > position) result.Append(row.Text.AsSpan(position, span.Start - position));
            var text = row.Text.Substring(span.Start, span.Length);
            result.Append(span.Style == NomadNetEditorStyle.Plain ? text : NomadNetTerminalControls.Color(text,
                span.Style switch
                {
                    NomadNetEditorStyle.Comment => "808080",
                    NomadNetEditorStyle.Heading => "30b8e0",
                    NomadNetEditorStyle.Field => "b89020",
                    NomadNetEditorStyle.Link => "40a080",
                    _ => "b070c0",
                }, settings, ansi));
            position = span.Start + span.Length;
        }
        result.Append(row.Text.AsSpan(position));
        return result.ToString();
    }
}
