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
            await peer.Messages.AnnounceAsync(cancellationToken: token);
            await MessageRetryPeer.UntilAsync(async () => (await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token)).Announces?.Any(item =>
                item.DestinationHash == peer.Hash && item.StampCost == 254) == true, token);
            var draft = window.FindControl<TextBox>("MessageBox")!;
            var send = window.FindControl<Button>("SendButton")!;
            var cancel = window.FindControl<Button>("CancelMessageButton")!;
            draft.Text = "Keep cancelled draft";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 2);
            window.FindControl<ListBox>("MessageList")!.SelectedIndex = 1;
            Assert.True(cancel.IsEnabled);
            Assert.False(send.IsEnabled);
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => send.IsEnabled && !cancel.IsEnabled);
            await MessageRetryPeer.UntilAsync(async () => (await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token)).Messages.Any(message =>
                message.Content == "Keep cancelled draft" && message.DeliveryState == "Cancelled"), token);
            Assert.Equal("Keep cancelled draft", draft.Text);
            Assert.Empty(peer.Received);
            await window.CloseAndWaitAsync();
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
