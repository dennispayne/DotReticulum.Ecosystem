using System.Text;

namespace NomadNet;

public sealed record NomadNetConfigValue
{
    public string? Scalar { get; }
    public IReadOnlyList<string>? List { get; }
    public bool IsList => List is not null;
    public NomadNetConfigValue(string scalar) => Scalar = scalar;
    public NomadNetConfigValue(IEnumerable<string> list) => List = Array.AsReadOnly(list.ToArray());
    public IReadOnlyList<string> AsList() => List ?? [Scalar!];
}

/// <summary>Bounded ConfigObj text document. Untouched text is retained verbatim; this is not a Python evaluator.</summary>
public sealed class NomadNetConfigDocument
{
    public const int MaximumBytes = 64 * 1024;
    public const int MaximumEntries = 2048;
    private sealed class Section(string name, Section? parent, int insert)
    {
        public string Name { get; } = name;
        public Section? Parent { get; } = parent;
        public int Insert { get; set; } = insert;
        public Dictionary<string, Section> Children { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Entry> Values { get; } = new(StringComparer.Ordinal);
    }
    private sealed record Entry(string Key, NomadNetConfigValue Value, int Start, int End, int ValueStart, int ValueEnd);
    private readonly Section _root;
    private readonly int _maximumBytes;
    public string Text { get; }
    private NomadNetConfigDocument(string text, Section root, int maximumBytes) { Text = text; _root = root; _maximumBytes = maximumBytes; }

    public static NomadNetConfigDocument Parse(string text, int maximumBytes = MaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumBytes is < 1 or > NomadNetEditor.MaximumBytes || Encoding.UTF8.GetByteCount(text) > maximumBytes || text.Contains('\0'))
            throw new InvalidDataException("ConfigObj exceeds its configured byte limit or contains NUL.");
        var root = new Section("", null, text.Length);
        var stack = new List<Section> { root };
        var entries = 0;
        for (var start = 0; start < text.Length;)
        {
            var lineEnd = EndOfLine(text, start);
            if (lineEnd - start > 8192) throw new InvalidDataException("ConfigObj line exceeds 8192 characters.");
            var end = AfterLine(text, lineEnd);
            var line = text[start..lineEnd].Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] == '#') { start = end; continue; }
            if (++entries > MaximumEntries) throw new InvalidDataException("ConfigObj exceeds 2048 entries/sections.");
            if (line[0] == '[')
            {
                var open = 0;
                var index = 0;
                while (index < line.Length && line[index] == '[')
                {
                    open++;
                    index++;
                    while (index < line.Length && char.IsWhiteSpace(line[index])) index++;
                }
                if (index >= line.Length) throw new InvalidDataException("Malformed ConfigObj section.");
                var nameEnd = index;
                if (line[index] is '\'' or '"')
                {
                    nameEnd = line.IndexOf(line[index], index + 1);
                    if (nameEnd < 0) throw new InvalidDataException("Unterminated ConfigObj section name.");
                    nameEnd++;
                }
                var closing = Math.Max(index, nameEnd);
                var close = 0;
                for (; closing < line.Length; closing++)
                {
                    if (line[closing] != ']') continue;
                    var endMarker = closing;
                    close = 0;
                    while (endMarker < line.Length && line[endMarker] == ']')
                    {
                        close++;
                        endMarker++;
                        while (endMarker < line.Length && char.IsWhiteSpace(line[endMarker])) endMarker++;
                    }
                    var suffix = line[endMarker..].TrimStart();
                    if (close == open && (suffix.Length == 0 || suffix[0] == '#')) break;
                }
                if (closing == line.Length || open != close || open > 16 || open > stack.Count)
                    throw new InvalidDataException("Malformed ConfigObj section nesting.");
                var name = Name(line[index..closing]);
                foreach (var section in stack)
                    if (section.Insert == text.Length) section.Insert = start;
                while (stack.Count > open) stack.RemoveAt(stack.Count - 1);
                var parent = stack[^1];
                if (parent.Values.ContainsKey(name) || parent.Children.ContainsKey(name))
                    throw new InvalidDataException("Duplicate ConfigObj section/key.");
                var child = new Section(name, parent, text.Length);
                parent.Children.Add(name, child);
                stack.Add(child);
                start = end;
                continue;
            }
            var separator = SeparatorOutsideQuotes(text, start, lineEnd);
            if (separator < 0) throw new InvalidDataException("Expected ConfigObj key=value.");
            var key = Name(text[start..separator].Trim().TrimStart('\uFEFF'));
            var valueStart = separator + 1;
            while (valueStart < lineEnd && char.IsWhiteSpace(text[valueStart])) valueStart++;
            NomadNetConfigValue value;
            int valueEnd;
            if (valueStart + 3 <= lineEnd && text.AsSpan(valueStart, 3) is var prefix &&
                (prefix.SequenceEqual("\"\"\"") || prefix.SequenceEqual("'''")))
            {
                var delimiter = text.Substring(valueStart, 3);
                var closing = valueStart + 3;
                while (true)
                {
                    closing = text.IndexOf(delimiter, closing, StringComparison.Ordinal);
                    if (closing < 0) throw new InvalidDataException("Unterminated ConfigObj multiline value.");
                    lineEnd = EndOfLine(text, closing);
                    var suffix = text[(closing + 3)..lineEnd].TrimStart();
                    if (suffix.Length == 0 || suffix[0] == '#') break;
                    closing += 3;
                }
                value = new(text[(valueStart + 3)..closing].Replace("\r\n", "\n").Replace('\r', '\n'));
                valueEnd = closing + 3;
                end = AfterLine(text, lineEnd);
                foreach (var multiline in text[valueStart..valueEnd].Split('\n'))
                    if (multiline.Length > 8192) throw new InvalidDataException("ConfigObj multiline line exceeds 8192 characters.");
            }
            else
            {
                valueEnd = valueStart + ValueCommentOffset(text[valueStart..lineEnd]);
                while (valueEnd > valueStart && char.IsWhiteSpace(text[valueEnd - 1])) valueEnd--;
                value = ParseValue(text[valueStart..valueEnd]);
            }
            var current = stack[^1];
            if (current.Children.ContainsKey(key) || !current.Values.TryAdd(key, new(key, value, start, end, valueStart, valueEnd)))
                throw new InvalidDataException("Duplicate ConfigObj key/section.");
            start = end;
        }
        var document = new NomadNetConfigDocument(text, root, maximumBytes);
        _ = document.Flatten();
        return document;
    }

    public NomadNetConfigValue? Get(string section, string key) =>
        _root.Children.TryGetValue(section, out var node) && node.Values.TryGetValue(key, out var entry)
            ? Resolve(node, entry, [], 0) : null;

    public bool Contains(string section, string key) =>
        _root.Children.TryGetValue(section, out var node) && node.Values.ContainsKey(key);

    internal bool RequiresExtendedStackGrammar => RequiresExtended(_root);
    private bool RequiresExtended(Section section) => section.Values.Values.Any(entry =>
        Text.AsSpan(entry.ValueStart, entry.ValueEnd - entry.ValueStart).StartsWith("'''") ||
        Text.AsSpan(entry.ValueStart, entry.ValueEnd - entry.ValueStart).StartsWith("\"\"\"") ||
        entry.Value.AsList().Any(value => value.Contains("%(", StringComparison.Ordinal))) ||
        section.Children.Values.Any(RequiresExtended);

    public IReadOnlyDictionary<string, NomadNetConfigValue> Flatten()
    {
        var values = new Dictionary<string, NomadNetConfigValue>(StringComparer.Ordinal);
        long expanded = 0;
        Visit(_root, "");
        return values;
        void Visit(Section section, string path)
        {
            foreach (var entry in section.Values.Values)
            {
                var resolved = Resolve(section, entry, [], 0);
                expanded += resolved.AsList().Sum(value => (long)value.Length);
                if (expanded > 4L * _maximumBytes) throw new InvalidDataException("Expanded ConfigObj document exceeds its bounded budget.");
                if (!values.TryAdd(path + entry.Key, resolved))
                    throw new InvalidDataException("Ambiguous dotted ConfigObj path.");
            }
            foreach (var child in section.Children.Values) Visit(child, path + child.Name + ".");
        }
    }

    public string EditSection(string section, IReadOnlyDictionary<string, NomadNetConfigValue?> changes)
    {
        var edits = new List<(int Start, int Length, string Text)>();
        var additions = new StringBuilder();
        var newline = Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _root.Children.TryGetValue(section, out var node);
        foreach (var (key, value) in changes)
        {
            if (node is not null && node.Values.TryGetValue(key, out var existing))
            {
                if (value is null) edits.Add((existing.Start, existing.End - existing.Start, ""));
                else edits.Add((existing.ValueStart, existing.ValueEnd - existing.ValueStart, Encode(value).Replace("\n", newline)));
            }
            else if (value is not null) additions.Append(EncodeName(key)).Append(" = ").Append(Encode(value).Replace("\n", newline)).Append(newline);
        }
        if (additions.Length > 0)
        {
            var offset = node?.Insert ?? Text.Length;
            var prefix = offset > 0 && Text[offset - 1] != '\n' ? newline : "";
            if (node is null) prefix += "[" + EncodeName(section) + "]" + newline;
            edits.Add((offset, 0, prefix + additions));
        }
        var result = new StringBuilder(Text);
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
            result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        var text = result.ToString();
        _ = Parse(text, _maximumBytes);
        return text;
    }

    public static NomadNetConfigValue ParseValue(string text)
    {
        text = text.Trim();
        if (text == ",") return new(Array.Empty<string>());
        if (text.Length == 0) return new("");
        if ((text.StartsWith("\"\"\"", StringComparison.Ordinal) && text.EndsWith("\"\"\"", StringComparison.Ordinal) ||
             text.StartsWith("'''", StringComparison.Ordinal) && text.EndsWith("'''", StringComparison.Ordinal)) && text.Length >= 6)
            return new(text[3..^3].Replace("\r\n", "\n"));
        var values = new List<string>();
        var start = 0;
        var comma = false;
        while (start < text.Length)
        {
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            var index = start;
            string value;
            if (text[index] is '\'' or '"')
            {
                var quote = text[index++];
                var end = text.IndexOf(quote, index);
                if (end < 0) throw new InvalidDataException("Unterminated ConfigObj quote.");
                value = text[index..end];
                index = end + 1;
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                if (index < text.Length && text[index] != ',') throw new InvalidDataException("Unexpected text after ConfigObj quote.");
            }
            else
            {
                while (index < text.Length && text[index] != ',') index++;
                value = text[start..index].TrimEnd();
                if (value.Length == 0) throw new InvalidDataException("Empty ConfigObj list member.");
            }
            values.Add(value);
            if (values.Count > MaximumEntries) throw new InvalidDataException("ConfigObj list exceeds 2048 items.");
            if (index == text.Length) break;
            comma = true;
            start = index + 1;
            if (text.AsSpan(start).Trim().Length == 0) break;
        }
        return comma ? new(values) : new(values[0]);
    }

    public static string Encode(NomadNetConfigValue value)
    {
        if (value.List is not { } list) return EncodeScalar(value.Scalar!);
        if (list.Any(item => item.Contains('\n') || item.Contains('\r')))
            throw new InvalidDataException("ConfigObj list members cannot contain multiline strings.");
        return list.Count == 0 ? "," : string.Join(", ", list.Select(EncodeScalar)) + (list.Count == 1 ? "," : "");
    }

    private static string EncodeScalar(string value)
    {
        if (value.Contains('\n') || value.Contains('\r') || value.Length > 0 && value[0] is '\'' or '"')
        {
            var delimiter = !value.Contains("'''", StringComparison.Ordinal) ? "'''" :
                !value.Contains("\"\"\"", StringComparison.Ordinal) ? "\"\"\"" :
                throw new InvalidDataException("ConfigObj cannot encode this value with both triple-quote delimiters.");
            return delimiter + value.Replace("\r\n", "\n") + delimiter;
        }
        if (value.Length != 0 && value == value.Trim() && !value.Contains(',') && !value.Contains('#')) return value;
        if (!value.Contains('"')) return "\"" + value + "\"";
        if (!value.Contains('\'')) return "'" + value + "'";
        if (!value.Contains("'''", StringComparison.Ordinal)) return "'''" + value + "'''";
        if (!value.Contains("\"\"\"", StringComparison.Ordinal)) return "\"\"\"" + value + "\"\"\"";
        throw new InvalidDataException("ConfigObj cannot quote this value.");
    }

    private static string EncodeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(char.IsControl) || name.Contains('[') || name.Contains(']')) throw new InvalidDataException("Invalid ConfigObj name.");
        if (!name.Contains('=') && !name.Contains('#') && name[0] is not ('\'' or '"')) return name;
        if (!name.Contains('"')) return "\"" + name + "\"";
        if (!name.Contains('\'')) return "'" + name + "'";
        throw new InvalidDataException("ConfigObj name cannot be quoted.");
    }

    private NomadNetConfigValue Resolve(Section section, Entry entry, HashSet<(Section Section, string Key)> trail, int depth)
    {
        if (depth > 32 || !trail.Add((section, entry.Key))) throw new InvalidDataException("ConfigObj interpolation loop/depth limit.");
        try
        {
            return entry.Value.List is { } list ? new(list.Select(Interpolate)) : new(Interpolate(entry.Value.Scalar!));
            string Interpolate(string value)
            {
                var output = new StringBuilder();
                var start = 0;
                while (true)
                {
                    var index = value.IndexOf("%(", start, StringComparison.Ordinal);
                    if (index < 0) { output.Append(value.AsSpan(start)); break; }
                    var end = value.IndexOf(")s", index + 2, StringComparison.Ordinal);
                    if (end < 0) { output.Append(value.AsSpan(start)); break; }
                    output.Append(value.AsSpan(start, index - start));
                    var key = value[(index + 2)..end];
                    Section? owner = section;
                    Entry? found = null;
                    for (; owner is not null; owner = owner.Parent)
                    {
                        if (owner.Values.TryGetValue(key, out found)) break;
                        if (owner.Children.TryGetValue("DEFAULT", out var defaults) && defaults.Values.TryGetValue(key, out found)) break;
                    }
                    if (owner is null || found is null) throw new InvalidDataException("Missing ConfigObj interpolation option.");
                    var replacement = Resolve(owner, found, trail, depth + 1);
                    if (replacement.IsList) throw new InvalidDataException("A ConfigObj list cannot be interpolated into a scalar.");
                    output.Append(replacement.Scalar);
                    if (output.Length > MaximumBytes) throw new InvalidDataException("Expanded ConfigObj value exceeds 64 KiB.");
                    start = end + 2;
                }
                if (output.Length > MaximumBytes) throw new InvalidDataException("Expanded ConfigObj value exceeds 64 KiB.");
                return output.ToString();
            }
        }
        finally { trail.Remove((section, entry.Key)); }
    }

    private static string Name(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && text[0] is '\'' or '"')
        {
            if (text[^1] != text[0]) throw new InvalidDataException("Unterminated ConfigObj name.");
            text = text[1..^1];
        }
        if (text.Length == 0 || text.Any(char.IsControl)) throw new InvalidDataException("Empty/invalid ConfigObj name.");
        return text;
    }
    private static int SeparatorOutsideQuotes(string text, int start, int end)
    {
        while (start < end && (char.IsWhiteSpace(text[start]) || text[start] == '\uFEFF')) start++;
        if (start < end && text[start] is '\'' or '"')
        {
            var closing = text.IndexOf(text[start], start + 1);
            if (closing < 0 || closing >= end) return -1;
            start = closing + 1;
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            return start < end && text[start] == '=' ? start : -1;
        }
        var separator = text.IndexOf('=', start, end - start);
        return separator >= 0 ? separator : -1;
    }
    private static int ValueCommentOffset(string text)
    {
        var quote = '\0';
        var first = true;
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (quote != '\0') { if (value == quote) quote = '\0'; continue; }
            if (value == '#') return index;
            if (value == ',') { first = true; continue; }
            if (first && char.IsWhiteSpace(value)) continue;
            if (first && value is '\'' or '"') quote = value;
            first = false;
        }
        if (quote != '\0') throw new InvalidDataException("Unterminated ConfigObj quote.");
        return text.Length;
    }
    private static int EndOfLine(string text, int start)
    {
        var next = text.IndexOf('\n', start);
        var end = next < 0 ? text.Length : next;
        return end > start && text[end - 1] == '\r' ? end - 1 : end;
    }
    private static int AfterLine(string text, int end)
    {
        if (end < text.Length && text[end] == '\r') end++;
        if (end < text.Length && text[end] == '\n') end++;
        return end;
    }
}
