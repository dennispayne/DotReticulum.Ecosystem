using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Sideband;
using System.Collections.Generic;
using LXST;
using LXST.Devices;

namespace Sideband.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly SidebandStateStore _store;
    private readonly SidebandClient _client;
    private readonly string? _commandLineRnsConfig;
    private readonly CancellationTokenSource _windowCancellation = new();
    private readonly object _operationGate = new();
    private readonly HashSet<Task> _pendingOperations = [];
    private readonly HashSet<string> _markingRead = [];
    private readonly TaskCompletionSource _closedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _smokeUi;
    private Task _initializationTask = Task.CompletedTask;
    private int _smokeRenderCount;
    private int _smokeStatusChanged;
    private int _smokeActionInvoked;
    private string? _selectedContact;
    private SidebandState? _state;
    private IReadOnlyList<SidebandChatMessage> _visibleMessages = [];
    private bool _navigationVisible = true;
    private bool _closing;
    private bool _allowClose;
    private Task? _closeTask;
    private readonly IAudioDeviceBackend? _mediaBackend;
    private CancellationTokenSource? _audioCancellation;
    private SidebandEncodedAudio? _recordedAudio;
    private Avalonia.Media.Imaging.Bitmap? _previewBitmap;
    private long _mediaSelectionVersion;

    internal static bool SmokeUiSucceeded { get; private set; }

    public MainWindow() : this(Program.DataDirectory, null, smokeUi: false) { }

    public MainWindow(string dataDirectory, bool smokeUi = false) : this(dataDirectory, null, smokeUi) { }

    public MainWindow(string dataDirectory, string? reticulumConfig, bool smokeUi = false, IAudioDeviceBackend? mediaBackend = null)
    {
        InitializeComponent();
        _commandLineRnsConfig = string.IsNullOrWhiteSpace(reticulumConfig) ? null : Path.GetFullPath(reticulumConfig);
        _smokeUi = smokeUi;
        _mediaBackend = mediaBackend;
        _store = new SidebandStateStore(dataDirectory);
        _client = new SidebandClient(_store);
        _client.StateChanged += OnStateChanged;
        _client.ErrorReported += OnErrorReported;
        ThemeSelector.ItemsSource = new[] { "Dark", "Light" };
        AttachmentKindSelector.ItemsSource = new[] { "File", "Image", "Audio" };
        AttachmentKindSelector.SelectedIndex = 0;
        ImageQualitySelector.ItemsSource = Enum.GetValues<SidebandImageQuality>();
        ImageQualitySelector.SelectedItem = SidebandImageQuality.Medium;
        Closed += OnClosed;
        Closing += OnClosing;
        Opened += OnOpened;
        Activated += (_, _) => MarkVisibleConversationRead();
        NavigateTo("Conversations");
        UpdateConnectivityView();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!_smokeUi) return;

        var renderCount = Interlocked.Increment(ref _smokeRenderCount);
        if (renderCount == 1)
        {
            Console.WriteLine("SMOKE_UI_RENDERED_INITIAL=1");
            Dispatcher.UIThread.Post(InvalidateVisual);
        }
        else if (Volatile.Read(ref _smokeStatusChanged) != 0 && !SmokeUiSucceeded)
        {
            if (_state is null || !_initializationTask.IsCompleted || Volatile.Read(ref _smokeActionInvoked) == 0) return;
            var hasImage = _state.Messages.Any(message => message.Image is not null);
            if (hasImage && MediaPreview.Source is null) return;
            SmokeUiSucceeded = true;
            if (hasImage) Console.WriteLine("SMOKE_UI_RENDERED_MEDIA=1");
            Console.WriteLine("SMOKE_UI_RENDERED_STATUS=" + StatusText.Text);
            _ = CloseAfterCaptureAsync();
        }
    }

    internal void NavigateTo(string page)
    {
        ConversationsPage.IsVisible = page == "Conversations";
        ContactsPage.IsVisible = page == "Contacts";
        ConnectivityPage.IsVisible = page == "Connectivity";
        IdentityPage.IsVisible = page == "Identity";
        PreferencesPage.IsVisible = page == "Preferences";
        UtilitiesPage.IsVisible = page == "Utilities";
        GuidePage.IsVisible = page == "Guide";
        PageTitle.Text = page switch
        {
            "Identity" => "Backup & Keys",
            _ => page,
        };
        if (page == "Connectivity") UpdateConnectivityView();
        if (page == "Contacts") RefreshExpandedContacts();
        if (page == "Conversations") MarkVisibleConversationRead();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        _initializationTask = InitializeAsync();
        await _initializationTask;
    }

    private async Task InitializeAsync()
    {
        try
        {
            _state = await _store.LoadAsync(_commandLineRnsConfig, _windowCancellation.Token);
            if (_commandLineRnsConfig is not null)
            {
                await _store.SetReticulumConfigDirectoryAsync(_commandLineRnsConfig, _windowCancellation.Token);
                _state = await _client.LoadAsync(_windowCancellation.Token);
            }

            if (_closing) return;
            ReticulumConfigBox.Text = _state.ReticulumConfigDirectory;
            DisplayNameBox.Text = _state.DisplayName;
            ThemeSelector.SelectedItem = _state.Theme;
            PropagationNodeBox.Text = _state.PropagationNode;
            PropagationDefaultBox.IsChecked = _state.PropagationByDefault;
            PropagationFallbackBox.IsChecked = _state.TryPropagationOnFail;
            PeriodicSyncBox.IsChecked = _state.PeriodicSynchronization;
            SyncIntervalBox.Text = _state.SynchronizationIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ApplyTheme(_state.Theme);
            RefreshContacts();
            RefreshExpandedContacts();
            SetStatus($"Offline · local data: {_store.DataDirectory}");
            UpdateConnectivityView();
            if (_smokeUi)
            {
                Console.WriteLine("SMOKE_UI_OPENED=Sideband");
                Dispatcher.UIThread.Post(() =>
                {
                    if (_closing) return;
                    SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Volatile.Write(ref _smokeActionInvoked, 1);
                    InvalidateVisual();
                });
            }
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested) { }
        catch (Exception ex) { ShowError("Could not load Sideband data: " + ex.Message); }
    }

    private void NavigateClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page }) NavigateTo(page);
    }

    private void ToggleNavigation(object? sender, RoutedEventArgs e)
    {
        _navigationVisible = !_navigationVisible;
        NavigationPanel.IsVisible = _navigationVisible;
    }

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();

    private void OpenConnectivityClicked(object? sender, RoutedEventArgs e) => NavigateTo("Connectivity");

    private void ConnectClicked(object? sender, RoutedEventArgs e) => TrackOperation(ConnectAsync);

    private async Task ConnectAsync()
    {
        ConnectButton.IsEnabled = false;
        SetStatus("Connecting to Reticulum…");
        ConnectivityStatusText.Text = "Opening the Reticulum node and resolving shared-instance ownership…";
        try
        {
            await _client.ConnectAsync(ReticulumConfigBox.Text, _windowCancellation.Token);
            _state = await _client.LoadAsync(_windowCancellation.Token);
            SetStatus("Connected · LXMF address " + (_state.OwnDeliveryHash ?? "available"));
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ConnectButton.IsEnabled = true;
            SetStatus("Connection failed · " + ex.Message);
            ConnectivityStatusText.Text = "Could not connect to Reticulum. Check the selected configuration and use rnstatus with that configuration to inspect the running node and interfaces.";
        }
        UpdateConnectivityView();
    }

    private void DisconnectClicked(object? sender, RoutedEventArgs e) => TrackOperation(DisconnectAsync);

    private async Task DisconnectAsync()
    {
        DisconnectButton.IsEnabled = false;
        try
        {
            await _client.DisconnectAsync();
            SetStatus("Disconnected from Reticulum.");
        }
        catch (Exception ex) { ShowError("Disconnect failed: " + ex.Message); }
        UpdateConnectivityView();
    }

    private void SaveConfigClicked(object? sender, RoutedEventArgs e) => TrackOperation(SaveConfigAsync);

    private async Task SaveConfigAsync()
    {
        try
        {
            var value = ReticulumConfigBox.Text;
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Select a Reticulum configuration directory or file.");
            await _store.SetReticulumConfigDirectoryAsync(value);
            _state = await _client.LoadAsync(_windowCancellation.Token);
            SetStatus("Saved Reticulum configuration path. This does not connect or modify the Reticulum files.");
            UpdateConnectivityView();
        }
        catch (Exception ex) { ShowError("Could not save Reticulum configuration path: " + ex.Message); }
    }

    private void SavePreferencesClicked(object? sender, RoutedEventArgs e) => TrackOperation(SavePreferencesAsync);

    private void SavePropagationClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        try
        {
            await _client.ConfigurePropagationAsync(PropagationNodeBox.Text, PropagationDefaultBox.IsChecked == true, _windowCancellation.Token);
            await _client.ConfigurePropagationFallbackAsync(PropagationFallbackBox.IsChecked == true, _windowCancellation.Token);
            SyncStatusText.Text = "Propagation settings saved. Sent means stored at the node, not recipient delivery.";
        }
        catch (Exception ex) { ShowError("Could not save propagation settings: " + ex.Message); }
    });

    private void SyncMessagesClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        SyncMessagesButton.IsEnabled = false;
        SyncStatusText.Text = "Fetching messages from the configured propagation node...";
        try
        {
            var count = await _client.SynchronizeAsync(cancellationToken: _windowCancellation.Token);
            SyncStatusText.Text = $"Fetch complete: {count} new messages accepted. Conversation persistence continues on the receive worker.";
        }
        catch (OperationCanceledException) { SyncStatusText.Text = "Message fetch cancelled."; }
        catch (Exception ex) { SyncStatusText.Text = "Message fetch failed: " + ex.Message; ShowError(SyncStatusText.Text); }
        finally { SyncMessagesButton.IsEnabled = _client.IsConnected; }
    });

    private void CancelSyncClicked(object? sender, RoutedEventArgs e) => _client.CancelSynchronization();

    private void SaveSyncScheduleClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        try
        {
            if (!int.TryParse(SyncIntervalBox.Text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                throw new ArgumentException("Enter a positive whole number of seconds for the synchronization interval.");
            await _client.ConfigurePeriodicSynchronizationAsync(PeriodicSyncBox.IsChecked == true, seconds, _windowCancellation.Token);
            SyncStatusText.Text = "Synchronization schedule saved. Checks run every 60 seconds while connected.";
        }
        catch (Exception ex) { ShowError("Could not save synchronization schedule: " + ex.Message); }
    });

    private void ImportSettingsClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        try
        {
            if (_client.IsConnected) throw new InvalidOperationException("Disconnect before importing settings.");
            var path = SettingsExchangePathBox.Text;
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var unsupported = await _store.ImportSettingsAsync(path, _windowCancellation.Token);
            _state = await _store.LoadAsync(cancellationToken: _windowCancellation.Token);
            DisplayNameBox.Text = _state.DisplayName;
            ThemeSelector.SelectedItem = _state.Theme;
            PropagationNodeBox.Text = _state.PropagationNode;
            PropagationDefaultBox.IsChecked = _state.PropagationByDefault;
            PropagationFallbackBox.IsChecked = _state.TryPropagationOnFail;
            PeriodicSyncBox.IsChecked = _state.PeriodicSynchronization;
            SyncIntervalBox.Text = _state.SynchronizationIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ApplyTheme(_state.Theme);
            PreferencesStatusText.Text = unsupported.Count == 0 ? "Settings imported." :
                "Settings imported. Preserved but not applied: " + string.Join(", ", unsupported);
        }
        catch (Exception ex) { ShowError("Settings import failed: " + ex.Message); }
    });

    private void ExportSettingsClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        try
        {
            var path = SettingsExchangePathBox.Text;
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            await _store.ExportSettingsAsync(path, _windowCancellation.Token);
            PreferencesStatusText.Text = "Settings exported; identity and conversations are not included.";
        }
        catch (Exception ex) { ShowError("Settings export failed: " + ex.Message); }
    });

    private async Task SavePreferencesAsync()
    {
        try
        {
            var name = DisplayNameBox.Text ?? string.Empty;
            var theme = ThemeSelector.SelectedItem as string;
            if (theme is null) throw new ArgumentException("Choose Dark or Light appearance.");
            await _store.SetPreferencesAsync(name, theme);
            _state = await _client.LoadAsync(_windowCancellation.Token);
            ApplyTheme(theme);
            var message = _client.IsConnected
                ? "Preferences saved. The announced display name takes effect next time Sideband connects."
                : "Preferences saved.";
            PreferencesStatusText.Text = message;
            SetStatus(message);
        }
        catch (Exception ex)
        {
            PreferencesStatusText.Text = ex.Message;
            ShowError("Could not save preferences: " + ex.Message);
        }
    }

    private void ApplyTheme(string theme)
    {
        if (Application.Current is App app) app.SetTheme(theme);
    }

    private void AddContactClicked(object? sender, RoutedEventArgs e) => TrackOperation(AddContactAsync);

    private async Task AddContactAsync()
    {
        try
        {
            var name = ContactNameBox.Text ?? string.Empty;
            var hash = ContactHashBox.Text ?? string.Empty;
            await _client.AddContactAsync(name, hash);
            ContactNameBox.Text = string.Empty;
            ContactHashBox.Text = string.Empty;
            SetStatus($"Added {name.Trim()}");
        }
        catch (Exception ex) { ShowError("Could not add contact: " + ex.Message); }
    }

    private void SendClicked(object? sender, RoutedEventArgs e) => TrackOperation(SendAsync);

    private async Task SendAsync()
    {
        if (_selectedContact is null)
        {
            ShowError("Select a contact before sending.");
            return;
        }

        var content = MessageBox.Text ?? string.Empty;
        SendButton.IsEnabled = false;
        try
        {
            if (_recordedAudio is not null)
            {
                if (!string.IsNullOrWhiteSpace(AttachmentPathBox.Text))
                    throw new InvalidOperationException("Discard the recording before attaching another file.");
                await _client.SendAudioAsync(_selectedContact, content, _recordedAudio, _windowCancellation.Token);
            }
            else if (string.IsNullOrWhiteSpace(AttachmentPathBox.Text))
                await _client.SendTextAsync(_selectedContact, content, _windowCancellation.Token);
            else if (AttachmentKindSelector.SelectedItem as string == "Image")
                await _client.SendImageAsync(_selectedContact, content, AttachmentPathBox.Text,
                    ImageQualitySelector.SelectedItem is SidebandImageQuality quality ? quality : SidebandImageQuality.Medium,
                    _windowCancellation.Token);
            else if (AttachmentKindSelector.SelectedItem as string == "Audio")
                await _client.SendAudioFileAsync(_selectedContact, content, AttachmentPathBox.Text, _windowCancellation.Token);
            else
                await _client.SendFileAsync(_selectedContact, content, AttachmentPathBox.Text, _windowCancellation.Token);
            ClearRecording();
            MessageBox.Text = string.Empty;
            AttachmentPathBox.Text = string.Empty;
            await RefreshMessagesAsync();
            SetStatus("Message queued for LXMF delivery.");
        }
        catch (Exception ex) { ShowError("Message was not queued: " + ex.Message); }
        finally { SendButton.IsEnabled = true; }
    }

    private void SaveAttachmentClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        try
        {
            var index = MessageList.SelectedIndex;
            if (_selectedContact is null || index < 0 || index >= _visibleMessages.Count)
                throw new InvalidOperationException("Select a message with an attachment.");
            var path = AttachmentExportPathBox.Text;
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            await _client.ExportAttachmentAsync(_selectedContact, _visibleMessages[index].Id, 0, path, _windowCancellation.Token);
            SetStatus("Attachment saved. Received files are never opened automatically.");
        }
        catch (Exception ex) { ShowError("Could not save attachment: " + ex.Message); }
    });

    private void SaveImageClicked(object? sender, RoutedEventArgs e) => ExportMedia(audio: false);
    private void SaveAudioClicked(object? sender, RoutedEventArgs e) => ExportMedia(audio: true);

    private void ExportMedia(bool audio) => TrackOperation(async () =>
    {
        try
        {
            var message = SelectedMessage();
            var path = AttachmentExportPathBox.Text;
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            await _client.ExportMediaAsync(message.PeerDeliveryHash, message.Id, audio, path, _windowCancellation.Token);
            SetStatus("Media exported without opening it.");
        }
        catch (Exception ex) { ShowError("Could not export media: " + ex.Message); }
    });

    private SidebandChatMessage SelectedMessage()
    {
        var index = MessageList.SelectedIndex;
        if (_selectedContact is null || index < 0 || index >= _visibleMessages.Count)
            throw new InvalidOperationException("Select a conversation message first.");
        return _visibleMessages[index];
    }

    private void MessageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var version = ++_mediaSelectionVersion;
        MediaPreview.Source = null;
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        MediaPreview.IsVisible = false;
        MediaStatusText.Text = string.Empty;
        if (MessageList.SelectedIndex < 0 || MessageList.SelectedIndex >= _visibleMessages.Count) return;
        var message = SelectedMessage();
        MediaStatusText.Text = message.Audio is { } audio ? $"Audio mode {audio.Mode}: {audio.Data.Length} bytes. Playback is explicit." : string.Empty;
        if (message.Image is null) return;
        TrackOperation(async () =>
        {
            try
            {
                var bytes = await _client.LoadImageAsync(message.PeerDeliveryHash, message.Id, _windowCancellation.Token);
                if (_closing || version != _mediaSelectionVersion) return;
                using var input = new MemoryStream(bytes, writable: false);
                _previewBitmap = new Avalonia.Media.Imaging.Bitmap(input);
                MediaPreview.Source = _previewBitmap;
                MediaPreview.IsVisible = true;
            }
            catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested) { }
            catch (Exception ex) { ShowError("Could not display image: " + ex.Message); }
        });
    }

    private IAudioDeviceBackend MediaBackend() => _mediaBackend ?? (OperatingSystem.IsWindows()
        ? new WindowsWaveAudioBackend()
        : OperatingSystem.IsLinux() ? new PulseAudioDeviceBackend()
        : throw new PlatformNotSupportedException("Sideband audio devices are currently implemented for Windows and Linux."));

    private void PlayAudioClicked(object? sender, RoutedEventArgs e) => RunAudioOperation(async token =>
    {
        var message = SelectedMessage();
        var frame = await _client.LoadAudioAsync(message.PeerDeliveryHash, message.Id, token);
        await SidebandMediaDevices.PlayAsync(MediaBackend(), null, frame, token);
        MediaStatusText.Text = "Audio playback complete.";
    });

    private void RecordAudioClicked(object? sender, RoutedEventArgs e) => RunAudioOperation(async token =>
    {
        RecordingStatusText.Text = "Recording for up to 10 seconds. Stop audio cancels recording.";
        var frame = await SidebandMediaDevices.RecordAsync(MediaBackend(), null, TimeSpan.FromSeconds(10), token);
        var encoded = await SidebandMediaCodec.EncodeOggOpusAsync(frame, token);
        ClearRecording();
        _recordedAudio = encoded;
        AttachmentPathBox.Text = string.Empty;
        RecordingStatusText.Text = "Recording ready. Send securely queues it; discard removes it.";
    });

    private void RunAudioOperation(Func<CancellationToken, Task> operation) => TrackOperation(async () =>
    {
        if (_audioCancellation is not null) { ShowError("Stop the current audio operation first."); return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        _audioCancellation = cancellation;
        StopAudioButton.IsEnabled = true;
        PlayAudioButton.IsEnabled = RecordAudioButton.IsEnabled = SendButton.IsEnabled = false;
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            MediaStatusText.Text = "Audio operation cancelled.";
            RecordingStatusText.Text = _recordedAudio is null ? string.Empty : "Previous recording retained.";
        }
        catch (Exception ex) { ShowError("Audio operation failed: " + ex.Message); }
        finally
        {
            _audioCancellation = null;
            StopAudioButton.IsEnabled = false;
            PlayAudioButton.IsEnabled = RecordAudioButton.IsEnabled = SendButton.IsEnabled = true;
        }
    });

    private void StopAudioClicked(object? sender, RoutedEventArgs e) => _audioCancellation?.Cancel();
    private void ClearRecordingClicked(object? sender, RoutedEventArgs e) => ClearRecording();

    private void ClearRecording()
    {
        if (_recordedAudio is { } recorded)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(recorded.EncodedBytes);
        _recordedAudio = null;
        RecordingStatusText.Text = string.Empty;
    }

    private void ContactSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        TrackOperation(ContactSelectionChangedAsync);

    private async Task ContactSelectionChangedAsync()
    {
        if (ContactList.SelectedIndex < 0 || _state is null || ContactList.SelectedIndex >= _state.Contacts.Count) return;
        _selectedContact = _state.Contacts[ContactList.SelectedIndex].DeliveryHash;
        ConversationPeerText.Text = _state.Contacts[ContactList.SelectedIndex].DisplayName + " · " + _selectedContact;
        TrustConversationBox.IsChecked = _state.Contacts[ContactList.SelectedIndex].Trusted;
        await RefreshMessagesAsync(_windowCancellation.Token);
    }

    private void SaveConversationTrustClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        try
        {
            var peer = _selectedContact ?? throw new InvalidOperationException("Select a conversation before changing trust.");
            await _client.SetConversationTrustAsync(peer, TrustConversationBox.IsChecked == true, _windowCancellation.Token);
            SetStatus("Conversation trust saved. Trusted outgoing messages request a signed reply ticket.");
        }
        catch (Exception ex) { ShowError("Could not save conversation trust: " + ex.Message); }
    });

    private void MarkConversationUnreadClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        try
        {
            var peer = _selectedContact ?? throw new InvalidOperationException("Select a conversation before marking it unread.");
            NavigateTo("Contacts");
            await _client.SetConversationReadAsync(peer, false, _windowCancellation.Token);
            SetStatus("Conversation marked unread.");
        }
        catch (Exception ex) { ShowError("Could not mark the conversation unread: " + ex.Message); }
    });

    private void MarkVisibleConversationRead()
    {
        if (_closing || !ConversationsPage.IsVisible || !IsActive || _selectedContact is not { } peer ||
            _state?.Contacts.Any(contact => contact.DeliveryHash == peer && contact.Unread) != true ||
            !_markingRead.Add(peer)) return;
        TrackOperation(async () =>
        {
            try { await _client.SetConversationReadAsync(peer, true, _windowCancellation.Token); }
            catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested) { }
            catch (Exception ex) { ShowError("Could not mark the conversation read: " + ex.Message); }
            finally { _markingRead.Remove(peer); }
        });
    }

    private void OnStateChanged(SidebandState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing) return;
            var selected = _selectedContact;
            _state = state;
            RefreshContacts();
            RefreshExpandedContacts();
            if (selected is not null) _selectedContact = selected;
            if (_selectedContact is not null) TrackOperation(() => RefreshMessagesAsync(_windowCancellation.Token));
            if (_client.IsConnected)
                SetStatus($"Connected · LXMF address {state.OwnDeliveryHash}");
            UpdateConnectivityView();
        });
    }

    private void OnErrorReported(string message) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closing) ShowError(message);
    });

    private void RefreshContacts()
    {
        if (_state is null) return;
        var oldSelection = _selectedContact;
        ContactList.ItemsSource = _state.Contacts
            .Select(FormatContact)
            .ToArray();
        var selectedIndex = _state.Contacts.ToList().FindIndex(contact => contact.DeliveryHash == oldSelection);
        if (selectedIndex >= 0) ContactList.SelectedIndex = selectedIndex;
        else if (_state.Contacts.Count > 0) ContactList.SelectedIndex = 0;
        RefreshExpandedContacts();
    }

    private void RefreshExpandedContacts()
    {
        if (_state is null) return;
        ExpandedContactList.ItemsSource = _state.Contacts
            .Select(FormatContact)
            .ToArray();
    }

    private static string FormatContact(SidebandContact contact) =>
        $"{(contact.Unread ? "[Unread] " : string.Empty)}{contact.DisplayName}  ·  {contact.DeliveryHash}{(contact.Trusted ? "  [Trusted]" : string.Empty)}";

    private async Task RefreshMessagesAsync(CancellationToken cancellationToken = default)
    {
        if (_selectedContact is not { } peer) return;
        try
        {
            var messages = await _client.GetMessagesAsync(peer, cancellationToken);
            if (_selectedContact != peer || _closing) return;
            var selectedId = MessageList.SelectedIndex >= 0 && MessageList.SelectedIndex < _visibleMessages.Count
                ? _visibleMessages[MessageList.SelectedIndex].Id : (Guid?)null;
            _visibleMessages = messages;
            MessageList.ItemsSource = messages.Select(message =>
                $"{message.Timestamp.LocalDateTime:g}  {(message.Incoming ? "Received" : message.DeliveryState)} ({message.DeliveryMethod})\n{message.Content}" +
                (message.Attachments is { Count: > 0 } files ? "\nFiles: " + string.Join(", ", files.Select(file => $"{file.Name} ({file.Length} bytes)")) : string.Empty) +
                (message.Image is { } image ? $"\nImage: {image.Format} ({image.Data.Length} bytes)" : string.Empty) +
                (message.Audio is { } audio ? $"\nAudio: mode {audio.Mode} ({audio.Data.Length} bytes)" : string.Empty))
                .ToArray();
            if (selectedId is not null) MessageList.SelectedIndex = messages.ToList().FindIndex(message => message.Id == selectedId);
            else if (_smokeUi)
            {
                var imageIndex = messages.ToList().FindIndex(message => message.Image is not null);
                if (imageIndex >= 0) MessageList.SelectedIndex = imageIndex;
            }
            if (messages.Count > 0) MessageList.ScrollIntoView(messages.Count - 1);
            MarkVisibleConversationRead();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { ShowError("Could not load conversation: " + ex.Message); }
    }

    private void UpdateConnectivityView()
    {
        var info = _client.ConnectionInfo;
        var connected = _client.IsConnected && info is not null;
        ConnectButton.IsEnabled = !connected;
        DisconnectButton.IsEnabled = connected;
        SaveConfigButton.IsEnabled = !connected;
        ReticulumConfigBox.IsEnabled = !connected;
        SyncMessagesButton.IsEnabled = connected;
        ImportSettingsButton.IsEnabled = !connected;
        ToolbarStatusText.Text = connected ? "Connected" : "Offline";
        ActivePropagationNodeText.Text = (_state?.PropagationNode ?? _state?.LastPropagationNode) is { } propagationNode
            ? $"{(_state?.PropagationNode is null ? "Automatic/remembered" : "Manual")} propagation node: {propagationNode}"
            : "No propagation node selected or discovered.";
        SyncScheduleStatusText.Text = _state?.PeriodicSynchronization == true
            ? $"Automatic fetch interval: {_state.SynchronizationIntervalSeconds} seconds; checks every 60 seconds. Last scheduled attempt: {_state.LastSynchronization?.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) ?? "not initialized"}. Retry pending: {_state.SynchronizationRetrying}."
            : "Automatic fetch is disabled. Schedule state is retained over restart.";

        if (!connected)
        {
            var selected = ReticulumConfigBox.Text;
            EffectiveConfigPathText.Text = string.IsNullOrWhiteSpace(selected)
                ? "Selected configuration path has not been loaded."
                : "Selected configuration: " + Path.GetFullPath(selected);
            RuntimeModeText.Text = "Shared-instance state is unknown until a connection succeeds. No daemon or interface health is inferred while offline.";
            ConnectivityStatusText.Text = "Not connected to Reticulum. Connect to determine the runtime state.";
            IdentityHashText.Text = "Identity: not available while offline";
            DeliveryHashText.Text = "LXMF address: not available while offline";
            return;
        }

        EffectiveConfigPathText.Text = $"Effective config directory: {info!.ConfigDirectory}\nConfig file: {info.ConfigFilePath}";
        RuntimeModeText.Text = info.SharedRole switch
        {
            DotReticulum.Applications.Runtime.SharedInstanceRole.Client =>
                "Sideband is connected via a shared Reticulum instance running on this system. It is using the shared owner and does not own the physical interfaces.",
            DotReticulum.Applications.Runtime.SharedInstanceRole.Host =>
                "Sideband owns the shared Reticulum instance on this system.",
            _ => "Sideband is running a standalone Reticulum node.",
        };
        ConnectivityStatusText.Text = "Connected to Reticulum. Runtime ownership is reported by the active .NET node.";
        IdentityHashText.Text = "Public identity hash: " + info.IdentityHash;
        DeliveryHashText.Text = "LXMF delivery address: " + info.DeliveryHash;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        ToolbarStatusText.Text = _client.IsConnected ? "Connected" : "Offline";
        if (_smokeUi)
        {
            Volatile.Write(ref _smokeStatusChanged, 1);
            Console.WriteLine("SMOKE_UI_STATUS_CHANGED=" + message);
            InvalidateVisual();
        }
    }

    private void ShowError(string message)
    {
        if (_closing) return;
        SetStatus(message);
        System.Diagnostics.Trace.TraceError("Sideband UI: {0}", message);
    }

    private async Task CloseAfterCaptureAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(Close);
        await _closedCompletion.Task.ConfigureAwait(false);
    }

    public async Task CloseAndWaitAsync()
    {
        if (_closedCompletion.Task.IsCompleted) return;

        await Dispatcher.UIThread.InvokeAsync(Close);
        await _closedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        _closing = true;
        IsEnabled = false;
        _closeTask ??= CompleteCloseAsync();
    }

    private async Task CompleteCloseAsync()
    {
        try
        {
            await _windowCancellation.CancelAsync().ConfigureAwait(false);
            try { await _initializationTask.ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("Sideband startup did not complete cleanly: {0}", ex); }
            await DrainPendingOperationsAsync().ConfigureAwait(false);
            _client.StateChanged -= OnStateChanged;
            _client.ErrorReported -= OnErrorReported;
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Sideband shutdown did not complete cleanly: {0}", ex);
        }
        finally
        {
            _windowCancellation.Dispose();
            _allowClose = true;
            await Dispatcher.UIThread.InvokeAsync(Close);
        }
    }

    private void TrackOperation(Func<Task> operationFactory)
    {
        if (_closing) return;
        var operation = operationFactory();
        lock (_operationGate) _pendingOperations.Add(operation);
        _ = operation.ContinueWith(
            completed =>
            {
                lock (_operationGate) _pendingOperations.Remove(completed);
                if (completed.IsFaulted)
                    System.Diagnostics.Trace.TraceError("Sideband UI operation failed: {0}", completed.Exception);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DrainPendingOperationsAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_operationGate) pending = [.. _pendingOperations];
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("A Sideband UI operation did not stop cleanly: {0}", ex); }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        MediaPreview.Source = null;
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        ClearRecording();
        _closedCompletion.TrySetResult();
    }
}
