using System.Reflection;
using System.Text.Json;
using System.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Sideband;
using Sideband.Desktop;
using DotReticulum.Core;
using LXMF;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMessageCancellationTests
{
    private const string Peer = "00112233445566778899aabbccddeeff";

    [Fact]
    [Trait("TestId", "T485")]
    public async Task CancellationAvailabilityIsPublishedAfterHashlessPreparationAdmission()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            await using var peer = await MessageRetryPeer.StartAsync(root, token);
            peer.Messages.SetInboundStampCost(254);
            await using var client = new SidebandClient(new(Path.Combine(root, "data")));
            await client.ConnectAsync(peer.ClientConfig, token);
            await peer.Messages.AnnounceAsync(cancellationToken: token);
            await MessageRetryPeer.UntilAsync(async () => (await client.LoadAsync(token)).Announces?.Any(item =>
                item.DestinationHash == peer.Hash && item.StampCost == 254) == true, token);
            var observed = new TaskCompletionSource<(Guid Id, bool Available)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var admitted = new TaskCompletionSource<(Guid Id, bool Available)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            client.ErrorReported += errors.Enqueue;
            client.StateChanged += state =>
            {
                var queued = state.Messages.FirstOrDefault(message => message.Content == "Admission boundary");
                if (queued is not null) observed.TrySetResult((queued.Id, client.CanRequestMessageCancellation(peer.Hash, queued.Id)));
            };
            client.MessageCancellationAvailabilityChanged += _ => throw new InvalidOperationException("injected observer failure");
            client.MessageCancellationAvailabilityChanged += id =>
                admitted.TrySetResult((id, client.CanRequestMessageCancellation(peer.Hash, id)));
            var sending = client.SendTextAsync(peer.Hash, "Admission boundary", token);
            var observation = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(observation.Available);
            var readiness = await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal(observation.Id, readiness.Id);
            Assert.True(readiness.Available);
            Assert.Contains(errors, error => error.Contains("injected observer failure", StringComparison.Ordinal));
            await client.RequestMessageCancellationAsync(peer.Hash, observation.Id, token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(5), token));
            Assert.Empty(peer.Received);
            Assert.False(client.CanRequestMessageCancellation(peer.Hash, observation.Id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T448")]
    public async Task TrackingCapacityIsExactLateOldCallbacksCannotRemoveReplacementAndDisposalClearsOwnership()
    {
        var root = Root();
        try
        {
            await using var peer = await MessageRetryPeer.StartAsync(root, TestContext.Current.CancellationToken);
            await using var client = new SidebandClient(new(Path.Combine(root, "data")));
            using var identity = Identity.Generate();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var track = typeof(SidebandClient).GetMethod("TrackOutgoingMessage", flags)!;
            var untrack = typeof(SidebandClient).GetMethod("UntrackOutgoingMessage", flags)!;
            var entries = Assert.IsAssignableFrom<IDictionary>(typeof(SidebandClient).GetField("_activeOutgoingMessages", flags)!.GetValue(client));
            var firstId = Guid.NewGuid();
            var old = LxMessage.Create(Convert.FromHexString(peer.Hash), identity, "Synthetic tracked owner");
            track.Invoke(client, [firstId, peer.Hash, peer.Messages, old]);
            for (var index = 1; index < SidebandClient.MaximumTrackedOutgoingMessages; index++)
                track.Invoke(client, [Guid.NewGuid(), peer.Hash, peer.Messages, LxMessage.Create(Convert.FromHexString(peer.Hash), identity, "Synthetic tracked owner")]);
            Assert.Equal(257, entries.Count);
            var overflow = Assert.Throws<TargetInvocationException>(() => track.Invoke(client,
                [Guid.NewGuid(), peer.Hash, peer.Messages, LxMessage.Create(Convert.FromHexString(peer.Hash), identity, "Must refuse")]));
            Assert.IsType<InvalidOperationException>(overflow.InnerException);
            Assert.Equal(257, entries.Count);
            untrack.Invoke(client, [firstId, old]);
            var replacement = LxMessage.Create(Convert.FromHexString(peer.Hash), identity, "New retry owner");
            track.Invoke(client, [firstId, peer.Hash, peer.Messages, replacement]);
            untrack.Invoke(client, [firstId, old]);
            Assert.Equal(257, entries.Count);
            untrack.Invoke(client, [firstId, replacement]);
            Assert.Equal(256, entries.Count);
            await client.DisposeAsync();
            Assert.Empty(entries);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T440")]
    public async Task ActualPrimaryCancellationDistinguishesStateEffectsFromUnknownIdSuccess()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-message-cancellation.json"), TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", root.GetProperty("revision").GetString());
        Assert.Equal("56b6a8e6b9f79256c4ae297340791eebcc1b6cda3cec04ab2dbe2f1efb53caa9", root.GetProperty("router_sha256").GetString());
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            Assert.Equal(name != "router-error", item.GetProperty("returned").GetBoolean());
            Assert.Equal(name is "unknown" or "router-error" ? 1 : 254, item.GetProperty("state").GetInt32());
            string[] expected = name switch
            {
                "packet" => ["process-outbound"], "resource" => ["resource-cancel", "process-outbound"],
                "router-error" => ["core-error", "core-trace"], _ => [],
            };
            Assert.Equal(expected, item.GetProperty("events").EnumerateArray().Select(value => value.GetString()));
        }
    }

    [Fact]
    [Trait("TestId", "T441")]
    public async Task InvalidHistoricalAndBoundedWaitingCancellationPreserveBytesAndCancelWithoutLifecycleGate()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            var message = await store.AddMessageAsync(SidebandMessageRetryTests.Failed(Peer), token);
            await using var client = new SidebandClient(store);
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            Assert.False(client.CanRequestMessageCancellation(Peer, message.Id));
            await Assert.ThrowsAsync<ArgumentException>(() => client.RequestMessageCancellationAsync("bad", message.Id, token));
            await Assert.ThrowsAsync<ArgumentException>(() => client.RequestMessageCancellationAsync(Peer, Guid.Empty, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestMessageCancellationAsync(Peer, message.Id, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestMessageCancellationAsync(Peer, Guid.NewGuid(), token));
            using var stop = new CancellationTokenSource();
            var gate = Assert.IsType<SemaphoreSlim>(typeof(SidebandStateStore)
                .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store));
            await gate.WaitAsync(token);
            try
            {
                var operations = Enumerable.Range(0, SidebandClient.MaximumPendingMessageCancellations)
                    .Select(_ => client.RequestMessageCancellationAsync(Peer, message.Id, stop.Token)).ToArray();
                Assert.All(operations, operation => Assert.False(operation.IsCompleted));
                await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestMessageCancellationAsync(Peer, message.Id, token));
                await stop.CancelAsync();
                foreach (var operation in operations) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
                var disposingOperations = Enumerable.Range(0, SidebandClient.MaximumPendingMessageCancellations)
                    .Select(_ => client.RequestMessageCancellationAsync(Peer, message.Id, token)).ToArray();
                Assert.All(disposingOperations, operation => Assert.False(operation.IsCompleted));
                await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
                foreach (var operation in disposingOperations)
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5), token));
            }
            finally { gate.Release(); }
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            await client.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.RequestMessageCancellationAsync(Peer, message.Id, token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T442")]
    public async Task RepeatedStampPreparationCancellationNeedsNoSubscriberAndRecoversForRealDeliveryAndRestart()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            await using var peer = await MessageRetryPeer.StartAsync(root, token);
            peer.Messages.SetInboundStampCost(254);
            var store = new SidebandStateStore(Path.Combine(root, "data"));
            await using var client = new SidebandClient(store);
            await client.ConnectAsync(peer.ClientConfig, token);
            await peer.Messages.AnnounceAsync(cancellationToken: token);
            await MessageRetryPeer.UntilAsync(async () => (await client.LoadAsync(token)).Announces?.Any(item =>
                item.DestinationHash == peer.Hash && item.StampCost == 254) == true, token);
            for (var index = 0; index < 3; index++)
            {
                var content = "Cancel preparation " + index;
                var sending = client.SendTextAsync(peer.Hash, content, token);
                await MessageRetryPeer.UntilAsync(async () => (await client.LoadAsync(token)).Messages.Any(message => message.Content == content), token);
                var selected = Assert.Single((await client.LoadAsync(token)).Messages, message => message.Content == content);
                await MessageRetryPeer.UntilAsync(() => Task.FromResult(client.CanRequestMessageCancellation(peer.Hash, selected.Id)), token);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    client.RequestMessageCancellationAsync(new string('f', 32), selected.Id, token));
                await client.RequestMessageCancellationAsync(peer.Hash, selected.Id, token).WaitAsync(TimeSpan.FromSeconds(5), token);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WaitAsync(TimeSpan.FromSeconds(5), token));
                await MessageRetryPeer.UntilAsync(async () => (await client.LoadAsync(token)).Messages.Any(message =>
                    message.Id == selected.Id && message.DeliveryState == "Cancelled"), token);
                Assert.False(client.CanRequestMessageCancellation(peer.Hash, selected.Id));
                await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestMessageCancellationAsync(peer.Hash, selected.Id, token));
            }
            Assert.Empty(peer.Received);
            peer.Messages.SetInboundStampCost(null);
            await Task.Delay(TimeSpan.FromSeconds(1.1), token);
            await peer.Messages.AnnounceAsync(cancellationToken: token);
            await MessageRetryPeer.UntilAsync(async () => (await client.LoadAsync(token)).Announces?.Any(item =>
                item.DestinationHash == peer.Hash && item.StampCost is null) == true, token);
            await client.SendTextAsync(peer.Hash, "Continued healthy delivery", token);
            await MessageRetryPeer.UntilAsync(() => Task.FromResult(peer.Received.Count == 1), token);
            await MessageRetryPeer.UntilAsync(async () => (await client.LoadAsync(token)).Messages.Any(message =>
                message.Content == "Continued healthy delivery" && message.DeliveryState == "Delivered"), token);
            await client.DisconnectAsync();
            var reopened = await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token);
            Assert.Equal(3, reopened.Messages.Count(message => message.DeliveryState == "Cancelled"));
            var delivered = Assert.Single(reopened.Messages, message => message.DeliveryState == "Delivered");
            await client.ConnectAsync(peer.ClientConfig, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestMessageCancellationAsync(peer.Hash, delivered.Id, token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string Root() => Path.Combine(AppContext.BaseDirectory, "message-cancellation-" + Guid.NewGuid().ToString("N"));
}

public sealed partial class SidebandHeadlessUiTests
{
    [AvaloniaFact]
    [Trait("TestId", "T443")]
    public async Task SelectedActiveCancellationWorksWhileSendOwnsLifecycleAndPreservesDraft()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "message-cancellation-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        using var releaseAdmission = new ManualResetEventSlim();
        try
        {
            await using var peer = await MessageRetryPeer.StartAsync(root, token);
            peer.Messages.SetInboundStampCost(254);
            var store = new SidebandStateStore(Path.Combine(root, "data"));
            await store.AddMessageAsync(SidebandMessageRetryTests.Failed(peer.Hash), token);
            window = new MainWindow(store.DataDirectory, peer.ClientConfig);
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 1);
            window.FindControl<Button>("ConnectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("ToolbarStatusText")?.Text == "Connected");
            window.FindControl<Button>("AnnouncesNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await peer.Messages.AnnounceAsync(cancellationToken: token);
            await WaitForAsync(() => window.FindControl<TextBox>("AnnounceAddressBox")?.Text == peer.Hash &&
                window.FindControl<TextBlock>("AnnounceDetailsText")?.Text?.Contains("Stamp cost: 254", StringComparison.Ordinal) == true);
            window.FindControl<Button>("ConversationsNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var draft = window.FindControl<TextBox>("MessageBox")!;
            var send = window.FindControl<Button>("SendButton")!;
            var cancel = window.FindControl<Button>("CancelMessageButton")!;
            var client = Assert.IsType<SidebandClient>(typeof(MainWindow).GetField("_client",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            var uiStateChanged = (Action<SidebandState>)Delegate.CreateDelegate(typeof(Action<SidebandState>), window,
                typeof(MainWindow).GetMethod("OnStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!);
            client.StateChanged -= uiStateChanged;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            await (Task)typeof(MainWindow).GetMethod("DrainPendingOperationsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.StateChanged += state =>
            {
                if (!state.Messages.Any(message => message.Content == "Keep cancelled draft")) return;
                published.TrySetResult();
                if (!releaseAdmission.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("The test did not release router admission.");
            };
            draft.Text = "Keep cancelled draft";
            var lifecycle = Assert.IsType<SemaphoreSlim>(typeof(SidebandClient).GetField("_lifecycle",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client));
            await lifecycle.WaitAsync(token);
            try { send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            finally { lifecycle.Release(); }
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await (Task)typeof(MainWindow).GetMethod("RefreshMessagesAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [token])!;
            Assert.Equal(2, window.FindControl<ListBox>("MessageList")?.ItemCount);
            window.FindControl<ListBox>("MessageList")!.SelectedIndex = 1;
            Assert.False(cancel.IsEnabled);
            Assert.False(send.IsEnabled);
            releaseAdmission.Set();
            await WaitForAsync(() => cancel.IsEnabled);
            client.StateChanged += uiStateChanged;
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => send.IsEnabled && !cancel.IsEnabled);
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemsSource?.Cast<string>().Any(row =>
                row.Contains("Cancelled", StringComparison.Ordinal) && row.Contains("Keep cancelled draft", StringComparison.Ordinal)) == true);
            Assert.Equal("Keep cancelled draft", draft.Text);
            Assert.Empty(peer.Received);
            await window.CloseAndWaitAsync();
            var saved = await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token);
            Assert.Contains(saved.Messages, message =>
                message.Content == "Keep cancelled draft" && message.DeliveryState == "Cancelled");
        }
        finally
        {
            releaseAdmission.Set();
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
