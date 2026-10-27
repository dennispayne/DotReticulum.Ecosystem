using System.Globalization;

namespace Lxmd;

internal sealed class LxmdConfig
{
    public const string Example = """
        # Lightweight Extensible Messaging Daemon configuration
        [propagation]
        enable_node = no
        # node_name = Anonymous Propagation Node
        announce_interval = 360
        announce_at_start = yes
        autopeer = yes
        autopeer_maxdepth = 6
        # message_storage_limit = 500
        # propagation_message_max_accepted_size = 256
        # propagation_sync_max_accepted_size = 10240
        # propagation_stamp_cost_target = 16
        # propagation_stamp_cost_flexibility = 3
        # peering_cost = 18
        # remote_peering_cost_max = 26
        # prioritise_destinations = 41d20c727598a3fbbdf9106133a3a0ed
        # max_peers = 20
        # static_peers = e17f833c4ddf8890dd3a79a6fea8161d
        # from_static_only = yes
        # sequential_pn_stamp_validation = yes
        # static_peers_bypass_sequential = yes
        # max_inbound_syncs = 3
        auth_required = no

        [lxmf]
        display_name = Anonymous Peer
        announce_at_start = no
        # announce_interval = 360
        # stamp_cost = 12
        delivery_transfer_max_accepted_size = 1
        # on_inbound = /path/to/handler

        [logging]
        loglevel = 4
        """;

    private sealed class Section(Section? parent)
    {
        public Section? Parent { get; } = parent;
        public Dictionary<string, Section> Children { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string[]> Values { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Lists { get; } = new(StringComparer.Ordinal);
    }

    private readonly Section _root;

    private LxmdConfig(Section root) => _root = root;

    public static LxmdConfig Load(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new FormatException("Configuration exceeds 4 MiB.");
        var root = new Section(null);
        var stack = new List<Section> { root };
        var lines = File.ReadAllLines(path);
        for (var index = 0; index < lines.Length; index++)
        {
            var original = lines[index].Trim();
            var assignment = FindUnquoted(original, '=');
            var rawValue = assignment >= 0 ? original[(assignment + 1)..].TrimStart() : "";
            var multiline = rawValue.StartsWith("\"\"\"", StringComparison.Ordinal) || rawValue.StartsWith("'''", StringComparison.Ordinal);
            var line = (multiline ? original : RemoveComment(original)).Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('['))
            {
                var depth = 0;
                while (depth < line.Length && line[depth] == '[') depth++;
                if (depth > 32 || depth > stack.Count || !line.EndsWith(new string(']', depth), StringComparison.Ordinal))
                    throw new FormatException("Invalid configuration section nesting.");
                var name = Unquote(line[depth..^depth].Trim());
                if (name.Length == 0 || name.Contains('[') || name.Contains(']')) throw new FormatException("Invalid configuration section name.");
                var parent = stack[depth - 1];
                if (parent.Values.ContainsKey(name) || !parent.Children.TryAdd(name, new Section(parent)))
                    throw new FormatException($"Duplicate configuration section '{name}'.");
                stack.RemoveRange(depth, stack.Count - depth);
                stack.Add(parent.Children[name]);
                continue;
            }

            var section = stack[^1];
            var separator = FindUnquoted(line, '=');
            if (separator <= 0) throw new FormatException($"Invalid configuration line: {line}");
            var key = Unquote(line[..separator].Trim());
            var value = line[(separator + 1)..].Trim();
            string[] values;
            if (value.StartsWith("\"\"\"", StringComparison.Ordinal) || value.StartsWith("'''", StringComparison.Ordinal))
            {
                var delimiter = value[..3];
                var accumulated = new System.Text.StringBuilder();
                var remaining = value[3..];
                while (true)
                {
                    var end = remaining.IndexOf(delimiter, StringComparison.Ordinal);
                    if (end >= 0)
                    {
                        accumulated.Append(remaining[..end]);
                        if (RemoveComment(remaining[(end + 3)..]).Trim().Length != 0)
                            throw new FormatException("Unexpected text after multiline configuration value.");
                        break;
                    }
                    accumulated.Append(remaining).Append('\n');
                    if (accumulated.Length > 1024 * 1024 || ++index >= lines.Length)
                        throw new FormatException("Unterminated or oversized multiline configuration value.");
                    remaining = lines[index];
                }
                values = [accumulated.ToString()];
            }
            else
            {
                values = ParseValues(value, out var list);
                if (list) section.Lists.Add(key);
            }
            if (key.Length == 0 || section.Children.ContainsKey(key) || !section.Values.TryAdd(key, values))
                throw new FormatException($"Duplicate or invalid configuration option '{key}'.");
        }
        return new LxmdConfig(root);
    }

    public string? Get(string section, string key)
    {
        if (!_root.Children.TryGetValue(section, out var values) || !values.Values.TryGetValue(key, out var value)) return null;
        if (values.Lists.Contains(key)) throw new FormatException($"[{section}] {key} must be a scalar value.");
        return Interpolate(values, key, value[0], []);
    }

    public string Get(string section, string key, string fallback) => Get(section, key) ?? fallback;

    public bool GetBoolean(string section, string key, bool fallback) =>
        Get(section, key) is { } value ? value.ToLowerInvariant() switch
        {
            "yes" or "true" or "on" or "1" => true,
            "no" or "false" or "off" or "0" => false,
            _ => throw new FormatException($"'{value}' is not a valid boolean for [{section}] {key}."),
        } : fallback;

    public int? GetInteger(string section, string key, int? fallback) =>
        Get(section, key) is { } value
            ? int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : fallback;

    public double GetNumber(string section, string key, double fallback) =>
        Get(section, key) is { } value
            ? double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)
            : fallback;

    public IReadOnlyList<string> GetList(string section, string key)
    {
        if (!_root.Children.TryGetValue(section, out var values) || !values.Values.TryGetValue(key, out var value)) return [];
        return value.Select(item => Interpolate(values, key, item, [])).ToArray();
    }

    private static string Interpolate(Section section, string key, string value, HashSet<(Section, string)> active)
    {
        if (active.Count >= 64 || !active.Add((section, key))) throw new FormatException($"Configuration interpolation loop at '{key}'.");
        try
        {
            var output = new System.Text.StringBuilder();
            var position = 0;
            while (position < value.Length)
            {
                var start = value.IndexOf("%(", position, StringComparison.Ordinal);
                if (start < 0) { output.Append(value.AsSpan(position)); break; }
                var end = value.IndexOf(")s", start + 2, StringComparison.Ordinal);
                if (end < 0) { output.Append(value.AsSpan(position)); break; }
                var name = value[(start + 2)..end];
                Section? owner = section;
                string[]? replacement = null;
                while (owner is not null)
                {
                    if (owner.Values.TryGetValue(name, out replacement)) break;
                    if (owner.Children.TryGetValue("DEFAULT", out var defaults) && defaults.Values.TryGetValue(name, out replacement)) break;
                    owner = owner.Parent;
                }
                if (owner is null || replacement is not { Length: 1 } || owner.Lists.Contains(name))
                    throw new FormatException($"Missing or non-scalar interpolation option '{name}'.");
                output.Append(value.AsSpan(position, start - position));
                output.Append(Interpolate(owner, name, replacement[0], active));
                if (output.Length > 1024 * 1024) throw new FormatException("Expanded configuration value exceeds 1 MiB.");
                position = end + 2;
            }
            return output.ToString();
        }
        finally { active.Remove((section, key)); }
    }

    private static string[] ParseValues(string value, out bool list)
    {
        var values = new List<string>();
        var position = 0;
        list = false;
        while (true)
        {
            var comma = FindUnquoted(value, ',', position);
            var item = (comma < 0 ? value[position..] : value[position..comma]).Trim();
            if (item.Length > 0) values.Add(Unquote(item));
            else if (comma >= 0 && position != 0) throw new FormatException("Empty configuration list member.");
            else if (!list && comma < 0) values.Add("");
            if (comma < 0) break;
            list = true;
            position = comma + 1;
            if (position == value.Length) break;
        }
        return values.ToArray();
    }

    private static string Unquote(string value)
    {
        if (value.Length == 0 || value[0] is not ('\'' or '"')) return value;
        if (value.Length < 2 || value.IndexOf(value[0], 1) != value.Length - 1)
            throw new FormatException("Unterminated or invalid quoted configuration value.");
        return value[1..^1];
    }

    private static int FindUnquoted(string value, char target, int start = 0)
    {
        char quote = '\0';
        var tokenStart = true;
        for (var index = start; index < value.Length; index++)
        {
            if (quote != '\0') { if (value[index] == quote) quote = '\0'; }
            else if (value[index] == target) return index;
            else if (tokenStart && value[index] is '\'' or '"') { quote = value[index]; tokenStart = false; }
            else if (!char.IsWhiteSpace(value[index])) tokenStart = false;
        }
        return -1;
    }

    private static string RemoveComment(string line)
    {
        char quote = '\0';
        var tokenStart = true;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0') { if (character == quote) quote = '\0'; }
            else if (tokenStart && character is '\'' or '"') { quote = character; tokenStart = false; }
            else if (character == '#') return line[..index];
            else if (character is '=' or ',' or '[') tokenStart = true;
            else if (!char.IsWhiteSpace(character)) tokenStart = false;
        }
        return line;
    }
}
