using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Sideband.Desktop;

namespace Sideband.Tests;

public sealed partial class SidebandHeadlessUiTests
{
    [AvaloniaFact]
    public async Task ImportedVoiceHistoryIsLabelledAsAssertionsAcrossOfflineDesktopRestart()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-voice-import-ui-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "unopened-rns");
        var token = TestContext.Current.CancellationToken;
        var devices = new SidebandVoiceLifecycleTests.Devices();
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            await store.AddCallEventAsync(new(DateTimeOffset.UtcNow, "incoming-missed", new string('a', 32), Imported: true), token);
            await store.AddCallEventAsync(new(DateTimeOffset.UtcNow, "incoming-success", new string('b', 32)), token);
            foreach (var restart in Enumerable.Range(0, 2))
            {
                window = new MainWindow(root, config, mediaBackend: devices);
                window.Show();
                await WaitForAsync(() => window.FindControl<ListBox>("VoiceHistoryList")!.ItemCount == 2);
                window.FindControl<Button>("VoiceNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var history = window.FindControl<ListBox>("VoiceHistoryList")!.Items.Cast<string>().ToArray();
                Assert.Single(history, text => text.StartsWith("[IMPORTED ASSERTION]", StringComparison.Ordinal));
                Assert.Contains(history, text => text.Contains("incoming-success", StringComparison.Ordinal) &&
                    !text.StartsWith("[IMPORTED ASSERTION]", StringComparison.Ordinal));
                Assert.False(Directory.Exists(config));
                Assert.Equal(0, devices.Captures);
                Assert.Equal(0, devices.Playbacks);
                if (restart == 1)
                {
                    window.FindControl<Button>("ClearVoiceHistoryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitForAsync(() => window.FindControl<ListBox>("VoiceHistoryList")!.ItemCount == 0);
                    Assert.Empty((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Calls!);
                }
                await window.CloseAndWaitAsync();
            }
        }
        finally
        {
            if (window is not null) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
