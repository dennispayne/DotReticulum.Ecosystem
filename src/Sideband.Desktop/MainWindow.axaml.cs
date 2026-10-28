using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly SidebandStateStore _store;
    private readonly SidebandClient _client;
    private readonly string? _commandLineRnsConfig;
    private readonly bool _smokeUi;
    private int _smokeRenderCount;
    private int _smokeStatusChanged;
    private string? _selectedContact;
    private SidebandState? _state;
    private bool _navigationVisible = true;
    private bool _closing;

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
        try
        {
            _state = await _store.LoadAsync(_commandLineRnsConfig);
            if (_commandLineRnsConfig is not null)
            {
                await _store.SetReticulumConfigDirectoryAsync(_commandLineRnsConfig);
                _state = await _client.LoadAsync();
            }

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

    private async void ConnectClicked(object? sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        SetStatus("Connecting to Reticulum…");
        ConnectivityStatusText.Text = "Opening the Reticulum node and resolving shared-instance ownership…";
        try
        {
            await _client.ConnectAsync(ReticulumConfigBox.Text);
            _state = await _client.LoadAsync();
            SetStatus("Connected · LXMF address " + (_state.OwnDeliveryHash ?? "available"));
        }
        catch (Exception ex)
        {
            ConnectButton.IsEnabled = true;
            SetStatus("Connection failed · " + ex.Message);
            ConnectivityStatusText.Text = "Could not connect to Reticulum. Check the selected configuration and use rnstatus with that configuration to inspect the running node and interfaces.";
        }
        UpdateConnectivityView();
    }

    private async void DisconnectClicked(object? sender, RoutedEventArgs e)
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

    private async void SaveConfigClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var value = ReticulumConfigBox.Text;
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Select a Reticulum configuration directory or file.");
            await _store.SetReticulumConfigDirectoryAsync(value);
            _state = await _client.LoadAsync();
            SetStatus("Saved Reticulum configuration path. This does not connect or modify the Reticulum files.");
            UpdateConnectivityView();
        }
        catch (Exception ex) { ShowError("Could not save Reticulum configuration path: " + ex.Message); }
    }

    private async void SavePreferencesClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var name = DisplayNameBox.Text ?? string.Empty;
            var theme = ThemeSelector.SelectedItem as string;
            if (theme is null) throw new ArgumentException("Choose Dark or Light appearance.");
            await _store.SetPreferencesAsync(name, theme);
            _state = await _client.LoadAsync();
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

    private async void AddContactClicked(object? sender, RoutedEventArgs e)
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

    private async void SendClicked(object? sender, RoutedEventArgs e)
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
            await _client.SendTextAsync(_selectedContact, content);
            MessageBox.Text = string.Empty;
            await RefreshMessagesAsync();
            SetStatus("Message queued for LXMF delivery.");
        }
        catch (Exception ex) { ShowError("Message was not queued: " + ex.Message); }
        finally { SendButton.IsEnabled = true; }
    }

    private async void ContactSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ContactList.SelectedIndex < 0 || _state is null || ContactList.SelectedIndex >= _state.Contacts.Count) return;
        _selectedContact = _state.Contacts[ContactList.SelectedIndex].DeliveryHash;
        ConversationPeerText.Text = _state.Contacts[ContactList.SelectedIndex].DisplayName + " · " + _selectedContact;
        await RefreshMessagesAsync();
    }

    private void OnStateChanged(SidebandState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var selected = _selectedContact;
            _state = state;
            RefreshContacts();
            RefreshExpandedContacts();
            if (selected is not null) _selectedContact = selected;
            if (_selectedContact is not null) _ = RefreshMessagesAsync();
            if (_client.IsConnected)
                SetStatus($"Connected · LXMF address {state.OwnDeliveryHash}");
            UpdateConnectivityView();
        });
    }

    private void OnErrorReported(string message) => Dispatcher.UIThread.Post(() => ShowError(message));

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

    private async Task RefreshMessagesAsync()
    {
        if (_selectedContact is null) return;
        try
        {
            var messages = await _client.GetMessagesAsync(_selectedContact);
            MessageList.ItemsSource = messages.Select(message =>
                $"{message.Timestamp.LocalDateTime:g}  {(message.Incoming ? "Received" : message.DeliveryState)}\n{message.Content}")
                .ToArray();
            if (messages.Count > 0) MessageList.ScrollIntoView(messages.Count - 1);
        }
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
        SetStatus(message);
        System.Diagnostics.Trace.TraceError("Sideband UI: {0}", message);
    }

    private async Task CloseAfterCaptureAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(Close);
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_closing) return;
        _closing = true;
        _client.StateChanged -= OnStateChanged;
        _client.ErrorReported -= OnErrorReported;
        await _client.DisposeAsync();
    }
}
