using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.TaskLists;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Sideband;

public static class SidebandMessageMarkup
{
    public const int MaximumMarkupLength = 262_144;
    private static readonly (MarkdownPipeline Markdown, InlineParserList Inlines) Parsers = CreatePipeline();
    private static readonly Regex Tags = Pattern(@"\[(?<tag>[^\]\n]*?)\]");
    private static readonly Regex Images = Pattern(@"\[img[^\]\n]*\][^\n]*\[/img[^\]\n]*\]", ignoreCase: true);

    public static string Build(string content, long? renderer, bool trustedOnly, bool trusted)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > 65_536) throw new InvalidDataException("Message text exceeds the 65536-character rendering limit.");
        var formatted = !(trustedOnly && !trusted) && renderer is
            SidebandMessageFormatting.MarkdownRenderer or SidebandMessageFormatting.BbCodeRenderer;
        var markup = formatted
            ? ProcessBbCode(renderer == SidebandMessageFormatting.MarkdownRenderer ? ConvertMarkdown(content) : content)
            : Escape(content);
        if (markup.Length > MaximumMarkupLength) throw new InvalidDataException("Message formatting exceeds its bounded output limit.");
        return string.IsNullOrWhiteSpace(markup) ? "[i]This message contains no text content[/i]" : markup;
    }

    public static string Escape(string content) => content.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("[", "&bl;", StringComparison.Ordinal).Replace("]", "&br;", StringComparison.Ordinal);

    private static Regex Pattern(string pattern, bool ignoreCase = false) => new(pattern,
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
        TimeSpan.FromSeconds(1));

    private static (MarkdownPipeline, InlineParserList) CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough |
            EmphasisExtraOptions.Subscript | EmphasisExtraOptions.Superscript | EmphasisExtraOptions.Marked)
            .UseTaskLists().UseFootnotes();
        builder.InlineParsers.FindExact<EmphasisInlineParser>()!.EmphasisDescriptors.Add(new('^', 1, 2, true));
        builder.InlineParsers.Insert(0, new SpoilerParser());
        return (builder.Build(), new InlineParserList(builder.InlineParsers));
    }

    private static string ProcessBbCode(string content)
    {
        content = Images.Replace(content, "");
        return Tags.Replace(content, match =>
        {
            var tag = match.Groups["tag"].Value;
            var lower = tag.ToLowerInvariant();
            var closing = lower.StartsWith('/');
            var name = closing ? lower[1..] : lower;
            if (name.StartsWith("code", StringComparison.Ordinal) || name.StartsWith("icode", StringComparison.Ordinal))
                return closing ? "[/size][/font]" : "[font=mono][size=14]";
            if (name.StartsWith("heading", StringComparison.Ordinal))
            {
                if (closing) return "[/size][/b]";
                var size = name is "heading" or "heading=1" ? 20 : name == "heading=2" ? 18 : 16;
                return $"[b][size={size}]";
            }
            if (name.StartsWith("list", StringComparison.Ordinal) || name.StartsWith("url", StringComparison.Ordinal)) return "";
            if (name.StartsWith('*') && match.Index > 0 && content[match.Index - 1] == '\n') return " - ";
            if (!closing && name.StartsWith("size=", StringComparison.Ordinal) && tag.AsSpan(5).IndexOfAnyExceptInRange('0', '9') < 0)
                return "[size=" + tag[5..] + "sp]";
            return match.Value;
        });
    }

    private static string ConvertMarkdown(string content)
    {
        var output = new StringBuilder();
        var document = Markdown.Parse(content, Parsers.Markdown);
        WriteBlocks(document, output, content, 0);
        return output.ToString().TrimEnd('\n');
    }

    private static void WriteBlocks(ContainerBlock container, StringBuilder output, string source, int depth)
    {
        if (depth > 64) throw new InvalidDataException("Message Markdown exceeds the nesting limit.");
        foreach (var block in container)
        {
            switch (block)
            {
                case FootnoteGroup notes:
                    output.Append("[b]Footnotes[/b]\n");
                    foreach (var note in notes.OfType<Footnote>())
                    {
                        var index = note.Order;
                        output.Append("[ANAME=footnote-").Append(index).Append(']').Append(index).Append("[/ANAME]. ");
                        WriteBlocks(note, output, source, depth + 1);
                    }
                    break;
                case HeadingBlock heading:
                    var level = Math.Min(heading.Level, 3);
                    output.Append("[heading=").Append(level).Append(']');
                    WriteInlines(heading.Inline, output, depth + 1);
                    output.Append("[/heading]\n");
                    break;
                case ParagraphBlock paragraph:
                    WriteInlines(paragraph.Inline, output, depth + 1);
                    output.Append("\n\n");
                    break;
                case QuoteBlock quote:
                    var raw = source.AsSpan(quote.Span.Start, quote.Span.Length).ToString();
                    var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                    var spoiler = lines.All(line => line.TrimStart(' ').StartsWith(">!", StringComparison.Ordinal) ||
                        line.TrimStart(' ').StartsWith("> !", StringComparison.Ordinal));
                    output.Append(spoiler ? "[SPOILER]\n" : "| [i]");
                    if (spoiler)
                    {
                        var body = string.Join('\n', lines.Select(line =>
                        {
                            var start = line.IndexOf('!');
                            return line[(start + 1)..].TrimStart(' ');
                        }));
                        WriteBlocks(Markdown.Parse(body, Parsers.Markdown), output, body, depth + 1);
                    }
                    else WriteBlocks(quote, output, source, depth + 1);
                    output.Append(spoiler ? "\n[/SPOILER]" : "[/i]");
                    break;
                case ListBlock list:
                    WriteList(list, output, source, depth + 1);
                    break;
                case CodeBlock code:
                    output.Append("[code]").Append(EscapeCode(code.Lines.ToString()))
                        .Append("\n[/code]\n");
                    break;
                case ThematicBreakBlock:
                    output.Append("-------------\n");
                    break;
                case HtmlBlock or LinkReferenceDefinitionGroup:
                    break;
                case ContainerBlock children:
                    WriteBlocks(children, output, source, depth + 1);
                    break;
                case LeafBlock leaf:
                    WriteInlines(leaf.Inline, output, depth + 1);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported Markdown block: {block.GetType().Name}.");
            }

            if (output.Length > MaximumMarkupLength) throw new InvalidDataException("Message formatting exceeds its bounded output limit.");
        }
    }

    private static string EscapeCode(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static void WriteList(ListBlock list, StringBuilder output, string source, int depth)
    {
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var body = new StringBuilder();
            WriteBlocks(item, body, source, depth + 1);
            var first = item.OfType<ParagraphBlock>().FirstOrDefault()?.Inline?.FirstChild;
            output.Append(first is TaskList ? "" : "\u2022 ");
            output.AppendJoin('\n', body.ToString().Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)));
            output.Append('\n');
        }
        output.Append('\n');
    }

    private static void WriteInlines(ContainerInline? container, StringBuilder output, int depth)
    {
        if (container is null) return;
        if (depth > 64) throw new InvalidDataException("Message Markdown exceeds the nesting limit.");
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    output.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    output.Append("[icode]").Append(code.Content).Append("[/icode]");
                    break;
                case SpoilerInline spoiler:
                    output.Append("[ISPOILER]");
                    var fragment = new ParagraphBlock { Lines = new StringLineGroup(spoiler.Content) };
                    new InlineProcessor(new MarkdownDocument(), Parsers.Inlines, false, null).ProcessInlineLeaf(fragment);
                    WriteInlines(fragment.Inline, output, depth + 1);
                    output.Append("[/ISPOILER]");
                    break;
                case EmphasisInline emphasis:
                    var tag = emphasis.DelimiterChar switch
                    {
                        '*' or '_' => emphasis.DelimiterCount == 2 ? "b" : "i",
                        '~' => emphasis.DelimiterCount == 2 ? "s" : "sub",
                        '^' => emphasis.DelimiterCount == 2 ? "u" : "sup",
                        '=' => "",
                        _ => throw new InvalidDataException("Unsupported Markdown emphasis.")
                    };
                    if (tag.Length > 0) output.Append('[').Append(tag).Append(']');
                    WriteInlines(emphasis, output, depth + 1);
                    if (tag.Length > 0) output.Append("[/").Append(tag).Append(']');
                    break;
                case LinkInline { IsImage: true }:
                    break;
                case LinkInline link:
                    output.Append("[u]");
                    WriteInlines(link, output, depth + 1);
                    output.Append("[/u] (").Append(link.Url).Append(')');
                    break;
                case AutolinkInline auto:
                    output.Append("[u]").Append(auto.Url).Append("[/u] (").Append(auto.Url).Append(')');
                    break;
                case LineBreakInline:
                    output.Append('\n');
                    break;
                case HtmlInline html:
                    output.Append(html.Tag);
                    break;
                case HtmlEntityInline entity:
                    output.Append(entity.Transcoded);
                    break;
                case TaskList task:
                    output.Append(task.Checked ? "\U000f0c52" : "\U000f0131");
                    break;
                case FootnoteLink { IsBackLink: true }:
                    break;
                case FootnoteLink note:
                    output.Append("[sup][u]").Append(note.Footnote.Order).Append("[/u][/sup]");
                    break;
                case ContainerInline children:
                    WriteInlines(children, output, depth + 1);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported Markdown inline: {inline.GetType().Name}.");
            }
        }

    }

    private sealed class SpoilerInline(string content) : LeafInline
    {
        public string Content { get; } = content;
    }

    private sealed class SpoilerParser : InlineParser
    {
        public SpoilerParser() => OpeningCharacters = ['>'];

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            if (slice.PeekChar(1) != '!') return false;
            if (processor.ParserStates[Index] is not int[] ends)
            {
                ends = new int[slice.Text.Length + 1];
                Array.Fill(ends, -1);
                var next = -1;
                for (var index = slice.End; index >= slice.Start; index--)
                {
                    if (slice.Text[index] == '\n') next = -1;
                    else if (index < slice.End && slice.Text[index] == '!' && slice.Text[index + 1] == '<') next = index;
                    ends[index] = next;
                }
                processor.ParserStates[Index] = ends;
            }
            var start = slice.Start + 2;
            if (start >= ends.Length || ends[start] <= start) return false;
            var end = ends[start];
            processor.Inline = new SpoilerInline(slice.Text[start..end].Trim());
            slice.Start = end + 2;
            return true;
        }
    }
}
