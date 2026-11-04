using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMapUiTests
{
    [AvaloniaFact]
    public async Task OriginalMapImportsRendersPansZoomsAndRestartsWithoutOnlineConsent()
    {
        var path = Path.Combine(Path.GetTempPath(), "sideband-map-ui-" + Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        try
        {
            window = new MainWindow(path);
            window.Show();
            await ReadyAsync(window);
            window.FindControl<Button>("MapNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("MapStatusText")!.Text!.Contains("Cache only", StringComparison.Ordinal));
            Assert.False(window.FindControl<CheckBox>("MapOnlineConsentBox")!.IsChecked);
            window.FindControl<TextBox>("MbTilesPathBox")!.Text = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-map.mbtiles");
            window.FindControl<Button>("ImportMbTilesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var viewport = window.FindControl<SituationMap>("MapViewport")!;
            await WaitForAsync(() => viewport.LoadedTileCount == 4);
            Assert.Contains("Synthetic acceptance map", window.FindControl<TextBlock>("MapAttributionText")!.Text);
            Assert.Contains("no network requests", window.FindControl<TextBlock>("MapStatusText")!.Text);
            viewport.SetMarkers([new("Reported fixture location", new(0, 0))]);
            Assert.Equal(1, viewport.MarkerCount);
            var origin = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window)!.Value;
            window.MouseDown(origin, Avalonia.Input.MouseButton.Left);
            window.MouseMove(origin + new Vector(30, 20));
            window.MouseUp(origin + new Vector(30, 20), Avalonia.Input.MouseButton.Left);
            Assert.NotEqual(0, viewport.Longitude);
            Assert.NotEqual(0, viewport.Latitude);
            viewport.SetView(0, 0, 0);
            await WaitForAsync(() => window.FindControl<TextBlock>("MapStatusText")!.Text!.Contains("0/1 tiles loaded", StringComparison.Ordinal));
            viewport.SetView(0, 0, 1);
            await WaitForAsync(() => viewport.LoadedTileCount == 4);
            await window.CloseAndWaitAsync();
            window = new MainWindow(path);
            window.Show();
            await ReadyAsync(window);
            window.FindControl<Button>("MapNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.FindControl<CheckBox>("MapOnlineConsentBox")!.IsChecked);
            window.FindControl<ComboBox>("MapSourceSelector")!.SelectedIndex = 1;
            window.FindControl<Button>("MapGoButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<SituationMap>("MapViewport")!.LoadedTileCount == 4);
            window.FindControl<TextBox>("MbTilesPathBox")!.Text = Path.Combine(path, "missing.mbtiles");
            window.FindControl<Button>("ImportMbTilesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("MapStatusText")!.Text!.StartsWith("Offline map import failed:", StringComparison.Ordinal));
            Assert.Equal(4, window.FindControl<SituationMap>("MapViewport")!.LoadedTileCount);
            await window.CloseAndWaitAsync();
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [AvaloniaFact]
    public void ViewportClipsPolesBoundsVisibleTilesAndSupportsCartesianSnapshots()
    {
        var viewport = new SituationMap();
        viewport.Measure(new Size(10_000, 10_000));
        viewport.Arrange(new Rect(0, 0, 10_000, 10_000));
        viewport.SetView(90, 500, 24);
        Assert.Equal(SidebandMapProjection.DisplayLatitudeLimit, viewport.Latitude);
        Assert.Equal(180, viewport.Longitude);
        Assert.InRange(viewport.VisibleTiles().Count, 1, 64);
        Assert.All(viewport.VisibleTiles(), tile => tile.Validate());
        viewport.SetView(64, 128, 2, cartesian: true);
        Assert.Equal(64, viewport.Latitude);
        Assert.Equal(128, viewport.Longitude);
        Assert.True(viewport.IsCartesian);
        Assert.Throws<ArgumentOutOfRangeException>(() => viewport.SetView(double.NaN, 0, 1));
    }

    [AvaloniaFact]
    public void ViewUpdatesSurviveFailingObserversAndRejectedMarkersPreserveLastGoodSet()
    {
        var viewport = new SituationMap();
        viewport.SetView(10, 20, 3);
        Assert.Equal(20, viewport.Longitude);
        var observed = 0;
        viewport.ViewChanged += (_, _) => throw new InvalidOperationException("Synthetic observer failure");
        viewport.ViewChanged += (_, _) => observed++;
        viewport.SetView(11, 21, 4);
        viewport.SetView(12, 22, 5);
        Assert.Equal(2, observed);
        Assert.Equal(22, viewport.Longitude);
        Assert.Equal(5, viewport.Zoom);
        viewport.SetMarkers([new("Last good report", new(10, 20))]);
        Assert.Throws<ArgumentOutOfRangeException>(() => viewport.SetMarkers(
            Enumerable.Repeat(new SituationMapMarker("Too many", new(0, 0)), 257)));
        Assert.Throws<ArgumentException>(() => viewport.SetMarkers([new("Invalid\nlabel", new(0, 0))]));
        Assert.Throws<ArgumentException>(() => viewport.SetMarkers([new("Invalid color", new(0, 0), "bad", "ffffff")]));
        Assert.Equal(1, viewport.MarkerCount);
    }

    private static Task ReadyAsync(MainWindow window) =>
        WaitForAsync(() => window.FindControl<TextBlock>("StatusText")!.Text?.Contains("Offline · local data:", StringComparison.Ordinal) == true);

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The bounded map UI operation did not reach its expected state.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
