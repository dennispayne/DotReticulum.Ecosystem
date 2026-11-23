using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private void InitializeConversationAppearance()
    {
        ContactList.ItemTemplate = new FuncDataTemplate<string>((text, _) =>
        {
            var contact = _conversationContacts.FirstOrDefault(item => FormatContact(item) == text);
            if (contact is null || _state is null) return new TextBlock { Text = text };
            var badge = SidebandConversationAppearance.FromState(_state).Resolve(_state, contact);
            var label = new TextBlock { Text = badge.Icon, MaxWidth = 160, TextWrapping = TextWrapping.Wrap };
            var icon = new Border
            {
                Name = "ConversationAppearanceBadge", Tag = badge, Padding = new Thickness(4), Child = label
            };
            if (badge.Foreground is { } foreground) label.Foreground = Brush.Parse("#" + foreground);
            if (badge.Background is { } background) icon.Background = Brush.Parse("#" + background);
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            ToolTip.SetTip(icon, "Original text badge, not a bundled upstream icon. Trust remains explicit in the contact label.");
            return row;
        });
    }

    private void LoadConversationAppearance()
    {
        var policy = SidebandConversationAppearance.FromState(
            _state ?? throw new InvalidOperationException("Load state before editing conversation appearance."));
        ShowConversationAppearanceBox.IsChecked = policy.Enabled;
        TrustedConversationAppearanceOnlyBox.IsChecked = !policy.FromAll;
    }

    private bool VerifyConversationAppearanceSmoke()
    {
        try
        {
            var contact = _conversationContacts.FirstOrDefault(item => item.DeliveryHash == _selectedContact);
            if (contact is null) return true;
            var state = _state ?? throw new InvalidOperationException("Conversation state was not loaded.");
            var expected = SidebandConversationAppearance.FromState(state).Resolve(state, contact);
            var container = ContactList.ContainerFromIndex(_conversationContacts.ToList().IndexOf(contact))
                ?? throw new InvalidOperationException("The selected conversation container was not realized.");
            var actual = container.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(border => border.Name == "ConversationAppearanceBadge" && Equals(border.Tag, expected));
            if (actual?.Child is not TextBlock text || text.Text != expected.Icon ||
                expected.Foreground is { } foreground && text.Foreground?.ToString() != Brush.Parse("#" + foreground).ToString() ||
                expected.Background is { } background && actual.Background?.ToString() != Brush.Parse("#" + background).ToString())
                throw new InvalidOperationException("The selected conversation badge did not render its effective icon and colors.");
            Console.WriteLine($"SMOKE_UI_CONVERSATION_APPEARANCE={expected.Icon}/{expected.Foreground ?? "theme"}/{expected.Background ?? "theme"}");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("sideband: Conversation appearance smoke failed: " + ex.Message);
            _ = CloseAfterCaptureAsync();
            return false;
        }
    }

    private void SaveConversationAppearanceClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        SaveConversationAppearanceButton.IsEnabled = false;
        try
        {
            await _client.ConfigureConversationAppearanceAsync(new(ShowConversationAppearanceBox.IsChecked == true,
                TrustedConversationAppearanceOnlyBox.IsChecked != true), _windowCancellation.Token);
            _state = await _client.LoadAsync(_windowCancellation.Token);
            LoadConversationAppearance();
            RefreshContacts();
            SetStatus("Conversation appearance saved and applied. Displayed peer styles do not grant trust or request permissions.");
        }
        catch (Exception ex) { ShowError("Could not save conversation appearance: " + ex.Message); }
        finally { if (!_closing) SaveConversationAppearanceButton.IsEnabled = true; }
    });
}
