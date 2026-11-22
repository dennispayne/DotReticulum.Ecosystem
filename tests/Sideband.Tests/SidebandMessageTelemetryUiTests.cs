using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Sideband.Desktop;

namespace Sideband.Tests;

public sealed partial class SidebandHeadlessUiTests
{
    [AvaloniaFact]
    [Trait("TestId", "T835")]
    public async Task SelectedTelemetryCopiesOnlySavedSnapshotAndRefusesLegacySelectionWithoutChangingDraftOrState()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "message-telemetry-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            foreach (var name in new[] { "battery", "information", "legacy" })
                await store.AddMessageAsync(new(Guid.NewGuid(), CopyPeer, true, "Keep title", "Keep text " + name,
                    DateTimeOffset.UtcNow, "Received", TelemetryHex: name == "legacy" ? null :
                    SidebandMessageTelemetryStorageTests.Fixture(name)), cancellationToken: token);
            await store.SetConversationReadAsync(CopyPeer, true, token);
            var copies = new List<string>();
            window = new MainWindow(root, null, clipboardWriter: text => { copies.Add(text); return Task.CompletedTask; });
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 3);
            var messages = window.FindControl<ListBox>("MessageList")!;
            var copy = window.FindControl<Button>("CopyMessageTelemetryButton")!;
            window.FindControl<TextBox>("MessageBox")!.Text = "Retain draft";
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            foreach (var (index, name) in new[] { (0, "battery"), (1, "information") })
            {
                messages.SelectedIndex = index;
                Assert.True(copy.IsEnabled);
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForAsync(() => copies.Count == index + 1);
                Assert.Equal(SidebandMessageTelemetry.CopyText(SidebandMessageTelemetryStorageTests.Fixture(name)), copies[index]);
            }
            messages.SelectedIndex = 2;
            Assert.False(copy.IsEnabled);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains("no captured telemetry", StringComparison.Ordinal) == true);
            Assert.Equal(2, copies.Count);
            Assert.Equal("Retain draft", window.FindControl<TextBox>("MessageBox")!.Text);
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            await window.CloseAndWaitAsync();
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    [Trait("TestId", "T836")]
    public async Task TelemetryAndRawCopyShareOnePendingOperationAndFailuresRecover()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "message-telemetry-errors-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var store = new SidebandStateStore(root);
            await store.AddMessageAsync(new(Guid.NewGuid(), CopyPeer, true, "", "Keep message",
                DateTimeOffset.UtcNow, "Received", TelemetryHex: SidebandMessageTelemetryStorageTests.Fixture("battery")), cancellationToken: token);
            var calls = 0;
            window = new MainWindow(root, null, clipboardWriter: _ => ++calls switch
            {
                1 => Task.FromException(new IOException("Synthetic telemetry clipboard failure")),
                2 => pending.Task,
                _ => Task.CompletedTask
            });
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 1);
            window.FindControl<ListBox>("MessageList")!.SelectedIndex = 0;
            var copy = window.FindControl<Button>("CopyMessageTelemetryButton")!;
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains("Synthetic telemetry clipboard failure", StringComparison.Ordinal) == true);
            Assert.True(copy.IsEnabled);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(copy.IsEnabled);
            Assert.False(window.FindControl<Button>("CopyMessageButton")!.IsEnabled);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, calls);
            pending.SetResult();
            await WaitForAsync(() => copy.IsEnabled);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => calls == 3);
            Assert.Single((await store.LoadAsync(cancellationToken: token)).Messages);
            await window.CloseAndWaitAsync();
        }
        finally
        {
            pending.TrySetResult();
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
