using System.Text;
using System.Globalization;

namespace NomadNet;

public sealed record NomadNetPageLink(string Label, string Target, string Fields);
public sealed partial record NomadNetRenderedPage(string Text, IReadOnlyList<NomadNetPageLink> Links);

public static class MicronText
{
    public const int MaximumPageBytes = 1_000_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static NomadNetRenderedPage Render(ReadOnlySpan<byte> page)
    {
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
        var literal = false;
        var table = false;
        var row = 0;
        foreach (var original in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = original;
            if (!literal)
            {
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line == "`=") { literal = true; continue; }
                var collapsible = line.StartsWith("`+>", StringComparison.Ordinal) || line.StartsWith("`->", StringComparison.Ordinal);
                var collapsed = line.StartsWith("`->", StringComparison.Ordinal);
                if (collapsible)
                    line = line[2..];
                if (line.StartsWith("`!", StringComparison.Ordinal)) line = line[2..];
                if (line.StartsWith("`(", StringComparison.Ordinal))
                {
                    var end = line.LastIndexOf(')');
                    var parts = end > 2 ? line[2..end].Split('`') : [];
                    if (parts.Length >= 2 && media.Count < 128)
                    {
                        media.Add(new(NomadNetTerminalText.Safe(parts[0].Trim()), NomadNetTerminalText.Safe(parts[^1].Trim())));
                        output.AppendLine($"[image {media.Count}: {media[^1].Alt}; use media {media.Count} <name.webp> to download]");
                    }
                    else output.AppendLine("[invalid image]");
                    row++;
                    continue;
                }
                if (line.StartsWith("`{", StringComparison.Ordinal))
                {
                    var end = line.IndexOf('}');
                    var parts = end > 2 ? line[2..end].Split('`') : [];
                    if (parts.Length is >= 1 and <= 3 && parts[0].Length > 0 && partials.Count < 64)
                    {
                        double? refresh = parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                            out var seconds) && double.IsFinite(seconds) && seconds >= 1 ? seconds : null;
                        var selection = parts.Length == 3 ? parts[2] : "";
                        var id = selection.Split('|').FirstOrDefault(token => token.StartsWith("pid=", StringComparison.Ordinal))?[4..]
                            ?? (partials.Count + 1).ToString(CultureInfo.InvariantCulture);
                        partials.Add(new(id, parts[0], refresh, selection));
                        output.AppendLine($"[partial {partials.Count}: {NomadNetTerminalText.Safe(id)}; use partial {partials.Count}]");
                    }
                    else output.AppendLine("[invalid partial]");
                    row++;
                    continue;
                }
                if (line.StartsWith("`t", StringComparison.Ordinal))
                {
                    table = !table;
                    continue;
                }
                if (!table && line.StartsWith(">", StringComparison.Ordinal))
                {
                    var depth = line.TakeWhile(character => character == '>').Count();
                    foreach (var fold in folds.Where(item => item.EndRow == int.MaxValue && item.Depth >= depth)) fold.EndRow = row;
                    if (collapsible) folds.Add(new(row, int.MaxValue, depth, collapsed));
                    var slug = NomadNetRenderedPage.HeadingSlug(line[depth..]);
                    if (slug.Length > 0) anchors.TryAdd(slug, row);
                    line = new string(' ', Math.Min(depth - 1, 8) * 2) + line[depth..];
                }
                else if (line.Length > 0 && line[0] == '<') line = line[1..];
                else if (!table && line.StartsWith("-", StringComparison.Ordinal)) line = new string(line.Length == 2 ? line[1] : '─', 40);
            }
            else if (line == "`=") { literal = false; continue; }
            if (literal && line == "\\`=") line = "`=";
            output.AppendLine(RenderInline(line, links, fields, anchors, row, literal));
            row++;
        }
        return new NomadNetRenderedPage(output.ToString().TrimEnd().Replace("\r\n", "\n", StringComparison.Ordinal), links)
        { Fields = fields, Anchors = anchors, Folds = folds, Partials = partials, Media = media };
    }

    private static string RenderInline(string line, List<NomadNetPageLink> links, List<NomadNetPageField> fields,
        Dictionary<string, int> anchors, int row, bool literal)
    {
        var output = new StringBuilder(line.Length);
        if (literal)
        {
            foreach (var character in line)
                if (!char.IsControl(character) || character == '\t') output.Append(character);
            return output.ToString();
        }
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] == '\\' && index + 1 < line.Length)
            {
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
                        output.Append('[').Append(links.Count).Append("] ").Append(label);
                        index = close;
                        continue;
                    }
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
                        .Replace("!", "", StringComparison.Ordinal).Split('x');
                    var width = int.TryParse(dimensions[0], out var w) ? Math.Clamp(w, 1, 256) : 24;
                    var rows = dimensions.Length == 2 && int.TryParse(dimensions[1], out var r) ? Math.Clamp(r, 1, 256) : 1;
                    var initial = line[(separator + 1)..end];
                    if (Encoding.UTF8.GetByteCount(initial) > 65536) throw new InvalidDataException("Micron field exceeds 64 KiB.");
                    var value = kind == "field" ? initial : properties.Length > 2 && properties[2].Length > 0 ? properties[2] : initial;
                    var selected = properties.Length > 3 && properties[3] == "*";
                    if (kind == "radio" && selected)
                        foreach (var member in fields.Where(item => item.Kind == "radio" && item.Name == name)) member.Selected = false;
                    var field = new NomadNetPageField(kind, name, value, kind == "field" ? "" : initial, flags.Contains('!'), selected, width, rows);
                    fields.Add(field);
                    output.Append("[field ").Append(fields.Count).Append(' ').Append(NomadNetTerminalText.Safe(name)).Append(": ")
                        .Append(kind == "field" ? field.DisplayValue : (selected ? "[x] " : "[ ] ") + NomadNetTerminalText.Safe(initial)).Append(']');
                    index = end;
                    continue;
                }
            }
            if (line[index] == '`' && index + 1 < line.Length)
            {
                var code = line[++index];
                if (code is 'F' or 'B')
                {
                    var length = index + 1 < line.Length && line[index + 1] == 'T' ? 7 : 3;
                    if (index + length < line.Length) index += length;
                    continue;
                }
                if (code == ':')
                {
                    var start = index + 1;
                    while (index + 1 < line.Length && (char.IsLetterOrDigit(line[index + 1]) || line[index + 1] is '_' or '-')) index++;
                    if (index >= start) anchors.TryAdd(line[start..(index + 1)], row);
                    continue;
                }
                if ("!*_=fbacrl`".Contains(code)) continue;
                continue;
            }
            if (!char.IsControl(line[index]) || line[index] == '\t') output.Append(line[index]);
        }
        return output.ToString();
    }
}
