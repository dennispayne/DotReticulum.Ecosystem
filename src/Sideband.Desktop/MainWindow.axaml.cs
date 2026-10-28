using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly SidebandStateStore _store;
    private readonly SidebandClient _client;
    private string? _selectedContact;
    private SidebandState? _state;

    public MainWindow() : this(Program.DataDirectory) { }

    public MainWindow(string dataDirectory)
    {
        InitializeComponent();
        _store = new SidebandStateStore(dataDirectory);
        _client = new SidebandClient(_store);
        _client.StateChanged += OnStateChanged;
        _client.ErrorReported += OnErrorReported;
        Closed += OnClosed;
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            _state = await _client.LoadAsync();
            ReticulumConfigBox.Text = _state.ReticulumConfigDirectory;
            RefreshContacts();
            StatusText.Text = $"Offline · local data: {_store.DataDirectory}";
        }
        catch (Exception ex) { ShowError("Could not load Sideband data: " + ex.Message); }
    }

    private async void ConnectClicked(object? sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        StatusText.Text = "Connecting to Reticulum…";
        try
        {
            await _client.ConnectAsync(ReticulumConfigBox.Text);
            StatusText.Text = $"Connected · LXMF address {_state?.OwnDeliveryHash}";
            ReticulumConfigBox.IsEnabled = false;
        }
        catch (Exception ex)
        {
            ConnectButton.IsEnabled = true;
            StatusText.Text = "Connection failed · " + ex.Message;
        }
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
            StatusText.Text = $"Added {name.Trim()}";
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
            StatusText.Text = "Message queued for LXMF delivery.";
        }
        catch (Exception ex) { ShowError("Message was not queued: " + ex.Message); }
        finally { SendButton.IsEnabled = true; }
    }

    private async void ContactSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ContactList.SelectedIndex < 0 || _state is null || ContactList.SelectedIndex >= _state.Contacts.Count) return;
        _selectedContact = _state.Contacts[ContactList.SelectedIndex].DeliveryHash;
        await RefreshMessagesAsync();
    }

    private void OnStateChanged(SidebandState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var selected = _selectedContact;
            _state = state;
            RefreshContacts();
            if (selected is not null) _selectedContact = selected;
            if (_selectedContact is not null) _ = RefreshMessagesAsync();
            if (_client.IsConnected) StatusText.Text = $"Connected · LXMF address {state.OwnDeliveryHash}";
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

    private void ShowError(string message)
    {
        StatusText.Text = message;
        System.Diagnostics.Trace.TraceError("Sideband UI: {0}", message);
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _client.StateChanged -= OnStateChanged;
        _client.ErrorReported -= OnErrorReported;
        await _client.DisposeAsync();
    }
}
