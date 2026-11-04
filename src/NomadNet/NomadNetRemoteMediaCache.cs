using System.Security.Cryptography;
using System.Text;

namespace NomadNet;

/// <summary>Bounded native browser WebP cache; not the source-image conversion cache.</summary>
public sealed class NomadNetRemoteMediaCache(string directory) : IDisposable
{
    public const int MaximumFiles = 256;
    public const long MaximumBytes = 192L * 1024 * 1024;
    private readonly string _profile = Path.GetFullPath(directory);
    private readonly string _root = Path.GetFullPath(Path.Combine(directory, "storage", "cache", "remote-images"));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<byte[]> GetAsync(string url, Func<CancellationToken, Task<byte[]>> fetch,
        bool reload = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (url.Length > 2048 || url.Any(char.IsControl)) throw new ArgumentException("Invalid bounded image URL.", nameof(url));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var item = new DirectoryInfo(_root); item is not null; item = item.Parent)
            {
                if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Image cache cannot contain directory links.");
                if (item.FullName == _profile) break;
            }
            Directory.CreateDirectory(_root);
            var target = Path.Combine(_root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".webp");
            if (File.Exists(target))
            {
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Cached image cannot be a link.");
                if (!reload)
                {
                    try
                    {
                        var cached = await NomadNetContent.ReadBoundedAsync(target, NomadNetHost.MaximumFileBytes, cancellationToken).ConfigureAwait(false);
                        _ = NomadNetImages.Dimensions(cached);
                        File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
                        return cached;
                    }
                    catch (InvalidDataException) { }
                }
            }
            var bytes = await fetch(cancellationToken).ConfigureAwait(false);
            if (bytes.Length > NomadNetHost.MaximumFileBytes) throw new InvalidDataException("Remote image exceeds 16 MiB.");
            _ = NomadNetImages.Dimensions(bytes);
            cancellationToken.ThrowIfCancellationRequested();
            var staging = target + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                await File.WriteAllBytesAsync(staging, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(staging, target, true);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
            var files = Directory.EnumerateFiles(_root, "*.webp").Take(4097).Select(path => new FileInfo(path)).ToArray();
            if (files.Length > 4096) throw new InvalidDataException("Remote image cache exceeds its scan bound.");
            var total = files.Sum(file => file.Length);
            var count = files.Length;
            foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal))
            {
                if (count <= MaximumFiles && total <= MaximumBytes) break;
                total -= file.Length;
                count--;
                file.Delete();
            }
            return bytes;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
