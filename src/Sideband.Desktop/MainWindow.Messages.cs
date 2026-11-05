using Avalonia.Interactivity;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private bool _messageActionsSmokeVerified;
    private bool _messageRetryRunning;

    private void UpdateRetryAvailability()
    {
        UpdatePaperPrintingAvailability();
        var index = MessageList.SelectedIndex;
        CopyMessageButton.IsEnabled = !_closing && _clipboardWriteTask.IsCompleted && index >= 0 && index < _visibleMessages.Count;
        CopyMessageTelemetryButton.IsEnabled = CopyMessageButton.IsEnabled && _visibleMessages[index].TelemetryHex is not null;
        RetryMessageButton.IsEnabled = !_closing && !_messageRetryRunning && _client.IsConnected &&
            index >= 0 && index < _visibleMessages.Count && SidebandMessageActions.CanRetryText(_visibleMessages[index]);
        CancelMessageButton.IsEnabled = !_closing && index >= 0 && index < _visibleMessages.Count &&
            _client.CanRequestMessageCancellation(_visibleMessages[index].PeerDeliveryHash, _visibleMessages[index].Id);
    }

    private void CancelMessageClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        try
        {
            var message = SelectedMessage();
            await _client.RequestMessageCancellationAsync(message.PeerDeliveryHash, message.Id, _windowCancellation.Token);
            await RefreshMessagesAsync(_windowCancellation.Token);
            SetStatus("Pending message cancellation requested. This is not a recall; a peer may already have received it.");
        }
        catch (Exception ex) { ShowError("Could not request message cancellation: " + ex.Message); }
        finally { UpdateRetryAvailability(); }
    });

    private void RetryMessageClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        if (_messageRetryRunning)
        {
            ShowError("A message text retry is already running.");
            return;
        }
        try
        {
            var message = SelectedMessage();
            if (ConfirmRetryMessageBox.IsChecked != true)
                throw new InvalidOperationException("Confirm retry of only the selected failed message's text first.");
            if (!string.IsNullOrEmpty(MessageBox.Text) || !string.IsNullOrEmpty(AttachmentPathBox.Text) || _recordedAudio is not null)
                throw new InvalidOperationException("Send or clear the current draft and attachment before retrying saved text.");
            if (!SidebandMessageActions.CanRetryText(message))
                throw new InvalidOperationException("Select an outgoing failed non-paper message with nonempty bounded text.");
            _messageRetryRunning = true;
            ConfirmRetryMessageBox.IsChecked = false;
            RetryMessageButton.IsEnabled = false;
            SendButton.IsEnabled = false;
            await _client.RetryMessageTextAsync(message.PeerDeliveryHash, message.Id, _windowCancellation.Token);
            await RefreshMessagesAsync(_windowCancellation.Token);
            SetStatus("Failed text queued as a new message with current settings; old failure replaced. Attachments were not resent.");
        }
        catch (Exception ex) { ShowError("Could not retry message text: " + ex.Message); }
        finally
        {
            _messageRetryRunning = false;
            SendButton.IsEnabled = true;
            UpdateRetryAvailability();
        }
    });

    private void DeleteMessageClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        try { await DeleteSelectedMessageAsync(); }
        catch (Exception ex) { ShowError("Could not delete message: " + ex.Message); }
    });

    private async Task DeleteSelectedMessageAsync()
    {
        var message = SelectedMessage();
        if (ConfirmDeleteMessageBox.IsChecked != true)
            throw new InvalidOperationException("Confirm deletion of the selected local history entry first.");
        ConfirmDeleteMessageBox.IsChecked = false;
        await _client.DeleteMessageAsync(message.PeerDeliveryHash, message.Id, _windowCancellation.Token);
        await RefreshMessagesAsync(_windowCancellation.Token);
        SetStatus("Local message entry removed. Delivery was not cancelled or recalled; shared media files are retained.");
    }

    private async Task RunMessageActionsSmokeAsync()
    {
        try
        {
            var before = await _client.LoadAsync(_windowCancellation.Token);
            var selected = before.Messages.FirstOrDefault()
                ?? throw new InvalidOperationException("Message actions smoke requires saved messages.");
            _selectedContact = selected.PeerDeliveryHash;
            await RefreshMessagesAsync(_windowCancellation.Token);
            MessageList.SelectedIndex = _visibleMessages.ToList().FindIndex(message => message.Id == selected.Id);
            if (MessageDetailsBox.Text?.Contains(selected.Id.ToString(), StringComparison.Ordinal) != true)
                throw new InvalidOperationException("Selected message details were not populated.");
            await CopySelectedMessageTextAsync();
            if (_smokeClipboardText != selected.Content)
                throw new InvalidOperationException("Message copy did not preserve the exact selected raw text in the private observation port.");
            Console.WriteLine("SMOKE_UI_MESSAGE_COPY=private-port-exact-raw");
            if (selected.TelemetryHex is not null)
            {
                await CopySelectedMessageTelemetryAsync();
                if (_smokeClipboardText is not { } copied || copied != SidebandMessageTelemetry.CopyText(selected.TelemetryHex))
                    throw new InvalidOperationException("Selected telemetry copy did not use this message's immutable snapshot.");
                Console.WriteLine("SMOKE_UI_MESSAGE_TELEMETRY_SHA256=" +
                    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(copied))));
            }
            try
            {
                await DeleteSelectedMessageAsync();
                throw new InvalidOperationException("Unconfirmed deletion incorrectly succeeded.");
            }
            catch (InvalidOperationException ex) when (ex.Message == "Confirm deletion of the selected local history entry first.") { }
            if (!SameSavedMessages((await _client.LoadAsync(_windowCancellation.Token)).Messages, before.Messages))
                throw new InvalidOperationException("Unconfirmed deletion changed saved history.");
            ConfirmDeleteMessageBox.IsChecked = true;
            await DeleteSelectedMessageAsync();
            var after = await new SidebandStateStore(_store.DataDirectory).LoadAsync(cancellationToken: _windowCancellation.Token);
            if (!SameSavedMessages(after.Messages, before.Messages.Where(message => message.Id != selected.Id ||
                message.PeerDeliveryHash != selected.PeerDeliveryHash).ToArray()) || !after.Contacts.SequenceEqual(before.Contacts) ||
                _client.IsConnected)
                throw new InvalidOperationException("Message deletion failed to preserve offline history and conversation policies.");
            _messageActionsSmokeVerified = true;
            SetStatus("Message actions verified: confirmed local deletion and unchanged remaining history.");
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("sideband: Message actions smoke failed: " + ex.Message);
            _ = CloseAfterCaptureAsync();
        }
    }

    private static bool SameSavedMessages(IReadOnlyList<SidebandChatMessage> left, IReadOnlyList<SidebandChatMessage> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            (pair.First with { Attachments = null }) == (pair.Second with { Attachments = null }) &&
            (pair.First.Attachments ?? []).SequenceEqual(pair.Second.Attachments ?? []));
}
