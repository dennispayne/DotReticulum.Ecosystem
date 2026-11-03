using System.Security.Cryptography;
using System.Text;
using LXMF;

namespace NomadNet;

public sealed record NomadNetAttachment(string Name, byte[] Data);

public static class NomadNetAttachments
{
    public const int MaximumAttachments = 64;
    public const int MaximumBytes = 16 * 1024 * 1024;
    public const long MaximumStoredBytes = 256L * 1024 * 1024;
    private static readonly SemaphoreSlim Writes = new(1, 1);

    public static string SafeName(string? name, string fallback = "attachment")
    {
        var clean = new string((name ?? "").Where(character => !char.IsControl(character)).ToArray());
        var start = clean.LastIndexOfAny(['/', '\\', ':']) + 1;
        clean = clean[start..].TrimStart('.');
        if (clean.Length == 0) return fallback;
        if (clean.Length <= 200) return clean;
        var extension = Path.GetExtension(clean);
        if (extension.Length > 16) extension = extension[..16];
        return clean[..(200 - extension.Length)] + extension;
    }

    public static IReadOnlyList<NomadNetAttachment> Extract(Dictionary<object, object?> fields)
    {
        var result = new List<NomadNetAttachment>();
        var total = 0;
        void Add(string name, byte[] bytes)
        {
            if (result.Count >= MaximumAttachments || bytes.Length > MaximumBytes - total)
                throw new InvalidDataException("Message attachments exceed the configured limits.");
            total += bytes.Length;
            result.Add(new NomadNetAttachment(name, bytes));
        }
        if (fields.GetValueOrDefault(LxmField.FileAttachments) is object?[] files)
            foreach (var value in files)
                if (value is object?[] { Length: >= 2 } pair && pair[1] is byte[] bytes)
                    Add(SafeName(pair[0] switch { byte[] text => Encoding.UTF8.GetString(text), string text => text, _ => null },
                        "attachment_" + result.Count), bytes);
        foreach (var (key, name, audio) in new[] { (LxmField.Image, "image", false), (LxmField.Audio, "audio", true) })
        {
            var value = fields.GetValueOrDefault(key);
            var bytes = value is byte[] raw ? raw : value is object?[] { Length: >= 2 } pair ? pair[1] as byte[] : null;
            var format = value is object?[] { Length: >= 2 } formatted ? formatted[0] : null;
            if (bytes is { Length: > 0 }) Add(name + MediaExtension(format, bytes, audio), bytes);
        }
        return result;
    }

    public static string MediaExtension(object? format, ReadOnlySpan<byte> bytes, bool audio)
    {
        if (format is string text)
        {
            var extension = new string(text.Where(char.IsAsciiLetterOrDigit).Take(8).ToArray()).ToLowerInvariant();
            if (extension.Length > 0) return "." + extension;
        }
        if (audio && format is byte or sbyte or short or ushort or int or uint or long)
        {
            var mode = Convert.ToInt64(format, System.Globalization.CultureInfo.InvariantCulture);
            if (mode is >= 16 and <= 25) return ".ogg";
            if (mode is >= 1 and <= 9) return ".c2";
        }
        if (bytes.Length < 12) return ".bin";
        if (!audio)
        {
            if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
            if (bytes[..3].SequenceEqual(new byte[] { 0xff, 0xd8, 0xff })) return ".jpg";
            if (bytes.StartsWith("GIF8"u8)) return ".gif";
            if (bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8)) return ".webp";
            if (bytes[..4].SequenceEqual(new byte[] { 0, 0, 0, 28 }) || bytes[..4].SequenceEqual(new byte[] { 0, 0, 0, 24 })) return ".heic";
        }
        else
        {
            if (bytes.StartsWith("OggS"u8)) return ".ogg";
            if (bytes[..2].SequenceEqual(new byte[] { 0xff, 0xfb }) || bytes.StartsWith("ID3"u8)) return ".mp3";
            if (bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WAVE"u8)) return ".wav";
            if (bytes.StartsWith("fLaC"u8)) return ".flac";
        }
        return ".bin";
    }

    public static async Task SaveAsync(string directory, string messageId, Dictionary<object, object?> fields, CancellationToken cancellationToken = default)
    {
        if (messageId.Length != 64 || messageId.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Expected a complete message hash.", nameof(messageId));
        var attachments = Extract(fields);
        if (attachments.Count == 0) return;
        await Writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveCoreAsync(directory, messageId, attachments, cancellationToken).ConfigureAwait(false); }
        finally { Writes.Release(); }
    }

    public static async Task DeleteAsync(string directory, IEnumerable<string> messageIds, CancellationToken cancellationToken = default)
    {
        await Writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var storage = Path.Combine(directory, "attachments");
            if (Directory.Exists(storage) && (File.GetAttributes(storage) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Refusing to delete through an attachment storage link.");
            var ids = messageIds.Distinct(StringComparer.Ordinal).Take(NomadNetStore.MaximumMessages + 1).ToArray();
            if (ids.Length > NomadNetStore.MaximumMessages) throw new InvalidDataException("Too many attachment roots for one deletion.");
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (id.Length != 64 || id.Any(character => !Uri.IsHexDigit(character))) continue;
                var root = Path.Combine(directory, "attachments", id.ToLowerInvariant());
                if (!Directory.Exists(root)) continue;
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateDirectories(root).Any())
                    throw new InvalidDataException("Refusing to delete an attachment directory containing links or subdirectories.");
                var files = Directory.EnumerateFiles(root).Take(MaximumAttachments * 4 + 1).ToArray();
                if (files.Length > MaximumAttachments * 4)
                    throw new InvalidDataException("Attachment directory exceeds its deletion safety bound.");
                foreach (var file in files)
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Refusing to delete an attachment link.");
                }
                foreach (var file in files) File.Delete(file);
                Directory.Delete(root);
            }
        }
        finally { Writes.Release(); }
    }

    private static async Task SaveCoreAsync(string directory, string messageId, IReadOnlyList<NomadNetAttachment> attachments, CancellationToken cancellationToken)
    {
        var storage = Path.Combine(directory, "attachments");
        var root = Path.Combine(directory, "attachments", messageId.ToLowerInvariant());
        var count = 0;
        long stored = 0;
        if (Directory.Exists(storage))
            foreach (var file in Directory.EnumerateFiles(storage, "*.bin", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetDirectoryName(file) == root) continue;
                stored += new FileInfo(file).Length;
                if (++count > 4096 || stored > MaximumStoredBytes)
                    throw new InvalidDataException("Attachment storage quota exceeded; remove old attachments before receiving more.");
            }
        if (count + attachments.Count > 4096 || stored + attachments.Sum(attachment => (long)attachment.Data.Length) > MaximumStoredBytes)
            throw new InvalidDataException("Attachment storage quota exceeded; remove old attachments before receiving more.");
        Directory.CreateDirectory(root);
        for (var index = 0; index < attachments.Count; index++)
        {
            var attachment = attachments[index];
            // Untrusted names are display metadata, never filesystem paths.
            var file = Path.Combine(root, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin");
            var staged = file + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                await File.WriteAllBytesAsync(staged, attachment.Data, cancellationToken).ConfigureAwait(false);
                File.Move(staged, file, overwrite: true);
                await File.WriteAllTextAsync(file + ".name", attachment.Name, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(file + ".sha256", Convert.ToHexStringLower(SHA256.HashData(attachment.Data)), cancellationToken).ConfigureAwait(false);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }
    }
}
