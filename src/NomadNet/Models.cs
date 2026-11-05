namespace NomadNet;

public sealed class NomadNetState
{
    public int SchemaVersion { get; set; } = 1;
    public List<NomadNetContact> Contacts { get; set; } = [];
    public List<NomadNetMessage> Messages { get; set; } = [];
    public List<NomadNetBookmark> Bookmarks { get; set; } = [];
    public List<NomadNetConversationAlert> ConversationAlerts { get; set; } = [];
    public List<string> IgnoredDestinations { get; set; } = [];
    public List<NomadNetConversationShell> ConversationShells { get; set; } = [];
}

public sealed record NomadNetSettings
{
    public const int MaximumSettingsBytes = 64 * 1024;
    public int SchemaVersion { get; init; } = 1;
    public string DisplayName { get; init; } = "NomadNet user";
    public string? RnsConfigDirectory { get; init; }
    public bool UsePrimaryConfiguration { get; init; }
    public string Editor { get; set; } = "nano";
    public bool EnableClient { get; set; } = true;
    public NomadNetUserInterface UserInterface { get; set; } = NomadNetUserInterface.Text;
    public bool NotifyOnNewMessage { get; set; } = true;
    public bool CompactAnnounceStream { get; set; } = true;
    public bool ClipboardCopy { get; set; }
    public int? LogLevel { get; set; } = 4;
    public NomadNetLogDestination? LogDestination { get; set; } = NomadNetLogDestination.File;
    public string? LogFile { get; set; }
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
    public long PropagationTransferLimitBytes { get; set; } = 256_000;
    public long PropagationSyncLimitBytes { get; set; } = 10_240_000;
    public long PropagationStorageLimitBytes { get; set; } = 2_000_000_000;
    public int PropagationStampCost { get; set; } = 16;
    public int? PropagationMaxPeers { get; set; }
    public List<string> PropagationStaticPeers { get; set; } = [];
    public List<string> PrioritisedDestinations { get; set; } = [];
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
    public NomadNetImageLoading ImageLoading { get; set; } = NomadNetImageLoading.Auto;
    public int? InboundStampCost { get; set; }
    public bool AcceptInvalidStamps { get; set; }
    public bool ComposeInMarkdown { get; set; } = true;
    public bool RrcRenderMarkdown { get; set; } = true;
    public bool RrcRenderMicron { get; set; } = true;
    public string? PagesPath { get; set; }
    public string? FilesPath { get; set; }
    public string? DownloadsPath { get; set; }
    public string? AttachmentSavePath { get; set; }
    public bool PrintMessages { get; set; }
    public bool PrintAllMessages { get; set; }
    public bool PrintTrustedMessages { get; set; }
    public List<string> AllowedMessagePrintDestinations { get; set; } = [];
    public string PrintCommand { get; set; } = "lp";
    public string PrintingMessageTemplate { get; set; } = NomadNetPrintingPolicy.DefaultTemplate;

    public async Task SaveAsync(string directory, CancellationToken cancellationToken = default)
    {
        Validate();
        var data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this, NomadNetJsonContext.Default.NomadNetSettings);
        if (data.Length > MaximumSettingsBytes) throw new InvalidDataException("NomadNet settings exceed the size limit.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await File.WriteAllBytesAsync(staged, data, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
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

        var bytes = await NomadNetContent.ReadBoundedAsync(path, MaximumSettingsBytes, cancellationToken).ConfigureAwait(false);
        var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        var settings = System.Text.Json.JsonSerializer.Deserialize(bytes.AsSpan(offset), NomadNetJsonContext.Default.NomadNetSettings)
            ?? throw new InvalidDataException("NomadNet settings file is empty.");
        using (var document = System.Text.Json.JsonDocument.Parse(bytes.AsMemory(offset)))
            if (!document.RootElement.TryGetProperty("propagationStorageLimitBytes", out _))
                settings.PropagationStorageLimitBytes = 500_000_000;
        if (settings.UsePrimaryConfiguration)
        {
            var primaryPath = Path.Combine(root, "config");
            NomadNetEditor.Guard(primaryPath);
            if (!File.Exists(primaryPath)) throw new FileNotFoundException("Active primary ConfigObj source is missing; restore it or explicitly repair native JSON.");
            var primaryBytes = await NomadNetLocalFiles.ReadAsync(primaryPath, NomadNetConfigDocument.MaximumBytes, cancellationToken).ConfigureAwait(false);
            try { settings = NomadNetConfigImport.Overlay(new System.Text.UTF8Encoding(false, true).GetString(primaryBytes), settings).Settings; }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(primaryBytes); }
        }
        settings.Validate();
        return settings with
        {
            DisplayName = settings.DisplayName.Trim(),
            PropagationNode = settings.PropagationNode is null ? null : NomadNetAddress.NormalizeHash(settings.PropagationNode),
            TrustedNodes = settings.TrustedNodes.Select(NomadNetAddress.NormalizeHash).Distinct(StringComparer.Ordinal).ToList(),
            PropagationStaticPeers = settings.PropagationStaticPeers.Select(NomadNetAddress.NormalizeHash).ToList(),
            PrioritisedDestinations = settings.PrioritisedDestinations.Select(NomadNetAddress.NormalizeHash)
                .Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    internal NomadNetSettings Copy() => this with
    {
        TrustedNodes = [.. TrustedNodes],
        PropagationStaticPeers = [.. PropagationStaticPeers],
        PrioritisedDestinations = [.. PrioritisedDestinations],
        AllowedMessagePrintDestinations = [.. AllowedMessagePrintDestinations],
    };

    internal void ValidateNode()
    {
        if (PropagationTransferLimitBytes is < 1000 or > 64 * 1024 * 1024 ||
            PropagationSyncLimitBytes is < 1000 or > 256 * 1024 * 1024 ||
            PropagationStorageLimitBytes is < 5000 or > 16L * 1024 * 1024 * 1024 ||
            PropagationStampCost is < 13 or > 256 ||
            PropagationMaxPeers is < 0 or > 65536 ||
            PropagationStaticPeers is null || PropagationStaticPeers.Count > 128 ||
            PrioritisedDestinations is null || PrioritisedDestinations.Count > 128)
            throw new InvalidDataException("Propagation limits, cost, peer capacity or destination lists exceed native safety bounds.");
        foreach (var hash in PropagationStaticPeers) _ = NomadNetAddress.NormalizeHash(hash);
        foreach (var hash in PrioritisedDestinations) _ = NomadNetAddress.NormalizeHash(hash);
    }

    internal void ValidateAttachmentPath()
    {
        if (AttachmentSavePath is { } attachmentPath &&
            (attachmentPath.Length > 2048 || attachmentPath.Any(char.IsControl) ||
             attachmentPath.Length > 0 && string.IsNullOrWhiteSpace(attachmentPath)))
            throw new InvalidDataException("Attachment save path exceeds native safety bounds.");
    }

    internal void Validate()
    {
        ValidateNode();
        ValidateAttachmentPath();
        ValidatePrinting();
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
            LogLevel is < 0 or > 7 ||
            LogDestination is { } destination && !Enum.IsDefined(destination) ||
            !Enum.IsDefined(UserInterface) ||
            !Enum.IsDefined(ImageLoading) ||
            string.IsNullOrWhiteSpace(Editor) || Editor.Length > 2048 || Editor.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(MediaConverter) || MediaConverter.Length > 1024 || MediaConverter.Any(char.IsControl) ||
            new[] { PagesPath, FilesPath, DownloadsPath, RnsConfigDirectory, LogFile }.Any(path => path is not null &&
                (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(char.IsControl))) ||
            NodeName is { } nodeName && (string.IsNullOrWhiteSpace(nodeName) || nodeName.Length > 80 || nodeName.Any(char.IsControl)) ||
            EnablePropagation && !EnableNode)
            throw new InvalidDataException("NomadNet settings contain an invalid version or display name.");
        if (PropagationNode is not null) _ = NomadNetAddress.NormalizeHash(PropagationNode);
        foreach (var hash in TrustedNodes) _ = NomadNetAddress.NormalizeHash(hash);
    }

    internal void ValidatePrinting()
    {
        _ = NomadNetPrintingPolicy.CommandArguments(PrintCommand);
        NomadNetPrintingPolicy.ValidateTemplate(PrintingMessageTemplate);
        if (AllowedMessagePrintDestinations is null || AllowedMessagePrintDestinations.Count > 128 ||
            AllowedMessagePrintDestinations.Any(value => value is null || value.Length > 128 || value.Any(char.IsControl)))
            throw new InvalidDataException("Print source list exceeds 128 bounded entries.");
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
public sealed record NomadNetConversationShell(string PeerHash, DateTimeOffset LastActivity);
public sealed record NomadNetConversation(string PeerHash, string? Name, DateTimeOffset LastActivity,
    int MessageCount, int Unread, int Failures);

public sealed record NomadNetMessage(
    string Id,
    string PeerHash,
    string Direction,
    DateTimeOffset Timestamp,
    string Title,
    string Content,
    string Status)
{
    public int? Renderer { get; init; }
    public int? ImageAttachmentIndex { get; init; }
    public string? HistoricalAuthentication { get; init; }
    public bool? HistoricalSignatureValidated { get; init; }
    public int? HistoricalState { get; init; }
    public int? HistoricalMethod { get; init; }
    public string ImportedAttachmentNames { get; init; } = "";
    public string? PrintingContent { get; init; }
}

public sealed record NomadNetBookmark(string DestinationHash, string Path, string Title);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(NomadNetState))]
[System.Text.Json.Serialization.JsonSerializable(typeof(NomadNetSettings))]
[System.Text.Json.Serialization.JsonSerializable(typeof(NomadNetContact))]
[System.Text.Json.Serialization.JsonSerializable(typeof(NomadNetMessage))]
[System.Text.Json.Serialization.JsonSerializable(typeof(NomadNetBookmark))]
[System.Text.Json.Serialization.JsonSerializable(typeof(NomadNetConversation))]
[System.Text.Json.Serialization.JsonSerializable(typeof(NomadNetProfileImportReport))]
internal partial class NomadNetJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
