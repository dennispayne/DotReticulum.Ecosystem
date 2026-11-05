using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Sideband;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMessageCopyTests
{
    [Fact]
    [Trait("TestId", "T470")]
    public async Task ActualPrimaryCopiesRawUnicodeMarkupEmptyAndControlsAndDoesNotDismissAfterClipboardFailure()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-message-copy.json"), TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", root.GetProperty("revision").GetString());
        Assert.Equal("9b2580f76310356d2e1a30b038857fbad4ee346aad9e2a25ab237515fff39928", root.GetProperty("source_sha256").GetString());
        Assert.Equal(6, root.GetProperty("cases").GetArrayLength());
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            var events = item.GetProperty("events").EnumerateArray().Select(value => Assert.IsType<string>(value.GetString())).ToArray();
            var copied = item.GetProperty("copied").EnumerateArray().Select(value => Assert.IsType<string>(value.GetString())).ToArray();
            if (name == "invalid-utf8")
            {
                Assert.Empty(events);
                Assert.Empty(copied);
                Assert.Equal("UnicodeDecodeError", item.GetProperty("error").GetString());
            }
            else if (name == "clipboard-error")
            {
                Assert.Equal(["copy"], events);
                Assert.Empty(copied);
                Assert.Equal("RuntimeError", item.GetProperty("error").GetString());
            }
            else
            {
                Assert.Equal(["copy", "dismiss"], events);
                Assert.Equal(Encoding.UTF8.GetString(Convert.FromHexString(item.GetProperty("content_hex").GetString()!)),
                    Assert.Single(copied));
                Assert.Equal(JsonValueKind.Null, item.GetProperty("error").ValueKind);
            }
        }
    }
}

public sealed partial class SidebandHeadlessUiTests
{
    private const string CopyPeer = "00112233445566778899aabbccddeeff";

    [AvaloniaFact]
    [Trait("TestId", "T471")]
    public async Task SelectedCopyPassesExactBoundedRawContentToOwnedPortWithoutTitleMediaDraftOrStateChanges()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "message-copy-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            await store.AddContactAsync(SidebandContact.Create("Copy peer", CopyPeer, trusted: true), token);
            var contents = new[] { "[b]raw[/b] **markdown**\n\u00e6 \u03bb", "", "raw\0\t\r\nend", new string('x', 65_536) };
            foreach (var content in contents)
                await store.AddMessageAsync(new(Guid.NewGuid(), CopyPeer, false, "Do not copy this title",
                    content, DateTimeOffset.UtcNow, "Failed", [new("do-not-open.txt", new string('a', 64), 12)]), token);
            var observed = new List<string>();
            window = new MainWindow(root, null, clipboardWriter: text => { observed.Add(text); return Task.CompletedTask; });
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == contents.Length);
            var messages = window.FindControl<ListBox>("MessageList")!;
            var copy = window.FindControl<Button>("CopyMessageButton")!;
            var draft = window.FindControl<TextBox>("MessageBox")!;
            draft.Text = "Keep current draft";
            window.FindControl<TextBox>("AttachmentPathBox")!.Text = "Do not open this draft attachment";
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            for (var index = 0; index < contents.Length; index++)
            {
                messages.SelectedIndex = index;
                Assert.True(copy.IsEnabled);
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForAsync(() => observed.Count == index + 1);
                Assert.Equal(contents[index], observed[^1]);
            }
            Assert.Equal("Keep current draft", draft.Text);
            Assert.Equal("Do not open this draft attachment", window.FindControl<TextBox>("AttachmentPathBox")!.Text);
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            Assert.Equal("Offline", window.FindControl<TextBlock>("ToolbarStatusText")!.Text);
            await window.CloseAndWaitAsync();
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    [Trait("TestId", "T472")]
    public async Task EmptySelectionCopyFailureAndSinglePendingWriteRefuseExplicitlyAndRecoverWithoutStateChanges()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "message-copy-errors-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var store = new SidebandStateStore(root);
            await store.AddMessageAsync(new(Guid.NewGuid(), CopyPeer, true, "Title", "Copy source", DateTimeOffset.UtcNow, "Delivered"), token);
            var calls = 0;
            Task Write(string _) => ++calls switch
            {
                1 => Task.FromException(new IOException("Synthetic clipboard unavailable")),
                2 => pending.Task,
                _ => Task.CompletedTask,
            };
            window = new MainWindow(root, null, clipboardWriter: Write);
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 1);
            var messages = window.FindControl<ListBox>("MessageList")!;
            var copy = window.FindControl<Button>("CopyMessageButton")!;
            messages.SelectedIndex = -1;
            Assert.False(copy.IsEnabled);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains("Select", StringComparison.Ordinal) == true);
            Assert.Equal(0, calls);
            messages.SelectedIndex = 0;
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains("Synthetic clipboard unavailable", StringComparison.Ordinal) == true);
            Assert.True(copy.IsEnabled);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(copy.IsEnabled);
            Assert.Equal(2, calls);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains("still pending", StringComparison.Ordinal) == true);
            Assert.Equal(2, calls);
            pending.SetResult();
            await WaitForAsync(() => copy.IsEnabled);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => calls == 3 && window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Selected raw", StringComparison.Ordinal) == true);
            Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Messages);
            await window.CloseAndWaitAsync();
        }
        finally
        {
            pending.TrySetResult();
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    [Trait("TestId", "T473")]
    public async Task ClipboardTimeoutRetainsOneBackendOperationAndWindowCloseCancelsWaitingWithoutClaimingRecall()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "message-copy-close-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var store = new SidebandStateStore(root);
            await store.AddMessageAsync(new(Guid.NewGuid(), CopyPeer, true, "", "Owned text", DateTimeOffset.UtcNow, "Delivered"), token);
            var calls = 0;
            window = new MainWindow(root, null, clipboardWriter: _ => { calls++; return pending.Task; });
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 1);
            window.FindControl<ListBox>("MessageList")!.SelectedIndex = 0;
            var copy = window.FindControl<Button>("CopyMessageButton")!;
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (window.FindControl<TextBlock>("StatusText")?.Text?.Contains("within five seconds", StringComparison.Ordinal) != true)
                await Task.Delay(20, timeout.Token);
            Assert.False(copy.IsEnabled);
            Assert.False(pending.Task.IsCompleted);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, calls);
            pending.SetResult();
            await WaitForAsync(() => copy.IsEnabled);
            await window.CloseAndWaitAsync();
            var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window = new MainWindow(root, null, clipboardWriter: _ => closing.Task);
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 1);
            window.FindControl<ListBox>("MessageList")!.SelectedIndex = 0;
            window.FindControl<Button>("CopyMessageButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.CloseAndWaitAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(closing.Task.IsCompleted);
            closing.SetException(new IOException("Synthetic late clipboard failure after close"));
            await Task.Delay(20, token);

        }
        finally
        {
            pending.TrySetResult();
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
