using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandDiscoveryUiTests
{
    [AvaloniaFact]
    public async Task CachedDiscoveryHasSeparateInspectionFilteringStaleRemovalAndExplicitFailureRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-discovery-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        try
        {
            var config = await SidebandDiscoveryLifecycleTests.Config(root, token);
            var storage = Path.Combine(config, "storage", "discovery");
            using var source = Identity.Generate();
            await using (var seed = new DiscoveryService(storage,
                new DiscoveryOptions { Enabled = true, RequiredStampValue = 8 }))
            {
                var payload = DiscoveryCodec.Create(new DiscoveryAdvertisement
                {
                    TransportIdentity = source.Hash, Name = "Synthetic discovery UI", Transport = true,
                    ReachableOn = "reference.example", Port = 4242, Latitude = 0, Longitude = 0,
                    PublishIfac = true, Passphrase = "synthetic-ui-secret", StampValue = 8,
                }, cancellationToken: token);
                Assert.True(await seed.ReceiveAsync(source, payload, 2, token));
            }
            var data = Path.Combine(root, "data");
            var store = new SidebandStateStore(data);
            await store.SetReticulumConfigDirectoryAsync(config, token);
            window = new MainWindow(data);
            window.Show();
            await Until(() => window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
            Click(window, "ConnectButton");
            await Until(() => (window.FindControl<TextBlock>("StatusText")!.Text ?? "").Contains("Connected", StringComparison.Ordinal));
            Click(window, "MapNavButton");
            var viewport = window.FindControl<SituationMap>("MapViewport")!;
            await Until(() => viewport.MarkerCount == 1);
            viewport.SetView(0, 0, 3);
            await Until(() => viewport.RenderedMarkerCount == 1);
            var marker = Assert.Single(viewport.DisplayClusters().SelectMany(cluster => cluster.Members));
            Assert.Equal(SidebandMapOverlayKind.Discovery, marker.Kind);
            Assert.Equal(new SidebandGeoCoordinate(0, 0), marker.Coordinate);
            var peerBefore = window.FindControl<TextBox>("TelemetryPeerBox")!.Text;
            var center = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            var details = window.FindControl<TextBlock>("MapDiscoveryDetailsText")!;
            await Until(() => details.Text?.Contains("Synthetic discovery UI", StringComparison.Ordinal) == true);
            Assert.Contains("not an LXMF address", details.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-ui-secret", details.Text, StringComparison.Ordinal);
            Assert.Equal(peerBefore, window.FindControl<TextBox>("TelemetryPeerBox")!.Text);
            Assert.Empty((await store.LoadAsync(cancellationToken: token)).Contacts);
            SetInterfaces(window, false);
            await Until(() => viewport.MarkerCount == 0);
            SetInterfaces(window, true);
            await Until(() => viewport.MarkerCount == 1);

            var backup = storage + ".saved";
            Directory.Move(storage, backup);
            await File.WriteAllTextAsync(storage, "Not a discovery directory", token);
            SetInterfaces(window, false);
            SetInterfaces(window, true);
            await Until(() => window.FindControl<TextBlock>("MapDiscoveryStatusText")!.Text?
                .StartsWith("Discovery cache refresh failed", StringComparison.Ordinal) == true);
            Assert.Equal(0, viewport.MarkerCount);
            Assert.Empty(details.Text ?? "");
            File.Delete(storage);
            Directory.Move(backup, storage);
            SetInterfaces(window, false);
            SetInterfaces(window, true);
            await Until(() => viewport.MarkerCount == 1);
            var cachedFiles = Directory.GetFiles(storage).ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes);
            foreach (var file in Directory.GetFiles(storage)) File.Delete(file);
            SetInterfaces(window, false);
            SetInterfaces(window, true);
            await Until(() => window.FindControl<TextBlock>("MapDiscoveryStatusText")!.Text?
                .StartsWith("0 cached transport", StringComparison.Ordinal) == true);
            Assert.Equal(0, viewport.MarkerCount);
            await window.CloseAndWaitAsync();
            await using (var maps = await SidebandMapStore.OpenAsync(Path.Combine(data, "maps"), cancellationToken: token))
                await maps.SaveViewAsync(maps.ViewSettings with
                {
                    IncludeInterfaces = false, Latitude = 45, Longitude = 45, Zoom = 5,
                }, token);
            foreach (var file in cachedFiles)
                await File.WriteAllBytesAsync(Path.Combine(storage, file.Key), file.Value, token);
            window = new MainWindow(data, config);
            window.Show();
            await Until(() => window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
            await window.RunDiscoverySmokeAsync();
            await Until(() => window.FindControl<TextBlock>("MapStatusText")!.Text?
                .Contains("tiles loaded.", StringComparison.Ordinal) == true);
            Assert.True(window.FindControl<CheckBox>("MapInterfacesBox")!.IsChecked);
            var smokeViewport = window.FindControl<SituationMap>("MapViewport")!;
            Assert.Equal(0, smokeViewport.Latitude);
            Assert.Equal(0, smokeViewport.Longitude);
            Assert.Equal(1, smokeViewport.RenderedMarkerCount);
            await window.CloseAndWaitAsync();
            if (Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_DISCOVERY_PROFILE") is { Length: > 0 } export)
            {
                await using (var maps = await SidebandMapStore.OpenAsync(Path.Combine(data, "maps"), cancellationToken: token))
                    await maps.SaveViewAsync(maps.ViewSettings with
                    {
                        IncludeInterfaces = false, Latitude = 45, Longitude = 45, Zoom = 5,
                    }, token);
                export = Path.GetFullPath(export);
                Assert.False(Directory.Exists(export), "Discovery profile export must not overwrite an existing profile.");
                var targetConfig = await SidebandDiscoveryLifecycleTests.Config(export, token);
                var targetStorage = Path.Combine(targetConfig, "storage", "discovery");
                Directory.CreateDirectory(targetStorage);
                foreach (var file in cachedFiles)
                    await File.WriteAllBytesAsync(Path.Combine(targetStorage, file.Key), file.Value, token);
                var targetData = Path.Combine(export, "data");
                foreach (var file in Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(targetData, Path.GetRelativePath(data, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
                await new SidebandStateStore(targetData).SetReticulumConfigDirectoryAsync(targetConfig, token);
            }
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void SetInterfaces(MainWindow window, bool enabled)
    {
        var control = window.FindControl<CheckBox>("MapInterfacesBox")!;
        control.IsChecked = enabled;
        control.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Primitives.ToggleButton.ClickEvent));
    }

    private static void Click(MainWindow window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Discovery UI condition did not complete within five seconds.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
