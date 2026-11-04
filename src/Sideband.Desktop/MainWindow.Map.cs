using System.Globalization;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private Task<SidebandMapStore>? _mapStoreTask;
    private CancellationTokenSource? _mapCancellation;
    private bool _restoredMap;
    private bool _changingMap;

    private void InitializeMap()
    {
        MapSourceSelector.ItemsSource = new[] { "Cached / online raster tiles", "Imported offline MBTiles" };
        MapSourceSelector.SelectedIndex = 0;
        MapPresetSelector.ItemsSource = SidebandMapPresets.Ids;
        MapPresetSelector.SelectedIndex = 0;
        MapViewport.ViewChanged += (_, _) =>
        {
            MapLatitudeBox.Text = MapViewport.Latitude.ToString("G10", CultureInfo.InvariantCulture);
            MapLongitudeBox.Text = MapViewport.Longitude.ToString("G10", CultureInfo.InvariantCulture);
            MapZoomBox.Text = MapViewport.Zoom.ToString(CultureInfo.InvariantCulture);
            if (!_changingMap) RequestMapRefresh();
        };
        MapViewport.SizeChanged += (_, _) =>
        {
            if (MapPage.IsVisible) RequestMapRefresh();
        };
    }

    private async Task<SidebandMapStore> MapStoreAsync()
    {
        var store = await (_mapStoreTask ??=
            SidebandMapStore.OpenAsync(Path.Combine(_store.DataDirectory, "maps"), cancellationToken: _windowCancellation.Token));
        if (!_restoredMap)
        {
            _restoredMap = true;
            var view = store.ViewSettings;
            MapSourceSelector.SelectedIndex = view.UseOffline ? 1 : 0;
            MapProviderBox.Text = view.Provider.UrlTemplate;
            MapAttributionBox.Text = view.Provider.Attribution;
            _changingMap = true;
            try
            {
                MapViewport.SetZoomRange(view.MinimumZoom, view.MaximumZoom);
                MapViewport.SetView(view.Latitude, view.Longitude, view.Zoom, view.IsCartesian);
            }
            finally { _changingMap = false; }
        }
        return store;
    }

    private void MapGoClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!double.TryParse(MapLatitudeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                !double.TryParse(MapLongitudeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) ||
                !int.TryParse(MapZoomBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var zoom))
                throw new ArgumentException("Enter finite coordinates and a whole-number zoom from 0 to 24.");
            _restoredMap = true;
            MapViewport.SetView(latitude, longitude, zoom,
                MapSourceSelector.SelectedIndex == 1 && _mapStoreTask?.IsCompletedSuccessfully == true &&
                _mapStoreTask.Result.OfflineMetadata?.IsCartesian == true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            MapStatusText.Text = "Map view failed: " + exception.Message;
            ShowError(MapStatusText.Text);
        }
    }

    private void MapZoomInClicked(object? sender, RoutedEventArgs e) => ChangeMapZoom(1);
    private void MapZoomOutClicked(object? sender, RoutedEventArgs e) => ChangeMapZoom(-1);
    private void ChangeMapZoom(int delta) => MapViewport.SetView(MapViewport.Latitude, MapViewport.Longitude,
        Math.Clamp(MapViewport.Zoom + delta, 0, SidebandMapProjection.MaximumZoom), MapViewport.IsCartesian);

    private void ApplyMapPresetClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var id = MapPresetSelector.SelectedItem as string ??
                throw new InvalidOperationException("Select a map provider preset.");
            var preset = SidebandMapPresets.Create(id, MapApiKeyBox.Text, MapProviderBox.Text);
            _restoredMap = true;
            MapOnlineConsentBox.IsChecked = false;
            MapSourceSelector.SelectedIndex = 0;
            MapProviderBox.Text = preset.Provider.UrlTemplate;
            MapAttributionBox.Text = preset.Provider.Attribution;
            MapApiKeyBox.Text = "";
            MapViewport.SetZoomRange(preset.MinimumZoom, preset.MaximumZoom);
            MapViewport.SetView(MapViewport.Latitude, MapViewport.Longitude, MapViewport.Zoom);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            MapStatusText.Text = "Map preset failed: " + exception.Message;
            ShowError(MapStatusText.Text);
        }
    }

    private void ImportMbTilesClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        ImportMbTilesButton.IsEnabled = false;
        try
        {
            var maps = await MapStoreAsync();
            var metadata = await maps.ImportMbTilesAsync(MbTilesPathBox.Text ?? "", _windowCancellation.Token);
            MapSourceSelector.SelectedIndex = 1;
            MapViewport.SetZoomRange(metadata.MinimumZoom, metadata.MaximumZoom);
            MapViewport.SetView(metadata.Latitude, metadata.Longitude,
                Math.Clamp(metadata.Zoom, metadata.MinimumZoom, metadata.StoredMaximumZoom), metadata.IsCartesian);
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            MapStatusText.Text = "Offline map import failed: " + exception.Message;
            ShowError(MapStatusText.Text);
        }
        finally { ImportMbTilesButton.IsEnabled = true; }
    });

    private void ClearMapCacheClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        _mapCancellation?.Cancel();
        MapOnlineConsentBox.IsChecked = false;
        try
        {
            var maps = await MapStoreAsync();
            var count = await maps.ClearDownloadedTilesAsync(_windowCancellation.Token);
            MapViewport.ReplaceTiles(new Dictionary<SidebandMapTile, Bitmap>());
            MapStatusText.Text = $"Removed {count} downloaded tiles. Online access disabled; imported offline map retained.";
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            MapStatusText.Text = "Map cache cleanup failed: " + exception.Message;
            ShowError(MapStatusText.Text);
        }
    });

    private void RequestMapRefresh()
    {
        if (_closing || !MapPage.IsVisible) return;
        _mapCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        _mapCancellation = cancellation;
        TrackOperation(async () =>
        {
            var tiles = new Dictionary<SidebandMapTile, Bitmap>();
            var transferred = false;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellation.Token);
                var maps = await MapStoreAsync();
                var offline = MapSourceSelector.SelectedIndex == 1;
                var provider = new SidebandMapProvider(MapProviderBox.Text ?? "", MapAttributionBox.Text ?? "");
                var consent = MapOnlineConsentBox.IsChecked == true;
                var visible = MapViewport.VisibleTiles();
                if (offline && maps.OfflineMetadata is null)
                    throw new InvalidOperationException("Import a raster MBTiles file before selecting the offline source.");
                MapAttributionText.Text = offline ? maps.OfflineMetadata!.Attribution : provider.Attribution;
                await maps.SaveViewAsync(new()
                {
                    Latitude = MapViewport.Latitude, Longitude = MapViewport.Longitude, Zoom = MapViewport.Zoom,
                    IsCartesian = MapViewport.IsCartesian, UseOffline = offline, Provider = provider,
                    MinimumZoom = MapViewport.MinimumZoom, MaximumZoom = MapViewport.MaximumZoom
                }, cancellation.Token);
                MapStatusText.Text = "Loading bounded raster viewport...";
                var failed = 0;
                string? firstError = null;
                foreach (var tile in visible)
                {
                    try
                    {
                        var bytes = offline ? await maps.GetOfflineTileAsync(tile, cancellation.Token) :
                            await maps.GetTileAsync(provider, tile, consent, cancellation.Token);
                        if (bytes is null) continue;
                        using var input = new MemoryStream(bytes, writable: false);
                        tiles.Add(tile, new Bitmap(input));
                    }
                    catch (Exception exception) when (exception is HttpRequestException or InvalidDataException)
                    {
                        failed++;
                        firstError ??= $"Tile {tile.Zoom}/{tile.X}/{tile.TmsY}: {exception.Message}";
                    }
                }
                cancellation.Token.ThrowIfCancellationRequested();
                MapViewport.ReplaceTiles(tiles);
                transferred = true;
                if (_smokeUi && Program.SmokeMapMode) InvalidateVisual();
                MapStatusText.Text = $"{tiles.Count}/{visible.Count} tiles loaded. " +
                    (offline ? "Owned offline snapshot; no network requests." :
                        consent ? "Online requests enabled for this session." : "Cache only; online requests disabled.") +
                    (tiles.Count == 0 ? " No tiles for this area/zoom; markers can still be displayed." : "");
                if (failed > 0)
                {
                    MapStatusText.Text += $" {failed} tiles failed. {firstError}";
                    ShowError(MapStatusText.Text);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                MapStatusText.Text = "Map loading failed: " + exception.Message;
                ShowError(MapStatusText.Text);
            }
            finally
            {
                if (!transferred) foreach (var bitmap in tiles.Values) bitmap.Dispose();
                if (ReferenceEquals(_mapCancellation, cancellation)) _mapCancellation = null;
                cancellation.Dispose();
            }
        });
    }
}
