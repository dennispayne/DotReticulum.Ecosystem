using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;
using LXMF;
using Sideband;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMessageRetryTests
{
    private const string Unknown = "00112233445566778899aabbccddeeff";

    [Fact]
    [Trait("TestId", "T412")]
    public async Task ExecutedPrimaryRetryCopiesOnlyTextAndDeletesBeforeDispatchIncludingDispatchFailure()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-message-retry.json"), TestContext.Current.CancellationToken));
        var root = fixture.RootElement;
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", root.GetProperty("revision").GetString());
        Assert.Equal("8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25", root.GetProperty("core_sha256").GetString());
        Assert.Equal("9b2580f76310356d2e1a30b038857fbad4ee346aad9e2a25ab237515fff39928", root.GetProperty("messages_sha256").GetString());
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            var events = item.GetProperty("events").EnumerateArray().Select(value => value.GetString()).ToArray();
            if (name == "invalid-utf8")
            {
                Assert.Empty(events);
                Assert.Equal(1, item.GetProperty("saved_rows").GetInt32());
                Assert.Equal("UnicodeDecodeError", item.GetProperty("error").GetString());
            }
            else
            {
                Assert.Equal(Encoding.UTF8.GetString(Convert.FromHexString(item.GetProperty("content_hex").GetString()!)),
                    item.GetProperty("composer").GetString());
                Assert.Equal(name == "send-failure" ? ["delete", "send"] : ["delete", "send", "dismiss", "reload"], events);
                Assert.Equal(0, item.GetProperty("saved_rows").GetInt32());
            }
        }
        var message = Failed(Unknown);
        Assert.True(SidebandMessageActions.CanRetryText(message));
        foreach (var invalid in new[]
        {
            message with { Incoming = true }, message with { DeliveryState = "Delivered" },
            message with { DeliveryState = "Queued" }, message with { DeliveryMethod = "Paper" },
            message with { PaperUri = "lxm://opaque" }, message with { Content = "" },
            message with { Content = " \t" }, message with { Content = new string('x', 65_537) },
        })
            Assert.False(SidebandMessageActions.CanRetryText(invalid));
        Assert.True(SidebandMessageActions.CanRetryText(message with { Content = new string('x', 65_536) }));
    }

    [Fact]
    [Trait("TestId", "T413")]
    public async Task InvalidOfflineAndCancelledRetriesPreserveExactSavedBytesAndDisposedClientRejectsWork()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            var failed = await store.AddMessageAsync(Failed(Unknown), token);
            var bytes = await File.ReadAllBytesAsync(store.StatePath, token);
            await using var client = new SidebandClient(store);
            await Assert.ThrowsAsync<ArgumentException>(() => client.RetryMessageTextAsync("bad", failed.Id, token));
            await Assert.ThrowsAsync<ArgumentException>(() => client.RetryMessageTextAsync(Unknown, Guid.Empty, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.RetryMessageTextAsync(Unknown, Guid.NewGuid(), token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.RetryMessageTextAsync(new string('f', 32), failed.Id, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.RetryMessageTextAsync(Unknown, failed.Id, token));
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RetryMessageTextAsync(Unknown, failed.Id, cancelled.Token));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(store.StatePath, token));
            await client.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.RetryMessageTextAsync(Unknown, failed.Id, token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T414")]
    public async Task AtomicReplacementRejectsChangedSourceAndCancellationWithoutLosingHistory()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            var original = await store.AddMessageAsync(Failed(Unknown), token);
            var other = await store.AddMessageAsync(original with { PeerDeliveryHash = new string('f', 32), Content = "Other peer, same local ID" }, token);
            var replacement = original with { Id = Guid.NewGuid(), DeliveryState = "Queued", Title = "" };
            var bytes = await File.ReadAllBytesAsync(store.StatePath, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.AddMessageAsync(replacement with { Content = "Different" }, true, token, original.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.AddMessageAsync(replacement with { PeerDeliveryHash = new string('f', 32) }, true, token, original.Id));
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.AddMessageAsync(replacement, true, cancelled.Token, original.Id));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(store.StatePath, token));
            await store.AddMessageAsync(replacement, true, token, original.Id);
            await store.UpdateMessageStateAsync(original.Id, "Failed", token);
            var persisted = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            var saved = Assert.Single(persisted.Messages, message => message.PeerDeliveryHash == Unknown);
            Assert.Equal(replacement, saved);
            Assert.Contains(other, persisted.Messages);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddMessageAsync(replacement, true, token, original.Id));
            Assert.Equal(2, (await store.LoadAsync(cancellationToken: token)).Messages.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T415")]
    public async Task RetryAdmissionCancellationAndConcurrentSelectionRemainBoundedWithHealthyDeliveryAndRestart()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            await using var peer = await MessageRetryPeer.StartAsync(root, token);
            var store = new SidebandStateStore(Path.Combine(root, "data"));
            await using var client = new SidebandClient(store);
            await client.ConfigureAnnouncementAsync(new(false), token);
            await client.ConnectAsync(peer.ClientConfig, token);
            var own = client.ConnectionInfo!.DeliveryHash;
            var missing = await store.AddMessageAsync(Failed(Unknown), token);
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            var pending = Enumerable.Range(0, SidebandClient.MaximumPendingMessageRetries)
                .Select(_ => client.RetryMessageTextAsync(Unknown, missing.Id, cancelled.Token)).ToArray();
            Assert.All(pending, operation => Assert.False(operation.IsCompleted));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.RetryMessageTextAsync(Unknown, missing.Id, token));
            await cancelled.CancelAsync();
            foreach (var operation in pending) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            await peer.Messages.AnnounceAsync(cancellationToken: token);
            var original = await store.AddMessageAsync(Failed(peer.Hash), token);
            var other = await store.AddMessageAsync(Failed(peer.Hash) with { Incoming = true, Content = "Keep incoming" }, token);
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                try { return await client.RetryMessageTextAsync(peer.Hash, original.Id, token); }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("Select an outgoing failed", StringComparison.Ordinal)) { return null; }
            }));
            var queued = Assert.Single(outcomes, outcome => outcome is not null)!;
            Assert.NotEqual(original.Id, queued.Id);
            Assert.Equal(original.Content, queued.Content);
            Assert.Equal("", queued.Title);
            Assert.Null(queued.Attachments);
            Assert.Null(queued.Image);
            Assert.Null(queued.Audio);
            await MessageRetryPeer.UntilAsync(() => Task.FromResult(peer.Received.Count == 1), token);
            Assert.Equal(original.Content, Assert.Single(peer.Received).Content);
            await MessageRetryPeer.UntilAsync(async () => (await client.LoadAsync(token)).Messages
                .Any(message => message.Id == queued.Id && message.DeliveryState == "Delivered"), token);
            await client.ConfigurePropagationAsync(peer.Hash, true, token);
            var refused = await store.AddMessageAsync(Failed(peer.Hash) with { Content = "Queue refusal then healthy current-policy retry" }, token);
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RetryMessageTextAsync(peer.Hash, refused.Id, token));
            Assert.Contains("Propagation node has not announced valid limits and stamp costs", refusal.Message);
            await MessageRetryPeer.UntilAsync(async () => (await client.LoadAsync(token)).Messages
                .Any(message => message.Content == refused.Content && message.DeliveryState == "Failed" && message.Id != refused.Id), token);
            var refusedReplacement = Assert.Single((await client.LoadAsync(token)).Messages, message => message.Content == refused.Content);
            Assert.Equal("Propagated", refusedReplacement.DeliveryMethod);
            await client.ConfigurePropagationAsync(null, false, token);
            var recovered = await client.RetryMessageTextAsync(peer.Hash, refusedReplacement.Id, token);
            Assert.Equal("Direct", recovered.DeliveryMethod);
            await MessageRetryPeer.UntilAsync(() => Task.FromResult(peer.Received.Count == 2), token);
            var blocked = client.RetryMessageTextAsync(Unknown, missing.Id, token);
            Assert.False(blocked.IsCompleted);
            await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(15), token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
            var reopened = await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token);
            Assert.Equal(own, reopened.OwnDeliveryHash);
            Assert.DoesNotContain(reopened.Messages, message => message.Id == original.Id);
            Assert.Contains(other, reopened.Messages);
            Assert.Contains(missing, reopened.Messages);
            Assert.Contains(reopened.Messages, message => message.Id == queued.Id && message.Content == original.Content);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    internal static SidebandChatMessage Failed(string peer) => new(Guid.NewGuid(), peer, false, "Saved title",
        "Saved retry text\nsecond line", DateTimeOffset.UtcNow, "Failed");
    private static string Root() => Path.Combine(AppContext.BaseDirectory, "message-retry-" + Guid.NewGuid().ToString("N"));
}

public sealed partial class SidebandHeadlessUiTests
{
    [AvaloniaFact]
    [Trait("TestId", "T416")]
    public async Task ConfirmedRetryProtectsExistingDraftAndQueuesSelectedTextThroughOrdinaryConnectedWindow()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "message-retry-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        try
        {
            await using var peer = await MessageRetryPeer.StartAsync(root, token);
            var store = new SidebandStateStore(Path.Combine(root, "data"));
            var original = await store.AddMessageAsync(SidebandMessageRetryTests.Failed(peer.Hash), token);
            window = new MainWindow(store.DataDirectory, peer.ClientConfig);
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 1);
            window.FindControl<Button>("ConnectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("ToolbarStatusText")?.Text == "Connected");
            await peer.Messages.AnnounceAsync(cancellationToken: token);
            var list = window.FindControl<ListBox>("MessageList")!;
            var retry = window.FindControl<Button>("RetryMessageButton")!;
            var confirm = window.FindControl<CheckBox>("ConfirmRetryMessageBox")!;
            list.SelectedIndex = 0;
            Assert.True(retry.IsEnabled);
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains("Confirm retry", StringComparison.Ordinal) == true);
            confirm.IsChecked = true;
            window.FindControl<TextBox>("MessageBox")!.Text = "Existing draft";
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains("current draft", StringComparison.Ordinal) == true);
            Assert.Equal("Existing draft", window.FindControl<TextBox>("MessageBox")!.Text);
            Assert.Empty(peer.Received);
            window.FindControl<TextBox>("MessageBox")!.Text = "";
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await MessageRetryPeer.UntilAsync(() => Task.FromResult(peer.Received.Count == 1), token);
            Assert.Equal(original.Content, Assert.Single(peer.Received).Content);
            await WaitForAsync(() => !confirm.IsChecked.GetValueOrDefault() && retry.IsEnabled == false);
            await window.CloseAndWaitAsync();
            var reopened = await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token);
            var retried = Assert.Single(reopened.Messages);
            Assert.NotEqual(original.Id, retried.Id);
            Assert.Equal(original.Content, retried.Content);
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

internal sealed class MessageRetryPeer : IAsyncDisposable
{
    private readonly TcpServerPacketInterface _server = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly PacketInterfaceManager _manager;
    private readonly TransportRouter _router;
    private readonly Identity _identity = Identity.Generate();
    private readonly CancellationTokenSource _stop = new();
    private Task _run = Task.CompletedTask;
    public LxmRouter Messages { get; }
    public ConcurrentQueue<LxMessage> Received { get; } = new();
    public string Hash => Messages.DeliveryHashHex;
    public string ClientConfig { get; }

    private MessageRetryPeer(string root)
    {
        ClientConfig = Path.Combine(root, "rns");
        _manager = new([_server]);
        _router = new(_manager, new());
        Messages = new(_router, _identity, "Retry peer");
        Messages.MessageReceived = received => Received.Enqueue(received.Message);
    }

    public static async Task<MessageRetryPeer> StartAsync(string root, CancellationToken token)
    {
        var peer = new MessageRetryPeer(root);
        try
        {
            await peer._manager.StartAsync(token);
            peer._run = peer._router.RunAsync(peer._stop.Token);
            peer.Messages.Start();
            Directory.CreateDirectory(peer.ClientConfig);
            var port = Assert.IsType<IPEndPoint>(peer._server.LocalEndpoint).Port;
            await File.WriteAllTextAsync(Path.Combine(peer.ClientConfig, "config"),
                "[reticulum]\n share_instance = no\n enable_transport = no\n[logging]\n loglevel = 0\n[interfaces]\n" +
                $" [[Retry peer]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
            return peer;
        }
        catch { await peer.DisposeAsync(); throw; }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await condition()) await Task.Delay(20, deadline.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await Messages.DisposeAsync();
        await _stop.CancelAsync();
        try { await _run; } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        await _router.DisposeAsync();
        await _manager.DisposeAsync();
        _identity.Dispose();
        _stop.Dispose();
    }
}
