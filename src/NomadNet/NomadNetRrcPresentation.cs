using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NomadNet;

public sealed record RrcTextSpan(string Text, string? Color = null, bool Bold = false)
{
    public MicronStyle? Formatting { get; init; }
}
public sealed record RrcStyledMessage(string TimestampColor, string NickColor, IReadOnlyList<RrcTextSpan> Body);

/// <summary>Original bounded console presentation; not an Urwid layout or copied theme.</summary>
public static partial class NomadNetRrcPresentation
{
    private static readonly string[] OriginalPalette = ["1a91c4", "bc6b24", "4c993a", "9568b7", "ca5c89", "249b92"];

    public static string NickColor(string hash, NomadNetSettings settings)
    {
        var colors = settings.RrcNickColorsTheme.Count == 0 ? OriginalPalette : settings.RrcNickColorsTheme.ToArray();
        var remainder = 0;
        if (hash.Length is not (12 or 32) || !hash.All(char.IsAsciiHexDigit)) return "55bbbb";
        foreach (var value in Convert.FromHexString(hash)) remainder = (remainder * 256 + value) % colors.Length;
        return colors[(remainder + 15) % colors.Length];
    }

    public static RrcStyledMessage Style(RrcChatMessage message, NomadNetSettings settings, string ownHash, string ownNick,
        IReadOnlyDictionary<string, string>? nicks = null)
    {
        NomadNetRrcPolicy.Validate(settings);
        if (Encoding.UTF8.GetByteCount(message.Text) > RrcSessionState.MaximumResourceBytes)
            throw new InvalidDataException("RRC presentation exceeds the message bound.");
        var nickColor = settings.RrcNickColors ? NickColor(message.Source, settings) : message.Source == ownHash ? "bbddff" : "55bbbb";
        var mentionColor = string.IsNullOrEmpty(settings.RrcMentionColor)
            ? settings.RrcNickColors ? NickColor(ownHash, settings) : "ffbb44" : settings.RrcMentionColor;
        var spans = new List<RrcTextSpan>();
        var stamp = "888888";
        if (message.Kind is "system" or "notice" or "error")
            return new(stamp, nickColor, [new(NomadNetTerminalText.Safe(message.Text), message.Kind == "error" ? "ee6655" : "999999")]);
        var source = message.Text;
        var code = CodeBlocks().Matches(source);
        var document = NomadNetPresentation.RrcDocument(source, settings.RrcRenderMarkdown, settings.RrcRenderMicron, message.Kind);
        var rendered = document.Text;
        var unprotected = rendered;
        if (code.Count != 0)
        {
            // Whole-body passes preserve Markdown/Micron context; rendering code fragments changes visible text.
            var masked = source.ToCharArray();
            foreach (Match block in code)
                for (var index = block.Index; index < block.Index + block.Length; index++)
                    if (masked[index] == '@') masked[index] = 'Q';
            unprotected = NomadNetPresentation.Rrc(new string(masked), settings.RrcRenderMarkdown, settings.RrcRenderMicron, message.Kind);
        }
        Add();
        var formatting = new MicronStyle?[rendered.Length];
        var offsets = new List<int> { 0 };
        for (var index = 0; index < rendered.Length; index++) if (rendered[index] == '\n') offsets.Add(index + 1);
        foreach (var span in document.Styles)
        {
            if (span.Row >= offsets.Count || span.Style == new MicronStyle()) continue;
            var start = offsets[span.Row] + span.Column;
            if (start >= formatting.Length) continue;
            Array.Fill(formatting, span.Style, start, Math.Min(span.Length, formatting.Length - start));
        }
        var combined = new List<RrcTextSpan>();
        var offset = 0;
        foreach (var span in spans)
        {
            var start = 0;
            while (start < span.Text.Length)
            {
                var style = formatting[offset + start];
                var end = start + 1;
                while (end < span.Text.Length && formatting[offset + end] == style) end++;
                combined.Add(span with { Text = span.Text[start..end], Formatting = style });
                start = end;
            }
            offset += span.Text.Length;
        }
        return new(stamp, nickColor, combined.AsReadOnly());

        void Add()
        {
            if (unprotected.Length != rendered.Length || ownNick.Length == 0) { spans.Add(new(rendered)); return; }
            var start = 0;
            var mentions = Mentions().Matches(rendered).Select(match => (Index: match.Index, Length: match.Length,
                Name: match.Groups[1].Value)).ToList();
            var own = "@" + ownNick;
            for (var index = 0; index <= rendered.Length - own.Length;)
            {
                var match = rendered.IndexOf(own, index, StringComparison.OrdinalIgnoreCase);
                if (match < 0) break;
                var end = match + own.Length;
                if ((match == 0 || !AsciiWord(rendered[match - 1])) &&
                    (end == rendered.Length || !AsciiWord(rendered[end])))
                    mentions.Add((match, own.Length, ownNick));
                index = end;
            }
            foreach (var mention in mentions.OrderBy(item => item.Index).ThenByDescending(item => item.Length))
            {
                if (mention.Index < start || !rendered.AsSpan(mention.Index, mention.Length)
                    .SequenceEqual(unprotected.AsSpan(mention.Index, mention.Length))) continue;
                var name = mention.Name;
                var self = name.Equals(ownNick, StringComparison.OrdinalIgnoreCase);
                var hash = nicks?.FirstOrDefault(pair => pair.Value.Equals(name, StringComparison.Ordinal)).Key;
                if (!self && (!settings.RrcNickColors || string.IsNullOrEmpty(hash))) continue;
                spans.Add(new(rendered[start..mention.Index]));
                spans.Add(new(rendered.Substring(mention.Index, mention.Length), self ? mentionColor : NickColor(hash!, settings), true));
                if (self && settings.RrcColorMentionTimestamps)
                    stamp = settings.RrcNickColors ? mentionColor : "ffbb44";
                start = mention.Index + mention.Length;
            }
            spans.Add(new(rendered[start..]));
        }
    }

    private static bool AsciiWord(char value) => char.IsAsciiLetterOrDigit(value) || value == '_';

    public static string Render(RrcChatMessage message, NomadNetSettings settings, string ownHash, string ownNick,
        IReadOnlyDictionary<string, string>? nicks = null, bool ansi = false, int width = 80)
    {
        if (width is < 24 or > 240) throw new ArgumentOutOfRangeException(nameof(width));
        var style = Style(message, settings, ownHash, ownNick, nicks);
        string time;
        try { time = DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { time = "        "; }
        var timestamp = "[" + time + "] ";
        var name = NomadNetTerminalControls.Name(message.Nick ?? (message.Source.Length >= 12 ? message.Source[..12] : "?"), settings.SanitizeNames);
        var nickname = message.Kind == "action" ? "* " + name + " " : "<" + name + "> ";
        var prefixWidth = 0;
        var output = new StringBuilder(" ");
        Write(timestamp, style.TimestampColor);
        if (settings.RrcJustifyMessages) { output.Append(' '); prefixWidth = timestamp.Length + 1; }
        var column = settings.RrcJustifyMessages ? prefixWidth : timestamp.Length;
        Wrapped(nickname, style.NickColor, message.Kind == "action");
        foreach (var span in style.Body)
            Wrapped(span.Text, message.Kind == "action" ? style.NickColor : span.Color, message.Kind == "action" || span.Bold,
                message.Kind == "action" ? new(Italic: true) : span.Formatting);
        if (settings.RrcSpaceMessages && message.Kind is "message" or "action") output.Append('\n');
        return output.ToString();

        void Wrapped(string text, string? color, bool bold, MicronStyle? formatting = null)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (rune.Value == '\r') continue;
                if (rune.Value == '\n' || column >= width - 1)
                {
                    output.Append('\n').Append(' ', 1 + prefixWidth);
                    column = prefixWidth;
                    if (rune.Value == '\n') continue;
                }
                Write(rune.ToString(), color, bold, formatting);
                column++;
            }
        }
        void Write(string text, string? color, bool bold = false, MicronStyle? formatting = null)
        {
            if (output.Length > 4 * 1024 * 1024) throw new InvalidDataException("RRC styled output exceeds4 MiB.");
            var effective = formatting ?? new();
            output.Append((effective with { Foreground = color ?? effective.Foreground, Bold = bold || effective.Bold }).Apply(text, settings, ansi));
        }
    }

    public static string RenderStream(RrcChatMessage message, NomadNetSettings settings, string ownHash, string ownNick,
        IReadOnlyDictionary<string, string>? nicks = null, bool ansi = false)
    {
        var style = Style(message, settings, ownHash, ownNick, nicks);
        var output = new StringBuilder();
        string time;
        try { time = DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { time = "        "; }
        Append("[" + time + "] ", style.TimestampColor);
        Append(NomadNetTerminalControls.Name(message.Nick ?? message.Source, settings.SanitizeNames) + ": ", style.NickColor);
        if (message.Kind == "action") Append("* ", style.NickColor);
        foreach (var span in style.Body)
            Append(span.Text, message.Kind == "action" ? style.NickColor : span.Color, message.Kind == "action" || span.Bold,
                message.Kind == "action" ? new(Italic: true) : span.Formatting);
        if (settings.RrcSpaceMessages && message.Kind is "message" or "action") output.Append('\n');
        return output.ToString();

        void Append(string text, string? color, bool bold = false, MicronStyle? formatting = null)
        {
            var effective = formatting ?? new();
            output.Append((effective with { Foreground = color ?? effective.Foreground, Bold = bold || effective.Bold }).Apply(text, settings, ansi));
            if (output.Length > NomadNetMicronComposition.MaximumBytes)
                throw new InvalidDataException("RRC styled output exceeds four MiB.");
        }
    }

    public static string CollapsedPane(string pane, bool visible, bool showGutters) =>
        visible ? pane == "users" ? "Users" : "Channels" :
            showGutters ? $"> {pane} (rrc pane {pane} on)" : "";

    [GeneratedRegex(@"```[^\n]*\n.*?```|(?<!`)`[^`\n]+`", RegexOptions.Singleline)]
    private static partial Regex CodeBlocks();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])@([A-Za-z0-9_]+)(?![A-Za-z0-9_])")]
    private static partial Regex Mentions();
}
