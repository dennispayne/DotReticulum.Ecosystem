using System.Text;
using System.Text.RegularExpressions;

namespace NomadNet;

internal static partial class MicronTable
{
    internal static IEnumerable<string> Rows(IReadOnlyList<string> lines, int maximumWidth)
    {
        if (lines.Count < 2) yield break;
        if (lines.Count > 4096) throw new InvalidDataException("Micron table exceeds 4096 rows.");
        var header = Cells(lines[0]);
        var alignment = Cells(lines[1]);
        var data = new List<string[]> { header };
        foreach (var line in lines.Skip(2))
        {
            var cells = Cells(line);
            data.Add(Enumerable.Range(0, header.Length).Select(index => index < cells.Length ? cells[index] : "").ToArray());
        }
        var widths = Enumerable.Range(0, header.Length).Select(column => Math.Max(3, data.Max(cells => Width(cells[column])))).ToArray();
        var excess = widths.Sum() + widths.Length * 3 + 1 - maximumWidth;
        foreach (var column in Enumerable.Range(0, widths.Length).OrderByDescending(column => widths[column]))
        {
            var reduction = Math.Min(Math.Max(0, excess), widths[column] - 3);
            widths[column] -= reduction;
            excess -= reduction;
        }
        string Border(char left, char middle, char right) =>
            left + string.Join(middle, widths.Select(width => new string('─', width + 2))) + right;
        yield return Border('┌', '┬', '┐');
        for (var row = 0; row < data.Count; row++)
        {
            var text = new StringBuilder("│");
            for (var column = 0; column < header.Length; column++)
            {
                var cell = data[row][column];
                if (Width(cell) > widths[column]) cell = Truncate(cell, widths[column] - 1);
                var padding = Math.Max(0, widths[column] - Width(cell));
                var align = row > 0 && column < alignment.Length ? alignment[column] : "";
                var left = align.EndsWith(':') ? align.StartsWith(':') ? padding / 2 : padding : 0;
                text.Append(' ').Append(' ', left).Append(cell).Append(' ', padding - left).Append(" │");
            }
            yield return text.ToString();
            if (row == 0) yield return Border('├', '┼', '┤');
        }
        yield return Border('└', '┴', '┘');
    }

    private static int Width(string cell) => Formatting().Replace(cell, "").Length;

    private static string Truncate(string cell, int width)
    {
        var end = 0;
        var visible = 0;
        var modifiers = Formatting().Matches(cell);
        var next = 0;
        while (end < cell.Length && visible < width)
        {
            if (next < modifiers.Count && modifiers[next].Index == end) end += modifiers[next++].Length;
            else
            {
                var length = char.IsHighSurrogate(cell[end]) && end + 1 < cell.Length && char.IsLowSurrogate(cell[end + 1]) ? 2 : 1;
                if (visible + length > width) break;
                end += length;
                visible += length;
            }
        }
        return cell[..end] + "``…";
    }

    [GeneratedRegex(@"`(?:[FB]T[0-9a-fA-F]{6}|[FB][0-9a-fA-F]{3}|[!*_=fb])")]
    private static partial Regex Formatting();

    private static string[] Cells(string line)
    {
        line = line.Trim();
        if (line.StartsWith('|')) line = line[1..];
        if (line.EndsWith('|')) line = line[..^1];
        var cells = new List<string>();
        var current = new StringBuilder();
        var escaped = false;
        foreach (var character in line)
        {
            if (escaped) { current.Append(character); escaped = false; }
            else if (character == '\\') escaped = true;
            else if (character == '|')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else current.Append(character);
        }
        cells.Add(current.ToString().Trim());
        if (cells.Count > 128) throw new InvalidDataException("Micron table exceeds 128 columns.");
        return cells.ToArray();
    }
}
