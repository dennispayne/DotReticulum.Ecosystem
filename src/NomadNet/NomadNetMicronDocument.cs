using System.Text;
using System.Text.RegularExpressions;

namespace NomadNet;

public sealed class NomadNetPageField(string kind, string name, string value, string label,
    bool masked, bool selected, int width, int rows)
{
    public string Kind { get; } = kind;
    public string Name { get; } = name;
    public string Value { get; internal set; } = value;
    public string Label { get; } = label;
    public bool Masked { get; } = masked;
    public bool Selected { get; internal set; } = selected;
    public int Width { get; } = width;
    public int Rows { get; } = rows;
    public string DisplayValue => Masked ? new string('*', Math.Min(256, Value.Length)) : NomadNetTerminalText.Safe(Value);
}

public sealed class NomadNetPageFold(int row, int endRow, int depth, bool collapsed)
{
    private bool _collapsed = collapsed;
    internal NomadNetPageFold? Source { get; init; }
    public int Row { get; } = row;
    public int EndRow { get; internal set; } = endRow;
    public int Depth { get; } = depth;
    public string OpenGlyph { get; init; } = "▾";
    public string ClosedGlyph { get; init; } = "▸";
    public bool Collapsed
    {
        get => Source?.Collapsed ?? _collapsed;
        internal set { if (Source is { } source) source.Collapsed = value; else _collapsed = value; }
    }
}

public sealed record NomadNetPagePartial(string? Id, string Target, double? RefreshSeconds, string Fields)
{
    internal int Row { get; init; }
}
public sealed partial record NomadNetPageMedia(string Alt, string Target);
internal sealed record MicronInlineToken(int Row, int Column, int Length, string Kind, int Index);

public sealed partial record NomadNetRenderedPage
{
    public IReadOnlyList<NomadNetPageField> Fields { get; init; } = [];
    public IReadOnlyDictionary<string, int> Anchors { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<NomadNetPageFold> Folds { get; init; } = [];
    public IReadOnlyList<NomadNetPagePartial> Partials { get; init; } = [];
    public IReadOnlyList<NomadNetPageMedia> Media { get; init; } = [];
    internal IReadOnlyList<MicronInlineToken> Tokens { get; init; } = [];
    public IReadOnlyList<MicronStyleSpan> Styles { get; init; } = [];
    public IReadOnlyList<MicronRowStyle> RowStyles { get; init; } = [];

    public string DisplayText => string.Join('\n', DisplayRows().Where((_, row) =>
        !Folds.Any(fold => fold.Collapsed && row > fold.Row && row < fold.EndRow)));

    public string DisplayWithImages(IReadOnlyDictionary<NomadNetPageMedia, string> previews)
    {
        var result = new List<string>();
        var mediaRows = Tokens.Where(token => token.Kind == "media").GroupBy(token => token.Row)
            .ToDictionary(group => group.Key, group => group.Select(token => Media[token.Index]));
        var row = 0;
        foreach (var text in DisplayRows())
        {
            if (!Folds.Any(fold => fold.Collapsed && row > fold.Row && row < fold.EndRow))
            {
                result.Add(text);
                if (mediaRows.TryGetValue(row, out var images))
                    foreach (var image in images)
                        if (previews.TryGetValue(image, out var preview)) result.Add(preview);
            }
            row++;
        }
        return string.Join('\n', result);
    }

    internal string TokenText(MicronInlineToken token, int index) => token.Kind switch
    {
        "link" => $"[{index + 1}] {Links[token.Index].Label}",
        "field" => FieldText(Fields[token.Index], index + 1),
        "media" => $"[image {index + 1}: {Media[token.Index].Alt}; use media {index + 1} <name.webp> to download]",
        _ => "",
    };

    private static string FieldText(NomadNetPageField field, int number) =>
        $"[field {number} {NomadNetTerminalText.Safe(field.Name)}: " +
        (field.Kind == "field" ? field.DisplayValue : (field.Selected ? "[x] " : "[ ] ") + NomadNetTerminalText.Safe(field.Label)) + "]";

    private IEnumerable<string> DisplayRows()
    {
        var rows = Text.Split('\n');
        var groups = Tokens.GroupBy(token => token.Row).ToDictionary(group => group.Key, group => group.OrderBy(token => token.Column));
        for (var row = 0; row < rows.Length; row++)
        {
            if (!groups.TryGetValue(row, out var tokens)) { yield return rows[row]; continue; }
            var result = new StringBuilder();
            var start = 0;
            foreach (var token in tokens)
            {
                result.Append(rows[row].AsSpan(start, token.Column - start));
                result.Append(TokenText(token, token.Index));
                start = token.Column + token.Length;
            }
            result.Append(rows[row].AsSpan(start));
            yield return result.ToString();
        }
    }

    public void SetField(int number, string value)
    {
        if (number < 1 || number > Fields.Count) throw new ArgumentOutOfRangeException(nameof(number));
        if (Encoding.UTF8.GetByteCount(value) > 65536 || value.Any(character => char.IsControl(character) && character is not ('\n' or '\t')))
            throw new ArgumentException("Field values are limited to 64 KiB of text without terminal controls.");
        var field = Fields[number - 1];
        if (field.Kind == "field") field.Value = value;
        else
        {
            var selected = value.ToLowerInvariant() switch
            {
                "on" or "true" or "1" => true,
                "off" or "false" or "0" => false,
                _ => throw new ArgumentException("Checkbox/radio values use on or off."),
            };
            if (selected && field.Kind == "radio")
                foreach (var member in Fields.Where(item => item.Kind == "radio" && item.Name == field.Name)) member.Selected = false;
            field.Selected = selected;
        }
    }

    public Dictionary<object, object?>? Submission(string selection)
    {
        if (selection.Length == 0) return null;
        var tokens = selection.Split('|');
        var all = tokens.Contains("*", StringComparer.Ordinal);
        var data = new Dictionary<object, object?>();
        foreach (var token in tokens)
        {
            var pair = token.Split('=');
            if (pair.Length == 2) data["var_" + pair[0]] = pair[1];
        }
        foreach (var field in Fields)
        {
            if (!all && !tokens.Contains(field.Name, StringComparer.Ordinal)) continue;
            var key = "field_" + field.Name;
            if (field.Kind == "field") data[key] = field.Value;
            else if (field.Selected)
            {
                var existing = data.GetValueOrDefault(key) as string;
                data[key] = field.Kind == "checkbox" && !string.IsNullOrEmpty(existing) ? existing + "," + field.Value : field.Value;
            }
        }
        if (data.Sum(item => Encoding.UTF8.GetByteCount((string)item.Key) + Encoding.UTF8.GetByteCount((string)item.Value!)) > 65536)
            throw new InvalidDataException("Micron form submission exceeds 64 KiB.");
        return data;
    }

    public void ToggleFold(int number)
    {
        if (number < 1 || number > Folds.Count) throw new ArgumentOutOfRangeException(nameof(number));
        Folds[number - 1].Collapsed = !Folds[number - 1].Collapsed;
    }

    public string AtAnchor(string name)
    {
        if (!Anchors.TryGetValue(name, out var row)) throw new ArgumentException("Unknown page anchor: " + name);
        foreach (var fold in Folds.Where(fold => row > fold.Row && row < fold.EndRow)) fold.Collapsed = false;
        return string.Join('\n', DisplayRows().Skip(row));
    }

    public static string HeadingSlug(string text)
    {
        text = ModifierPattern().Replace(text, "");
        return SlugPattern().Replace(text, "-").Trim('-').ToLowerInvariant();
    }

    [GeneratedRegex(@"`[FB]T[0-9a-fA-F]{6}|`[FB][0-9a-fA-F]{3}|`:[A-Za-z0-9_\-]*|`[!*_=fbacrl`<>{]")]
    private static partial Regex ModifierPattern();
    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex SlugPattern();
}
