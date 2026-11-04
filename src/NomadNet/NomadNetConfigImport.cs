using System.Globalization;
using System.Text;

namespace NomadNet;

public sealed record NomadNetConfigImportResult(NomadNetSettings Settings, IReadOnlyList<string> UnsupportedKeys)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Explicit core ConfigObj migration, not a replacement for the complete ConfigObj grammar.</summary>
public static class NomadNetConfigImport
{
    public const int MaximumConfigBytes = 64 * 1024;

    internal static (int Cap, bool Filter) RrcImportHistoryPolicy(string text)
    {
        var values = Read(text);
        var cap = values.TryGetValue("rrc.history_per_room_cap", out var raw) ? Integer(Scalar(raw)) : 500;
        if (cap is < 0 or > RrcSessionState.MaximumHistory)
            throw new InvalidDataException("Imported RRC history cap exceeds the native 0–1000 safety range.");
        return (cap, !values.TryGetValue("rrc.filter_loaded_history", out raw) || Boolean(Scalar(raw)));
    }

    public static NomadNetConfigImportResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaximumConfigBytes) throw new InvalidDataException("Configuration exceeds 64 KiB.");
        var values = Read(text);
        var settings = new NomadNetSettings { EnableClient = false, NodeAnnounceIntervalMinutes = 720, NodeAnnounceAtStart = false,
            NodePageRefreshIntervalMinutes = 0, NodeFileRefreshIntervalMinutes = 0, LogLevel = null, LogDestination = null,
            CompactAnnounceStream = false };
        if (values.TryGetValue("logging.loglevel", out var logLevel))
            settings.LogLevel = NomadNetLogging.ParseLevel(Scalar(logLevel));
        if (values.TryGetValue("logging.destination", out var logDestination))
            settings.LogDestination = Scalar(logDestination).Equals("file", StringComparison.OrdinalIgnoreCase)
                ? NomadNetLogDestination.File : NomadNetLogDestination.Stdout;
        if (settings.LogDestination == NomadNetLogDestination.File && values.TryGetValue("logging.logfile", out var logFile))
            settings.LogFile = Scalar(logFile);
        var unsupported = new List<string>();
        var warnings = new List<string>();
        ApplyPrinting(values, settings, warnings);
        var disablePropagation = true;
        foreach (var (key, raw) in values)
        {
            if (key is "logging.loglevel" or "logging.destination" or "logging.logfile") continue;
            if (key is "printing.print_messages" or "printing.print_command" or "printing.print_from" or "printing.message_template") continue;
            if (key is "node.static_peers" or "node.prioritise_destinations")
            {
                var destinations = Destinations(raw, key, warnings);
                if (key == "node.static_peers") settings.PropagationStaticPeers = destinations;
                else settings.PrioritisedDestinations = destinations.Distinct(StringComparer.Ordinal).ToList();
                continue;
            }
            if (key is not ("textui.editor" or "textui.image_loading" or "textui.clipboard_copy" or "client.compose_in_markdown" or "rrc.render_markdown" or "rrc.render_micron" or
                "client.enable_client" or "client.announce_at_start" or "client.announce_interval" or
                "client.periodic_lxmf_sync" or "client.lxmf_sync_interval" or "client.lxmf_sync_limit" or
                "client.try_propagation_on_send_fail" or "client.required_stamp_cost" or "client.accept_invalid_stamps" or
                "client.max_accepted_size" or "client.downloads_path" or "node.enable_node" or "node.disable_propagation" or
                "client.user_interface" or "client.notify_on_new_message" or "client.attachment_save_path" or "client.compact_announce_stream" or
                "node.node_name" or "node.announce_interval" or "node.pages_path" or "node.files_path" or
                "node.announce_at_start" or "node.page_refresh_interval" or "node.file_refresh_interval" or
                "node.max_transfer_size" or "node.max_sync_size" or "node.propagation_cost" or
                "node.max_peers" or "node.message_storage_limit"))
            {
                unsupported.Add(key);
                continue;
            }
            var value = Scalar(raw);
            switch (key)
            {
                case "textui.editor": settings.Editor = value; break;
                case "textui.image_loading": settings.ImageLoading = NomadNetImagePolicy.Parse(value); break;
                case "textui.clipboard_copy": settings.ClipboardCopy = Boolean(value); break;
                case "client.compose_in_markdown": settings.ComposeInMarkdown = Boolean(value); break;
                case "rrc.render_markdown": settings.RrcRenderMarkdown = Boolean(value); break;
                case "rrc.render_micron": settings.RrcRenderMicron = Boolean(value); break;
                case "client.enable_client": settings.EnableClient = Boolean(value); break;
                case "client.user_interface": settings.UserInterface = NomadNetClientRuntime.ParseInterface(value); break;
                case "client.notify_on_new_message": settings.NotifyOnNewMessage = Boolean(value); break;
                case "client.compact_announce_stream": settings.CompactAnnounceStream = Boolean(value); break;
                case "client.announce_at_start": settings.ClientAnnounceAtStart = Boolean(value); break;
                case "client.announce_interval": settings.ClientAnnounceIntervalMinutes = Math.Max(30, Integer(value)); break;
                case "client.periodic_lxmf_sync": settings.PeriodicLxmfSync = Boolean(value); break;
                case "client.lxmf_sync_interval":
                    var interval = Integer(value);
                    if (interval > 0) settings.LxmfSyncIntervalMinutes = interval;
                    break;
                case "client.lxmf_sync_limit": settings.LxmfSyncLimit = Math.Max(0, Integer(value)); break;
                case "client.try_propagation_on_send_fail": settings.TryPropagationOnSendFail = Boolean(value); break;
                case "client.required_stamp_cost":
                    var cost = value.Equals("none", StringComparison.OrdinalIgnoreCase) ? 0 : Integer(value);
                    settings.InboundStampCost = cost <= 0 ? null : Math.Min(255, cost);
                    break;
                case "client.accept_invalid_stamps": settings.AcceptInvalidStamps = Boolean(value); break;
                case "client.max_accepted_size":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) || !double.IsFinite(size))
                        throw new InvalidDataException("Invalid client.max_accepted_size.");
                    if (size <= 0) size = 500;
                    if (size * 1000 is < 1024 or > 64 * 1024 * 1024)
                        throw new InvalidDataException("Imported delivery limit must fit the native 1-KiB–64-MiB safety range.");
                    settings.DeliveryTransferLimitBytes = checked((int)(size * 1000));
                    break;
                case "client.downloads_path": settings.DownloadsPath = ExpandHome(value); break;
                case "client.attachment_save_path": settings.AttachmentSavePath = ExpandHome(value); break;
                case "node.enable_node": settings.EnableNode = Boolean(value); break;
                case "node.disable_propagation": disablePropagation = Boolean(value); break;
                case "node.node_name": settings.NodeName = value.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : value; break;
                case "node.announce_interval": settings.NodeAnnounceIntervalMinutes = Math.Max(1, Integer(value)); break;
                case "node.announce_at_start": settings.NodeAnnounceAtStart = Boolean(value); break;
                case "node.page_refresh_interval": settings.NodePageRefreshIntervalMinutes = Math.Max(0, Integer(value)); break;
                case "node.file_refresh_interval": settings.NodeFileRefreshIntervalMinutes = Math.Max(0, Integer(value)); break;
                case "node.pages_path": settings.PagesPath = value; break;
                case "node.files_path": settings.FilesPath = value; break;
                case "node.max_transfer_size": settings.PropagationTransferLimitBytes = NomadNetNodeConfiguration.Bytes(value, 1000, 1); break;
                case "node.max_sync_size": settings.PropagationSyncLimitBytes = NomadNetNodeConfiguration.Bytes(value, 1000, 1); break;
                case "node.message_storage_limit": settings.PropagationStorageLimitBytes = NomadNetNodeConfiguration.Bytes(value, 1_000_000, 0.005); break;
                case "node.propagation_cost": settings.PropagationStampCost = Math.Max(13, Integer(value)); break;
                case "node.max_peers": settings.PropagationMaxPeers = Math.Max(0, Integer(value)); break;
            }
        }
        settings.EnablePropagation = settings.EnableNode && !disablePropagation;
        if (values.ContainsKey("node.max_sync_size"))
            settings.PropagationSyncLimitBytes = Math.Max(settings.PropagationTransferLimitBytes, settings.PropagationSyncLimitBytes);
        _ = NomadNetNodeConfiguration.CreateOptions(settings);
        settings.ValidateAttachmentPath();
        settings.ValidatePrinting();
        return new(settings, unsupported.Order(StringComparer.Ordinal).ToArray()) { Warnings = warnings.ToArray() };
    }

    public static async Task<NomadNetConfigImportResult> ImportAsync(string configFile, string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(configFile);
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Import requires an unoccupied target directory.");
        var data = await NomadNetContent.ReadBoundedAsync(source, MaximumConfigBytes, cancellationToken).ConfigureAwait(false);
        var result = Parse(new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF'));
        var staging = target + ".import-" + Guid.NewGuid().ToString("N");
        try
        {
            await result.Settings.SaveAsync(staging, cancellationToken).ConfigureAwait(false);
            _ = await NomadNetStore.OpenAsync(staging, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        return result;
    }

    private static Dictionary<string, string> Read(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var sections = new HashSet<string>(StringComparer.Ordinal);
        string? section = null;
        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            if (raw.Length > 8192) throw new InvalidDataException("Configuration line exceeds 8192 characters.");
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']') || line.StartsWith("[[", StringComparison.Ordinal))
                    throw new InvalidDataException("Nested or malformed ConfigObj sections are not supported.");
                section = line[1..^1].Trim();
                if (section.Length == 0 || !sections.Add(section)) throw new InvalidDataException("Empty or duplicate configuration section.");
                continue;
            }
            var separator = line.IndexOf('=');
            if (section is null || separator <= 0) throw new InvalidDataException("Expected a section and key=value configuration entry.");
            var key = line[..separator].Trim();
            if (key.Length == 0 || !values.TryAdd(section + "." + key, line[(separator + 1)..].Trim()))
                throw new InvalidDataException("Empty or duplicate configuration key.");
        }
        return values;
    }

    private static string StripComment(string line)
    {
        char quote = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] is '\'' or '"')
            {
                if (quote == '\0') quote = line[index];
                else if (line[index] == quote) quote = '\0';
            }
            else if (line[index] == '#' && quote == '\0') return line[..index];
        }
        if (quote != '\0') throw new InvalidDataException("Unterminated/multiline quoted configuration values are not supported.");
        return line;
    }

    private static string Scalar(string value)
    {
        if (value.Length >= 2 && value[0] is '\'' or '"' && value[^1] == value[0])
            return value[1..^1];
        if (value.Contains(',') || value.Contains('"') || value.Contains('\''))
            throw new InvalidDataException("Expected an unambiguous scalar configuration value.");
        return value;
    }

    private static List<string> Destinations(string raw, string key, List<string> warnings)
    {
        var tokens = CommaValues(raw);
        if (tokens.Count > 128) throw new InvalidDataException($"Imported {key} exceeds 128 entries.");
        var destinations = new List<string>();
        for (var index = 0; index < tokens.Count; index++)
        {
            try
            {
                var compact = new StringBuilder(tokens[index].Length);
                foreach (var character in tokens[index])
                {
                    if (character is ' ' or '\t' or '\r' or '\n' or '\v' or '\f')
                    {
                        if ((compact.Length & 1) != 0) throw new ArgumentException("Hex whitespace interrupts a byte.");
                    }
                    else compact.Append(character);
                }
                destinations.Add(NomadNetAddress.NormalizeHash(compact.ToString()));
            }
            catch (ArgumentException) { warnings.Add($"Skipped invalid {key} destination at index {index + 1}."); }
        }
        return destinations;
    }

    private static List<string> CommaValues(string raw)
    {
        var tokens = new List<string>();
        var start = 0;
        var quote = '\0';
        for (var index = 0; index <= raw.Length; index++)
        {
            if (index < raw.Length && raw[index] is '\'' or '"')
            {
                if (quote == '\0') quote = raw[index];
                else if (quote == raw[index]) quote = '\0';
            }
            if (index != raw.Length && (raw[index] != ',' || quote != '\0')) continue;
            var token = raw[start..index].Trim();
            if (token.Length != 0 || index != raw.Length || start == 0) tokens.Add(Scalar(token));
            start = index + 1;
        }
        return tokens;
    }

    private static void ApplyPrinting(Dictionary<string, string> values, NomadNetSettings settings, List<string> warnings)
    {
        if (!values.TryGetValue("printing.print_messages", out var enabled)) return;
        settings.PrintMessages = Boolean(Scalar(enabled));
        if (values.TryGetValue("printing.print_command", out var command)) settings.PrintCommand = Scalar(command);
        if (!settings.PrintMessages) return;
        if (values.TryGetValue("printing.print_from", out var raw)) ApplyPrintFrom(settings, raw);
        if (values.TryGetValue("printing.message_template", out var template))
        {
            var path = ExpandHome(Scalar(template));
            if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(char.IsControl))
                throw new InvalidDataException("Invalid printing template path.");
            path = Path.GetFullPath(path);
            if (File.Exists(path) || Directory.Exists(path))
                settings.PrintingMessageTemplate = NomadNetPrintingPolicy.ReadTemplateAsync(path).GetAwaiter().GetResult();
            else warnings.Add("Missing printing.message_template uses the original native default; import does not create foreign files.");
        }
    }

    internal static void ApplyPrintFrom(NomadNetSettings settings, string raw)
    {
        var tokens = CommaValues(raw);
        settings.PrintAllMessages = false;
        settings.PrintTrustedMessages = false;
        settings.AllowedMessagePrintDestinations = [];
        // ConfigObj distinguishes a scalar from a comma-list, even one with a trailing comma.
        var scalar = tokens.Count == 1 && raw.TrimEnd().LastOrDefault() != ',' &&
            (raw.TrimStart().StartsWith('\'') || raw.TrimStart().StartsWith('"') || !raw.Contains(','));
        if (scalar)
        {
            var value = tokens[0];
            settings.PrintAllMessages = value.Equals("everywhere", StringComparison.OrdinalIgnoreCase);
            settings.PrintTrustedMessages = value.Equals("trusted", StringComparison.OrdinalIgnoreCase);
            if (value.Length == 32) settings.AllowedMessagePrintDestinations.Add(value);
        }
        else
        {
            settings.AllowedMessagePrintDestinations = tokens;
            settings.PrintTrustedMessages = tokens.Any(value => value.Equals("trusted", StringComparison.OrdinalIgnoreCase));
        }
        settings.ValidatePrinting();
    }

    private static bool Boolean(string value) => value.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => throw new InvalidDataException("Invalid boolean configuration value."),
    };

    private static int Integer(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
        ? result : throw new InvalidDataException("Invalid bounded integer configuration value.");

    internal static string ExpandHome(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        if (path.StartsWith('~')) throw new InvalidDataException("Named-user home expansion is unsupported; supply an explicit path.");
        return path;
    }
}
