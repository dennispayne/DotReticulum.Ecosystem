using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NomadNet;

public sealed class NomadNetPageCache(string directory, TimeProvider? timeProvider = null)
{
    private readonly string _directory = Path.Combine(directory, "cache", "pages");
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public const int DefaultLifetimeSeconds = 12 * 60 * 60;

    public static int Lifetime(ReadOnlySpan<byte> page)
    {
        if (!page.StartsWith("#!c="u8)) return DefaultLifetimeSeconds;
        var newline = page.IndexOf((byte)'\n');
        var header = page[4..(newline < 0 ? page.Length : newline)];
        return int.TryParse(Encoding.UTF8.GetString(header), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? seconds : DefaultLifetimeSeconds;
    }

    public async Task<byte[]?> ReadAsync(string url, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Entry(url);
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length is < 8 or > MicronText.MaximumPageBytes + 8) { File.Delete(path); return null; }
            var stored = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (stored.Length < 8 || BinaryPrimitives.ReadInt64LittleEndian(stored) <= _time.GetUtcNow().ToUnixTimeSeconds())
            {
                File.Delete(path);
                return null;
            }
            return stored[8..];
        }
        finally { _gate.Release(); }
    }

    public async Task StoreAsync(string url, byte[] page, CancellationToken cancellationToken = default)
    {
        if (page.Length > MicronText.MaximumPageBytes) throw new InvalidDataException("Page exceeds cache size limit.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Entry(url);
            if (File.Exists(path)) File.Delete(path);
            var lifetime = Lifetime(page);
            if (lifetime <= 0) return;
            var data = new byte[page.Length + 8];
            BinaryPrimitives.WriteInt64LittleEndian(data, _time.GetUtcNow().ToUnixTimeSeconds() + lifetime);
            page.CopyTo(data, 8);
            var staged = path + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                await File.WriteAllBytesAsync(staged, data, cancellationToken).ConfigureAwait(false);
                File.Move(staged, path, overwrite: true);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
            var entries = new DirectoryInfo(_directory).GetFiles("*.page").OrderBy(file => file.LastWriteTimeUtc).ToList();
            var bytes = entries.Sum(file => file.Length);
            while (entries.Count > 128 || bytes > 32 * 1024 * 1024)
            {
                var oldest = entries[0];
                bytes -= oldest.Length;
                oldest.Delete();
                entries.RemoveAt(0);
            }
        }
        finally { _gate.Release(); }
    }

    public void Remove(string url) => File.Delete(Entry(url));
    private string Entry(string url) => Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".page");
}
