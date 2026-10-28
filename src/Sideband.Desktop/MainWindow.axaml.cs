using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Sideband;
using System.Collections.Generic;

namespace Sideband.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly SidebandStateStore _store;
    private readonly SidebandClient _client;
    private readonly string? _commandLineRnsConfig;
    private readonly CancellationTokenSource _windowCancellation = new();
    private readonly object _operationGate = new();
    private readonly HashSet<Task> _pendingOperations = [];
    private readonly TaskCompletionSource _closedCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _smokeUi;
    private Task _initializationTask = Task.CompletedTask;
    private int _smokeRenderCount;
    private int _smokeStatusChanged;
    private string? _selectedContact;
    private SidebandState? _state;
    private bool _navigationVisible = true;
    private bool _closing;
    private bool _allowClose;
    private Task? _closeTask;

    internal static bool SmokeUiSucceeded { get; private set; }

    public MainWindow() : this(Program.DataDirectory, null, smokeUi: false) { }

    public MainWindow(string dataDirectory, bool smokeUi = false) : this(dataDirectory, null, smokeUi) { }

    public MainWindow(string dataDirectory, string? reticulumConfig, bool smokeUi = false)
    {
        InitializeComponent();
        _commandLineRnsConfig = string.IsNullOrWhiteSpace(reticulumConfig) ? null : Path.GetFullPath(reticulumConfig);
        _smokeUi = smokeUi;
        _store = new SidebandStateStore(dataDirectory);
        _client = new SidebandClient(_store);
        _client.StateChanged += OnStateChanged;
        _client.ErrorReported += OnErrorReported;
        ThemeSelector.ItemsSource = new[] { "Dark", "Light" };
        Closed += OnClosed;
        Closing += OnClosing;
        Opened += OnOpened;
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
            Dispatcher.UIThread.Post(() => SendButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        }
        else if (Volatile.Read(ref _smokeStatusChanged) != 0 && !SmokeUiSucceeded)
        {
            SmokeUiSucceeded = true;
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
            ApplyTheme(_state.Theme);
            RefreshContacts();
            RefreshExpandedContacts();
            SetStatus($"Offline · local data: {_store.DataDirectory}");
            UpdateConnectivityView();
            if (_smokeUi) Console.WriteLine("SMOKE_UI_OPENED=Sideband");
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
            await _client.SendTextAsync(_selectedContact, content, _windowCancellation.Token);
            MessageBox.Text = string.Empty;
            await RefreshMessagesAsync();
            SetStatus("Message queued for LXMF delivery.");
        }
        catch (Exception ex) { ShowError("Message was not queued: " + ex.Message); }
        finally { SendButton.IsEnabled = true; }
    }

    private void ContactSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        TrackOperation(ContactSelectionChangedAsync);

    private async Task ContactSelectionChangedAsync()
    {
        if (ContactList.SelectedIndex < 0 || _state is null || ContactList.SelectedIndex >= _state.Contacts.Count) return;
        _selectedContact = _state.Contacts[ContactList.SelectedIndex].DeliveryHash;
        ConversationPeerText.Text = _state.Contacts[ContactList.SelectedIndex].DisplayName + " · " + _selectedContact;
        await RefreshMessagesAsync(_windowCancellation.Token);
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
            .Select(contact => $"{contact.DisplayName}  ·  {contact.DeliveryHash}")
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
            .Select(contact => $"{contact.DisplayName}  ·  {contact.DeliveryHash}")
            .ToArray();
    }

    private async Task RefreshMessagesAsync(CancellationToken cancellationToken = default)
    {
        if (_selectedContact is null) return;
        try
        {
            var messages = await _client.GetMessagesAsync(_selectedContact, cancellationToken);
            MessageList.ItemsSource = messages.Select(message =>
                $"{message.Timestamp.LocalDateTime:g}  {(message.Incoming ? "Received" : message.DeliveryState)}\n{message.Content}")
                .ToArray();
            if (messages.Count > 0) MessageList.ScrollIntoView(messages.Count - 1);
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
        ToolbarStatusText.Text = connected ? "Connected" : "Offline";

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
        _closedCompletion.TrySetResult();
    }
}
