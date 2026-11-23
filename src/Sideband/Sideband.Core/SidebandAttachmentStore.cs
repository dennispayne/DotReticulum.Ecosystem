using System.Security.Cryptography;

namespace Sideband;

public sealed record SidebandAttachment(string Name, string ContentHash, int Length);

internal sealed class SidebandAttachmentStore(string dataDirectory)
{
    internal const int MaximumAttachmentBytes = 2 * 1024 * 1024;
    internal const int MaximumMessageBytes = 4 * 1024 * 1024;
    private const long MaximumStoredBytes = 128L * 1024 * 1024;
    private readonly string _directory = Path.Combine(dataDirectory, "attachments");
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." ||
            name.Any(character => char.IsControl(character) || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
            throw new InvalidDataException("An attachment must have a safe plain filename of at most 255 characters.");
    }

    internal static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.Asynchronous);
        if (stream.Length is < 0 or > MaximumAttachmentBytes)
            throw new InvalidDataException("An attachment exceeds the 2 MiB safety limit.");
        var data = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            throw new IOException("The attachment changed while it was being read.");
        return data;
    }

    internal async Task<SidebandAttachment> StoreAsync(string name, byte[] data, CancellationToken cancellationToken)
    {
        ValidateName(name);
        if (data.Length > MaximumAttachmentBytes) throw new InvalidDataException("An attachment exceeds the 2 MiB safety limit.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(data));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var target = Path.Combine(_directory, hash);
            if (File.Exists(target))
            {
                var existing = await ReadBoundedAsync(target, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(data)))
                    throw new InvalidDataException("Stored attachment integrity check failed.");
                return new SidebandAttachment(name, hash, data.Length);
            }
            var storedFiles = new DirectoryInfo(_directory).EnumerateFiles().ToArray();
            if (storedFiles.Length >= 4_096 || storedFiles.Sum(file => file.Length) + data.Length > MaximumStoredBytes)
                throw new IOException("The attachment store reached its 128 MiB or 4,096-file safety limit. Export and remove unused files before receiving more attachments.");
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new SidebandAttachment(name, hash, data.Length);
        }
        finally { _gate.Release(); }
    }

    internal async Task<byte[]> LoadAsync(SidebandAttachment attachment, CancellationToken cancellationToken)
    {
        Validate(attachment);
        var data = await ReadBoundedAsync(Path.Combine(_directory, attachment.ContentHash), cancellationToken).ConfigureAwait(false);
        if (data.Length != attachment.Length || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(data), Convert.FromHexString(attachment.ContentHash)))
            throw new InvalidDataException("Stored attachment integrity check failed.");
        return data;
    }

    internal async Task ExportAsync(SidebandAttachment attachment, string target, CancellationToken cancellationToken)
    {
        var data = await LoadAsync(attachment, cancellationToken).ConfigureAwait(false);
        await using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        try
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            File.Delete(target);
            throw;
        }
    }

    internal static void Validate(SidebandAttachment attachment)
    {
        if (attachment is null) throw new InvalidDataException("Null attachment metadata.");
        ValidateName(attachment.Name);
        if (attachment.ContentHash is null || attachment.ContentHash.Length != 64 || attachment.ContentHash.Any(character => !char.IsAsciiHexDigit(character)) ||
            attachment.Length is < 0 or > MaximumAttachmentBytes)
            throw new InvalidDataException("Invalid attachment metadata.");
    }
}
