namespace NomadNet;

public sealed class NomadNetState
{
    public int SchemaVersion { get; set; } = 1;
    public List<NomadNetContact> Contacts { get; set; } = [];
    public List<NomadNetMessage> Messages { get; set; } = [];
    public List<NomadNetBookmark> Bookmarks { get; set; } = [];
    public List<NomadNetConversationAlert> ConversationAlerts { get; set; } = [];
    public List<string> IgnoredDestinations { get; set; } = [];
}

public sealed record NomadNetSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string DisplayName { get; init; } = "NomadNet user";
    public bool EnableClient { get; set; } = true;
    public bool ClientAnnounceAtStart { get; set; } = true;
    public int ClientAnnounceIntervalMinutes { get; set; } = 360;
    public DateTimeOffset? LastClientAnnounce { get; set; }
    public bool EnableNode { get; set; }
    public string? NodeName { get; set; }
    public int NodeAnnounceIntervalMinutes { get; set; } = 360;
    public bool NodeAnnounceAtStart { get; set; } = true;
    public double NodePageRefreshIntervalMinutes { get; set; } = 5d / 60;
    public double NodeFileRefreshIntervalMinutes { get; set; } = 5d / 60;
    public bool EnableExecutablePages { get; set; }
    public bool EnablePropagation { get; set; }
    public string? PropagationNode { get; set; }
    public bool UsePropagation { get; set; } = true;
    public List<string> TrustedNodes { get; set; } = [];
    public bool PeriodicLxmfSync { get; set; } = true;
    public int LxmfSyncIntervalMinutes { get; set; } = 360;
    public int LxmfSyncLimit { get; set; } = 8;
    public DateTimeOffset? LastLxmfSync { get; set; }
    public bool TryPropagationOnSendFail { get; set; } = true;
    public int DeliveryTransferLimitBytes { get; set; } = 1_000_000;
    public string MediaConverter { get; set; } = "ffmpeg";
    public int? InboundStampCost { get; set; }
    public bool AcceptInvalidStamps { get; set; }
    public string? PagesPath { get; set; }
    public string? FilesPath { get; set; }
    public string? DownloadsPath { get; set; }

    public async Task SaveAsync(string directory, CancellationToken cancellationToken = default)
    {
        Validate();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await File.WriteAllBytesAsync(staged, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this,
                NomadNetJsonContext.Default.NomadNetSettings), cancellationToken).ConfigureAwait(false);
            File.Move(staged, path, overwrite: true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    public static async Task<NomadNetSettings> OpenAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        if (!File.Exists(path))
        {
            var defaults = new NomadNetSettings();
            var data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(defaults, NomadNetJsonContext.Default.NomadNetSettings);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                await File.WriteAllBytesAsync(temporary, data, cancellationToken).ConfigureAwait(false);
                try { File.Move(temporary, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("NomadNet settings exceed the size limit.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var settings = await System.Text.Json.JsonSerializer.DeserializeAsync(stream, NomadNetJsonContext.Default.NomadNetSettings,
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("NomadNet settings file is empty.");
        settings.Validate();
        return settings with
        {
            DisplayName = settings.DisplayName.Trim(),
            PropagationNode = settings.PropagationNode is null ? null : NomadNetAddress.NormalizeHash(settings.PropagationNode),
            TrustedNodes = settings.TrustedNodes.Select(NomadNetAddress.NormalizeHash).Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    private void Validate()
    {
        if (InboundStampCost is < 1 or > 254)
            throw new InvalidDataException("Inbound stamp cost must be null or 1–254; the primary's 255 clamp is unsupported by pinned LXMF 1.2.1.");
        if (ClientAnnounceIntervalMinutes is < 30 or > 10080)
            throw new InvalidDataException("Client announce interval must be 30–10080 minutes.");
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(DisplayName) ||
            DisplayName.Trim().Length > 80 || DisplayName.Any(char.IsControl) ||
            NodeAnnounceIntervalMinutes is < 1 or > 10080 ||
            !double.IsFinite(NodePageRefreshIntervalMinutes) || NodePageRefreshIntervalMinutes is < 0 or > 10080 ||
            !double.IsFinite(NodeFileRefreshIntervalMinutes) || NodeFileRefreshIntervalMinutes is < 0 or > 10080 ||
            LxmfSyncIntervalMinutes is < 1 or > 525600 ||
            LxmfSyncLimit is < 0 or > 100000 ||
            TrustedNodes is null || TrustedNodes.Count > 128 ||
            DeliveryTransferLimitBytes is < 1024 or > 64 * 1024 * 1024 ||
            string.IsNullOrWhiteSpace(MediaConverter) || MediaConverter.Length > 1024 || MediaConverter.Any(char.IsControl) ||
            new[] { PagesPath, FilesPath, DownloadsPath }.Any(path => path is not null &&
                (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(char.IsControl))) ||
            NodeName is { } nodeName && (string.IsNullOrWhiteSpace(nodeName) || nodeName.Length > 80 || nodeName.Any(char.IsControl)) ||
            EnablePropagation && !EnableNode)
            throw new InvalidDataException("NomadNet settings contain an invalid version or display name.");
        if (PropagationNode is not null) _ = NomadNetAddress.NormalizeHash(PropagationNode);
        foreach (var hash in TrustedNodes) _ = NomadNetAddress.NormalizeHash(hash);
    }
}

public sealed record NomadNetContact(string DestinationHash, string Name)
{
    public int TrustLevel { get; init; } = 2;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Ignored { get; init; }
    public bool HostsNode { get; init; }
    public int PreferredDelivery { get; init; } = 1;
    public bool IdentifyOnConnect { get; init; }
    public int? SortRank { get; init; }
    public string Notes { get; init; } = "";
}

public sealed record NomadNetConversationAlert(string PeerHash, int Unread, int Failures);
public sealed record NomadNetConversation(string PeerHash, string? Name, DateTimeOffset LastActivity,
    int MessageCount, int Unread, int Failures);

public sealed record NomadNetMessage(
    string Id,
    string PeerHash,
    string Direction,
    DateTimeOffset Timestamp,
    string Title,
    string Content,
    string Status);

public sealed record NomadNetBookmark(string DestinationHash, string Path, string Title);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(NomadNetState))]
[System.Text.Json.Serialization.JsonSerializable(typeof(NomadNetSettings))]
[System.Text.Json.Serialization.JsonSerializable(typeof(NomadNetContact))]
[System.Text.Json.Serialization.JsonSerializable(typeof(NomadNetMessage))]
[System.Text.Json.Serialization.JsonSerializable(typeof(NomadNetBookmark))]
[System.Text.Json.Serialization.JsonSerializable(typeof(NomadNetConversation))]
internal partial class NomadNetJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
