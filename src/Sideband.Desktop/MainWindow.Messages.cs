using Avalonia.Interactivity;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private bool _messageActionsSmokeVerified;

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
