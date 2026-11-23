using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandAnnounceUiTests
{
    [AvaloniaFact]
    public async Task OriginalStreamInspectsConverseSelectsNodeDeletesAndRestartsWithoutGrantingTrust()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-announces-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        const string delivery = "00112233445566778899aabbccddeeff";
        const string propagation = "aabbccddeeff00112233445566778899";
        var devices = new SidebandVoiceLifecycleTests.Devices();
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            await store.AddContactAsync(SidebandContact.Create("Known name", delivery, trusted: true), token);
            await store.SetConversationTelemetryAsync(delivery, true, true, token);
            await store.ObserveAnnounceAsync(new(delivery, SidebandAnnounce.Delivery, DateTimeOffset.UtcNow,
                "Announced name", 13, Signal: new(-77, 12.5, 94)), token);
            await store.ObserveAnnounceAsync(new(propagation, SidebandAnnounce.Propagation, DateTimeOffset.UtcNow,
                "Propagation", 16, "lxmd 1.2.1", true), token);
            window = new MainWindow(root, mediaBackend: devices, reticulumConfig: null);
            window.Show();
            await Ready(window);
            Click(window, "AnnouncesNavButton");
            await Until(() => window.FindControl<ListBox>("AnnouncesList")!.ItemCount == 2 &&
                window.FindControl<Button>("RefreshAnnouncesButton")!.IsEnabled);
            Select(window, propagation);
            Assert.Contains("lxmd 1.2.1", window.FindControl<TextBlock>("AnnounceDetailsText")!.Text);
            Assert.False(window.FindControl<Button>("ConverseAnnounceButton")!.IsEnabled);
            Click(window, "UseAnnounceNodeButton");
            await Until(() => Status(window).StartsWith("Propagation node saved.", StringComparison.Ordinal));
            Click(window, "DeleteAnnounceButton");
            await Until(() => window.FindControl<ListBox>("AnnouncesList")!.ItemCount == 1 &&
                window.FindControl<Button>("RefreshAnnouncesButton")!.IsEnabled);
            Select(window, delivery);
            Assert.False(window.FindControl<Button>("UseAnnounceNodeButton")!.IsEnabled);
            Assert.Contains("-77", window.FindControl<TextBlock>("AnnounceDetailsText")!.Text);
            Click(window, "ConverseAnnounceButton");
            await Until(() => window.FindControl<ScrollViewer>("ConversationsPage")!.IsVisible &&
                window.FindControl<Button>("RefreshAnnouncesButton")!.IsEnabled);
            await window.CloseAndWaitAsync();
            var saved = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.Equal(propagation, saved.PropagationNode);
            var contact = Assert.Single(saved.Contacts);
            Assert.Equal("Known name", contact.DisplayName);
            Assert.True(contact.Trusted);
            Assert.True(contact.AllowTelemetryRequests);
            Assert.True(contact.SendTelemetry);
            Assert.Empty(saved.Messages);
            window = new MainWindow(root, mediaBackend: devices, reticulumConfig: null);
            window.Show();
            await Ready(window);
            Click(window, "AnnouncesNavButton");
            await Until(() => window.FindControl<ListBox>("AnnouncesList")!.ItemCount == 1);
            Assert.Equal(delivery, window.FindControl<TextBox>("AnnounceAddressBox")!.Text);
            Assert.Equal(0, devices.Captures);
            Assert.Equal(0, devices.Playbacks);
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task EmptyStreamDisablesActionsWithoutInventingContacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-announces-empty-" + Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        try
        {
            window = new MainWindow(root);
            window.Show();
            await Ready(window);
            Click(window, "AnnouncesNavButton");
            await Until(() => Status(window).StartsWith("Offline saved history", StringComparison.Ordinal));
            foreach (var name in new[] { "ConverseAnnounceButton", "UseAnnounceNodeButton", "CopyAnnounceButton", "DeleteAnnounceButton" })
                Assert.False(window.FindControl<Button>(name)!.IsEnabled);
            await window.CloseAndWaitAsync();
            Assert.Empty((await new SidebandStateStore(root).LoadAsync(cancellationToken: TestContext.Current.CancellationToken)).Contacts);
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Select(MainWindow window, string address)
    {
        var list = window.FindControl<ListBox>("AnnouncesList")!;
        list.SelectedIndex = list.ItemsSource!.Cast<string>().ToList().FindIndex(item => item.Contains(address, StringComparison.Ordinal));
        Assert.True(list.SelectedIndex >= 0);
    }
    private static string Status(MainWindow window) => window.FindControl<TextBlock>("AnnouncesStatusText")!.Text ?? "";
    private static void Click(MainWindow window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Task Ready(MainWindow window) => Until(() =>
        window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(DateTime.UtcNow < deadline, "Announce controls did not reach the required state.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
