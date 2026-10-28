using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
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
    public void WindowPresentsChatControlsAndReportsSendWithoutSelection()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-ui-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var window = new MainWindow(path);
            window.Show();

            Assert.True(window.IsVisible);
            Assert.NotNull(window.FindControl<ListBox>("ContactList"));
            Assert.NotNull(window.FindControl<ListBox>("MessageList"));
            Assert.False(window.FindControl<Button>("VoiceButton")!.IsEnabled);
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
}
