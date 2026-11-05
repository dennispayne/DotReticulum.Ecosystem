using Avalonia.Controls;
using Avalonia.Interactivity;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private SidebandPrintingService? _printing;
    private Guid? _printSelection;
    private string? _printSelectionCommand;
    private bool _printSelectionEnabled;

    private void InitializePrinting()
    {
        _printing = _client.CreatePrintingService();
        _printing.Log = text => System.Diagnostics.Trace.TraceInformation("Sideband: {0}", text);
        _printing.Start();
    }

    private void LoadPrintingSettings()
    {
        if (_state is null) return;
        if (this.FindControl<TextBox>("PrintCommandBox") is { } command) command.Text = _state.Printing.Command;
        if (this.FindControl<CheckBox>("EnablePaperPrintingBox") is { } enabled) enabled.IsChecked = _state.Printing.Enabled;
        if (this.FindControl<CheckBox>("ApprovePrintCommandBox") is { } approved) approved.IsChecked = false;
        ResetPaperPrintConsent();
        UpdatePaperPrintingAvailability();
    }

    private void SavePrintingClicked(object? sender, RoutedEventArgs e) => RunPaperOperation(async () =>
    {
        var command = this.FindControl<TextBox>("PrintCommandBox")?.Text ?? "lp";
        var enabled = this.FindControl<CheckBox>("EnablePaperPrintingBox")?.IsChecked == true;
        var approved = this.FindControl<CheckBox>("ApprovePrintCommandBox")?.IsChecked == true;
        await _client.ConfigurePrintingAsync(enabled, command, approved, _windowCancellation.Token);
        _state = await _client.LoadAsync(_windowCancellation.Token);
        LoadPrintingSettings();
        SetStatus(enabled
            ? "Local print command activated. Each selected paper print still requires separate consent."
            : "Paper printing disabled. No automatic incoming printing is performed.");
    });

    private void ResetPaperPrintConsent()
    {
        _printSelection = null;
        if (this.FindControl<CheckBox>("ConfirmPaperPrintBox") is { } consent) consent.IsChecked = false;
    }

    private void UpdatePaperPrintingAvailability()
    {
        if (_state is null) return;
        var index = MessageList.SelectedIndex;
        var message = index >= 0 && index < _visibleMessages.Count ? _visibleMessages[index] : null;
        if (_printSelection != message?.Id || _printSelectionCommand != _state.Printing.Command ||
            _printSelectionEnabled != _state.Printing.Enabled)
        {
            ResetPaperPrintConsent();
            _printSelection = message?.Id;
            _printSelectionCommand = _state.Printing.Command;
            _printSelectionEnabled = _state.Printing.Enabled;
        }
        var ready = !_closing && !_paperUiBusy && _state.Printing.Enabled &&
            message is not null && SidebandPrintingConfiguration.CanPrint(message);
        if (this.FindControl<Button>("PrintPaperButton") is { } button) button.IsEnabled = ready;
        if (this.FindControl<CheckBox>("ConfirmPaperPrintBox") is { } consent) consent.IsEnabled = ready;
        if (this.FindControl<TextBlock>("PaperPrintCommandText") is { } display)
            display.Text = _state.Printing.Enabled ? "Reviewed local command: " + _state.Printing.Command : "Paper printing is disabled.";
    }

    private void PrintPaperClicked(object? sender, RoutedEventArgs e) => RunPaperOperation(PrintSelectedPaperAsync);

    private async Task PrintSelectedPaperAsync()
    {
        var message = SelectedMessage();
        var consent = this.FindControl<CheckBox>("ConfirmPaperPrintBox");
        var confirmed = consent?.IsChecked == true && _printSelection == message.Id;
        var command = _printSelectionCommand ?? throw new InvalidOperationException("Review the selected paper and local command first.");
        ResetPaperPrintConsent();
        var printer = _printing ?? throw new InvalidOperationException("Printing service is unavailable.");
        var result = await printer.PrintPaperAsync(message.Id, command, confirmed, _windowCancellation.Token);
        if (!result.Success) throw new IOException(result.Detail);
        SetStatus("Paper QR command completed. This is software process success, not confirmation of physical printing or delivery.");
    }

    private async ValueTask DisposePrintingAsync()
    {
        if (_printing is { } printing)
        {
            await printing.DisposeAsync().ConfigureAwait(false);
            _printing = null;
        }
    }
}
