using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandObjectUiTests
{
    [AvaloniaFact]
    public async Task ClassificationSeparatesListsPreservesSelectionAndRestoresHistoryAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-object-ui-" + Guid.NewGuid().ToString("N"));
        const string objectPeer = "00112233445566778899aabbccddeeff";
        const string ordinaryPeer = "ffeeddccbbaa99887766554433221100";
        var token = TestContext.Current.CancellationToken;
        var devices = new SidebandVoiceLifecycleTests.Devices();
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            await store.AddContactAsync(SidebandContact.Create("Original object", objectPeer, trusted: true) with { IsObject = true }, token);
            await store.AddContactAsync(SidebandContact.Create("Ordinary peer", ordinaryPeer), token);
            await store.AddMessageAsync(new(Guid.NewGuid(), ordinaryPeer, true, "", "Retained history",
                DateTimeOffset.UtcNow, "Received"), token);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using (var telemetry = await SidebandTelemetryStore.OpenAsync(Path.Combine(root, "telemetry"), cancellationToken: token))
            {
                var report = SidebandTelemetryCodec.Create(now,
                    SidebandTelemetryCodec.FromLocation(new(51, 1, 0, 0, 0, 0, now)));
                await telemetry.AppendAsync(objectPeer, SidebandTelemetryCodec.Encode(report), now, token);
            }
            window = new MainWindow(root, Path.Combine(root, "unused-rns"), mediaBackend: devices);
            window.Show();
            await UntilAsync(() => window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
            Assert.Single(Items(window, "ContactList"));
            await UntilAsync(() => window.FindControl<TextBlock>("ConversationPeerText")!.Text!.Contains(ordinaryPeer, StringComparison.Ordinal));
            Assert.Contains(Items(window, "MessageList"), item => item.Contains("Retained history", StringComparison.Ordinal));
            Click(window, "ObjectsNavButton");
            Assert.Single(Items(window, "ObjectList"));
            Click(window, "InspectObjectButton");
            await UntilAsync(() => window.FindControl<TextBox>("TelemetryDetailsText")!.Text?.Contains(objectPeer, StringComparison.Ordinal) == true);
            Assert.True(window.FindControl<ScrollViewer>("TelemetryPage")!.IsVisible);
            Assert.Contains("not authenticated physical position", window.FindControl<TextBox>("TelemetryDetailsText")!.Text);
            Click(window, "ContactsNavButton");
            window.FindControl<ListBox>("ExpandedContactList")!.SelectedIndex = 1;
            Click(window, "ClassifyObjectButton");
            await UntilAsync(() => window.FindControl<TextBlock>("ObjectsStatusText")!.Text?.StartsWith("Object classification saved.", StringComparison.Ordinal) == true);
            Assert.Empty(Items(window, "ContactList"));
            Assert.Empty(Items(window, "MessageList"));
            Assert.Equal(2, Items(window, "ObjectList").Count());
            window.FindControl<ListBox>("ObjectList")!.SelectedIndex = 1;
            Click(window, "RestoreObjectButton");
            await UntilAsync(() => window.FindControl<TextBlock>("ObjectsStatusText")!.Text?.StartsWith("Conversation classification restored.", StringComparison.Ordinal) == true);
            Assert.Single(Items(window, "ContactList"));
            Assert.Single(Items(window, "ObjectList"));
            await window.CloseAndWaitAsync();
            window = new MainWindow(root, Path.Combine(root, "unused-rns"), mediaBackend: devices);
            window.Show();
            await UntilAsync(() => window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
            Assert.Single(Items(window, "ContactList"));
            Assert.Contains(ordinaryPeer, Items(window, "ContactList").Single());
            Assert.Single(Items(window, "ObjectList"));
            await UntilAsync(() => Items(window, "MessageList").Any(item => item.Contains("Retained history", StringComparison.Ordinal)));
            Assert.Equal(0, devices.Captures);
            Assert.Equal(0, devices.Playbacks);
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Click(MainWindow window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<string> Items(MainWindow window, string name) =>
        window.FindControl<ListBox>(name)!.ItemsSource?.Cast<string>() ?? [];
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(DateTime.UtcNow < deadline, "Object desktop state did not reach the asserted condition.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
