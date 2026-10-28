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
        if (settings.SchemaVersion != 1 || string.IsNullOrWhiteSpace(settings.DisplayName) ||
            settings.DisplayName.Trim().Length > 80 || settings.DisplayName.Any(char.IsControl))
            throw new InvalidDataException("NomadNet settings contain an invalid version or display name.");
        return settings with { DisplayName = settings.DisplayName.Trim() };
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
