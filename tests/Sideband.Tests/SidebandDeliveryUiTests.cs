using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandDeliveryUiTests
{
    [AvaloniaFact]
    public async Task OriginalPreferencesSaveIndependentlyAndRestoreWithoutOpeningDevices()
    {
        var root = Path.Combine(Path.GetTempPath(), "delivery-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        var devices = new SidebandVoiceLifecycleTests.Devices();
        MainWindow? window = null;
        try
        {
            window = new MainWindow(root, Path.Combine(root, "unused"), mediaBackend: devices);
            window.Show();
            await Until(() => window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
            window.FindControl<Button>("PreferencesNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.FindControl<CheckBox>("LimitIncomingMessagesBox")!.IsChecked);
            window.FindControl<TextBox>("DisplayNameBox")!.Text = "Not saved";
            window.FindControl<CheckBox>("LimitIncomingMessagesBox")!.IsChecked = false;
            window.FindControl<Button>("SaveDeliverySettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.FindControl<TextBlock>("DeliverySettingsStatusText")!.Text?.StartsWith("Delivery limit saved", StringComparison.Ordinal) == true);
            Assert.False(SidebandDeliveryConfiguration.FromState(await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).LimitIncomingToOneMegabyte);
            Assert.Equal("Sideband", (await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).DisplayName);
            await window.CloseAndWaitAsync();
            window = new MainWindow(root, Path.Combine(root, "unused"), mediaBackend: devices);
            window.Show();
            await Until(() => window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
            Assert.False(window.FindControl<CheckBox>("LimitIncomingMessagesBox")!.IsChecked);
            Assert.Equal(0, devices.Captures);
            Assert.Equal(0, devices.Playbacks);

            async Task Until(Func<bool> condition)
            {
                var until = DateTime.UtcNow.AddSeconds(15);
                while (!condition())
                {
                    if (DateTime.UtcNow >= until) throw new TimeoutException("Delivery preferences did not reach the asserted state.");
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(10, token);
                }
            }
        }
        finally
        {
            if (window is not null) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
