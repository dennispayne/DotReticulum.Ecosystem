using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sideband;
using SkiaSharp;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMapStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RealHttpTransportUsesXyzAndDoesNotFollowRedirects()
    {
        var directory = TemporaryDirectory();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task<string>? pending = null;
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var provider = new SidebandMapProvider($"http://127.0.0.1:{port}/{{z}}/{{x}}/{{y}}.png",
                "Controlled loopback HTTP; explicit insecure opt-in", AllowInsecureHttp: true);
            await using var store = await SidebandMapStore.OpenAsync(directory, cancellationToken: lifetime.Token);
            pending = ServeOnceAsync(redirect: false);
            Assert.Equal(TileHandler.Raster, await store.GetTileAsync(provider, new(1, 0, 0), true, lifetime.Token));
            var request = await pending;
            Assert.StartsWith("GET /1/0/1.png HTTP/1.1", request);
            Assert.Contains("User-Agent: DotReticulum.Ecosystem-Sideband/2.2", request);
            pending = ServeOnceAsync(redirect: true);
            await Assert.ThrowsAsync<HttpRequestException>(() => store.GetTileAsync(provider, new(1, 1, 0), true, lifetime.Token));
            Assert.StartsWith("GET /1/1/1.png HTTP/1.1", await pending);
            Assert.Single(Directory.GetFiles(directory, "*.tile"));
        }
        finally
        {
            lifetime.Cancel();
            listener.Stop();
            if (pending is not null)
            {
                try { await pending; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
            Directory.Delete(directory, recursive: true);
        }

        async Task<string> ServeOnceAsync(bool redirect)
        {
            using var peer = await listener.AcceptTcpClientAsync(lifetime.Token);
            await using var network = peer.GetStream();
            using var reader = new StreamReader(network, Encoding.ASCII, leaveOpen: true);
            var request = new StringBuilder();
            for (var index = 0; index < 50; index++)
            {
                var line = await reader.ReadLineAsync(lifetime.Token);
                if (line is null || line.Length == 0) break;
                request.AppendLine(line);
                if (request.Length > 8192) throw new InvalidDataException("The controlled map request exceeds test bounds.");
            }
            var headers = redirect ?
                "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/must-not-follow\r\nContent-Length: 0\r\nConnection: close\r\n\r\n" :
                $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {TileHandler.Raster.Length}\r\nConnection: close\r\n\r\n";
            await network.WriteAsync(Encoding.ASCII.GetBytes(headers), lifetime.Token);
            if (!redirect) await network.WriteAsync(TileHandler.Raster, lifetime.Token);
            return request.ToString();
        }
    }
    [Fact]
    public async Task OfflineConsentCacheQuotaRestartAndProviderIsolation()
    {
        var directory = TemporaryDirectory();
        var handler = new TileHandler();
        using var http = new HttpClient(handler);
        var options = new SidebandMapCacheOptions { MaximumTiles = 1 };
        var provider = new SidebandMapProvider("https://example.invalid/{z}/{x}/{y}.png", "Synthetic");
        try
        {
            await using (var store = await SidebandMapStore.OpenAsync(directory, http, options, Token))
            {
                Assert.Null(await store.GetTileAsync(provider, new(1, 0, 0), cancellationToken: Token));
                Assert.Equal(0, handler.Requests);
                var bytes = await store.GetTileAsync(provider, new(1, 0, 0), allowOnline: true, cancellationToken: Token);
                Assert.Equal(TileHandler.Raster, bytes);
                Assert.Equal(bytes, await store.GetTileAsync(provider, new(1, 0, 0), cancellationToken: Token));
                Assert.Equal(1, handler.Requests);
                Assert.Equal("/1/0/1.png", handler.LastUri!.AbsolutePath);
                Assert.Contains("DotReticulum", handler.UserAgent);
                Assert.Null(await store.GetTileAsync(provider with { UrlTemplate = "https://other.invalid/{z}/{x}/{y}.png" }, new(1, 0, 0), cancellationToken: Token));
                await store.GetTileAsync(provider, new(1, 1, 1), allowOnline: true, cancellationToken: Token);
                Assert.Null(await store.GetTileAsync(provider, new(1, 0, 0), cancellationToken: Token));
                Assert.Single(Directory.GetFiles(directory, "*.tile"));
                await store.SaveViewAsync(new() { Latitude = 51.5, Longitude = -0.125, Zoom = 12, Provider = provider }, Token);
            }
            await using var reloaded = await SidebandMapStore.OpenAsync(directory, http, options, Token);
            Assert.Equal(TileHandler.Raster, await reloaded.GetTileAsync(provider, new(1, 1, 1), cancellationToken: Token));
            Assert.Equal(2, handler.Requests);
            Assert.Equal(51.5, reloaded.ViewSettings.Latitude);
            Assert.Equal(12, reloaded.ViewSettings.Zoom);
            Assert.Equal(provider, reloaded.ViewSettings.Provider);
            Assert.Equal(1, await reloaded.ClearDownloadedTilesAsync(Token));
            Assert.Null(await reloaded.GetTileAsync(provider, new(1, 1, 1), cancellationToken: Token));
            await Assert.ThrowsAsync<IOException>(() => SidebandMapStore.OpenAsync(directory, http, options, Token));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task TruncationOversizeHttpAndCorruptCacheFailWithoutSuccessShapedFallback()
    {
        var directory = TemporaryDirectory();
        var handler = new TileHandler();
        using var http = new HttpClient(handler);
        var provider = new SidebandMapProvider("https://example.invalid/{z}/{x}/{y}.png", "Synthetic");
        try
        {
            await using var store = await SidebandMapStore.OpenAsync(directory, http,
                new() { MaximumTileBytes = 1024 }, Token);
            handler.Content = new byte[1025];
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetTileAsync(provider, new(0, 0, 0), true, Token));
            handler.Content = TileHandler.Raster[..30];
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetTileAsync(provider, new(0, 0, 0), true, Token));
            handler.Status = HttpStatusCode.NotFound;
            await Assert.ThrowsAsync<HttpRequestException>(() => store.GetTileAsync(provider, new(0, 0, 0), true, Token));
            Assert.Empty(Directory.GetFiles(directory, "*.tile"));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            handler.Status = HttpStatusCode.OK;
            handler.Content = TileHandler.Raster;
            await store.GetTileAsync(provider, new(0, 0, 0), true, Token);
            await File.WriteAllBytesAsync(Directory.GetFiles(directory, "*.tile").Single(), [1, 2, 3], Token);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetTileAsync(provider, new(0, 0, 0), cancellationToken: Token));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CancellationDisposalAndFailedPeerDoNotStrandOwnership()
    {
        var directory = TemporaryDirectory();
        var handler = new TileHandler { Block = true };
        using var http = new HttpClient(handler);
        var provider = new SidebandMapProvider("https://example.invalid/{z}/{x}/{y}.png", "Synthetic");
        try
        {
            var store = await SidebandMapStore.OpenAsync(directory, http, cancellationToken: Token);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var pending = store.GetTileAsync(provider, new(0, 0, 0), true, cancellation.Token);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            handler.Block = false;
            Assert.NotNull(await store.GetTileAsync(provider, new(0, 0, 0), true, Token));
            handler.Block = true;
            pending = store.GetTileAsync(provider, new(1, 0, 0), true, Token);
            await Task.WhenAll(store.DisposeAsync().AsTask(), store.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.GetTileAsync(provider, new(0, 0, 0), cancellationToken: Token));
            await using var reopened = await SidebandMapStore.OpenAsync(directory, http, cancellationToken: Token);
            Assert.NotNull(await reopened.GetTileAsync(provider, new(0, 0, 0), cancellationToken: Token));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ImportedActualSqliteTilesMatchPrimaryTmsLookupAndPersistWithoutSourceChanges()
    {
        // GenerateSidebandMapVectors.py executes primary d069122 _load_tile using real SQLite.
        var directory = TemporaryDirectory();
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-map.mbtiles");
        var original = await File.ReadAllBytesAsync(source, Token);
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-maps.json"), Token));
        Assert.Equal(fixture.RootElement.GetProperty("mbtiles_fixture_sha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(original)));
        try
        {
            await using (var store = await SidebandMapStore.OpenAsync(directory, cancellationToken: Token))
            {
                Assert.Equal("Synthetic acceptance map; no geographic content", (await store.ImportMbTilesAsync(source, Token)).Attribution);
                foreach (var item in fixture.RootElement.GetProperty("mbtiles_tiles").EnumerateArray())
                {
                    if (item.GetProperty("x").GetInt32() >= 2) continue;
                    var tile = new SidebandMapTile(1, item.GetProperty("x").GetInt32(), item.GetProperty("tms_y").GetInt32());
                    var bytes = Assert.IsType<byte[]>(await store.GetOfflineTileAsync(tile, Token));
                    Assert.Equal(item.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
                }
                Assert.Null(await store.GetOfflineTileAsync(new(0, 0, 0), Token));
            }
            await using var reloaded = await SidebandMapStore.OpenAsync(directory, cancellationToken: Token);
            Assert.NotNull(reloaded.OfflineMetadata);
            Assert.NotNull(await reloaded.GetOfflineTileAsync(new(1, 0, 0), Token));
            Assert.Equal(original, await File.ReadAllBytesAsync(source, Token));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ImportRejectsLiveWalAndMalformedReplacementPreservingLastGoodMap()
    {
        var directory = TemporaryDirectory();
        var sources = TemporaryDirectory();
        var source = Path.Combine(sources, "source.mbtiles");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-map.mbtiles"), source);
        try
        {
            await using var store = await SidebandMapStore.OpenAsync(directory, cancellationToken: Token);
            await store.ImportMbTilesAsync(source, Token);
            await File.WriteAllBytesAsync(source + "-wal", [1], Token);
            await Assert.ThrowsAsync<IOException>(() => store.ImportMbTilesAsync(source, Token));
            File.Delete(source + "-wal");
            await File.WriteAllBytesAsync(source, new byte[4096], Token);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportMbTilesAsync(source, Token));
            Assert.NotNull(await store.GetOfflineTileAsync(new(1, 0, 0), Token));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(sources, recursive: true);
        }
    }

    [Fact]
    public async Task AdmissionIsBoundedAndDisposalCancelsEveryAcceptedWaiter()
    {
        var directory = TemporaryDirectory();
        using var http = new HttpClient(new TileHandler { Block = true });
        var provider = new SidebandMapProvider("https://example.invalid/{z}/{x}/{y}.png", "Synthetic");
        try
        {
            await using var store = await SidebandMapStore.OpenAsync(directory, http, cancellationToken: Token);
            var accepted = Enumerable.Range(0, SidebandMapStore.MaximumPendingOperations)
                .Select(_ => store.GetTileAsync(provider, new(0, 0, 0), true, Token)).ToArray();
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetTileAsync(provider, new(0, 0, 0), true, Token));
            await store.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(accepted));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sideband-map-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TileHandler : HttpMessageHandler
    {
        public static byte[] Raster { get; } = MakeRaster();
        public byte[] Content { get; set; } = Raster;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool Block { get; set; }
        public int Requests { get; private set; }
        public Uri? LastUri { get; private set; }
        public string UserAgent { get; private set; } = "";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            LastUri = request.RequestUri;
            UserAgent = request.Headers.UserAgent.ToString();
            Entered.TrySetResult();
            if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(Status) { Content = new ByteArrayContent(Content) };
        }
        private static byte[] MakeRaster()
        {
            using var bitmap = new SKBitmap(2, 2);
            bitmap.Erase(SKColors.Teal);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }
    }
}
