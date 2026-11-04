using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandStartupAnnounceUiTests
{
    [AvaloniaFact]
    public async Task OriginalStartupControlSavesAndRestoresWithoutConnectingOrOpeningDevices()
    {
        var root = Path.Combine(Path.GetTempPath(), "startup-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        var devices = new SidebandVoiceLifecycleTests.Devices();
        MainWindow? window = null;
        try
        {
            window = new MainWindow(root, Path.Combine(root, "unused"), mediaBackend: devices);
            window.Show();
            await Until(() => window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
            Assert.True(window.FindControl<CheckBox>("StartupAnnouncementBox")!.IsChecked);
            Assert.False(window.FindControl<Button>("AnnounceNowButton")!.IsEnabled);
            window.FindControl<CheckBox>("StartupAnnouncementBox")!.IsChecked = false;
            window.FindControl<Button>("SaveStartupAnnouncementButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.FindControl<TextBlock>("AnnouncementStatusText")!.Text?.StartsWith("Startup announcement setting saved", StringComparison.Ordinal) == true);
            Assert.False(SidebandAnnounceConfiguration.FromState(await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).AnnounceOnStartup);
            await window.CloseAndWaitAsync();
            window = new MainWindow(root, Path.Combine(root, "unused"), mediaBackend: devices);
            window.Show();
            await Until(() => window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
            Assert.False(window.FindControl<CheckBox>("StartupAnnouncementBox")!.IsChecked);
            Assert.Equal(0, devices.Captures);
            Assert.Equal(0, devices.Playbacks);
        }
        finally
        {
            if (window is not null) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        async Task Until(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!condition())
            {
                if (DateTime.UtcNow >= until) throw new TimeoutException("Startup announcement controls did not reach the asserted state.");
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10, token);
            }
        }
    }
}
