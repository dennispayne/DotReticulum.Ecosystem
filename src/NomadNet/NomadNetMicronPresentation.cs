using System.Text;

namespace NomadNet;

public sealed partial record NomadNetRenderedPage
{
    public string Present(NomadNetSettings settings, bool ansi = false, int width = 80,
        IReadOnlyDictionary<NomadNetPageMedia, string>? images = null, int startRow = 0) =>
        string.Join('\n', PresentLines(settings, ansi, width, images, startRow));

    public IReadOnlyList<string> PresentLines(NomadNetSettings settings, bool ansi = false, int width = 80,
        IReadOnlyDictionary<NomadNetPageMedia, string>? images = null, int startRow = 0, bool pad = false, bool wrap = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (width is < 1 or > 512 || startRow < 0) throw new ArgumentOutOfRangeException(nameof(width));
        var spans = Styles.GroupBy(span => span.Row).ToDictionary(group => group.Key, group => group.ToArray());
        var rows = RowStyles.ToDictionary(style => style.Row);
        var controls = Tokens.GroupBy(token => token.Row).ToDictionary(group => group.Key, group => group.OrderBy(token => token.Column).ToArray());
        var folds = Folds.ToDictionary(fold => fold.Row);
        var result = new List<string>();
        var total = 0;
        var row = 0;
        foreach (var original in DisplayRows())
        {
            if (row < startRow || Folds.Any(fold => fold.Collapsed && row > fold.Row && row < fold.EndRow)) { row++; continue; }
            var rowStyle = rows.GetValueOrDefault(row);
            var rowControls = controls.GetValueOrDefault(row) ?? [];
            var replacements = rowControls.Select(token =>
                (token.Column, token.Length, Replacement: TokenText(token, token.Index).Length)).ToArray();
            var line = new List<(string Text, MicronStyle Style)>();
            if (folds.TryGetValue(row, out var fold))
                line.Add(((fold.Collapsed ? fold.ClosedGlyph : fold.OpenGlyph) + " ", new(Heading: true)));
            var position = 0;
            foreach (var span in spans.GetValueOrDefault(row) ?? [])
            {
                var start = Map(span.Column);
                var end = Map(span.Column + span.Length);
                if (start > position) line.Add((original[position..start], new()));
                if (end > start) line.Add((original[start..end], span.Style));
                position = end;
            }
            if (position < original.Length) line.Add((original[position..], new()));
            Write(line, rowStyle?.Alignment ?? "left");
            if (images is not null)
                foreach (var token in rowControls)
                    if (token.Kind == "media" && images.TryGetValue(Media[token.Index], out var preview))
                        foreach (var imageLine in preview.Split('\n')) Write([(imageLine, new())], "left");
            row++;

            int Map(int column)
            {
                var delta = 0;
                foreach (var token in replacements)
                {
                    if (column <= token.Column) break;
                    if (column < token.Column + token.Length)
                        return Math.Min(original.Length, token.Column + delta + Math.Min(column - token.Column, token.Replacement));
                    delta += token.Replacement - token.Length;
                }
                return Math.Clamp(column + delta, 0, original.Length);
            }
        }
        return result.AsReadOnly();

        void Write(IReadOnlyList<(string Text, MicronStyle Style)> chunks, string alignment)
        {
            var buffer = new StringBuilder();
            var visible = 0;
            foreach (var (text, style) in chunks)
            {
                var run = new StringBuilder();
                foreach (var rune in text.EnumerateRunes())
                {
                    var count = rune.Value == '\t' ? 4 - visible % 4 : 1;
                    for (var index = 0; index < count; index++)
                    {
                        if (!wrap && visible == width) continue;
                        if (visible == width)
                        {
                            buffer.Append(style.Apply(run.ToString(), settings, ansi));
                            run.Clear();
                            Flush();
                        }
                        if (rune.Value == '\t') run.Append(' ');
                        else if (!Rune.IsControl(rune) && rune.Value is not (>= 0x200B and <= 0x200F) and
                            not (>= 0x202A and <= 0x202E) and not (>= 0x2060 and <= 0x206F) and not 0xFEFF and
                            not (>= 0xFFF0 and <= 0xFFF8)) run.Append(rune.ToString());
                        else continue;
                        visible++;
                    }
                }
                buffer.Append(style.Apply(run.ToString(), settings, ansi));
            }
            Flush();

            void Flush()
            {
                var left = alignment switch { "center" => (width - visible) / 2, "right" => width - visible, _ => 0 };
                var rendered = new string(' ', left) + buffer + (pad ? new string(' ', width - visible - left) : "");
                total += rendered.Length + 1;
                if (total > NomadNetMicronComposition.MaximumBytes)
                    throw new InvalidDataException("Presented Micron output exceeds four MiB.");
                result.Add(rendered);
                buffer.Clear();
                visible = 0;
            }
        }
    }
}
