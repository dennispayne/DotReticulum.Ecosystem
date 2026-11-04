using Avalonia.Controls;
using Avalonia.Interactivity;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _remoteCommandsCancellation;
    private bool _remoteSmokeVerified;
    private bool _remoteSettingsLoading;

    private void LoadRemoteSettings() => RunRemoteOperation(async () =>
    {
        if (_remoteSettingsLoading) return;
        _remoteSettingsLoading = true;
        var previousPeer = RemoteCommandPeerBox.Text;
        var selectedPeer = _selectedContact;
        SaveRemoteAuthorizationButton.IsEnabled = false;
        RemoteRequestsAnyoneBox.IsEnabled = RemoteRequestsTrustedBox.IsEnabled = RemoteIgnoreUnknownBox.IsEnabled = false;
        try
        {
            var authorization = await _client.GetCommandAuthorizationAsync(_windowCancellation.Token);
            if (_closing) return;
            RemoteRequestsAnyoneBox.IsChecked = authorization.AllowRequestsFromAnyone;
            RemoteRequestsTrustedBox.IsChecked = authorization.AllowRequestsFromTrusted;
            RemoteIgnoreUnknownBox.IsChecked = authorization.IgnoreUnknown;
            if (selectedPeer is not null && RemoteCommandPeerBox.Text == previousPeer)
                RemoteCommandPeerBox.Text = selectedPeer;
            LoadRemoteConversation();
            RemoteStatusText.Text = "Request policy loaded. Queued commands are not delivery or remote execution.";
        }
        finally
        {
            _remoteSettingsLoading = false;
            if (!_closing)
            {
                SaveRemoteAuthorizationButton.IsEnabled = true;
                RemoteRequestsAnyoneBox.IsEnabled = RemoteRequestsTrustedBox.IsEnabled = RemoteIgnoreUnknownBox.IsEnabled = true;
            }
        }
    });

    private void RemotePeerChanged(object? sender, TextChangedEventArgs e) => LoadRemoteConversation();

    private void LoadRemoteConversation()
    {
        if (_state is null) return;
        var peer = RemoteCommandPeerBox.Text?.Trim().ToLowerInvariant();
        var contact = _state.Contacts.FirstOrDefault(value => value.DeliveryHash == peer);
        RemoteConversationRequestsBox.IsChecked = contact?.AllowTelemetryRequests == true;
        RemoteConversationPolicyText.Text = contact is null
            ? "No saved conversation. Add a contact before setting its permission."
            : $"{contact.DisplayName}: {(contact.Trusted ? "trusted" : "not trusted")}. " +
                (contact.ConversationKnown ? "" : "Discovered contact only; saving permission explicitly starts a conversation. ") +
                "Trusted-only request mode overrides individual permission; change trust in Conversations.";
        UpdateRemoteHistory();
    }

    private void UpdateRemoteHistory()
    {
        if (_state is null || !RemotePage.IsVisible) return;
        var peer = RemoteCommandPeerBox.Text?.Trim().ToLowerInvariant();
        RemoteHistoryList.ItemsSource = _state.Messages.Where(message => message.PeerDeliveryHash == peer)
            .OrderBy(message => message.Timestamp).TakeLast(50).Select(message =>
                $"{message.Timestamp.LocalDateTime:g} {(message.Incoming ? "Received" : message.DeliveryState)} ({message.DeliveryMethod})\n{message.Content}")
            .ToArray();
    }

    private void SaveRemoteAuthorizationClicked(object? sender, RoutedEventArgs e) => RunRemoteOperation(async () =>
    {
        await _client.ConfigureCommandAuthorizationAsync(new(
            RemoteRequestsAnyoneBox.IsChecked == true,
            RemoteRequestsTrustedBox.IsChecked == true,
            RemoteIgnoreUnknownBox.IsChecked == true), _windowCancellation.Token);
        RemoteStatusText.Text = "Request policy saved. Global and conversation permissions are shared with telemetry.";
    });

    private void SaveRemoteConversationClicked(object? sender, RoutedEventArgs e) => RunRemoteOperation(async () =>
    {
        var peer = RemotePeer();
        var state = await _client.LoadAsync(_windowCancellation.Token);
        var contact = state.Contacts.FirstOrDefault(value => value.DeliveryHash == peer)
            ?? throw new InvalidOperationException("Add this LXMF address as a contact before setting conversation permissions.");
        await _client.SetConversationTelemetryAsync(peer, contact.SendTelemetry,
            RemoteConversationRequestsBox.IsChecked == true, _windowCancellation.Token);
        _state = await _client.LoadAsync(_windowCancellation.Token);
        LoadRemoteConversation();
        RemoteStatusText.Text = "Conversation request permission saved without changing trust or telemetry sending.";
    });

    private void SendRemoteCommandClicked(object? sender, RoutedEventArgs e) => RunRemoteOperation(async () =>
    {
        var peer = RemotePeer();
        if (_remoteCommandsCancellation is null || _remoteCommandsCancellation.IsCancellationRequested)
        {
            _remoteCommandsCancellation?.Dispose();
            _remoteCommandsCancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        }
        var result = await _client.SendCommandAsync(peer, RemoteCommandBox.Text ?? "",
            RemotePropagatedBox.IsChecked == true, _remoteCommandsCancellation.Token);
        if (!result.Queued)
            throw new InvalidOperationException(result.Status switch
            {
                SidebandCommandQueueStatus.Empty => "Enter a nonempty command, such as ping or echo TEXT.",
                SidebandCommandQueueStatus.NotConnected => "Connect to Reticulum before sending commands.",
                SidebandCommandQueueStatus.NoAddress => "Choose a remote LXMF delivery address other than your own.",
                SidebandCommandQueueStatus.QueueFull => "The bounded outbound queue is full. Wait or cancel pending commands.",
                _ => "The command was not admitted."
            });
        RemoteCommandBox.Text = "";
        RemoteStatusText.Text = $"Queued command {result.OperationId:N}. Admission is not delivery or remote execution; inspect conversation replies and delivery state.";
    });

    private void CancelRemoteCommandsClicked(object? sender, RoutedEventArgs e) => RunRemoteOperation(async () =>
    {
        if (_remoteCommandsCancellation is not null) await _remoteCommandsCancellation.CancelAsync();
        RemoteStatusText.Text = "Cancellation requested for commands queued by this window. Already delivered commands cannot be recalled.";
    });

    private string RemotePeer() => SidebandContact.TryNormalizeDeliveryHash(RemoteCommandPeerBox.Text, out var peer)
        ? peer : throw new ArgumentException("Enter a 32-character hexadecimal LXMF delivery address.");

    private async Task RunRemoteSmokeAsync(string peer)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await _client.AddContactAsync("Synthetic remote acceptance", peer, deadline.Token);
            _selectedContact = peer;
            await ConnectAsync();
            if (!_client.IsConnected) throw new InvalidOperationException("Remote acceptance could not connect.");
            NavigateTo("Remote");
            await WaitAsync(() => SaveRemoteAuthorizationButton.IsEnabled && RemoteCommandPeerBox.Text == peer);
            RemoteRequestsAnyoneBox.IsChecked = false;
            RemoteRequestsTrustedBox.IsChecked = false;
            RemoteIgnoreUnknownBox.IsChecked = true;
            SaveRemoteAuthorizationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => RemoteStatusText.Text?.StartsWith("Request policy saved.", StringComparison.Ordinal) == true);
            RemoteConversationRequestsBox.IsChecked = true;
            SaveRemoteConversationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => RemoteStatusText.Text?.StartsWith("Conversation request permission saved", StringComparison.Ordinal) == true);
            foreach (var (command, reply) in new[] { ("ping", "Ping reply"), ("echo echo repeated", "Echo reply: repeated") })
                await ExchangeAsync(command, reply);
            RemoteCommandPeerBox.Text = new string('a', 32);
            RemoteCommandBox.Text = "ping";
            SendRemoteCommandButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => RemoteCommandBox.Text == "");
            CancelRemoteCommandsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => RemoteStatusText.Text?.StartsWith("Cancellation requested", StringComparison.Ordinal) == true);
            RemoteCommandPeerBox.Text = peer;
            await ExchangeAsync("echo after cancellation", "Echo reply: after cancellation");
            _remoteSmokeVerified = true;
            InvalidateVisual();
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ShowError("Remote acceptance failed: " + exception.Message);
            Console.Error.WriteLine("Remote acceptance failed: " + exception.Message);
        }

        async Task ExchangeAsync(string command, string reply)
        {
            RemoteCommandBox.Text = command;
            SendRemoteCommandButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => RemoteCommandBox.Text == "" &&
                _state!.Messages.Any(message => message.PeerDeliveryHash == peer && message.Incoming && message.Content == reply) &&
                RemoteHistoryList.ItemsSource?.Cast<string>().Any(item => item.Contains(reply, StringComparison.Ordinal)) == true);
        }

        async Task WaitAsync(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(10, deadline.Token);
        }
    }

    private void RunRemoteOperation(Func<Task> operation) => TrackOperation(async () =>
    {
        RemoteStatusText.Text = "Remote operation pending.";
        try { await operation(); }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested) { }
        catch (OperationCanceledException) when (_remoteCommandsCancellation?.IsCancellationRequested == true)
        { if (!_closing) RemoteStatusText.Text = "Command cancellation requested."; }
        catch (Exception exception)
        {
            if (!_closing)
            {
                var message = "Remote command failed: " + exception.Message;
                RemoteStatusText.Text = message;
                ShowError(message);
            }
            else System.Diagnostics.Trace.TraceError("Remote command failed during close: {0}", exception);
        }
    });
}
