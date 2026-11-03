using System.Text;

namespace NomadNet;

public static class NomadNetMicronComposition
{
    public const int MaximumBytes = 4 * 1024 * 1024;

    public static NomadNetRenderedPage Compose(NomadNetRenderedPage root,
        IReadOnlyDictionary<NomadNetPagePartial, NomadNetRenderedPage> children)
    {
        var rows = new List<string>();
        var fields = new List<NomadNetPageField>();
        var links = new List<NomadNetPageLink>();
        var media = new List<NomadNetPageMedia>();
        var tokens = new List<MicronInlineToken>();
        var folds = new List<NomadNetPageFold>();
        var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
        var bytes = 0;
        void Append(NomadNetRenderedPage page, bool isRoot)
        {
            var sourceRows = page.Text.Split('\n');
            var mapping = new int[sourceRows.Length + 1];
            var grouped = page.Tokens.GroupBy(token => token.Row).ToDictionary(group => group.Key,
                group => group.OrderBy(token => token.Column));
            var partialRows = isRoot ? root.Partials.ToDictionary(partial => partial.Row) : [];
            for (var row = 0; row < sourceRows.Length; row++)
            {
                mapping[row] = rows.Count;
                if (partialRows.TryGetValue(row, out var partial) && children.TryGetValue(partial, out var child))
                {
                    Append(child, false);
                    continue;
                }
                var line = new StringBuilder();
                var start = 0;
                IEnumerable<MicronInlineToken> rowTokens = grouped.TryGetValue(row, out var found) ? found : [];
                foreach (var token in rowTokens)
                {
                    line.Append(sourceRows[row].AsSpan(start, token.Column - start));
                    var index = token.Kind switch { "field" => fields.Count, "link" => links.Count, _ => media.Count };
                    var column = line.Length;
                    line.Append(page.TokenText(token, index));
                    tokens.Add(new(rows.Count, column, line.Length - column, token.Kind, index));
                    if (token.Kind == "field") fields.Add(page.Fields[token.Index]);
                    else if (token.Kind == "link") links.Add(page.Links[token.Index]);
                    else media.Add(page.Media[token.Index]);
                    start = token.Column + token.Length;
                }
                line.Append(sourceRows[row].AsSpan(start));
                bytes += Encoding.UTF8.GetByteCount(line.ToString()) + 1;
                if (bytes > MaximumBytes || fields.Count > 128 || links.Count > 2048 || media.Count > 128)
                    throw new InvalidDataException("Composed Micron page exceeds aggregate text/control bounds.");
                rows.Add(line.ToString());
            }
            mapping[^1] = rows.Count;
            foreach (var (name, row) in page.Anchors)
                anchors.TryAdd(name, mapping[Math.Clamp(row, 0, sourceRows.Length)]);
            foreach (var fold in page.Folds)
                folds.Add(new(mapping[Math.Clamp(fold.Row, 0, sourceRows.Length)],
                    mapping[Math.Clamp(fold.EndRow, 0, sourceRows.Length)], fold.Depth, fold.Collapsed) { Source = fold });
        }
        Append(root, true);
        return new(string.Join('\n', rows), links)
        { Fields = fields, Media = media, Tokens = tokens, Anchors = anchors,
            Folds = folds.OrderBy(fold => fold.Row).ToArray(), Partials = root.Partials };
    }
}
