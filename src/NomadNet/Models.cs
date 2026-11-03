namespace NomadNet;

public sealed class NomadNetState
{
    public int SchemaVersion { get; set; } = 1;
    public List<NomadNetContact> Contacts { get; set; } = [];
    public List<NomadNetMessage> Messages { get; set; } = [];
    public List<NomadNetBookmark> Bookmarks { get; set; } = [];
}

public sealed record NomadNetSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string DisplayName { get; init; } = "NomadNet user";
    public bool EnableNode { get; set; }
    public string? NodeName { get; set; }
    public int NodeAnnounceIntervalMinutes { get; set; } = 360;
    public bool EnableExecutablePages { get; set; }
    public bool EnablePropagation { get; set; }
    public string? PropagationNode { get; set; }
    public int DeliveryTransferLimitBytes { get; set; } = 1_000_000;

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

        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("NomadNet settings exceed the size limit.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var settings = await System.Text.Json.JsonSerializer.DeserializeAsync(stream, NomadNetJsonContext.Default.NomadNetSettings,
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("NomadNet settings file is empty.");
        settings.Validate();
        return settings with { DisplayName = settings.DisplayName.Trim() };
    }

    private void Validate()
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(DisplayName) ||
            DisplayName.Trim().Length > 80 || DisplayName.Any(char.IsControl) ||
            NodeAnnounceIntervalMinutes is < 1 or > 10080 ||
            DeliveryTransferLimitBytes is < 1024 or > 64 * 1024 * 1024 ||
            NodeName is { } nodeName && (string.IsNullOrWhiteSpace(nodeName) || nodeName.Length > 80 || nodeName.Any(char.IsControl)) ||
            EnablePropagation && !EnableNode)
            throw new InvalidDataException("NomadNet settings contain an invalid version or display name.");
        if (PropagationNode is not null) _ = NomadNetAddress.NormalizeHash(PropagationNode);
    }
}

public sealed record NomadNetContact(string DestinationHash, string Name);

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
internal partial class NomadNetJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
