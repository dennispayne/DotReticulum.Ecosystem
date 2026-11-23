using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandRemoteUiTests
{
    [AvaloniaFact]
    public async Task OfflinePoliciesPermissionsAndErrorsPersistWithoutOpeningDevices()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-remote-ui-" + Guid.NewGuid().ToString("N"));
        const string peer = "00112233445566778899aabbccddeeff";
        var token = TestContext.Current.CancellationToken;
        var devices = new SidebandVoiceLifecycleTests.Devices();
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            await store.AddContactAsync(SidebandContact.Create("Remote peer", peer), token);
            await store.SetConversationTelemetryAsync(peer, true, false, token);
            var announcedOnly = JsonNode.Parse(await File.ReadAllTextAsync(store.StatePath, token))!;
            announcedOnly["Contacts"]![0]!["ConversationKnown"] = false;
            await File.WriteAllTextAsync(store.StatePath, announcedOnly.ToJsonString(), token);
            window = new MainWindow(root, Path.Combine(root, "unused-rns"), mediaBackend: devices);
            window.Show();
            await ReadyAsync(window);
            var windowStore = Assert.IsType<SidebandStateStore>(typeof(MainWindow)
                .GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            var gate = Assert.IsType<SemaphoreSlim>(typeof(SidebandStateStore)
                .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(windowStore));
            await gate.WaitAsync(token);
            try
            {
                Click(window, "RemoteNavButton");
                Assert.False(window.FindControl<Button>("SaveRemoteAuthorizationButton")!.IsEnabled);
                Assert.False(window.FindControl<CheckBox>("RemoteRequestsTrustedBox")!.IsEnabled);
                window.FindControl<TextBox>("RemoteCommandPeerBox")!.Text = peer.ToUpperInvariant();
                Click(window, "RemoteNavButton");
            }
            finally { gate.Release(); }
            await UntilAsync(() => window.FindControl<Button>("SaveRemoteAuthorizationButton")!.IsEnabled);
            Assert.Equal(peer.ToUpperInvariant(), window.FindControl<TextBox>("RemoteCommandPeerBox")!.Text);
            Assert.True(window.FindControl<CheckBox>("RemoteRequestsTrustedBox")!.IsEnabled);
            Assert.StartsWith("Request policy loaded.", Status(window));
            Assert.Contains("Discovered contact only", window.FindControl<TextBlock>("RemoteConversationPolicyText")!.Text);
            window.FindControl<CheckBox>("RemoteRequestsTrustedBox")!.IsChecked = true;
            window.FindControl<CheckBox>("RemoteIgnoreUnknownBox")!.IsChecked = true;
            Click(window, "SaveRemoteAuthorizationButton");
            await UntilAsync(() => Status(window).StartsWith("Request policy saved.", StringComparison.Ordinal));
            window.FindControl<CheckBox>("RemoteConversationRequestsBox")!.IsChecked = true;
            Click(window, "SaveRemoteConversationButton");
            await UntilAsync(() => Status(window).StartsWith("Conversation request permission saved", StringComparison.Ordinal));
            Assert.DoesNotContain("Discovered contact only", window.FindControl<TextBlock>("RemoteConversationPolicyText")!.Text);
            window.FindControl<TextBox>("RemoteCommandBox")!.Text = "ping";
            Click(window, "SendRemoteCommandButton");
            await UntilAsync(() => Status(window).Contains("Connect to Reticulum", StringComparison.Ordinal));
            Assert.Equal("ping", window.FindControl<TextBox>("RemoteCommandBox")!.Text);
            window.FindControl<TextBox>("RemoteCommandPeerBox")!.Text = "bad";
            Click(window, "SendRemoteCommandButton");
            await UntilAsync(() => Status(window).Contains("32-character", StringComparison.Ordinal));
            Click(window, "CancelRemoteCommandsButton");
            await UntilAsync(() => Status(window).StartsWith("Cancellation requested", StringComparison.Ordinal));
            await window.CloseAndWaitAsync();
            var state = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.False(state.Telemetry.Enabled);
            Assert.False(state.Telemetry.AllowRequestsFromAnyone);
            Assert.True(state.Telemetry.AllowRequestsFromTrusted);
            Assert.True(state.ImportedSettings!["lxmf_ignore_unknown"].GetBoolean());
            Assert.True(Assert.Single(state.Contacts).SendTelemetry);
            Assert.True(Assert.Single(state.Contacts).AllowTelemetryRequests);
            Assert.True(Assert.Single(state.Contacts).ConversationKnown);
            Assert.False(Assert.Single(state.Contacts).Trusted);
            window = new MainWindow(root, Path.Combine(root, "unused-rns"), mediaBackend: devices);
            window.Show();
            await ReadyAsync(window);
            Click(window, "RemoteNavButton");
            await UntilAsync(() => window.FindControl<CheckBox>("RemoteIgnoreUnknownBox")!.IsChecked == true);
            Assert.True(window.FindControl<CheckBox>("RemoteRequestsTrustedBox")!.IsChecked);
            Assert.True(window.FindControl<CheckBox>("RemoteConversationRequestsBox")!.IsChecked);
            await window.CloseAndWaitAsync();
            Assert.Empty((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Messages);
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
    public async Task RealDesktopCommandsReceiveRepliesAndCancelUnreachableWorkBeforeClose()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-remote-network-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        try
        {
            var serverConfig = Path.Combine(root, "server-rns");
            var clientConfig = Path.Combine(root, "client-rns");
            Directory.CreateDirectory(serverConfig);
            Directory.CreateDirectory(clientConfig);
            var common = "[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n";
            await File.WriteAllTextAsync(Path.Combine(serverConfig, "config"), common +
                $" [[Remote]]\n type = TCPServerInterface\n enabled = yes\n listen_ip = 127.0.0.1\n listen_port = {port}\n", token);
            await File.WriteAllTextAsync(Path.Combine(clientConfig, "config"), common +
                $" [[Remote]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
            await using var server = new SidebandClient(new(Path.Combine(root, "server")));
            await server.ConfigureCommandAuthorizationAsync(new(AllowRequestsFromAnyone: true), token);
            await server.ConnectAsync(serverConfig, token);
            var peer = server.ConnectionInfo!.DeliveryHash;
            var data = Path.Combine(root, "client");
            await new SidebandStateStore(data).AddContactAsync(SidebandContact.Create("Remote", peer), token);
            window = new MainWindow(data, clientConfig);
            window.Show();
            await ReadyAsync(window);
            Click(window, "ConnectButton");
            await UntilAsync(() => window.FindControl<TextBlock>("ToolbarStatusText")!.Text == "Connected");
            Click(window, "AnnounceNowButton");
            await UntilAsync(() => window.FindControl<TextBlock>("AnnouncementStatusText")!.Text == "Delivery destination announced.");
            await server.AnnounceAsync(token);
            Click(window, "RemoteNavButton");
            await UntilAsync(() => window.FindControl<TextBox>("RemoteCommandPeerBox")!.Text == peer);
            foreach (var (command, reply) in new[] { ("ping", "Ping reply"), ("echo echo repeated", "Echo reply: repeated") })
            {
                window.FindControl<TextBox>("RemoteCommandBox")!.Text = command;
                Click(window, "SendRemoteCommandButton");
                await UntilAsync(() => History(window).Any(value => value.Contains(reply, StringComparison.Ordinal)));
                Assert.Equal("", window.FindControl<TextBox>("RemoteCommandBox")!.Text);
            }
            window.FindControl<TextBox>("RemoteCommandPeerBox")!.Text = new string('a', 32);
            window.FindControl<TextBox>("RemoteCommandBox")!.Text = "ping";
            Click(window, "SendRemoteCommandButton");
            await UntilAsync(() => Status(window).StartsWith("Queued command ", StringComparison.Ordinal));
            Click(window, "CancelRemoteCommandsButton");
            await UntilAsync(() => Status(window).StartsWith("Cancellation requested", StringComparison.Ordinal));
            await window.CloseAndWaitAsync();
            Assert.Contains((await new SidebandStateStore(data).LoadAsync(cancellationToken: token)).Messages,
                message => message.Incoming && message.Content == "Ping reply");
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Click(MainWindow window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string Status(MainWindow window) => window.FindControl<TextBlock>("RemoteStatusText")!.Text ?? "";
    private static IEnumerable<string> History(MainWindow window) =>
        window.FindControl<ListBox>("RemoteHistoryList")!.ItemsSource?.Cast<string>() ?? [];
    private static Task ReadyAsync(MainWindow window) => UntilAsync(() =>
        window.FindControl<TextBlock>("StatusText")!.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(DateTime.UtcNow < deadline, "Remote desktop state did not reach the asserted condition.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
