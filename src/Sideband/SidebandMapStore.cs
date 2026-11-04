using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace Sideband;

public sealed record SidebandMapCacheOptions
{
    public int MaximumTiles { get; init; } = 1024;
    public long MaximumCacheBytes { get; init; } = 64 * 1024 * 1024;
    public int MaximumTileBytes { get; init; } = 2 * 1024 * 1024;
    public long MaximumMbTilesBytes { get; init; } = 1024L * 1024 * 1024;
    internal void Validate()
    {
        if (MaximumTiles is < 1 or > 10_000 || MaximumTileBytes is < 64 or > 8 * 1024 * 1024 ||
            MaximumCacheBytes < MaximumTileBytes || MaximumCacheBytes > 1024L * 1024 * 1024 ||
            MaximumMbTilesBytes is < 512 or > 4L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(SidebandMapCacheOptions));
    }
}

/// <summary>Owned raster cache. Online requests require consent on each call; offline misses return null.</summary>
public sealed class SidebandMapStore : IAsyncDisposable
{
    public const int MaximumPendingOperations = 64;
    private readonly string _directory;
    private readonly FileStream _lease;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SidebandMapCacheOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifetime = new();
    private bool _closing;
    private Task? _disposal;
    private int _pendingOperations;
    private string MbTilesPath => Path.Combine(_directory, "offline.mbtiles");
    public SidebandMbTilesMetadata? OfflineMetadata { get; private set; }
    public SidebandMapViewSettings ViewSettings { get; private set; } = new();

    private SidebandMapStore(string directory, FileStream lease, HttpClient? http, SidebandMapCacheOptions options)
    {
        _directory = directory;
        _lease = lease;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _options = options;
    }

    public static async Task<SidebandMapStore> OpenAsync(string directory, HttpClient? http = null,
        SidebandMapCacheOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        options.Validate();
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lease = new FileStream(Path.Combine(directory, "maps.lock"), FileOptionsFor(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        var store = new SidebandMapStore(directory, lease, http, options);
        try
        {
            store.TrimCache(0, 0);
            var viewPath = Path.Combine(directory, "view.json");
            if (File.Exists(viewPath))
            {
                await using var input = new FileStream(viewPath, FileOptionsFor(FileMode.Open, FileAccess.Read, FileShare.Read));
                var bytes = await ReadBoundedAsync(input, 16_384, cancellationToken).ConfigureAwait(false);
                store.ViewSettings = JsonSerializer.Deserialize(bytes, SidebandMapJsonContext.Default.SidebandMapViewSettings)
                    ?? throw new InvalidDataException("The saved map view is empty.");
                store.ViewSettings.Validate();
            }
            if (File.Exists(store.MbTilesPath))
            {
                if (new FileInfo(store.MbTilesPath).Length > options.MaximumMbTilesBytes)
                    throw new InvalidDataException("The saved offline map exceeds its size limit.");
                store.OfflineMetadata = await Task.Run(() => ReadMetadata(store.MbTilesPath, options.MaximumTileBytes, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            return store;
        }
        catch
        {
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<byte[]?> GetTileAsync(SidebandMapProvider provider, SidebandMapTile tile, bool allowOnline = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var uri = provider.TileUri(tile);
        using var operation = Operation(cancellationToken);
        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
            var path = Path.Combine(_directory, key + ".tile");
            if (File.Exists(path))
            {
                var cached = await ReadTileAsync(path, operation.Token).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return cached;
            }

            if (!allowOnline) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("DotReticulum.Ecosystem-Sideband/2.2 (experimental desktop map)");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > _options.MaximumTileBytes)
                throw new InvalidDataException("The map tile exceeds its encoded size limit.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(input, _options.MaximumTileBytes, timeout.Token).ConfigureAwait(false);
            ValidateRaster(bytes);
            TrimCache(bytes.Length, 1);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await WritePrivateAsync(temporary, bytes, operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return bytes;
        }
        finally { _gate.Release(); }
    }

    public async Task<int> ClearDownloadedTilesAsync(CancellationToken cancellationToken = default)
    {
        using var operation = Operation(cancellationToken);
        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            var count = 0;
            foreach (var path in Directory.EnumerateFiles(_directory, "*.tile"))
            {
                operation.Token.ThrowIfCancellationRequested();
                if (count >= 10_000) throw new InvalidDataException("The map cache contains too many entries.");
                File.Delete(path);
                count++;
            }
            return count;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveViewAsync(SidebandMapViewSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        using var operation = Operation(cancellationToken);
        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        var target = Path.Combine(_directory, "view.json");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, SidebandMapJsonContext.Default.SidebandMapViewSettings);
            if (bytes.Length > 16_384) throw new InvalidDataException("Map view settings exceed their storage bound.");
            await WritePrivateAsync(temporary, bytes, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
            ViewSettings = settings;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    public async Task<byte[]?> GetOfflineTileAsync(SidebandMapTile tile, CancellationToken cancellationToken = default)
    {
        tile.Validate();
        using var operation = Operation(cancellationToken);
        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            if (OfflineMetadata is null) return null;
            var bytes = await Task.Run(() =>
            {
                using var database = new ProfileSqlite(MbTilesPath, operation.Token, verifyIntegrity: false, immutable: true);
                var sql = string.Create(CultureInfo.InvariantCulture,
                    $"SELECT tile_data FROM tiles WHERE zoom_level={tile.Zoom} AND tile_column={tile.X} AND tile_row={tile.TmsY} LIMIT 2");
                var rows = database.Rows(sql, 1);
                if (rows.Count == 0) return null;
                return rows[0][0] as byte[] ?? throw new InvalidDataException("MBTiles tile_data is not a binary raster.");
            }, operation.Token).ConfigureAwait(false);
            if (bytes is null) return null;
            if (bytes.Length > _options.MaximumTileBytes) throw new InvalidDataException("The offline map tile exceeds its encoded size limit.");
            ValidateRaster(bytes);
            return bytes;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Imports a stopped/checkpointed raster database. A live WAL is rejected, never silently omitted.</summary>
    public async Task<SidebandMbTilesMetadata> ImportMbTilesAsync(string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        source = Path.GetFullPath(source);
        using var operation = Operation(cancellationToken);
        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        var temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".mbtiles.tmp");
        try
        {
            if (source == MbTilesPath) throw new ArgumentException("Select an external MBTiles file, not the owned snapshot.", nameof(source));
            CheckSource(source);
            byte[] snapshotHash;
            await using (var input = new FileStream(source, FileOptionsFor(FileMode.Open, FileAccess.Read, FileShare.Read)))
            await using (var output = new FileStream(temporary, FileOptionsFor(FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)))
            {
                await CopyBoundedAsync(input, output, _options.MaximumMbTilesBytes, operation.Token).ConfigureAwait(false);
                output.Position = 0;
                snapshotHash = await HashBoundedAsync(output, _options.MaximumMbTilesBytes, operation.Token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            var metadata = await Task.Run(() => ReadMetadata(temporary, _options.MaximumTileBytes, operation.Token), operation.Token).ConfigureAwait(false);
            CheckSource(source);
            await using (var check = new FileStream(source, FileOptionsFor(FileMode.Open, FileAccess.Read, FileShare.Read)))
            {
                var sourceHash = await HashBoundedAsync(check, _options.MaximumMbTilesBytes, operation.Token).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(snapshotHash, sourceHash))
                    throw new IOException("The MBTiles source changed during import; stop its writer and retry.");
            }
            CheckSource(source);
            operation.Token.ThrowIfCancellationRequested();
            File.Move(temporary, MbTilesPath, overwrite: true);
            OfflineMetadata = metadata;
            return metadata;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    private void CheckSource(string source)
    {
        var file = new FileInfo(source);
        if (!file.Exists || file.Length is < 512 || file.Length > _options.MaximumMbTilesBytes ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Select a regular, bounded MBTiles database.");
        if (File.Exists(source + "-wal") && new FileInfo(source + "-wal").Length > 0)
            throw new IOException("Checkpoint and close the MBTiles writer before import; a live WAL cannot be ignored.");
    }

    private static SidebandMbTilesMetadata ReadMetadata(string path, int maximumTileBytes, CancellationToken token)
    {
        using var database = new ProfileSqlite(path, token, immutable: true);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in database.Rows("SELECT name,value FROM metadata", 256))
        {
            if (row.Length != 2 || row[0] is not string key || row[1] is not string value ||
                key.Length > 256 || value.Length > 4096 || !metadata.TryAdd(key, value))
                throw new InvalidDataException("MBTiles metadata has duplicate, malformed or oversized entries.");
        }
        var sample = database.Rows("SELECT zoom_level,tile_column,tile_row,tile_data FROM tiles LIMIT 1", 1);
        if (sample.Count == 1)
        {
            if (sample[0][3] is not byte[] bytes || bytes.Length > maximumTileBytes)
                throw new InvalidDataException("The first MBTiles raster exceeds its size limit or is not binary.");
            ValidateRaster(bytes);
        }
        return SidebandMbTilesMetadata.Parse(metadata);
    }

    private async Task<byte[]> ReadTileAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > _options.MaximumTileBytes)
            throw new InvalidDataException("A cached map tile exceeds its size limit; remove the corrupt cache entry.");
        await using var input = new FileStream(path, FileOptionsFor(FileMode.Open, FileAccess.Read, FileShare.Read));
        var bytes = await ReadBoundedAsync(input, _options.MaximumTileBytes, token).ConfigureAwait(false);
        ValidateRaster(bytes);
        return bytes;
    }

    internal static void ValidateRaster(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp) ||
            codec.Info.Width is < 1 or > 2048 || codec.Info.Height is < 1 or > 2048)
            throw new InvalidDataException("A map tile must be a raster PNG, JPEG or WebP no larger than 2048 pixels per side.");
        using var bitmap = new SKBitmap(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("The raster map tile is truncated or corrupt.");
    }

    private void TrimCache(long additionalBytes, int additionalTiles)
    {
        var files = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.tile"))
        {
            if (files.Count >= 10_000) throw new InvalidDataException("The map cache contains too many entries.");
            var file = new FileInfo(path);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Map cache entries must not be links.");
            files.Add(file);
        }
        long bytes = files.Sum(file => file.Length);
        var count = files.Count;
        foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal))
        {
            if (bytes + additionalBytes <= _options.MaximumCacheBytes && count + additionalTiles <= _options.MaximumTiles) break;
            File.Delete(file.FullName);
            bytes -= file.Length;
            count--;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, int limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        await CopyBoundedAsync(input, output, limit, token).ConfigureAwait(false);
        return output.ToArray();
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken token)
    {
        var buffer = new byte[16_384];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("The map input exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> HashBoundedAsync(Stream input, long limit, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[16_384];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("The map input exceeds its size limit.");
            hash.AppendData(buffer.AsSpan(0, read));
        }
        return hash.GetHashAndReset();
    }

    private static async Task WritePrivateAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var output = new FileStream(path, FileOptionsFor(FileMode.CreateNew, FileAccess.Write, FileShare.None));
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static FileStreamOptions FileOptionsFor(FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows() && mode is FileMode.CreateNew or FileMode.OpenOrCreate)
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private MapOperation Operation(CancellationToken token)
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_pendingOperations >= MaximumPendingOperations)
                throw new InvalidOperationException("The map store has reached its bounded operation capacity; retry after pending work finishes.");
            var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            _pendingOperations++;
            return new(this, linked);
        }
    }

    private sealed class MapOperation : IDisposable
    {
        private readonly SidebandMapStore _owner;
        private CancellationTokenSource? _cancellation;
        public CancellationToken Token { get; }
        internal MapOperation(SidebandMapStore owner, CancellationTokenSource cancellation)
        {
            _owner = owner;
            _cancellation = cancellation;
            Token = cancellation.Token;
        }
        public void Dispose()
        {
            var cancellation = Interlocked.Exchange(ref _cancellation, null);
            if (cancellation is null) return;
            cancellation.Dispose();
            lock (_owner._lifetime)
            {
                _owner._pendingOperations--;
                if (_owner._closing && _owner._pendingOperations == 0) _owner._operationsDrained.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetime)
        {
            if (_disposal is not null) return new(_disposal);
            _closing = true;
            _disposal = DisposeCoreAsync();
            return new(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _shutdown.Cancel();
        Task drained;
        lock (_lifetime) drained = _pendingOperations == 0 ? Task.CompletedTask : _operationsDrained.Task;
        await drained.ConfigureAwait(false);
        try
        {
            if (_ownsHttp) _http.Dispose();
            await _lease.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Dispose();
            _shutdown.Dispose();
        }
    }
}
