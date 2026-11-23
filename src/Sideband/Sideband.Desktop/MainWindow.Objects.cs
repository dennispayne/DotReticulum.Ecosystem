using Avalonia.Interactivity;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private IReadOnlyList<SidebandContact> _conversationContacts = [];
    private IReadOnlyList<SidebandContact> _objectContacts = [];
    private bool _objectsUiBusy;

    private void RefreshObjects()
    {
        var previous = ObjectList.SelectedIndex is var index && index >= 0 && index < _objectContacts.Count
            ? _objectContacts[index].DeliveryHash : null;
        _objectContacts = _state?.Contacts.Where(contact => contact.IsObject).ToArray() ?? [];
        ObjectList.ItemsSource = _objectContacts.Select(FormatContact).ToArray();
        var selected = _objectContacts.ToList().FindIndex(contact => contact.DeliveryHash == previous);
        ObjectList.SelectedIndex = selected >= 0 ? selected : _objectContacts.Count > 0 ? 0 : -1;
    }

    private void ClassifyObjectClicked(object? sender, RoutedEventArgs e) => RunObjectOperation(async () =>
    {
        var index = ExpandedContactList.SelectedIndex;
        var contact = _state is not null && index >= 0 && index < _state.Contacts.Count
            ? _state.Contacts[index] : throw new InvalidOperationException("Select an existing contact first.");
        await _client.SetConversationObjectAsync(contact.DeliveryHash, true, _windowCancellation.Token);
        _state = await _client.LoadAsync(_windowCancellation.Token);
        RefreshContacts();
        NavigateTo("Objects");
        ObjectsStatusText.Text = "Object classification saved. Existing history and permissions are unchanged.";
    });

    private void RestoreObjectClicked(object? sender, RoutedEventArgs e) => RunObjectOperation(async () =>
    {
        var contact = SelectedObject();
        await _client.SetConversationObjectAsync(contact.DeliveryHash, false, _windowCancellation.Token);
        _state = await _client.LoadAsync(_windowCancellation.Token);
        _selectedContact = contact.DeliveryHash;
        RefreshContacts();
        ObjectsStatusText.Text = "Conversation classification restored. Existing history and permissions are unchanged.";
    });

    private void InspectObjectClicked(object? sender, RoutedEventArgs e) => RunObjectOperation(async () =>
    {
        var contact = SelectedObject();
        var reports = await _client.GetTelemetryAsync(cancellationToken: _windowCancellation.Token);
        var report = reports.FirstOrDefault(value => value.SourceHash == contact.DeliveryHash)
            ?? throw new InvalidOperationException("No visible report for this object in the newest bounded telemetry query. Display-trust policy remains enforced.");
        TelemetryReportsList.SelectedIndex = -1;
        _telemetryPreview = "OBJECT REPORTED TELEMETRY (not authenticated physical position)\n" +
            contact.DeliveryHash + "\n" + FormatTelemetry(report.Report);
        NavigateTo("Telemetry");
        TelemetryPeerBox.Text = contact.DeliveryHash;
        TelemetryDetailsText.Text = _telemetryPreview;
    });

    private SidebandContact SelectedObject() => ObjectList.SelectedIndex is var index &&
        index >= 0 && index < _objectContacts.Count ? _objectContacts[index] :
        throw new InvalidOperationException("Select an object first.");

    private void RunObjectOperation(Func<Task> operation) => TrackOperation(async () =>
    {
        if (_objectsUiBusy) { ShowError("Another object operation is pending."); return; }
        _objectsUiBusy = true;
        ClassifyObjectButton.IsEnabled = InspectObjectButton.IsEnabled = RestoreObjectButton.IsEnabled = false;
        ObjectsStatusText.Text = "Object operation pending.";
        try { await operation(); }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_closing)
            {
                ObjectsStatusText.Text = "Object operation failed: " + exception.Message;
                ShowError(ObjectsStatusText.Text);
            }
            else System.Diagnostics.Trace.TraceError("Object operation failed during close: {0}", exception);
        }
        finally
        {
            _objectsUiBusy = false;
            if (!_closing)
                ClassifyObjectButton.IsEnabled = InspectObjectButton.IsEnabled = RestoreObjectButton.IsEnabled = true;
        }
    });
}
