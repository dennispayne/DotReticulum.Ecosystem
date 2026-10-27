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

    private readonly Dictionary<string, Dictionary<string, string>> _sections;

    private LxmdConfig(Dictionary<string, Dictionary<string, string>> sections) => _sections = sections;

    public static LxmdConfig Load(string path)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = "default";
        sections[section] = new(StringComparer.OrdinalIgnoreCase);
        foreach (var original in File.ReadLines(path))
        {
            var line = RemoveComment(original).Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (section.Length == 0) throw new FormatException("Configuration contains an empty section.");
                if (section.StartsWith('[')) throw new NotSupportedException("Nested lxmd configuration sections are not supported.");
                if (!sections.TryGetValue(section, out var values)) sections[section] = values = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0) throw new FormatException($"Invalid configuration line: {line}");
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.StartsWith("\"\"\"", StringComparison.Ordinal) || value.StartsWith("'''", StringComparison.Ordinal) ||
                value.Contains("%(", StringComparison.Ordinal))
                throw new NotSupportedException("ConfigObj multiline values and interpolation are not supported.");
            if (value.Length >= 2 && value[0] is '\'' or '"' && value.IndexOf(value[0], 1) == value.Length - 1) value = value[1..^1];
            if (!sections[section].TryAdd(key, value)) throw new FormatException($"Duplicate configuration option '{key}' in [{section}].");
        }

        return new LxmdConfig(sections);
    }

    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value) ? value : null;

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

    public IReadOnlyList<string> GetList(string section, string key) =>
        Get(section, key) is { } value ? value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Length >= 2 && item[0] is '\'' or '"' && item[^1] == item[0] ? item[1..^1] : item).ToArray() : [];

    private static string RemoveComment(string line)
    {
        char quote = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0') { if (character == quote) quote = '\0'; }
            else if (character is '\'' or '"') quote = character;
            else if (character == '#') return line[..index];
        }
        return line;
    }
}
