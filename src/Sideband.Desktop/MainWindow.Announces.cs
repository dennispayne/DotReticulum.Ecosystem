using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private IReadOnlyList<SidebandAnnounce> _visibleAnnounces = [];
    private bool _announcesBusy;
    private bool _announcesRefreshAgain;
    private bool _announcesRefreshReportStatus;

    private void RefreshAnnouncesClicked(object? sender, RoutedEventArgs e) => RefreshAnnounces();
    private void RefreshAnnounces(bool reportStatus = true)
    {
        if (_announcesBusy)
        {
            _announcesRefreshAgain = true;
            _announcesRefreshReportStatus |= reportStatus;
            return;
        }
        RunAnnounceOperation(async () =>
        {
            await LoadAnnouncesAsync();
            if (reportStatus)
                AnnouncesStatusText.Text = _client.IsConnected
                    ? "History loaded with current cached delivery stamp costs. No network request was sent."
                    : "Offline saved history loaded; stamp costs are the last observed values.";
        });
    }

    private async Task LoadAnnouncesAsync()
    {
        var selected = SelectedAnnounceOrNull()?.DestinationHash;
        var records = await _client.GetAnnouncesAsync(_windowCancellation.Token);
        if (_closing) return;
        _visibleAnnounces = records.Reverse().ToArray();
        AnnouncesList.ItemsSource = _visibleAnnounces.Select(item =>
            $"{(item.DestinationType == SidebandAnnounce.Delivery ? "Delivery" : "Propagation")} | " +
            $"{DisplayAnnounceName(item.Name) ?? "(unnamed)"} | {item.DestinationHash} | {item.ReceivedAt:u}").ToArray();
        var index = _visibleAnnounces.ToList().FindIndex(item => item.DestinationHash == selected);
        AnnouncesList.SelectedIndex = index >= 0 ? index : _visibleAnnounces.Count > 0 ? 0 : -1;
        UpdateAnnounceSelection();
    }

    private static string? DisplayAnnounceName(string? name) =>
        name is null ? null : new string(name.Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    private SidebandAnnounce? SelectedAnnounceOrNull() => AnnouncesList.SelectedIndex is var index &&
        index >= 0 && index < _visibleAnnounces.Count ? _visibleAnnounces[index] : null;

    private SidebandAnnounce SelectedAnnounce() => SelectedAnnounceOrNull() ??
        throw new InvalidOperationException("Select an announcement first.");

    private void AnnounceSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateAnnounceSelection();
    private void UpdateAnnounceSelection()
    {
        var selected = SelectedAnnounceOrNull();
        AnnounceLocalButton.IsEnabled = !_announcesBusy && _client.IsConnected;
        ConverseAnnounceButton.IsEnabled = !_announcesBusy && selected?.DestinationType == SidebandAnnounce.Delivery;
        UseAnnounceNodeButton.IsEnabled = !_announcesBusy && selected?.DestinationType == SidebandAnnounce.Propagation;
        CopyAnnounceButton.IsEnabled = DeleteAnnounceButton.IsEnabled = !_announcesBusy && selected is not null;
        AnnounceAddressBox.Text = selected?.DestinationHash ?? "";
        AnnounceDetailsText.Text = selected is null ? "Select an announcement." :
            $"{DisplayAnnounceName(selected.Name) ?? "(unnamed)"}\n{selected.DestinationType}\nReceived {selected.ReceivedAt:u}\n" +
            $"Stamp cost: {selected.StampCost?.ToString(CultureInfo.InvariantCulture) ?? "unknown/disabled"}" +
            (selected.NodeVersion is null ? "" : "\nNode version: " + DisplayAnnounceName(selected.NodeVersion)) +
            (selected.NodeEnabled.HasValue ? "\nNode active at announcement: " + selected.NodeEnabled : "") +
            (selected.Signal is { } signal
                ? $"\nRSSI: {Measurement(signal.Rssi)} dBm | SNR: {Measurement(signal.Snr)} dB | Quality: {Measurement(signal.Quality)}%"
                : "\nPhysical-layer measurements unavailable.");
    }

    private static string Measurement(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unavailable";

    private void AnnounceLocalClicked(object? sender, RoutedEventArgs e) => RunAnnounceOperation(async () =>
    {
        await _client.AnnounceAsync(_windowCancellation.Token);
        AnnouncesStatusText.Text = "Local delivery announcement submitted; this is not peer receipt confirmation.";
    });

    private void ConverseAnnounceClicked(object? sender, RoutedEventArgs e) => RunAnnounceOperation(async () =>
    {
        var announce = SelectedAnnounce();
        if (announce.DestinationType != SidebandAnnounce.Delivery)
            throw new InvalidOperationException("A propagation-node address is not a delivery conversation.");
        var state = await _client.LoadAsync(_windowCancellation.Token);
        var name = state.Contacts.FirstOrDefault(contact => contact.DeliveryHash == announce.DestinationHash)?.DisplayName;
        if (name is null)
            name = !string.IsNullOrWhiteSpace(announce.Name) && announce.Name.Trim().Length <= 64 && !announce.Name.Any(char.IsControl)
                ? announce.Name : announce.DestinationHash;
        await _client.AddContactAsync(name, announce.DestinationHash, _windowCancellation.Token);
        _state = await _client.LoadAsync(_windowCancellation.Token);
        _selectedContact = announce.DestinationHash;
        RefreshContacts();
        NavigateTo("Conversations");
        await RefreshMessagesAsync(_windowCancellation.Token);
        AnnouncesStatusText.Text = "Conversation opened without changing trust or telemetry permissions.";
    });

    private void UseAnnounceNodeClicked(object? sender, RoutedEventArgs e) => RunAnnounceOperation(async () =>
    {
        var announce = SelectedAnnounce();
        if (announce.DestinationType != SidebandAnnounce.Propagation)
            throw new InvalidOperationException("Select a propagation-node announcement.");
        var state = await _client.LoadAsync(_windowCancellation.Token);
        await _client.ConfigurePropagationAsync(announce.DestinationHash, state.PropagationByDefault, _windowCancellation.Token);
        PropagationNodeBox.Text = announce.DestinationHash;
        AnnouncesStatusText.Text = "Propagation node saved. Announcement history is not proof of present reachability.";
    });

    private void CopyAnnounceClicked(object? sender, RoutedEventArgs e) => RunAnnounceOperation(async () =>
    {
        await WriteClipboardTextAsync(SelectedAnnounce().DestinationHash);
        AnnouncesStatusText.Text = "Destination address copied.";
    });

    private void DeleteAnnounceClicked(object? sender, RoutedEventArgs e) => RunAnnounceOperation(async () =>
    {
        await _client.DeleteAnnounceAsync(SelectedAnnounce().DestinationHash, _windowCancellation.Token);
        await LoadAnnouncesAsync();
        AnnouncesStatusText.Text = "Announcement deleted. Conversations, permissions and selected propagation node are unchanged.";
    });

    private void RunAnnounceOperation(Func<Task> operation) => TrackOperation(async () =>
    {
        if (_announcesBusy) { AnnouncesStatusText.Text = "Another announce operation is pending."; return; }
        _announcesBusy = true;
        RefreshAnnouncesButton.IsEnabled = false;
        UpdateAnnounceSelection();
        try { await operation(); }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closing)
            {
                AnnouncesStatusText.Text = "Announce operation failed: " + ex.Message;
                ShowError(AnnouncesStatusText.Text);
            }
            else System.Diagnostics.Trace.TraceError("Announce operation failed during close: {0}", ex);
        }
        finally
        {
            _announcesBusy = false;
            if (!_closing)
            {
                RefreshAnnouncesButton.IsEnabled = true;
                UpdateAnnounceSelection();
                if (_announcesRefreshAgain)
                {
                    var reportStatus = _announcesRefreshReportStatus;
                    _announcesRefreshAgain = _announcesRefreshReportStatus = false;
                    RefreshAnnounces(reportStatus);
                }
                InvalidateVisual();
            }
        }
    });
}
