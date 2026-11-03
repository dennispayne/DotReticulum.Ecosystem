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
    public int Row { get; } = row;
    public int EndRow { get; internal set; } = endRow;
    public int Depth { get; } = depth;
    public bool Collapsed { get; internal set; } = collapsed;
}

public sealed record NomadNetPagePartial(string Id, string Target, double? RefreshSeconds, string Fields);
public sealed record NomadNetPageMedia(string Alt, string Target);

public sealed partial record NomadNetRenderedPage
{
    public IReadOnlyList<NomadNetPageField> Fields { get; init; } = [];
    public IReadOnlyDictionary<string, int> Anchors { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<NomadNetPageFold> Folds { get; init; } = [];
    public IReadOnlyList<NomadNetPagePartial> Partials { get; init; } = [];
    public IReadOnlyList<NomadNetPageMedia> Media { get; init; } = [];

    public string DisplayText => string.Join('\n', Text.Split('\n').Where((_, row) =>
        !Folds.Any(fold => fold.Collapsed && row > fold.Row && row < fold.EndRow)));

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
        return string.Join('\n', Text.Split('\n').Skip(row));
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
