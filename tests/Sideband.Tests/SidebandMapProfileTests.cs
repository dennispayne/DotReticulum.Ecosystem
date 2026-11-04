using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DotReticulum.Core;
using LXMF;
using Sideband;
using Sideband.Desktop;

namespace Sideband.Tests;

public sealed partial class SidebandProfileImportTests
{
    [Fact]
    public async Task MapProfileSelectsActualPrimaryLayersAndPersistsOfflineWithoutFollowingExternalFiles()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-map-profile.json"), TestContext.Current.CancellationToken));
        foreach (var item in fixture.RootElement.GetProperty("selection").EnumerateArray())
        {
            using var profile = new Profile();
            var config = new Dictionary<object, object?>
            {
                ["map_layer"] = item.GetProperty("selected").GetString(),
                ["map_lat"] = 51.5, ["map_lon"] = -0.125, ["map_zoom"] = 12L,
                ["map_custom_1_url"] = "https://maps.example.invalid/{z}/{x}/{y}.png",
                ["map_use_online"] = true, ["map_storage_file"] = Path.Combine(profile.Root, "do-not-read.mbtiles"),
            };
            foreach (var flag in item.GetProperty("enabled").EnumerateObject()) config[flag.Name] = flag.Value.GetBoolean();
            await File.WriteAllBytesAsync(Path.Combine(profile.Source, "sideband_config"), MsgPack.Pack(config), TestContext.Current.CancellationToken);
            var before = profile.Fingerprint();
            var result = await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
            await using var maps = await SidebandMapStore.OpenAsync(Path.Combine(profile.Target, "maps"), cancellationToken: TestContext.Current.CancellationToken);
            var expected = SidebandMapPresets.Create(item.GetProperty("result").GetString()!,
                customUrl: "https://maps.example.invalid/{z}/{x}/{y}.png");
            Assert.Equal(expected.Provider, maps.ViewSettings.Provider);
            Assert.Equal(51.5, maps.ViewSettings.Latitude);
            Assert.Equal(-0.125, maps.ViewSettings.Longitude);
            Assert.Equal(12, maps.ViewSettings.Zoom);
            Assert.Equal(0, maps.ViewSettings.MinimumZoom);
            Assert.Equal(19, maps.ViewSettings.MaximumZoom);
            Assert.False(maps.ViewSettings.UseOffline);
            Assert.Null(maps.OfflineMetadata);
            Assert.Null(await maps.GetTileAsync(expected.Provider, new(0, 0, 0), cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains(result.Notices, notice => notice.Contains("Online consent is not imported", StringComparison.Ordinal));
            Assert.Equal(before, profile.Fingerprint());
        }
    }

    [Theory]
    [InlineData("map_lat")]
    [InlineData("map_lon")]
    [InlineData("map_zoom")]
    [InlineData("map_layer")]
    [InlineData("map_use_atlas")]
    public async Task MalformedMapProfileRollsBackAtomically(string key)
    {
        using var profile = new Profile();
        var config = new Dictionary<object, object?> { ["map_layer"] = "atlas", ["map_use_atlas"] = true };
        config[key] = key == "map_layer" ? 9L : "not numeric or boolean";
        await File.WriteAllBytesAsync(Path.Combine(profile.Source, "sideband_config"), MsgPack.Pack(config), TestContext.Current.CancellationToken);
        var before = profile.Fingerprint();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(profile.Target));
        Assert.Empty(Directory.GetDirectories(profile.Root, ".native.import-*"));
        Assert.Equal(before, profile.Fingerprint());
    }

    [AvaloniaFact]
    public async Task ImportedMapActuallyOpensInDesktopWithOwnKeyAndWithoutOnlineConsent()
    {
        using var profile = new Profile();
        await File.WriteAllBytesAsync(Path.Combine(profile.Source, "sideband_config"), MsgPack.Pack(new Dictionary<object, object?>
        {
            ["map_layer"] = "st_dark", ["map_use_st_dark"] = true,
            ["map_st_api_key"] = "synthetic-stadia-key", ["map_lat"] = 48.8566, ["map_lon"] = 2.3522,
            ["map_zoom"] = 12L, ["map_use_online"] = true,
        }), TestContext.Current.CancellationToken);
        await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        var window = new MainWindow(profile.Target);
        try
        {
            window.Show();
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")!.Text?.Contains("Offline · local data:", StringComparison.Ordinal) == true);
            window.FindControl<Button>("MapNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("MapStatusText")!.Text!.Contains("Cache only", StringComparison.Ordinal));
            var viewport = window.FindControl<SituationMap>("MapViewport")!;
            Assert.Equal(48.8566, viewport.Latitude);
            Assert.Equal(2.3522, viewport.Longitude);
            Assert.Equal(12, viewport.Zoom);
            Assert.Equal(19, viewport.MaximumZoom);
            Assert.False(window.FindControl<CheckBox>("MapOnlineConsentBox")!.IsChecked);
            Assert.Contains("api_key=synthetic-stadia-key", window.FindControl<TextBox>("MapProviderBox")!.Text);
        }
        finally { await window.CloseAndWaitAsync(); }

        static async Task WaitForAsync(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!predicate())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Imported map did not reach its visible offline state.");
                await Task.Delay(20, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingKeyIsReportedAndImportedCoordinatesFollowPrimaryConstructorClamps(bool keyRequired)
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-map-profile.json"), TestContext.Current.CancellationToken));
        foreach (var observation in fixture.RootElement.GetProperty("constructor_clamps").EnumerateArray())
        {
            using var profile = new Profile();
            await File.WriteAllBytesAsync(Path.Combine(profile.Source, "sideband_config"), MsgPack.Pack(new Dictionary<object, object?>
            {
                ["map_layer"] = keyRequired ? "mt_topo" : "osm", ["map_use_topo"] = true,
                ["map_lat"] = observation.GetProperty("latitude").GetDouble(),
                ["map_lon"] = observation.GetProperty("longitude").GetDouble(),
                ["map_zoom"] = observation.GetProperty("zoom").GetInt64(),
            }), TestContext.Current.CancellationToken);
            var result = await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
            await using var maps = await SidebandMapStore.OpenAsync(Path.Combine(profile.Target, "maps"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(SidebandMapPresets.Create("osm").Provider, maps.ViewSettings.Provider);
            Assert.Equal(observation.GetProperty("result_latitude").GetDouble(), maps.ViewSettings.Latitude);
            Assert.Equal(observation.GetProperty("result_longitude").GetDouble(), maps.ViewSettings.Longitude);
            Assert.Equal(observation.GetProperty("result_zoom").GetInt32(), maps.ViewSettings.Zoom);
            Assert.Equal(keyRequired, result.Notices.Any(notice => notice.Contains("own API key", StringComparison.Ordinal)));
        }
    }
}
