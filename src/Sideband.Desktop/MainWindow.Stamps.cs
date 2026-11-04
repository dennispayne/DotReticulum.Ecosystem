using System.Globalization;
using Avalonia.Interactivity;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private void LoadStampSettings()
    {
        var configuration = SidebandStampConfiguration.FromState(
            _state ?? throw new InvalidOperationException("Load application state before editing stamps."));
        RequireStampsBox.IsChecked = configuration.RequireStamps;
        InboundStampCostBox.Text = configuration.InboundStampCost?.ToString(CultureInfo.InvariantCulture);
        RejectInvalidStampsBox.IsChecked = configuration.RejectInvalidStamps;
    }

    private void SaveStampPolicyClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        SaveStampPolicyButton.IsEnabled = false;
        try
        {
            var text = InboundStampCostBox.Text;
            int? cost = null;
            if (!string.IsNullOrWhiteSpace(text))
                cost = int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                    ? value : throw new ArgumentException("Enter a whole stamp cost below 255, or leave it empty.");
            await _client.ConfigureStampPolicyAsync(new(RequireStampsBox.IsChecked == true, cost,
                RejectInvalidStampsBox.IsChecked == true), _windowCancellation.Token);
            _state = await _client.LoadAsync(_windowCancellation.Token);
            LoadStampSettings();
            StampPolicyStatusText.Text = _client.IsConnected
                ? "Stamp policy saved and applied; delivery announcement submitted."
                : "Stamp policy saved; it will apply when connected.";
        }
        catch (Exception ex)
        {
            StampPolicyStatusText.Text = ex.Message;
            ShowError("Could not save stamp policy: " + ex.Message);
        }
        finally { if (!_closing) SaveStampPolicyButton.IsEnabled = true; }
    });
}
