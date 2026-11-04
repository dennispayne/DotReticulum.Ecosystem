using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NomadNet;

/// <summary>Explicit terminal export of already stored, checksum-verified attachments.</summary>
public static partial class NomadNetAttachmentExport
{
    public const int MaximumCollisions = 1000;

    public static string Destination(NomadNetSettings settings, string profile) =>
        Path.GetFullPath(string.IsNullOrEmpty(settings.AttachmentSavePath)
            ? settings.DownloadsPath ?? Path.Combine(profile, "downloads")
            : settings.AttachmentSavePath);

    public static async Task<IReadOnlyList<(int Index, string Name)>> ListAsync(string profile, string messageId,
        CancellationToken cancellationToken = default)
    {
        if (messageId.Length != 64 || messageId.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Invalid stored attachment identifier.");
        var root = Path.Combine(profile, "attachments", messageId.ToLowerInvariant());
        RejectLinks(Path.GetFullPath(root));
        var names = new List<(int Index, string Name)>();
        for (var index = 0; index < NomadNetAttachments.MaximumAttachments; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(root, index.ToString(CultureInfo.InvariantCulture) + ".bin.name");
            if (!File.Exists(path)) continue;
            var data = await NomadNetLocalFiles.ReadAsync(path, 1024, cancellationToken).ConfigureAwait(false);
            names.Add((index, NomadNetAttachments.SafeName(new UTF8Encoding(false, true).GetString(data))));
        }
        return names;
    }

    public static async Task<string> SaveAsync(NomadNetSettings settings, string profile, string messageId,
        int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settings.Validate();
        var bytes = await NomadNetAttachments.ReadAsync(profile, messageId, index, cancellationToken).ConfigureAwait(false);
        try
        {
            var storedName = Path.Combine(profile, "attachments", messageId.ToLowerInvariant(),
                index.ToString(CultureInfo.InvariantCulture) + ".bin.name");
            var nameBytes = await NomadNetLocalFiles.ReadAsync(storedName, 1024, cancellationToken).ConfigureAwait(false);
            var name = NomadNetAttachments.SafeName(new UTF8Encoding(false, true).GetString(nameBytes));
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                OperatingSystem.IsWindows() && (name.EndsWith('.') || name.EndsWith(' ') ||
                    IsWindowsDevice(name)))
                throw new InvalidDataException("Stored attachment name is not a safe export filename.");
            var directory = Destination(settings, profile);
            if (directory.Length > 4096) throw new InvalidDataException("Attachment export path exceeds native safety bounds.");
            RejectLinks(directory);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            RejectLinks(directory);
            var staged = Path.Combine(directory, ".attachment-" + Guid.NewGuid().ToString("N") + ".new");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    Share = FileShare.None, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var stream = new FileStream(staged, options))
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                var extension = Path.GetExtension(name);
                var stem = name[..(name.Length - extension.Length)];
                for (var counter = 0; counter <= MaximumCollisions; counter++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectLinks(directory);
                    var output = Path.Combine(directory, counter == 0 ? name :
                        stem + "_" + counter.ToString(CultureInfo.InvariantCulture) + extension);
                    try
                    {
                        if (OperatingSystem.IsLinux())
                        {
                            // File.Move's Unix existence check is not an atomic no-replace operation.
                            if (RenameNoReplace(-100, staged, -100, output, 1) != 0)
                                throw new IOException("Could not publish attachment export (OS error " +
                                    Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture) + ").");
                        }
                        else if (OperatingSystem.IsWindows()) File.Move(staged, output, overwrite: false);
                        else throw new PlatformNotSupportedException("Attachment export is verified on Windows and Linux only.");
                        return output;
                    }
                    catch (IOException) when (File.Exists(output) || Directory.Exists(output)) { }
                }
                throw new IOException("Attachment export collision limit reached; choose another destination.");
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool IsWindowsDevice(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9';
    }

    private static void RejectLinks(string directory)
    {
        for (var item = new DirectoryInfo(directory); item is not null; item = item.Parent)
        {
            try
            {
                if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Attachment export directory cannot contain links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameNoReplace(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);
}
