using System.Text;
using System.Globalization;

namespace NomadNet;

public sealed record NomadNetPageLink(string Label, string Target, string Fields);
public sealed partial record NomadNetRenderedPage(string Text, IReadOnlyList<NomadNetPageLink> Links);

public static class MicronText
{
    public const int MaximumPageBytes = 1_000_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static NomadNetRenderedPage Render(ReadOnlySpan<byte> page, int dividerWidth = 40)
    {
        if (dividerWidth is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(dividerWidth));
        if (page.Length > MaximumPageBytes) throw new InvalidDataException("Nomad Network page exceeds the one-megabyte client limit.");
        var source = new string(StrictUtf8.GetString(page).Where(character =>
            character is '\n' or '\r' or '\t' || (!char.IsControl(character) &&
            character is not (>= '\u200B' and <= '\u200F') and not (>= '\u202A' and <= '\u202E') and
            not (>= '\u2060' and <= '\u206F') and not '\uFEFF' and not (>= '\uFFF0' and <= '\uFFF8'))).ToArray());
        var output = new StringBuilder(Math.Min(source.Length, MaximumPageBytes));
        var links = new List<NomadNetPageLink>();
        var fields = new List<NomadNetPageField>();
        var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
        var folds = new List<NomadNetPageFold>();
        var partials = new List<NomadNetPagePartial>();
        var media = new List<NomadNetPageMedia>();
        var tokens = new List<MicronInlineToken>();
        var styling = new MicronStyleState();
        var foldGlyphs = new[] { "▾", "▸" };
        var literal = false;
        var table = false;
        var tableLines = new List<string>();
        var tableWidth = 100;
        string? tableAlignment = null;
        var depth = 0;
        var pendingAnchors = new Dictionary<string, int>(StringComparer.Ordinal);
        var row = 0;
        foreach (var original in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = original;
            var heading = false;
            var divider = false;
            NomadNetPageFold? newFold = null;
            if (!literal)
            {
                if (!table && line.Length > 0 && line[0] == '<')
                {
                    depth = 0;
                    foreach (var fold in folds.Where(item => item.EndRow == int.MaxValue)) fold.EndRow = row;
                    line = line.TrimStart('<');
                    if (line.Length == 0) continue;
                }
                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    var glyphs = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (glyphs.Length >= 2 && glyphs[0] == "#!fold")
                    {
                        var open = glyphs[1];
                        if (open.Length > 32 || glyphs.ElementAtOrDefault(2)?.Length > 32)
                            throw new InvalidDataException("Micron fold glyphs exceed 32 characters.");
                        foldGlyphs = [open, glyphs.ElementAtOrDefault(2) ?? open];
                    }
                    continue;
                }
                if (line == "`=") { literal = true; continue; }
                var collapsible = line.StartsWith("`+>", StringComparison.Ordinal) || line.StartsWith("`->", StringComparison.Ordinal);
                var collapsed = line.StartsWith("`->", StringComparison.Ordinal);
                if (collapsible)
                    line = line[2..];
                if (line.StartsWith(">", StringComparison.Ordinal) && line.Contains("`<", StringComparison.Ordinal))
                {
                    line = line.TrimStart('>');
                    collapsible = false;
                }
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("\\", StringComparison.Ordinal) &&
                    (table || line.StartsWith("\\`t", StringComparison.Ordinal) ||
                    line.StartsWith("\\`{", StringComparison.Ordinal) || line.StartsWith("\\`(", StringComparison.Ordinal)))
                    line = line[1..];
                if (line.StartsWith("`t", StringComparison.Ordinal))
                {
                    table = !table;
                    if (!table)
                    {
                        if (tableAlignment is not null) styling.Alignment = tableAlignment;
                        foreach (var tableLine in MicronTable.Rows(tableLines, tableWidth))
                        {
                            output.AppendLine(RenderInline(tableLine, links, fields, anchors, tokens, row, false, styling));
                            styling.Rows.Add(new(row, styling.Alignment));
                            if (output.Length > NomadNetMicronComposition.MaximumBytes)
                                throw new InvalidDataException("Rendered Micron page exceeds four MiB.");
                            foreach (var name in pendingAnchors.Keys) anchors.TryAdd(name, row);
                            pendingAnchors.Clear();
                            row++;
                        }
                        if (tableAlignment is not null) styling.Alignment = "left";
                        tableLines.Clear();
                    }
                    else
                    {
                        var options = line[2..];
                        tableAlignment = options.Length > 0 ? options[0] switch { 'l' => "left", 'c' => "center", 'r' => "right", _ => null } : null;
                        if (tableAlignment is not null) options = options[1..];
                        tableWidth = int.TryParse(options, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ? width : 100;
                        if (tableWidth is < 1 or > 512) throw new InvalidDataException("Micron table width must be between 1 and 512.");
                    }
                    continue;
                }
                if (table && line.Length > 0)
                {
                    if (tableLines.Count >= 4096) throw new InvalidDataException("Micron table exceeds 4096 rows.");
                    tableLines.Add(line);
                    continue;
                }
                if (line.StartsWith("`(", StringComparison.Ordinal))
                {
                    var end = line.LastIndexOf(')');
                    var parts = end > 2 ? line[2..end].Split('`') : [];
                    if (parts.Length >= 2)
                    {
                        if (media.Count >= 128) throw new InvalidDataException("Micron page exceeds 128 images.");
                        media.Add(NomadNetPageMedia.Parse(parts));
                        var description = $"[image {media.Count}: {media[^1].Alt}; use media {media.Count} <name.webp> to download]";
                        tokens.Add(new(row, 0, description.Length, "media", media.Count - 1));
                        output.AppendLine(description);
                    }
                    else output.AppendLine("[invalid image]");
                    foreach (var name in pendingAnchors.Keys) anchors.TryAdd(name, row);
                    pendingAnchors.Clear();
                    row++;
                    continue;
                }
                if (line.StartsWith("`{", StringComparison.Ordinal))
                {
                    var end = line.IndexOf('}');
                    var parts = end > 2 ? line[2..end].Split('`') : [];
                    var validRefresh = parts.Length == 1 || (parts.Length >= 2 &&
                        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _));
                    if (parts.Length is >= 1 and <= 3 && parts[0].Length > 0 && validRefresh)
                    {
                        if (partials.Count >= 64) throw new InvalidDataException("Micron page exceeds 64 partials.");
                        double? refresh = parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                            out var seconds) && double.IsFinite(seconds) && seconds >= 1 ? seconds : null;
                        var selection = parts.Length == 3 ? parts[2] : "";
                        var id = selection.Split('|').LastOrDefault(token => token.StartsWith("pid=", StringComparison.Ordinal))?.Split('=')[1];
                        partials.Add(new(id, parts[0], refresh, selection) { Row = row });
                        output.AppendLine($"[partial {partials.Count}: {NomadNetTerminalText.Safe(id ?? parts[0])}; use partial {partials.Count}]");
                    }
                    else output.AppendLine("[invalid partial]");
                    foreach (var name in pendingAnchors.Keys) anchors.TryAdd(name, row);
                    pendingAnchors.Clear();
                    row++;
                    continue;
                }
                if (line.StartsWith(">", StringComparison.Ordinal))
                {
                    depth = line.TakeWhile(character => character == '>').Count();
                    if (depth > 256) throw new InvalidDataException("Micron heading depth exceeds 256.");
                    if (line.Length == depth) continue;
                    heading = true;
                    if (collapsible)
                    {
                        if (folds.Count >= 256) throw new InvalidDataException("Micron page exceeds 256 folds.");
                        newFold = new(row, int.MaxValue, depth, collapsed) { OpenGlyph = foldGlyphs[0], ClosedGlyph = foldGlyphs[1] };
                    }
                    var slug = NomadNetRenderedPage.HeadingSlug(line[depth..]);
                    if (slug.Length > 0) pendingAnchors.TryAdd(slug, row);
                    line = line[depth..];
                }
                else if (line.StartsWith("-", StringComparison.Ordinal))
                {
                    divider = true;
                    line = new string(line.Length == 2 && !char.IsControl(line[1]) ? line[1] : '─', dividerWidth);
                }
                if (!heading)
                    foreach (var fold in folds.Where(item => item.EndRow == int.MaxValue && item.Depth > depth)) fold.EndRow = row;
            }
            else if (line == "`=") { literal = false; continue; }
            if (literal && line == "\\`=") line = "`=";
            var latchedStyle = styling.Current;
            if (heading) styling.Current = new(Heading: true);
            var inline = RenderInline(line, links, fields, pendingAnchors, tokens, row, literal || divider, styling);
            if (heading) styling.Current = latchedStyle;
            if (anchors.Count + pendingAnchors.Keys.Count(name => !anchors.ContainsKey(name)) > 2048)
                throw new InvalidDataException("Micron page exceeds 2048 anchors.");
            if (inline.Length == 0 && line.Length > 0) continue;
            if (heading)
                foreach (var fold in folds.Where(item => item.EndRow == int.MaxValue && item.Depth >= depth)) fold.EndRow = row;
            if (newFold is not null) folds.Add(newFold);
            foreach (var name in pendingAnchors.Keys) anchors.TryAdd(name, row);
            pendingAnchors.Clear();
            if (anchors.Count > 2048) throw new InvalidDataException("Micron page exceeds 2048 anchors.");
            if (heading && depth > 1)
            {
                var indent = (depth - 1) * 2;
                inline = new string(' ', indent) + inline;
                for (var index = tokens.Count - 1; index >= 0 && tokens[index].Row == row; index--)
                    tokens[index] = tokens[index] with { Column = tokens[index].Column + indent };
                for (var index = styling.Spans.Count - 1; index >= 0 && styling.Spans[index].Row == row; index--)
                    styling.Spans[index] = styling.Spans[index] with { Column = styling.Spans[index].Column + indent };
            }
            styling.Rows.Add(new(row, styling.Alignment, heading ? depth : 0));
            output.AppendLine(inline);
            if (output.Length > NomadNetMicronComposition.MaximumBytes)
                throw new InvalidDataException("Rendered Micron page exceeds four MiB.");
            row++;
        }
        return new NomadNetRenderedPage(output.ToString().TrimEnd().Replace("\r\n", "\n", StringComparison.Ordinal), links)
        { Fields = fields, Anchors = anchors, Folds = folds, Partials = partials, Media = media, Tokens = tokens,
            Styles = styling.Spans, RowStyles = styling.Rows };
    }

    private static string RenderInline(string line, List<NomadNetPageLink> links, List<NomadNetPageField> fields,
        Dictionary<string, int> anchors, List<MicronInlineToken> tokens, int row, bool literal, MicronStyleState styling)
    {
        var output = new StringBuilder(line.Length);
        if (literal)
        {
            foreach (var character in line)
                if (!char.IsControl(character) || character == '\t') output.Append(character);
            styling.Add(row, 0, output.Length);
            return output.ToString();
        }
        var segment = 0;
        void Flush()
        {
            styling.Add(row, segment, output.Length - segment);
            segment = output.Length;
        }
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '\\')
            {
                if (index + 1 == line.Length) continue;
                var escaped = line[++index];
                if (!char.IsControl(escaped) || escaped == '\t') output.Append(escaped);
                continue;
            }
            if (line[index] == '`' && index + 1 < line.Length && line[index + 1] == '[')
            {
                var close = line.IndexOf(']', index + 2);
                if (close > index)
                {
                    var parts = line[(index + 2)..close].Split('`');
                    var target = parts.Length switch { 1 => parts[0], 2 or 3 => parts[1], _ => "" };
                    var label = parts.Length switch { 1 => target, 2 or 3 => parts[0], _ => "" };
                    if (label.Length == 0) label = target;
                    if (target.Length > 0 && parts.Length <= 3)
                    {
                        var selection = parts.Length == 3 ? parts[2] : "";
                        label = NomadNetTerminalText.Safe(label);
                        if (links.Count >= 2048) throw new InvalidDataException("Micron page exceeds 2048 links.");
                        links.Add(new NomadNetPageLink(label, NomadNetTerminalText.Safe(target), NomadNetTerminalText.Safe(selection)));
                        var column = output.Length;
                        output.Append('[').Append(links.Count).Append("] ").Append(label);
                        tokens.Add(new(row, column, output.Length - column, "link", links.Count - 1));
                    }
                    index = close;
                    continue;
                }
            }
            if (line[index] == '`' && index + 1 < line.Length && line[index + 1] == '<')
            {
                var separator = line.IndexOf('`', index + 2);
                var end = separator >= 0 ? line.IndexOf('>', separator + 1) : -1;
                if (end > separator && separator > index)
                {
                    if (fields.Count >= 128) throw new InvalidDataException("Micron page exceeds 128 form controls.");
                    var properties = line[(index + 2)..separator].Split('|');
                    var flags = properties.Length > 1 ? properties[0] : "";
                    var name = properties.Length > 1 ? properties[1] : properties[0];
                    if (name.Length > 256 || name.Any(char.IsControl)) throw new InvalidDataException("Invalid Micron field name.");
                    var kind = flags.Contains('^') ? "radio" : flags.Contains('?') ? "checkbox" : "field";
                    var dimensions = flags.Replace("^", "", StringComparison.Ordinal).Replace("?", "", StringComparison.Ordinal)
                        .Replace("!", "", StringComparison.Ordinal).Split('x', 2);
                    var width = int.TryParse(dimensions[0], out var w) ? Math.Clamp(w, 1, 256) : 24;
                    var rows = dimensions.Length == 2 && int.TryParse(dimensions[1], out var r) ? Math.Clamp(r, 1, 256) : 1;
                    var initial = line[(separator + 1)..end];
                    if (Encoding.UTF8.GetByteCount(initial) > 65536) throw new InvalidDataException("Micron field exceeds 64 KiB.");
                    var value = kind == "field" ? initial : properties.Length > 2 && properties[2].Length > 0 ? properties[2] : initial;
                    var selected = properties.Length > 3 && properties[3] == "*";
                    if (kind == "radio" && selected)
                        foreach (var member in fields.Where(item => item.Kind == "radio" && item.Name == name)) member.Selected = false;
                    var field = new NomadNetPageField(kind, name, value, kind == "field" ? "" : initial, kind == "field" && flags.Contains('!'), selected, width, rows);
                    fields.Add(field);
                    var column = output.Length;
                    output.Append("[field ").Append(fields.Count).Append(' ').Append(NomadNetTerminalText.Safe(name)).Append(": ")
                        .Append(kind == "field" ? field.DisplayValue : (selected ? "[x] " : "[ ] ") + NomadNetTerminalText.Safe(initial)).Append(']');
                    tokens.Add(new(row, column, output.Length - column, "field", fields.Count - 1));
                    index = end;
                    continue;
                }
            }
            if (line[index] == '`' && index + 1 < line.Length)
            {
                var code = line[++index];
                if (code is 'F' or 'B')
                {
                    var length = index + 7 < line.Length && line[index + 1] == 'T' ? 7 : 3;
                    if (index + length < line.Length)
                    {
                        Flush();
                        var color = line.Substring(index + (length == 7 ? 2 : 1), length == 7 ? 6 : 3);
                        styling.Current = code == 'F' ? styling.Current with { Foreground = color }
                            : styling.Current with { Background = color };
                        index += length;
                    }
                    continue;
                }
                if (code == ':')
                {
                    var start = index + 1;
                    while (index + 1 < line.Length)
                    {
                        var status = Rune.DecodeFromUtf16(line.AsSpan(index + 1), out var rune, out var consumed);
                        if (status != System.Buffers.OperationStatus.Done ||
                            !(Rune.IsLetterOrDigit(rune) || Rune.IsNumber(rune) || rune.Value is '_' or '-')) break;
                        index += consumed;
                    }
                    if (index >= start) anchors.TryAdd(line[start..(index + 1)], row);
                    continue;
                }
                Flush();
                styling.Current = code switch
                {
                    '!' => styling.Current with { Bold = !styling.Current.Bold },
                    '_' => styling.Current with { Underline = !styling.Current.Underline },
                    '*' => styling.Current with { Italic = !styling.Current.Italic },
                    'f' => styling.Current with { Foreground = null },
                    'b' => styling.Current with { Background = null },
                    '`' => new(),
                    _ => styling.Current,
                };
                styling.Alignment = code switch
                {
                    'c' => "center", 'r' => "right", 'l' or 'a' or '`' => "left", _ => styling.Alignment,
                };
                continue;
            }
            if (line[index] == '`') continue;
            if (!char.IsControl(line[index]) || line[index] == '\t') output.Append(line[index]);
        }
        Flush();
        return output.ToString();
    }
}
