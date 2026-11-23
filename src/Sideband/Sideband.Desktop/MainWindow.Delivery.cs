using Avalonia.Interactivity;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private void LoadDeliverySettings() => LimitIncomingMessagesBox.IsChecked =
        SidebandDeliveryConfiguration.FromState(
            _state ?? throw new InvalidOperationException("Load application state before editing delivery limits."))
        .LimitIncomingToOneMegabyte;

    private void SaveDeliverySettingsClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        SaveDeliverySettingsButton.IsEnabled = false;
        try
        {
            await _client.ConfigureDeliveryAsync(new(LimitIncomingMessagesBox.IsChecked == true), _windowCancellation.Token);
            _state = await _client.LoadAsync(_windowCancellation.Token);
            LoadDeliverySettings();
            DeliverySettingsStatusText.Text = _client.IsConnected
                ? "Delivery limit saved and applied to new transfers."
                : "Delivery limit saved; it will apply when connected.";
        }
        catch (Exception ex)
        {
            DeliverySettingsStatusText.Text = ex.Message;
            ShowError("Could not save delivery limit: " + ex.Message);
        }
        finally { if (!_closing) SaveDeliverySettingsButton.IsEnabled = true; }
    });
}
