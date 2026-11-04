using System.Text.Json;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMapOverlayUiTests
{
    [AvaloniaFact]
    public async Task ActualReferenceSensorShapesUseNamespacedMarkersAndPersistedFiltersWithoutNetworkOrDevices()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-overlay-ui-" + Guid.NewGuid().ToString("N"));
        const string first = "22222222222222222222222222222222";
        const string second = "33333333333333333333333333333333";
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        try
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-map-overlays.json"), token));
            var interfaces = SidebandTelemetryCodec.Decode(Convert.FromHexString(
                fixture.RootElement.GetProperty("identity_cases")[0].GetProperty("wire_hex").GetString()!));
            var connections = SidebandTelemetryCodec.Decode(Convert.FromHexString(
                fixture.RootElement.GetProperty("connection_marker_cases").EnumerateArray()
                    .First(value => value.GetProperty("type_label").GetString() == "blocked").GetProperty("wire_hex").GetString()!));
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Preserve actual reference sensor shapes, replacing only the synthetic historical time with the current native time.
            var report = SidebandTelemetryCodec.Create(now, interfaces.Sensors.Concat(connections.Sensors)
                .Where(reading => reading.Id != (byte)SidebandTelemetrySensorId.Time).ToArray());
            var store = new SidebandStateStore(root);
            await store.AddContactAsync(SidebandContact.Create("First reporter", first), token);
            await store.AddContactAsync(SidebandContact.Create("Second reporter", second), token);
            await using (var telemetry = await SidebandTelemetryStore.OpenAsync(Path.Combine(root, "telemetry"), cancellationToken: token))
                foreach (var source in new[] { first, second })
                    await telemetry.AppendAsync(source, SidebandTelemetryCodec.Encode(report), now, token);
            window = new MainWindow(root);
            window.Show();
            await UntilAsync(() => window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
            Click(window, "MapNavButton");
            var viewport = window.FindControl<SituationMap>("MapViewport")!;
            await UntilAsync(() => viewport.MarkerCount == 4);
            var timer = Assert.IsType<DispatcherTimer>(typeof(MainWindow)
                .GetField("_mapMarkerRefreshTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.Equal(TimeSpan.FromSeconds(60), timer.Interval);
            Assert.True(timer.IsEnabled);
            Set(window, "MapConnectionsBox", true);
            await UntilAsync(() => viewport.MarkerCount == 6);
            Assert.Equal(6, viewport.DisplayClusters().SelectMany(cluster => cluster.Members).Select(marker => marker.Key).Distinct().Count());
            Set(window, "MapHideBlockedBox", true);
            Assert.Equal(4, viewport.MarkerCount);
            Set(window, "MapInterfacesBox", false);
            Assert.Equal(0, viewport.MarkerCount);
            Set(window, "MapHideBlockedBox", false);
            Assert.Equal(2, viewport.MarkerCount);
            var fixedPath = Path.Combine(root, "fixed_locations");
            await File.WriteAllTextAsync(fixedPath, $"{first},0,0\n", token);
            window.FindControl<TextBox>("MapFixedLocationsPathBox")!.Text = fixedPath;
            Click(window, "ImportFixedLocationsButton");
            await UntilAsync(() => window.FindControl<TextBlock>("MapFixedLocationsStatusText")!.Text?.StartsWith("Local fixed locations replaced", StringComparison.Ordinal) == true);
            var peer = Assert.Single(viewport.DisplayClusters().SelectMany(cluster => cluster.Members),
                marker => marker.Kind == SidebandMapOverlayKind.Peer);
            Assert.Equal(new SidebandGeoCoordinate(0, 0), peer.Coordinate);
            await File.WriteAllTextAsync(fixedPath, $"{first},invalid,0\n", token);
            Click(window, "ImportFixedLocationsButton");
            await UntilAsync(() => window.FindControl<TextBlock>("MapFixedLocationsStatusText")!.Text?.StartsWith("Fixed-location operation failed", StringComparison.Ordinal) == true);
            Assert.Equal(3, viewport.MarkerCount);
            window.FindControl<TextBox>("MapHistorySecondsBox")!.Text = "120";
            Click(window, "ApplyMapHistoryButton");
            await UntilAsync(() => window.FindControl<TextBlock>("MapStatusText")!.Text?.Contains("Cache only", StringComparison.Ordinal) == true);
            Click(window, "ContactsNavButton");
            Assert.False(timer.IsEnabled);
            await window.CloseAndWaitAsync();
            Assert.False(timer.IsEnabled);
            window = new MainWindow(root);
            window.Show();
            await UntilAsync(() => window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
            Click(window, "MapNavButton");
            await UntilAsync(() => window.FindControl<TextBox>("MapHistorySecondsBox")!.Text == "120");
            Assert.False(window.FindControl<CheckBox>("MapInterfacesBox")!.IsChecked);
            Assert.True(window.FindControl<CheckBox>("MapConnectionsBox")!.IsChecked);
            Assert.False(window.FindControl<CheckBox>("MapOnlineConsentBox")!.IsChecked);
            Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).MapFixedLocations!);
            await window.CloseAndWaitAsync();
            if (Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MAP_OBJECT_PROFILE") is { Length: > 0 } export)
            {
                export = Path.GetFullPath(export);
                Assert.False(Directory.Exists(export), "Native synthetic fixture export must not overwrite an existing profile.");
                Directory.CreateDirectory(export);
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(export, Path.GetRelativePath(root, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
                await new SidebandStateStore(export).SetConversationObjectAsync(first, true, token);
                await using var maps = await SidebandMapStore.OpenAsync(Path.Combine(export, "maps"), cancellationToken: token);
                await maps.SaveViewAsync(maps.ViewSettings with
                {
                    Latitude = 0, Longitude = 0, Zoom = 0, HistoryLimitSeconds = 604800,
                    ClusterMarkers = true, IncludeInterfaces = true, IncludeConnections = true, HideBlocked = false
                }, token);
            }
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task ClusterActivationZoomsWithoutAnObserverAndMarkerObserversAreIsolated()
    {
        var viewport = new SituationMap { Width = 400, Height = 300 };
        var window = new Window { Width = 400, Height = 300, Content = viewport };
        try
        {
            viewport.SetView(0, 0, 1);
            viewport.SetMarkers([new("First", new(0, 0)), new("Second", new(0, 0))]);
            window.Show();
            await UntilAsync(() => viewport.RenderedClusterCount == 1);
            var center = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, Avalonia.Input.MouseButton.Left);
            window.MouseUp(center, Avalonia.Input.MouseButton.Left);
            Assert.Equal(19, viewport.Zoom);
            await UntilAsync(() => viewport.RenderedMarkerCount == 2);
            var observed = 0;
            viewport.MarkerActivated += (_, _) => throw new InvalidOperationException("Synthetic marker observer");
            viewport.MarkerActivated += (_, _) => observed++;
            window.MouseDown(center, Avalonia.Input.MouseButton.Left);
            window.MouseUp(center, Avalonia.Input.MouseButton.Left);
            Assert.Equal(1, observed);
            viewport.ClusterMarkers = false;
            Assert.Equal(2, viewport.DisplayClusters().Count);
        }
        finally { window.Close(); }
    }

    private static void Set(MainWindow window, string name, bool value)
    {
        var control = window.FindControl<CheckBox>(name)!;
        control.IsChecked = value;
        control.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Primitives.ToggleButton.ClickEvent));
    }
    private static void Click(MainWindow window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(DateTime.UtcNow < deadline, "Overlay desktop state did not reach the asserted condition.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
