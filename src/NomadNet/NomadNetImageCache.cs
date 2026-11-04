using System.Security.Cryptography;

namespace NomadNet;

public sealed class NomadNetImageCache(string directory, NomadNetImages images)
{
    public const int MaximumFiles = 256;
    public const long MaximumBytes = 192L * 1024 * 1024;
    private readonly string _root = Path.GetFullPath(Path.Combine(directory, "storage", "cache", "conversions"));
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<byte[]> ConvertAsync(string path, CancellationToken cancellationToken = default)
        => ConvertAsync(() => NomadNetContent.ReadBoundedAsync(path, NomadNetHost.MaximumFileBytes, cancellationToken), cancellationToken);

    public Task<byte[]> ConvertAsync(byte[] source, CancellationToken cancellationToken = default)
        => ConvertAsync(() => Task.FromResult(source), cancellationToken);

    private async Task<byte[]> ConvertAsync(Func<Task<byte[]>> read, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = await read().ConfigureAwait(false);
            if (source.Length is 0 or > NomadNetHost.MaximumFileBytes)
                throw new InvalidDataException("Image source exceeds the encoded size bound.");
            var key = Convert.ToHexStringLower(SHA256.HashData(source)) + ".q85.d1200.webp";
            var profile = Path.GetFullPath(directory);
            for (var item = new DirectoryInfo(_root); item is not null; item = item.Parent)
            {
                if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Conversion cache cannot contain directory links.");
                if (item.FullName == profile) break;
            }
            Directory.CreateDirectory(_root);
            if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Conversion cache cannot be a link.");
            var target = Path.Combine(_root, key);
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) == 0 &&
                new FileInfo(target).Length <= NomadNetHost.MaximumFileBytes)
            {
                var cached = await NomadNetContent.ReadBoundedAsync(target, NomadNetHost.MaximumFileBytes, cancellationToken).ConfigureAwait(false);
                try
                {
                    _ = NomadNetImages.Dimensions(cached);
                    File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
                    return cached;
                }
                catch (InvalidDataException) { }
            }
            var encoded = await images.ConvertAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
            var staging = target + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                await File.WriteAllBytesAsync(staging, encoded, cancellationToken).ConfigureAwait(false);
                File.Move(staging, target, true);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
            Prune();
            return encoded;
        }
        finally { _gate.Release(); }
    }

    private void Prune()
    {
        var candidates = Directory.EnumerateFiles(_root, "*.webp").Take(4097).Select(path => new FileInfo(path)).ToArray();
        if (candidates.Length > 4096) throw new InvalidDataException("Conversion cache directory exceeds its scan safety bound.");
        var bytes = candidates.Sum(file => file.Length);
        var count = candidates.Length;
        foreach (var file in candidates.OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal))
        {
            if (count <= MaximumFiles && bytes <= MaximumBytes) break;
            bytes -= file.Length;
            count--;
            file.Delete();
        }
    }
}
