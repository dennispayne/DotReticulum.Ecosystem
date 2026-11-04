using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Sideband;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMessageActionsTests
{
    private const string Peer = "00112233445566778899aabbccddeeff";
    private const string Other = "ffeeddccbbaa99887766554433221100";

    [Fact]
    [Trait("TestId", "T378")]
    public async Task SingleMessageRemovalMatchesExecutedPrimarySqlWithoutChangingOtherHistoryOrPolicies()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-conversation-policy.json"), token));
            Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", fixture.RootElement.GetProperty("revision").GetString());
            Assert.Equal("8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25",
                fixture.RootElement.GetProperty("source_sha256").GetString());
            var store = new SidebandStateStore(root);
            for (byte value = 1; value <= 3; value++)
                await store.AddMessageAsync(new(Id(value), value == 3 ? Other : Peer, value != 2, "",
                    "Same text", DateTimeOffset.UtcNow, value == 2 ? "Queued" : "Received"), token);
            await store.SetConversationTrustAsync(Peer, true, token);
            var before = await store.LoadAsync(cancellationToken: token);
            await using var client = new SidebandClient(store);
            foreach (var item in fixture.RootElement.GetProperty("deletion_cases").EnumerateArray())
            {
                var target = Id(item.GetProperty("target").GetByte());
                if (before.Messages.Any(message => message.Id == target) &&
                    (await store.LoadAsync(cancellationToken: token)).Messages.Any(message => message.Id == target))
                    await client.DeleteMessageAsync(Peer, target, token);
                else
                    await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteMessageAsync(Peer, target, token));
                var persisted = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
                Assert.Equal(item.GetProperty("remaining").EnumerateArray().Select(value => Id(value.GetByte())),
                    persisted.Messages.Select(message => message.Id));
                Assert.Equal(before.Contacts, persisted.Contacts);
                Assert.False(client.IsConnected);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T379")]
    public async Task InvalidCancelledAndLateDeliveryUpdatesCannotRemoveOrResurrectAnotherMessage()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            var message = await store.AddMessageAsync(new(Id(1), Peer, false, "Saved", "Pending",
                DateTimeOffset.UtcNow, "Queued"), token);
            await using var client = new SidebandClient(store);
            var bytes = await File.ReadAllBytesAsync(store.StatePath, token);
            await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteMessageAsync("bad", message.Id, token));
            await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteMessageAsync(Peer, Guid.Empty, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteMessageAsync(Other, message.Id, token));
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DeleteMessageAsync(Peer, message.Id, cancelled.Token));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(store.StatePath, token));
            await Task.WhenAll(client.DeleteMessageAsync(Peer.ToUpperInvariant(), message.Id, token),
                store.UpdateMessageStateAsync(message.Id, "Delivered", token));
            await store.UpdateMessageStateAsync(message.Id, "Failed", token);
            Assert.Empty((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Messages);
            await store.AddMessageAsync(message with { Id = Id(2), Content = "Continued service" }, token);
            Assert.Equal("Continued service", Assert.Single(await client.GetMessagesAsync(Peer, token)).Content);
            await client.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.DeleteMessageAsync(Peer, Id(2), token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static Guid Id(byte value) => new(Enumerable.Repeat(value, 16).ToArray());
    private static string Root() => Path.Combine(AppContext.BaseDirectory, "message-actions-" + Guid.NewGuid().ToString("N"));
}

public sealed partial class SidebandHeadlessUiTests
{
    [AvaloniaFact]
    [Trait("TestId", "T380")]
    public async Task MessageDetailsAndConfirmationFollowSelectionAndDeletedHistoryStaysRemovedAfterRestart()
    {
        const string peer = "00112233445566778899aabbccddeeff";
        var root = Path.Combine(AppContext.BaseDirectory, "message-actions-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            var first = await store.AddMessageAsync(new(Guid.NewGuid(), peer, true, "Selected title", "First text",
                DateTimeOffset.UtcNow, "Received"), token);
            var second = await store.AddMessageAsync(first with { Id = Guid.NewGuid(), Content = "Other text" }, token);
            await store.SetConversationReadAsync(peer, true, token);
            window = new MainWindow(root);
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 2);
            var list = window.FindControl<ListBox>("MessageList")!;
            var confirm = window.FindControl<CheckBox>("ConfirmDeleteMessageBox")!;
            var delete = window.FindControl<Button>("DeleteMessageButton")!;
            list.SelectedIndex = 0;
            Assert.Contains(first.Id.ToString(), window.FindControl<TextBox>("MessageDetailsBox")!.Text);
            Assert.Contains("Selected title", window.FindControl<TextBox>("MessageDetailsBox")!.Text);
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains(
                "Confirm deletion", StringComparison.Ordinal) == true);
            Assert.Equal(2, (await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Messages.Count);
            confirm.IsChecked = true;
            list.SelectedIndex = 1;
            Assert.False(confirm.IsChecked);
            list.SelectedIndex = 0;
            confirm.IsChecked = true;
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => list.ItemCount == 1);
            Assert.False(confirm.IsChecked);
            Assert.Equal(string.Empty, window.FindControl<TextBox>("MessageDetailsBox")!.Text);
            await window.CloseAndWaitAsync();
            window = new MainWindow(root);
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 1);
            var persisted = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.Equal(second.Id, Assert.Single(persisted.Messages).Id);
            Assert.Single(persisted.Contacts);
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
