using Avalonia.Interactivity;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private void LoadStartupAnnouncementSettings() => StartupAnnouncementBox.IsChecked =
        SidebandAnnounceConfiguration.FromState(
            _state ?? throw new InvalidOperationException("Load application state before editing announcements."))
        .AnnounceOnStartup;

    private void SaveStartupAnnouncementClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        try
        {
            await _client.ConfigureAnnouncementAsync(new(StartupAnnouncementBox.IsChecked == true), _windowCancellation.Token);
            AnnouncementStatusText.Text = "Startup announcement setting saved. Disabling suppresses a pending startup announcement; enabling applies on the next connection.";
        }
        catch (Exception ex) { AnnouncementStatusText.Text = ex.Message; ShowError("Could not save announcement setting: " + ex.Message); }
    });

    private void AnnounceNowClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        try
        {
            await _client.AnnounceAsync(_windowCancellation.Token);
            AnnouncementStatusText.Text = "Delivery destination announced.";
        }
        catch (Exception ex) { AnnouncementStatusText.Text = ex.Message; ShowError("Could not announce: " + ex.Message); }
    });
}
