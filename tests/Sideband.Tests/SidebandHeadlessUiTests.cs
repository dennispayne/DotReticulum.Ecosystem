using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Sideband.Desktop;

[assembly: AvaloniaTestApplication(typeof(Sideband.Tests.TestAppBuilder))]

namespace Sideband.Tests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

[Trait("Category", "Integration")]
public sealed class SidebandHeadlessUiTests
{
    [AvaloniaFact]
    public async Task NavigationPreferencesAndConnectivityUseSavedApplicationState()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-ui-test-" + Guid.NewGuid().ToString("N"));
        var rnsConfig = Path.Combine(path, "isolated-rns");
        try
        {
            Directory.CreateDirectory(rnsConfig);
            await File.WriteAllTextAsync(Path.Combine(rnsConfig, "config"),
                "[reticulum]\n  enable_transport = no\n  share_instance = no\n\n[logging]\n  loglevel = 4\n");
            var window = new MainWindow(path, rnsConfig);
            window.Show();
            await WaitForAsync(() => window.FindControl<TextBox>("DisplayNameBox")?.Text == "Sideband");

            Assert.True(window.IsVisible);
            Assert.NotNull(window.FindControl<ListBox>("ContactList"));
            Assert.NotNull(window.FindControl<ListBox>("MessageList"));
            Assert.False(window.FindControl<Button>("VoiceUnavailableButton")?.IsEnabled ?? true);
            Assert.True(window.FindControl<Button>("SendButton")!.IsEnabled);
            window.FindControl<Button>("ContactsNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.FindControl<ScrollViewer>("ContactsPage")!.IsVisible);

            var connectivityNav = window.FindControl<Button>("ConnectivityNavButton")!;
            connectivityNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.FindControl<ScrollViewer>("ConnectivityPage")!.IsVisible);
            Assert.Contains(rnsConfig, window.FindControl<TextBlock>("EffectiveConfigPathText")!.Text);
            Assert.Contains("unknown until a connection", window.FindControl<TextBlock>("RuntimeModeText")!.Text);
            window.FindControl<Button>("ConnectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<Button>("DisconnectButton")?.IsEnabled == true);
            Assert.Contains("standalone Reticulum node", window.FindControl<TextBlock>("RuntimeModeText")!.Text);
            Assert.Contains(Path.Combine(rnsConfig, "config"), window.FindControl<TextBlock>("EffectiveConfigPathText")!.Text);
            Assert.Contains("Public identity hash:", window.FindControl<TextBlock>("IdentityHashText")!.Text);
            Assert.Contains("LXMF delivery address:", window.FindControl<TextBlock>("DeliveryHashText")!.Text);
            window.FindControl<Button>("DisconnectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<Button>("ConnectButton")?.IsEnabled == true);

            var preferencesNav = window.FindControl<Button>("PreferencesNavButton")!;
            preferencesNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.FindControl<TextBox>("DisplayNameBox")!.Text = "Alice";
            window.FindControl<ComboBox>("ThemeSelector")!.SelectedItem = "Light";
            window.FindControl<Button>("SavePreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("PreferencesStatusText")?.Text == "Preferences saved.");

            var state = await new SidebandStateStore(path).LoadAsync();
            Assert.Equal("Alice", state.DisplayName);
            Assert.Equal("Light", state.Theme);
            Assert.Equal(Path.GetFullPath(rnsConfig), state.ReticulumConfigDirectory);
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);

            window.FindControl<TextBox>("DisplayNameBox")!.Text = string.Empty;
            window.FindControl<Button>("SavePreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("PreferencesStatusText")?.Text?.Contains("displayName") == true);
            Assert.Contains("Could not save preferences", window.FindControl<TextBlock>("StatusText")!.Text);

            window.Close();
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [AvaloniaFact]
    public void WindowPresentsChatControlsAndReportsSendWithoutSelection()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-ui-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var window = new MainWindow(path, Path.Combine(path, "isolated-rns"));
            window.Show();
            var status = window.FindControl<TextBlock>("StatusText")!;
            var send = window.FindControl<Button>("SendButton")!;
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal("Select a contact before sending.", status.Text);
            window.Close();
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            await Dispatcher.UIThread.InvokeAsync(() => { });
        }

        Assert.True(condition(), "The expected UI state was not reached.");
    }
}
