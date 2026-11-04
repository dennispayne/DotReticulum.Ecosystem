using System.Globalization;
using System.Text;

namespace NomadNet;

public sealed record NomadNetConfigImportResult(NomadNetSettings Settings, IReadOnlyList<string> UnsupportedKeys);

/// <summary>Explicit core ConfigObj migration, not a replacement for the complete ConfigObj grammar.</summary>
public static class NomadNetConfigImport
{
    public const int MaximumConfigBytes = 64 * 1024;

    public static NomadNetConfigImportResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaximumConfigBytes) throw new InvalidDataException("Configuration exceeds 64 KiB.");
        var values = Read(text);
        var settings = new NomadNetSettings { EnableClient = false, NodeAnnounceIntervalMinutes = 720, NodeAnnounceAtStart = false,
            NodePageRefreshIntervalMinutes = 0, NodeFileRefreshIntervalMinutes = 0 };
        var unsupported = new List<string>();
        var disablePropagation = true;
        foreach (var (key, raw) in values)
        {
            if (key is not ("client.enable_client" or "client.announce_at_start" or "client.announce_interval" or
                "client.periodic_lxmf_sync" or "client.lxmf_sync_interval" or "client.lxmf_sync_limit" or
                "client.try_propagation_on_send_fail" or "client.required_stamp_cost" or "client.accept_invalid_stamps" or
                "client.max_accepted_size" or "client.downloads_path" or "node.enable_node" or "node.disable_propagation" or
                "node.node_name" or "node.announce_interval" or "node.pages_path" or "node.files_path" or
                "node.announce_at_start" or "node.page_refresh_interval" or "node.file_refresh_interval"))
            {
                unsupported.Add(key);
                continue;
            }
            var value = Scalar(raw);
            switch (key)
            {
                case "client.enable_client": settings.EnableClient = Boolean(value); break;
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
                case "node.enable_node": settings.EnableNode = Boolean(value); break;
                case "node.disable_propagation": disablePropagation = Boolean(value); break;
                case "node.node_name": settings.NodeName = value.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : value; break;
                case "node.announce_interval": settings.NodeAnnounceIntervalMinutes = Math.Max(1, Integer(value)); break;
                case "node.announce_at_start": settings.NodeAnnounceAtStart = Boolean(value); break;
                case "node.page_refresh_interval": settings.NodePageRefreshIntervalMinutes = Math.Max(0, Integer(value)); break;
                case "node.file_refresh_interval": settings.NodeFileRefreshIntervalMinutes = Math.Max(0, Integer(value)); break;
                case "node.pages_path": settings.PagesPath = value; break;
                case "node.files_path": settings.FilesPath = value; break;
            }
        }
        settings.EnablePropagation = settings.EnableNode && !disablePropagation;
        return new(settings, unsupported.Order(StringComparer.Ordinal).ToArray());
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
            if (raw.Length > 4096) throw new InvalidDataException("Configuration line exceeds 4096 characters.");
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
