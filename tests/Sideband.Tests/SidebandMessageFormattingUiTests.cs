using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sideband;
using Sideband.Desktop;

namespace Sideband.Tests;

public sealed partial class SidebandHeadlessUiTests
{
    [AvaloniaFact]
    [Trait("TestId", "T574")]
    public async Task FormattingPreferencesAndTrustRerenderActualRowsWithoutChangingRawDetailsDraftsOrHistory()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "message-format-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            var contact = SidebandContact.Create("Markup peer", "00112233445566778899aabbccddeeff");
            await store.AddContactAsync(contact, token);
            var message = new SidebandChatMessage(Guid.NewGuid(), contact.DeliveryHash, true, "", "**bold**",
                DateTimeOffset.UtcNow, "Received", Renderer: 2);
            await store.AddMessageAsync(message, false, token);
            window = new MainWindow(root);
            window.Show();
            await WaitForAsync(() => StyledRows(window).Any(runs => runs.Any(run => run.Text == "bold" && run.Style.Bold)));
            var list = window.FindControl<ListBox>("MessageList")!;
            list.SelectedIndex = 0;
            var details = window.FindControl<TextBox>("MessageDetailsBox")!;
            Assert.EndsWith("**bold**", details.Text);
            window.FindControl<TextBox>("MessageBox")!.Text = "Keep **draft**";
            window.FindControl<CheckBox>("ComposeMarkdownBox")!.IsChecked = true;
            window.FindControl<CheckBox>("TrustedMarkupOnlyBox")!.IsChecked = true;
            window.FindControl<Button>("SaveMessageFormattingButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => StyledRows(window).Any(runs => runs.Any(run => run.Text == "**bold**" && !run.Style.Bold)));
            Assert.Equal(0, list.SelectedIndex);
            Assert.EndsWith("**bold**", details.Text);
            Assert.Equal("Keep **draft**", window.FindControl<TextBox>("MessageBox")!.Text);
            Assert.Equal(message, Assert.Single((await store.LoadAsync(cancellationToken: token)).Messages));
            Assert.False(Assert.Single((await store.LoadAsync(cancellationToken: token)).Contacts).Trusted);
            window.FindControl<CheckBox>("TrustConversationBox")!.IsChecked = true;
            window.FindControl<Button>("SaveConversationTrustButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => StyledRows(window).Any(runs => runs.Any(run => run.Text == "bold" && run.Style.Bold)));
            Assert.Equal("Keep **draft**", window.FindControl<TextBox>("MessageBox")!.Text);
            await window.CloseAndWaitAsync();
            window = new MainWindow(root);
            window.Show();
            await WaitForAsync(() => StyledRows(window).Any(runs => runs.Any(run => run.Text == "bold" && run.Style.Bold)));
            Assert.True(window.FindControl<CheckBox>("ComposeMarkdownBox")!.IsChecked);
            Assert.True(window.FindControl<CheckBox>("TrustedMarkupOnlyBox")!.IsChecked);
            await window.CloseAndWaitAsync();
            Assert.Equal(message, Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Messages));
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    [Trait("TestId", "T575")]
    public async Task DuplicateRawRowsRetainOwnRendererAndInvalidFormattingRemainsVisibleWithoutBreakingOtherRows()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "message-format-invalid-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            var contact = SidebandContact.Create("Markup peer", "00112233445566778899aabbccddeeff");
            await store.AddContactAsync(contact, token);
            var timestamp = DateTimeOffset.UtcNow;
            foreach (var renderer in new long?[] { null, 3 })
                await store.AddMessageAsync(new(Guid.NewGuid(), contact.DeliveryHash, true, "", "[b]same[/b]",
                    timestamp, "Received", Renderer: renderer), false, token);
            await store.AddMessageAsync(new(Guid.NewGuid(), contact.DeliveryHash, true, "", "[size=NaN]bad[/size]",
                timestamp, "Received", Renderer: 3), false, token);
            window = new MainWindow(root);
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")!.GetVisualDescendants().OfType<TextBlock>()
                .Count(block => block.Name == "FormattedMessageBody") == 3);
            var rows = StyledRows(window).ToArray();
            Assert.Contains(rows, runs => runs.Any(run => run.Text == "[b]same[/b]" && !run.Style.Bold));
            Assert.Contains(rows, runs => runs.Any(run => run.Text == "same" && run.Style.Bold));
            var error = Assert.Single(window.FindControl<ListBox>("MessageList")!.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Name == "FormattedMessageBody" && block.Tag is InvalidDataException);
            var displayed = string.Concat(error.Inlines!.OfType<Run>().Select(run => run.Text));
            Assert.Contains("Formatting failed; raw text follows:", displayed);
            Assert.Contains("[size=NaN]bad[/size]", displayed);
            Assert.Equal(3, (await store.LoadAsync(cancellationToken: token)).Messages.Count);
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static IEnumerable<IReadOnlyList<SidebandTextRun>> StyledRows(MainWindow window) =>
        window.FindControl<ListBox>("MessageList")!.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.Name == "FormattedMessageBody")
            .Select(block => block.Tag).OfType<IReadOnlyList<SidebandTextRun>>();
}
