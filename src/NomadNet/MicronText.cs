using System.Text;

namespace NomadNet;

public sealed record NomadNetPageLink(string Label, string Target, string Fields);
public sealed record NomadNetRenderedPage(string Text, IReadOnlyList<NomadNetPageLink> Links);

public static class MicronText
{
    public const int MaximumPageBytes = 1_000_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static NomadNetRenderedPage Render(ReadOnlySpan<byte> page)
    {
        if (page.Length > MaximumPageBytes) throw new InvalidDataException("Nomad Network page exceeds the one-megabyte client limit.");
        var source = StrictUtf8.GetString(page);
        var output = new StringBuilder(Math.Min(source.Length, MaximumPageBytes));
        var links = new List<NomadNetPageLink>();
        var literal = false;
        foreach (var original in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = original;
            if (!literal)
            {
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line == "`=") { literal = true; continue; }
                if (line.StartsWith("`+>", StringComparison.Ordinal) || line.StartsWith("`->", StringComparison.Ordinal))
                    line = line[2..];
                if (line.StartsWith("`!", StringComparison.Ordinal)) line = line[2..];
                if (line.StartsWith("`(", StringComparison.Ordinal))
                {
                    output.AppendLine("[image content is not rendered]");
                    continue;
                }
                if (line.StartsWith("`{", StringComparison.Ordinal))
                {
                    output.AppendLine("[dynamic page content is not rendered]");
                    continue;
                }
                if (line.StartsWith("`t", StringComparison.Ordinal))
                {
                    output.AppendLine("[table content shown as source text]");
                    line = line[2..];
                }
                if (line.StartsWith(">", StringComparison.Ordinal))
                {
                    var depth = line.TakeWhile(character => character == '>').Count();
                    line = new string(' ', Math.Min(depth - 1, 8) * 2) + line[depth..];
                }
                else if (line.Length > 0 && line[0] == '<') line = line[1..];
                else if (line.StartsWith("-", StringComparison.Ordinal)) line = new string(line.Length == 2 ? line[1] : '─', 40);
            }
            else if (line == "`=") { literal = false; continue; }
            if (literal && line == "\\`=") line = "`=";
            output.AppendLine(RenderInline(line, links, literal));
        }
        return new NomadNetRenderedPage(output.ToString().TrimEnd(), links);
    }

    private static string RenderInline(string line, List<NomadNetPageLink> links, bool literal)
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
                        var fields = parts.Length == 3 ? parts[2] : "";
                        label = NomadNetTerminalText.Safe(label);
                        links.Add(new NomadNetPageLink(label, NomadNetTerminalText.Safe(target), NomadNetTerminalText.Safe(fields)));
                        output.Append('[').Append(links.Count).Append("] ").Append(label);
                        index = close;
                        continue;
                    }
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
                    while (index + 1 < line.Length && (char.IsLetterOrDigit(line[index + 1]) || line[index + 1] is '_' or '-')) index++;
                    continue;
                }
                if ("!*_=fbacrl`".Contains(code)) continue;
                if (code == '<') { output.Append("[form field: terminal editing not implemented]"); continue; }
                continue;
            }
            if (!char.IsControl(line[index]) || line[index] == '\t') output.Append(line[index]);
        }
        return output.ToString();
    }
}
