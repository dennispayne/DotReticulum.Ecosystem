using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Sideband;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandConversationActionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("TestId", "T530")]
    public async Task ClearDeleteRepeatAndUnknownMatchExecutedPrimarySqlWithRealTelemetry(bool delete)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "conversation-actions-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            using var vectors = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "sideband-conversation-policy.json"), token));
            Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", vectors.RootElement.GetProperty("revision").GetString());
            Assert.Equal("8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25",
                vectors.RootElement.GetProperty("source_sha256").GetString());
            var store = new SidebandStateStore(root);
            for (byte value = 1; value <= 3; value++)
                await store.AddMessageAsync(new(new Guid(Enumerable.Repeat(value, 16).ToArray()), Hash(value == 3 ? 2 : 1),
                    value != 2, "", "Synthetic history", DateTimeOffset.UtcNow, value == 2 ? "Queued" : "Received"), token);
            foreach (var value in new[] { 1, 2 }) await store.SetConversationTrustAsync(Hash(value), true, token);
            await using (var telemetry = await SidebandTelemetryStore.OpenAsync(Path.Combine(root, "telemetry"), cancellationToken: token))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var wire = SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.Create(now,
                    SidebandTelemetryCodec.FromData(SidebandTelemetrySensorId.Information,
                        SidebandTelemetryValue.Fields(("contents", SidebandTelemetryValue.From("Synthetic observation"))))));
                foreach (var value in new[] { 1, 2 })
                    Assert.Equal(SidebandTelemetryAppendResult.Added, await telemetry.AppendAsync(Hash(value), wire, now, token));
            }
            await using var client = new SidebandClient(store);
            var cases = vectors.RootElement.GetProperty("conversation_actions").EnumerateArray()
                .Where(item => item.GetProperty("action").GetString() == (delete ? "delete" : "clear")).ToArray();
            Assert.Equal(3, cases.Length);
            foreach (var item in cases)
            {
                await client.ClearConversationAsync(Hash(item.GetProperty("target").GetString() == "unknown" ? 9 : 1), delete, token);
                var state = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
                Assert.Equal(item.GetProperty("remaining_messages").EnumerateArray().Select(value => value.GetInt32()),
                    state.Messages.Select(message => (int)message.Id.ToByteArray()[0]).Order());
                Assert.Equal(item.GetProperty("remaining_conversations").EnumerateArray().Select(value => Hash(value.GetInt32())),
                    state.Contacts.Select(contact => contact.DeliveryHash).Order());
                Assert.All(state.Contacts, contact => Assert.True(contact.Trusted));
                Assert.Equal(item.GetProperty("remaining_telemetry").EnumerateArray().Select(value => Hash(value.GetInt32())),
                    (await client.GetTelemetryAsync(cancellationToken: token)).Select(report => report.SourceHash).Order());
                Assert.False(client.IsConnected);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string Hash(int value) => Convert.ToHexStringLower(Enumerable.Repeat((byte)value, 16).ToArray());
}

public sealed partial class SidebandHeadlessUiTests
{
    [AvaloniaFact]
    [Trait("TestId", "T531")]
    public async Task ConversationHistoryActionsRequireFreshConfirmationProtectDraftAndPersistOnlySelectedRemoval()
    {
        const string peer = "00112233445566778899aabbccddeeff";
        const string other = "ffeeddccbbaa99887766554433221100";
        var root = Path.Combine(AppContext.BaseDirectory, "conversation-actions-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            foreach (var destination in new[] { peer, other })
            {
                await store.AddMessageAsync(new(Guid.NewGuid(), destination, true, "", "Synthetic " + destination,
                    DateTimeOffset.UtcNow, "Received"), token);
                await store.SetConversationReadAsync(destination, true, token);
            }
            window = new MainWindow(root);
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("ContactList")?.ItemCount == 2);
            var contacts = window.FindControl<ListBox>("ContactList")!;
            var messages = window.FindControl<ListBox>("MessageList")!;
            var confirm = window.FindControl<CheckBox>("ConfirmConversationActionBox")!;
            var clear = window.FindControl<Button>("ClearConversationButton")!;
            var remove = window.FindControl<Button>("DeleteConversationButton")!;
            var draft = window.FindControl<TextBox>("MessageBox")!;
            contacts.SelectedIndex = 0;
            await WaitForAsync(() => messages.ItemCount == 1 && clear.IsEnabled);
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains("Confirm removal", StringComparison.Ordinal) == true);
            Assert.Equal(2, (await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Messages.Count);
            confirm.IsChecked = true;
            contacts.SelectedIndex = 1;
            await WaitForAsync(() => confirm.IsChecked == false);
            contacts.SelectedIndex = 0;
            await WaitForAsync(() => window.FindControl<TextBlock>("ConversationPeerText")?.Text?.Contains(peer, StringComparison.Ordinal) == true);
            draft.Text = "Unsent draft stays";
            confirm.IsChecked = true;
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => messages.ItemCount == 0 && clear.IsEnabled);
            var cleared = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.Equal(other, Assert.Single(cleared.Messages).PeerDeliveryHash);
            Assert.Equal(2, cleared.Contacts.Count);
            Assert.Equal("Unsent draft stays", draft.Text);
            confirm.IsChecked = true;
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains("current draft", StringComparison.Ordinal) == true);
            Assert.Equal(2, (await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts.Count);
            draft.Text = string.Empty;
            confirm.IsChecked = true;
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => contacts.ItemCount == 1 && messages.ItemCount == 1);
            Assert.False(confirm.IsChecked);
            await window.CloseAndWaitAsync();
            window = new MainWindow(root);
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("ContactList")?.ItemCount == 1);
            var persisted = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.Equal(other, Assert.Single(persisted.Contacts).DeliveryHash);
            Assert.Equal(other, Assert.Single(persisted.Messages).PeerDeliveryHash);
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
