using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandSynchronizationUiTests
{
    [AvaloniaFact]
    public async Task ManualLimitControlsPersistRejectInvalidInputAndReopenWithoutDevices()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        var devices = new SidebandVoiceLifecycleTests.Devices();
        MainWindow? window = null;
        try
        {
            window = new MainWindow(root, Path.Combine(root, "unused"), mediaBackend: devices);
            window.Show();
            await Until(() => window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
            Assert.False(window.FindControl<CheckBox>("LimitManualFetchBox")!.IsChecked);
            Assert.Equal("3", window.FindControl<TextBox>("MaximumFetchMessagesBox")!.Text);
            window.FindControl<CheckBox>("LimitManualFetchBox")!.IsChecked = true;
            window.FindControl<TextBox>("MaximumFetchMessagesBox")!.Text = "2";
            window.FindControl<Button>("SaveSynchronizationSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.FindControl<TextBlock>("SynchronizationSettingsStatusText")!.Text?.StartsWith("Manual fetch limit saved", StringComparison.Ordinal) == true);
            window.FindControl<TextBox>("MaximumFetchMessagesBox")!.Text = "0";
            window.FindControl<Button>("SaveSynchronizationSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.FindControl<TextBlock>("SynchronizationSettingsStatusText")!.Text?.Contains("positive", StringComparison.Ordinal) == true);
            Assert.Equal(new(true, 2), SidebandSynchronizationConfiguration.FromState(await new SidebandStateStore(root).LoadAsync(cancellationToken: token)));
            await window.CloseAndWaitAsync();
            window = new MainWindow(root, Path.Combine(root, "unused"), mediaBackend: devices);
            window.Show();
            await Until(() => window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
            Assert.True(window.FindControl<CheckBox>("LimitManualFetchBox")!.IsChecked);
            Assert.Equal("2", window.FindControl<TextBox>("MaximumFetchMessagesBox")!.Text);
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
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Manual fetch controls did not reach the asserted state.");
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10, token);
            }
        }
    }
}
