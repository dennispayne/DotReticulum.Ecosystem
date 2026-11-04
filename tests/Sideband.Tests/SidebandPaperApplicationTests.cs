using System.Net;
using System.Net.Sockets;
using DotReticulum.Applications;
using LXMF;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandPaperApplicationTests
{
    [Fact]
    public async Task OriginalPaperIsOfflineBoundedExportableAndSurvivesRestartWithDuplicateAwareImport()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "paper-app-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (serverConfig, peerConfig) = await ConfigurePair(root, token);
            var originStore = new SidebandStateStore(Path.Combine(root, "origin"));
            var recipientStore = new SidebandStateStore(Path.Combine(root, "recipient"));
            await using var origin = new SidebandClient(originStore);
            await using var recipient = new SidebandClient(recipientStore);
            await origin.ConnectAsync(serverConfig, token);
            await recipient.ConnectAsync(peerConfig, token);
            var target = recipient.ConnectionInfo!.DeliveryHash;
            var source = origin.ConnectionInfo!.DeliveryHash;
            await origin.AddContactAsync("Recipient", target, token);
            await origin.SetConversationTrustAsync(target, true, token);
            await origin.SetConversationTelemetryAsync(target, true, true, token);
            await origin.ConfigureTelemetryAsync(new() { Enabled = true, InformationEnabled = true, Information = "Authorized paper metadata" }, token);
            var errors = new List<string>();
            var observed = 0;
            origin.StateChanged += _ => throw new InvalidOperationException("paper observer failure");
            origin.StateChanged += _ => Interlocked.Increment(ref observed);
            origin.ErrorReported += error => { lock (errors) errors.Add(error); };
            var paper = await origin.CreatePaperMessageAsync(target, "Signed original ✓\nsecond line", token);
            Assert.False(paper.Incoming);
            Assert.Equal("Paper", paper.DeliveryState);
            Assert.Equal("Paper", paper.DeliveryMethod);
            Assert.Equal("", paper.Title);
            Assert.NotNull(paper.PaperUri);
            Assert.Equal(Convert.FromHexString(target), LxMessage.GetPaperDestinationHash(paper.PaperUri));
            Assert.True(Volatile.Read(ref observed) > 0);
            lock (errors) Assert.Contains(errors, error => error.Contains("paper observer failure", StringComparison.Ordinal));
            Assert.Empty((await recipient.LoadAsync(token)).Messages);
            // Reverse creation also resolves the source identity before validating received metadata.
            var reverse = await recipient.CreatePaperMessageAsync(source, "Return offline message", token);
            Assert.DoesNotContain((await recipient.LoadAsync(token)).Messages, message => message.Incoming);
            Assert.Equal(SidebandPaperIngestResult.NonLocal, await origin.IngestPaperUriAsync(paper.PaperUri, token));
            Assert.Equal(SidebandPaperIngestResult.Rejected, await recipient.IngestPaperUriAsync("lxm://not-a-message", token));
            var tampered = paper.PaperUri[..^8] + (paper.PaperUri[^8] == 'A' ? "B" : "A") + paper.PaperUri[^7..];
            Assert.Equal(SidebandPaperIngestResult.Rejected, await recipient.IngestPaperUriAsync(tampered, token));
            using (var cancelled = new CancellationTokenSource())
            {
                await cancelled.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recipient.IngestPaperUriAsync(paper.PaperUri, cancelled.Token));
            }
            Assert.Equal(SidebandPaperIngestResult.LocalDelivery, await recipient.IngestPaperUriAsync(paper.PaperUri, token));
            Assert.Equal(SidebandPaperIngestResult.Duplicate, await recipient.IngestPaperUriAsync(paper.PaperUri, token));
            await recipient.DisconnectAsync();
            var received = Assert.Single((await recipient.LoadAsync(token)).Messages, message => message.Incoming);
            Assert.Equal(paper.Content, received.Content);
            Assert.Equal(source, received.PeerDeliveryHash);
            Assert.Equal("Received", received.DeliveryState);
            // Pinned LXMF ingestion deliberately routes paper via its propagated inbound pipeline.
            Assert.Equal("Propagated", received.DeliveryMethod);
            Assert.NotEmpty(await recipient.GetTelemetryAsync(cancellationToken: token));
            await recipient.ConnectAsync(peerConfig, token);
            Assert.Equal(SidebandPaperIngestResult.Duplicate, await recipient.IngestPaperUriAsync(paper.PaperUri, token));
            Assert.Single((await recipient.LoadAsync(token)).Messages, message => message.Incoming);
            await Assert.ThrowsAsync<ArgumentException>(() => origin.CreatePaperMessageAsync(target, new string('界', 1500), token));
            Assert.Single((await origin.LoadAsync(token)).Messages);
            var whitespace = await origin.CreatePaperMessageAsync(target, " ", token);
            Assert.Equal(" ", whitespace.Content);
            Assert.Equal(SidebandPaperIngestResult.LocalDelivery, await recipient.IngestPaperUriAsync(whitespace.PaperUri!, token));
            await recipient.DisconnectAsync();
            Assert.Contains((await recipient.LoadAsync(token)).Messages, message => message.Incoming && message.Content == " ");
            await origin.DisconnectAsync();
            Assert.Equal(paper.PaperUri, await origin.GetPaperMessageUriAsync(paper.Id, token));
            var export = Path.Combine(root, "message.lxm");
            await origin.ExportPaperMessageAsync(paper.Id, export, token);
            Assert.Equal(paper.PaperUri, await File.ReadAllTextAsync(export, token));
            await Assert.ThrowsAsync<IOException>(() => origin.ExportPaperMessageAsync(paper.Id, export, token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => origin.GetPaperMessageUriAsync(Guid.NewGuid(), token));
            await using var reopened = new SidebandClient(new(originStore.DataDirectory));
            Assert.Equal(paper.PaperUri, await reopened.GetPaperMessageUriAsync(paper.Id, token));
            Assert.Equal(paper, (await reopened.LoadAsync(token)).Messages.Single(message => message.Id == paper.Id));
            await origin.ConnectAsync(serverConfig, token);
            Assert.Equal(SidebandPaperIngestResult.LocalDelivery, await origin.IngestPaperUriAsync(reverse.PaperUri!, token));
            await origin.DisconnectAsync();
            Assert.Contains((await origin.LoadAsync(token)).Messages, message => message.Incoming && message.Content == reverse.Content);
        }

        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OrdinaryWhitespaceContentAndTitleAreNotSuppressedAsMetadataOnlyMessages()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "paper-whitespace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (serverConfig, peerConfig) = await ConfigurePair(root, token);
            await using var client = new SidebandClient(new(Path.Combine(root, "recipient")));
            await client.ConnectAsync(serverConfig, token);
            await using var peer = ReticulumNode.Open(peerConfig);
            using var identity = peer.LoadOrCreateIdentity("paper-whitespace");
            await peer.StartAsync(token);
            await using var router = new LxmRouter(peer.Router, identity);
            router.Start();
            foreach (var (content, title) in new[] { (" \t\n", ""), ("", " \t") })
            {
                var message = LxMessage.Create(Convert.FromHexString(client.ConnectionInfo!.DeliveryHash), identity, content, title);
                var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                message.StateChanged += state =>
                {
                    if (state.State == LxmState.Delivered) delivered.TrySetResult();
                    else if (state.State is LxmState.Failed or LxmState.Rejected)
                        delivered.TrySetException(new InvalidOperationException("Ordinary whitespace message delivery failed."));
                };
                await router.SendAsync(message, token);
                await delivered.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            }
            using var received = CancellationTokenSource.CreateLinkedTokenSource(token);
            received.CancelAfter(TimeSpan.FromSeconds(10));
            while ((await client.LoadAsync(received.Token)).Messages.Count < 2)
                await Task.Delay(10, received.Token);
            await client.DisconnectAsync();
            var messages = (await client.LoadAsync(token)).Messages;
            Assert.Equal(2, messages.Count);
            Assert.Contains(messages, message => message.Content == " \t\n" && message.Title == "");
            Assert.Contains(messages, message => message.Content == "" && message.Title == " \t");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StoreAcceptsLegacyPaperHistoryButRejectsUnboundedOrMismatchedExports()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "paper-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SidebandStateStore(root);
            var legacy = new SidebandChatMessage(Guid.NewGuid(), new string('1', 32), false, "", "Legacy paper",
                DateTimeOffset.UtcNow, "Paper", DeliveryMethod: "Paper");
            await store.AddMessageAsync(legacy, token);
            Assert.Null(Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Messages).PaperUri);
            using var fixture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-paper.json"), token));
            var uri = fixture.RootElement.GetProperty("cases")[0].GetProperty("uri").GetString()!;
            var destination = fixture.RootElement.GetProperty("destination").GetString()!;
            await Assert.ThrowsAsync<ArgumentException>(() => store.AddMessageAsync(legacy with { PaperUri = "" }, token));
            await Assert.ThrowsAsync<ArgumentException>(() => store.AddMessageAsync(legacy with { PaperUri = "lxm://" + new string('a', 5000) }, token));
            await Assert.ThrowsAsync<ArgumentException>(() => store.AddMessageAsync(legacy with { PaperUri = uri }, token));
            await Assert.ThrowsAsync<ArgumentException>(() => store.AddMessageAsync(legacy with
                { PaperUri = uri, PeerDeliveryHash = destination, DeliveryMethod = "Direct" }, token));
            var valid = legacy with { Id = Guid.NewGuid(), PaperUri = uri, PeerDeliveryHash = destination };
            await store.AddMessageAsync(valid, token);
            Assert.Equal(uri, (await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Messages.Last().PaperUri);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InvalidInputUnknownIdentityCancellationDisconnectAndDisposalKeepServiceAndHistoryIntact()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "paper-life-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = Path.Combine(root, "rns");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"), "[reticulum]\n share_instance = no\n enable_transport = no\n", token);
            var store = new SidebandStateStore(Path.Combine(root, "profile"));
            var client = new SidebandClient(store);
            await using var lifetime = client;
            var unknown = "00112233445566778899aabbccddeeff";
            await Assert.ThrowsAsync<ArgumentException>(() => client.CreatePaperMessageAsync("bad", "text", token));
            await Assert.ThrowsAsync<ArgumentException>(() => client.CreatePaperMessageAsync(unknown, "", token));
            await Assert.ThrowsAsync<ArgumentException>(() => client.CreatePaperMessageAsync(unknown, new string('x', LxMessage.PaperMdu + 1), token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreatePaperMessageAsync(unknown, "text", token));
            foreach (var uri in new[] { null, "", "https://invalid", "lxm://!", "lxm://" + new string('a', 5000) })
                Assert.Equal(SidebandPaperIngestResult.Rejected, await client.IngestPaperUriAsync(uri!, token));
            await client.ConnectAsync(config, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreatePaperMessageAsync(unknown, "unknown identity", token));
            using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cancelled.CancelAfter(TimeSpan.FromMilliseconds(50));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreatePaperMessageAsync(unknown, "cancel lookup", cancelled.Token));
            }
            var interrupted = client.CreatePaperMessageAsync(unknown, "disconnect lookup", token);
            await client.DisconnectAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted);
            await client.ConnectAsync(config, token);
            var disposedLookup = client.CreatePaperMessageAsync(unknown, "dispose lookup", token);
            await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10), token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disposedLookup);
            Assert.Empty((await store.LoadAsync(cancellationToken: token)).Messages);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.CreatePaperMessageAsync(unknown, "disposed", token));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.IngestPaperUriAsync("", token));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetPaperMessageUriAsync(Guid.NewGuid(), token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PaperAdmissionHasExactSharedThresholdAndReleasesAfterCancellationRestartPersistenceAndDisposal()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "paper-admission-" + Guid.NewGuid().ToString("N"));
        var cancellations = new List<CancellationTokenSource>();
        try
        {
            var (serverConfig, peerConfig) = await ConfigurePair(root, token);
            var store = new SidebandStateStore(Path.Combine(root, "origin"));
            await using var client = new SidebandClient(store);
            await using var recipient = new SidebandClient(new(Path.Combine(root, "recipient")));
            await client.ConnectAsync(serverConfig, token);
            await recipient.ConnectAsync(peerConfig, token);
            const string unknown = "00112233445566778899aabbccddeeff";
            using var fixture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-paper.json"), token));
            var nonlocal = fixture.RootElement.GetProperty("cases")[0].GetProperty("uri").GetString()!;
            Assert.Equal(8, SidebandClient.MaximumPendingPaperOperations);
            var pending = new List<Task<SidebandChatMessage>>();
            for (var i = 0; i < SidebandClient.MaximumPendingPaperOperations; i++)
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                cancellations.Add(cancellation);
                pending.Add(client.CreatePaperMessageAsync(unknown, "pending", cancellation.Token));
            }
            Assert.All(pending, operation => Assert.False(operation.IsCompleted));
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreatePaperMessageAsync(unknown, "over capacity", token));
            Assert.Contains("capacity reached (8 pending)", rejected.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.IngestPaperUriAsync(nonlocal, token));
            using (var cancelled = new CancellationTokenSource())
            {
                await cancelled.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreatePaperMessageAsync(unknown, "already cancelled", cancelled.Token));
            }
            await cancellations[^1].CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending[^1]);
            var replacement = client.CreatePaperMessageAsync(unknown, "replacement", token);
            Assert.False(replacement.IsCompleted);
            pending.Add(replacement);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreatePaperMessageAsync(unknown, "full again", token));
            await client.DisconnectAsync();
            foreach (var operation in pending)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Empty((await store.LoadAsync(cancellationToken: token)).Messages);
            await client.ConnectAsync(serverConfig, token);
            var errors = new List<string>();
            client.ErrorReported += error => { lock (errors) errors.Add(error); };
            client.StateChanged += _ => throw new InvalidOperationException("bounded paper observer failure");
            for (var i = 0; i <= SidebandClient.MaximumPendingPaperOperations; i++)
            {
                var paper = await client.CreatePaperMessageAsync(recipient.ConnectionInfo!.DeliveryHash, "recovered " + i, token);
                Assert.Equal(paper.PaperUri, await client.GetPaperMessageUriAsync(paper.Id, token));
                Assert.Equal(SidebandPaperIngestResult.NonLocal, await client.IngestPaperUriAsync(nonlocal, token));
            }
            lock (errors) Assert.Contains(errors, error => error.Contains("bounded paper observer failure", StringComparison.Ordinal));
            pending.Clear();
            for (var i = 0; i < SidebandClient.MaximumPendingPaperOperations; i++)
                pending.Add(client.CreatePaperMessageAsync(unknown, "dispose pending", token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreatePaperMessageAsync(unknown, "dispose over capacity", token));
            await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10), token);
            foreach (var operation in pending)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.CreatePaperMessageAsync(unknown, "disposed", token));
            Assert.Equal(SidebandClient.MaximumPendingPaperOperations + 1,
                (await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token)).Messages.Count);
        }
        finally
        {
            foreach (var cancellation in cancellations) cancellation.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<(string Server, string Peer)> ConfigurePair(string root, CancellationToken token)
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var server = Path.Combine(root, "server-rns");
        var peer = Path.Combine(root, "peer-rns");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(peer);
        const string common = "[reticulum]\n share_instance = no\n enable_transport = no\n\n[interfaces]\n";
        await File.WriteAllTextAsync(Path.Combine(server, "config"), common +
            $" [[Paper]]\n type = TCPServerInterface\n enabled = yes\n listen_ip = 127.0.0.1\n listen_port = {port}\n", token);
        await File.WriteAllTextAsync(Path.Combine(peer, "config"), common +
            $" [[Paper]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
        return (server, peer);
    }
}
