using System.Globalization;

namespace NomadNet;

public sealed record NomadNetConfigurationField(string Section, string Key, string Type, string Default,
    string Validation = "", IReadOnlyList<string>? Options = null, string Disposition = "runtime")
{
    public NomadNetConfigValue DefaultValue => Type == "list" ? new(Array.Empty<string>()) : new(Default);
}

/// <summary>Factual primary form keys/defaults, with explicit native execution dispositions.</summary>
public static class NomadNetConfigurationFields
{
    public static IReadOnlyList<NomadNetConfigurationField> All { get; } = Array.AsReadOnly(new[]
    {
        B("node", "enable_node", false), E("node", "node_name", "None"), B("node", "announce_at_start", true),
        N("node", "announce_interval", "360"), B("node", "disable_propagation", true), N("node", "propagation_cost", "16"),
        F("node", "max_transfer_size", "256"), F("node", "max_sync_size", "10240"), E("node", "pages_path"),
        N("node", "page_refresh_interval", "0"), E("node", "files_path"), N("node", "file_refresh_interval", "0"),
        L("node", "prioritise_destinations"), L("node", "static_peers"), N("node", "max_peers", ""), F("node", "message_storage_limit", "2000"),
        E("client", "downloads_path", "~/Downloads"), E("client", "attachment_save_path"), B("client", "notify_on_new_message", true),
        B("client", "announce_at_start", true), N("client", "announce_interval", "360"), B("client", "try_propagation_on_send_fail", true),
        B("client", "periodic_lxmf_sync", true), N("client", "lxmf_sync_interval", "360"), N("client", "lxmf_sync_limit", "8"),
        new("client", "required_stamp_cost", "edit", "None", "stamp_cost"), B("client", "accept_invalid_stamps", false),
        F("client", "max_accepted_size", "500"), B("client", "compact_announce_stream", true), B("client", "compose_in_markdown", true),
        D("logging", "loglevel", "4", ["0", "1", "2", "3", "4", "5", "6", "7"]), D("logging", "destination", "file", ["file", "stdout"]),
        E("logging", "logfile"),
        F("textui", "intro_time", "1", "stored-only"), E("textui", "intro_text", "Nomad Network", "stored-only"),
        D("textui", "theme", "dark", ["dark", "light"], "stored-only"),
        D("textui", "colormode", "24bit", ["monochrome", "16", "88", "256", "24bit"], "stored-only"),
        D("textui", "image_loading", "auto", ["never", "manual", "auto", "always"]),
        D("textui", "glyphs", "nerdfont", ["plain", "unicode", "nerdfont"], "stored-only"),
        B("textui", "mouse_enabled", true, "stored-only"), E("textui", "editor", "nano"),
        B("textui", "hide_guide", false, "stored-only"), B("textui", "sanitize_names", true, "stored-only"),
        B("textui", "clipboard_copy", false), N("textui", "animation_interval", "1", "stored-only"),
        B("utilities", "enable_utilities", false, "stored-only"),
        N("rrc", "history_per_room_cap", "500"), B("rrc", "filter_loaded_history", true),
        F("rrc", "ephemeral_notices", "10"), B("rrc", "color_mention_timestamps", true),
        B("rrc", "render_markdown", true), B("rrc", "render_micron", true), B("rrc", "nick_colors", true),
        B("rrc", "justify_msgs", true), B("rrc", "space_msgs", false),
        B("rrc", "show_gutters", true), E("rrc", "mention_color"),
        L("rrc", "nick_colors_theme"), B("rrc", "enable_esoterics", false, "upstream-placeholder"),
        B("printing", "print_messages", false), E("printing", "print_command", "lp"), L("printing", "print_from"), E("printing", "message_template"),
        B("reticulum", "enable_transport", false), B("reticulum", "share_instance", true), E("reticulum", "instance_name", "default"),
        E("reticulum", "shared_instance_type"), N("reticulum", "shared_instance_port", ""), N("reticulum", "instance_control_port", ""),
        B("reticulum", "discover_interfaces", false), B("reticulum", "enable_remote_management", false), L("reticulum", "remote_management_allowed"),
        B("reticulum", "respond_to_probes", false), B("reticulum", "use_implicit_proof", true),
        B("reticulum", "panic_on_interface_error", false, "safety-refused"),
    });

    public static IReadOnlyList<string> Sections { get; } = Array.AsReadOnly(All.Select(field => field.Section).Distinct(StringComparer.Ordinal).ToArray());
    public static IReadOnlyList<NomadNetConfigurationField> ForSection(string section)
    {
        var fields = All.Where(field => field.Section == section).ToArray();
        return fields.Length == 0 ? throw new ArgumentException("Unknown primary configuration section.") : fields;
    }

    internal static NomadNetConfigValue Populate(NomadNetConfigurationField field, NomadNetConfigValue? current)
    {
        if (current is null) return field.DefaultValue;
        if (field.Type == "list") return new(current.AsList().Where(value => value.Length != 0));
        var text = current.List is { } list ? string.Join(", ", list) : current.Scalar!;
        if (field.Type == "checkbox")
        {
            var value = text.Trim().ToLowerInvariant();
            var enabled = value is not ("" or "false" or "off" or "no") &&
                (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number != 0);
            return new(enabled ? "yes" : "no");
        }
        if (field.Type == "dropdown" && !field.Options!.Contains(text, StringComparer.Ordinal)) return field.DefaultValue;
        return new(text);
    }

    internal static void Validate(NomadNetConfigurationField field, NomadNetConfigValue value)
    {
        if (value.AsList().Count > 128 || value.AsList().Any(item => item.Length > 4096 || item.Any(char.IsControl)))
            throw new InvalidDataException("Form values exceed 128 items/4096 characters or contain control characters.");
        if (field.Type == "list")
        {
            if (!value.IsList) throw new InvalidDataException("This field requires a JSON list.");
        }
        else
        {
            if (value.IsList) throw new InvalidDataException("This field requires a scalar.");
            var text = value.Scalar!;
            if (field.Type == "checkbox" && text is not ("yes" or "no")) throw new InvalidDataException("Checkbox requires true/false or yes/no.");
            if (field.Type == "dropdown" && !field.Options!.Contains(text, StringComparer.Ordinal)) throw new InvalidDataException("Select a documented dropdown option.");
            if (text.Length != 0 && field.Validation == "number" && !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException("This field requires a whole number.");
            if (text.Length != 0 && field.Validation == "float" &&
                (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)))
                throw new InvalidDataException("This field requires a finite decimal number.");
            if (field.Validation == "stamp_cost" && text.Length != 0 && !text.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                (!int.TryParse(text, out var cost) || cost is < 1 or > 255))
                throw new InvalidDataException("Stamp cost requires None or 1–255; pinned native LXMF additionally refuses255 on save.");
        }
        if (field.Key is "static_peers" or "prioritise_destinations" or "remote_management_allowed")
            foreach (var item in value.AsList())
            {
                try { _ = NomadNetAddress.NormalizeHash(item); }
                catch (ArgumentException exception) { throw new InvalidDataException(field.Key + " requires 32 hexadecimal characters per identity/destination.", exception); }
            }
        if (field.Key is "mention_color" or "nick_colors_theme")
            foreach (var item in value.AsList().Where(item => item.Length != 0))
                if (item.Length != 6 || !item.All(char.IsAsciiHexDigit)) throw new InvalidDataException("Colors require six hexadecimal digits.");
        if (field.Section == "reticulum" && field.Key is "shared_instance_port" or "instance_control_port" && value.Scalar is { Length: > 0 } port &&
            (!int.TryParse(port, out var parsed) || parsed is < 1 or > 65535))
            throw new InvalidDataException("RNS ports must be1–65535.");
    }

    private static NomadNetConfigurationField B(string section, string key, bool value, string disposition = "runtime") =>
        new(section, key, "checkbox", value ? "yes" : "no", Disposition: disposition);
    private static NomadNetConfigurationField E(string section, string key, string value = "", string disposition = "runtime") =>
        new(section, key, "edit", value, Disposition: disposition);
    private static NomadNetConfigurationField N(string section, string key, string value, string disposition = "runtime") =>
        new(section, key, "edit", value, "number", Disposition: disposition);
    private static NomadNetConfigurationField F(string section, string key, string value, string disposition = "runtime") =>
        new(section, key, "edit", value, "float", Disposition: disposition);
    private static NomadNetConfigurationField L(string section, string key, string disposition = "runtime") =>
        new(section, key, "list", "", Disposition: disposition);
    private static NomadNetConfigurationField D(string section, string key, string value, string[] options, string disposition = "runtime") =>
        new(section, key, "dropdown", value, Options: Array.AsReadOnly(options), Disposition: disposition);
}
