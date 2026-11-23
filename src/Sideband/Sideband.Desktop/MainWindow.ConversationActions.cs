using Avalonia.Interactivity;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private bool _conversationActionRunning;
    private bool _conversationActionsSmokeVerified;

    private void UpdateConversationActionAvailability()
    {
        var available = !_closing && !_conversationActionRunning &&
            _state?.Contacts.Any(contact => contact.DeliveryHash == _selectedContact &&
                contact.ConversationKnown && !contact.IsObject) == true;
        ClearConversationButton.IsEnabled = DeleteConversationButton.IsEnabled = available;
    }

    private void ClearConversationClicked(object? sender, RoutedEventArgs e) =>
        TrackOperation(() => RunConversationActionAsync(delete: false));

    private void DeleteConversationClicked(object? sender, RoutedEventArgs e) =>
        TrackOperation(() => RunConversationActionAsync(delete: true));

    private async Task RunConversationActionAsync(bool delete)
    {
        try { await RemoveSelectedConversationHistoryAsync(delete); }
        catch (Exception ex) { ShowError("Could not remove local conversation history: " + ex.Message); }
    }

    private async Task RemoveSelectedConversationHistoryAsync(bool delete)
    {
        if (_conversationActionRunning) throw new InvalidOperationException("A conversation action is already running.");
        var peer = _selectedContact ?? throw new InvalidOperationException("Select a saved conversation first.");
        if (_state?.Contacts.Any(contact => contact.DeliveryHash == peer && contact.ConversationKnown && !contact.IsObject) != true)
            throw new InvalidOperationException("Select a saved conversation first.");
        if (ConfirmConversationActionBox.IsChecked != true)
            throw new InvalidOperationException("Confirm removal of the selected local history/conversation first.");
        if (delete && (!string.IsNullOrEmpty(MessageBox.Text) || !string.IsNullOrEmpty(AttachmentPathBox.Text) || _recordedAudio is not null))
            throw new InvalidOperationException("Send or clear the current draft and attachment before deleting its conversation.");
        ConfirmConversationActionBox.IsChecked = false;
        _conversationActionRunning = true;
        UpdateConversationActionAvailability();
        try
        {
            await _client.ClearConversationAsync(peer, delete, _windowCancellation.Token);
            _state = await _client.LoadAsync(_windowCancellation.Token);
            RefreshContacts();
            await RefreshMessagesAsync(_windowCancellation.Token);
            RequestTelemetryRefresh();
            SetStatus(delete
                ? "Local conversation, history and peer telemetry removed. Shared media retained; delivery was not cancelled or recalled."
                : "Selected local message history cleared. Conversation policies, telemetry and drafts retained; delivery was not cancelled or recalled.");
        }
        finally
        {
            _conversationActionRunning = false;
            UpdateConversationActionAvailability();
        }
    }

    private async Task RunConversationActionsSmokeAsync()
    {
        try
        {
            var before = await _client.LoadAsync(_windowCancellation.Token);
            var selected = before.Contacts.FirstOrDefault(contact => contact.ConversationKnown && !contact.IsObject)
                ?? throw new InvalidOperationException("Conversation actions smoke requires a saved synthetic conversation.");
            if (!before.Messages.Any(message => message.PeerDeliveryHash == selected.DeliveryHash))
                throw new InvalidOperationException("Conversation actions smoke requires synthetic selected history.");
            ContactList.SelectedIndex = _conversationContacts.ToList().FindIndex(contact => contact.DeliveryHash == selected.DeliveryHash);
            await ContactSelectionChangedAsync();
            try
            {
                await RemoveSelectedConversationHistoryAsync(delete: false);
                throw new InvalidOperationException("Unconfirmed history clearing incorrectly succeeded.");
            }
            catch (InvalidOperationException ex) when (ex.Message == "Confirm removal of the selected local history/conversation first.") { }
            MessageBox.Text = "Synthetic unsent draft";
            ConfirmConversationActionBox.IsChecked = true;
            await RemoveSelectedConversationHistoryAsync(delete: false);
            var cleared = await _client.LoadAsync(_windowCancellation.Token);
            if (!SameSavedMessages(cleared.Messages, before.Messages.Where(message => message.PeerDeliveryHash != selected.DeliveryHash).ToArray()) ||
                !cleared.Contacts.SequenceEqual(before.Contacts) || MessageBox.Text != "Synthetic unsent draft")
                throw new InvalidOperationException("History clearing changed unrelated history, policies or draft.");
            ConfirmConversationActionBox.IsChecked = true;
            try
            {
                await RemoveSelectedConversationHistoryAsync(delete: true);
                throw new InvalidOperationException("Conversation deletion incorrectly discarded an unsent draft.");
            }
            catch (InvalidOperationException ex) when (ex.Message == "Send or clear the current draft and attachment before deleting its conversation.") { }
            MessageBox.Text = string.Empty;
            ConfirmConversationActionBox.IsChecked = true;
            await RemoveSelectedConversationHistoryAsync(delete: true);
            var after = await new SidebandStateStore(_store.DataDirectory).LoadAsync(cancellationToken: _windowCancellation.Token);
            if (after.Contacts.Any(contact => contact.DeliveryHash == selected.DeliveryHash) ||
                !SameSavedMessages(after.Messages, cleared.Messages) ||
                !after.Contacts.SequenceEqual(before.Contacts.Where(contact => contact.DeliveryHash != selected.DeliveryHash)) ||
                _client.IsConnected || ConfirmConversationActionBox.IsChecked == true)
                throw new InvalidOperationException("Conversation deletion failed to preserve offline unrelated state.");
            _conversationActionsSmokeVerified = true;
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("sideband: Conversation actions smoke failed: " + ex.Message);
            _ = CloseAfterCaptureAsync();
        }
    }
}
