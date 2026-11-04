using System.Globalization;
using Avalonia.Interactivity;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private void LoadSynchronizationSettings()
    {
        var configuration = SidebandSynchronizationConfiguration.FromState(
            _state ?? throw new InvalidOperationException("Load application state before editing synchronization."));
        LimitManualFetchBox.IsChecked = configuration.LimitManualFetch == true;
        MaximumFetchMessagesBox.Text = configuration.MaximumMessages.ToString(CultureInfo.InvariantCulture);
    }

    private void SaveSynchronizationSettingsClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        SaveSynchronizationSettingsButton.IsEnabled = false;
        try
        {
            if (!int.TryParse(MaximumFetchMessagesBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var maximum))
                throw new ArgumentException("Enter a positive whole number of messages.");
            await _client.ConfigureSynchronizationAsync(new(LimitManualFetchBox.IsChecked == true, maximum), _windowCancellation.Token);
            _state = await _client.LoadAsync(_windowCancellation.Token);
            LoadSynchronizationSettings();
            SynchronizationSettingsStatusText.Text = "Manual fetch limit saved. Automatic scheduled fetches remain unlimited.";
        }
        catch (Exception ex)
        {
            SynchronizationSettingsStatusText.Text = ex.Message;
            ShowError("Could not save manual fetch limit: " + ex.Message);
        }
        finally { if (!_closing) SaveSynchronizationSettingsButton.IsEnabled = true; }
    });
}
