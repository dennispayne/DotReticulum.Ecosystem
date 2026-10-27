using System.Net;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotLXST.Tests;

[Trait("Category", "Integration")]
public sealed class TelephoneLifecycleTests
{
    private sealed class RawFactory : IAudioCodecFactory
    {
        public IAudioCodec Create(CodecKind kind) => new RawAudioCodec();
    }

    private sealed class BlockingSink : IAudioSink
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly PacketInterfaceManager _serverManager;
        private readonly PacketInterfaceManager _clientManager;
        private readonly TransportRouter _server;
        private readonly TransportRouter _client;
        private readonly Identity _serverIdentity = Identity.Generate();
        private readonly Identity _clientIdentity = Identity.Generate();
        private readonly CancellationTokenSource _stop = new();
        private Task _serverRun = Task.CompletedTask;
        private Task _clientRun = Task.CompletedTask;
        public TelephoneEndpoint Phone { get; }
        public Link? LastLink { get; private set; }

        public Rig(TelephoneOptions options)
        {
            var port = LiveStreamTests.FreePort();
            _serverManager = new([new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, port))]);
            _clientManager = new([new TcpClientPacketInterface("127.0.0.1", port)]);
            _server = new(_serverManager, new());
            _client = new(_clientManager, new());
            Phone = new(_server, _serverIdentity, options);
        }

        public async Task StartAsync()
        {
            await _serverManager.StartAsync(_stop.Token);
            _serverRun = _server.RunAsync(_stop.Token);
            await _clientManager.StartAsync(_stop.Token);
            _clientRun = _client.RunAsync(_stop.Token);
            Phone.Start();
            await Phone.AnnounceAsync();
            await LiveStreamTests.WaitForAsync(() => _client.HasPath(Phone.DestinationHash));
        }

        public StreamSession Connect(out Task established)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LastLink = _client.Links.Connect(_serverIdentity, Phone.DestinationHash);
            var transport = new ReticulumStreamTransport(LastLink);
            var session = new StreamSession(transport, new RawFactory());
            var link = LastLink;
            session.SignalReceived += signal =>
            {
                if (signal == (long)CallState.Available) link.Identify(_clientIdentity);
                if (signal == (long)CallState.Established) completion.TrySetResult();
            };
            link.Closed += _ => transport.Dispose();
            session.Start();
            established = completion.Task;
            return session;
        }

        public async ValueTask DisposeAsync()
        {
            LastLink?.Teardown();
            await Phone.DisposeAsync();
            await _stop.CancelAsync();
            try { await Task.WhenAll(_serverRun, _clientRun); }
            catch (OperationCanceledException) { }
            await _client.DisposeAsync();
            await _server.DisposeAsync();
            await _clientManager.DisposeAsync();
            await _serverManager.DisposeAsync();
            _serverIdentity.Dispose();
            _clientIdentity.Dispose();
            _stop.Dispose();
        }
    }

    [Fact]
    public async Task EveryTelephoneDisposerWaitsForBlockedSinkAndReceiveWorker()
    {
        var sink = new BlockingSink();
        await using var rig = new Rig(new()
        {
            AutoAnswerDelay = TimeSpan.FromMilliseconds(20), Sink = sink, CodecFactory = new RawFactory(),
        });
        await rig.StartAsync();
        await using var peer = rig.Connect(out var established);
        await established.WaitAsync(TimeSpan.FromSeconds(10));
        await peer.SendFrameAsync(new(CodecKind.Raw, new byte[] { 0, 0, 0 }));
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var first = rig.Phone.DisposeAsync().AsTask();
        var second = rig.Phone.DisposeAsync().AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally { sink.Release.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task HangingUpDuringPathDiscoveryCancelsOnlyThatAttemptAndAllowsNextCall()
    {
        await using var rig = new Rig(new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(20) });
        await rig.StartAsync();
        using var unknown = Identity.Generate();
        var abandoned = rig.Phone.CallAsync(unknown);
        Assert.Equal(CallState.Calling, rig.Phone.State);
        rig.Phone.Hangup();
        await using var next = rig.Connect(out var established);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await abandoned.WaitAsync(TimeSpan.FromSeconds(2)));
        await established.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CallState.Established, rig.Phone.State);
    }

    [Fact]
    public async Task AuthorizationFailureAndThrowingObserversDoNotDisableNextCall()
    {
        var attempts = 0;
        var errors = new List<string>();
        await using var rig = new Rig(new()
        {
            AutoAnswerDelay = TimeSpan.FromMilliseconds(20),
            AuthorizeCaller = _ => ++attempts == 1 ? throw new InvalidOperationException("authorization observer") : true,
        });
        rig.Phone.Error = (context, _) => errors.Add(context);
        rig.Phone.StateChanged += _ => throw new InvalidOperationException("state observer");
        var establishedObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Phone.StateChanged += state => { if (state == CallState.Established) establishedObserved.TrySetResult(); };
        await rig.StartAsync();
        await using (var first = rig.Connect(out _))
        {
            await LiveStreamTests.WaitForAsync(() => rig.LastLink!.Status == LinkStatus.Closed);
        }
        Assert.Equal(CallState.Available, rig.Phone.State);
        await using var second = rig.Connect(out var established);
        await established.WaitAsync(TimeSpan.FromSeconds(10));
        await establishedObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(errors, value => value.Contains("authorization", StringComparison.Ordinal));
        Assert.Contains(errors, value => value.Contains("observer", StringComparison.Ordinal));
    }
}
