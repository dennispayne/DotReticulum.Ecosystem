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
        private readonly TcpServerPacketInterface _serverInterface;
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
            _serverInterface = new(new IPEndPoint(IPAddress.Loopback, port));
            _serverManager = new([_serverInterface]);
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
            await LiveStreamTests.WaitForAsync(() => _serverInterface.ClientCount == 1);
            Phone.Start();
            await Phone.AnnounceAsync();
            await LiveStreamTests.WaitForAsync(() => _client.HasPath(Phone.DestinationHash));
        }

        public StreamSession Connect(out Task established, Action<long>? receiveSignal = null, IAudioCodecFactory? codecFactory = null)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LastLink = CreateLink();
            var transport = new ReticulumStreamTransport(LastLink);
            var session = new StreamSession(transport, codecFactory ?? new RawFactory());
            var link = LastLink;
            session.SignalReceived += signal =>
            {
                receiveSignal?.Invoke(signal);
                if (signal == (long)CallState.Available) link.Identify(_clientIdentity);
                if (signal == (long)CallState.Established) completion.TrySetResult();
            };
            link.Closed += _ => transport.Dispose();
            session.Start();
            established = completion.Task;
            return session;
        }

        public Link CreateLink() =>
            LastLink = _client.Links.Connect(_serverIdentity, Phone.DestinationHash);

        public async Task<LocalDestination> AnnounceSilentPeerAsync()
        {
            var destination = new LocalDestination(_clientIdentity,
                Destination.Create(DestinationType.Single, "lxst", ["telephony"], _clientIdentity))
            {
                AcceptLinks = true,
                ProofStrategy = ProofStrategy.None,
            };
            _client.RegisterLocalDestination(destination);
            await _client.AnnounceAsync(destination);
            await LiveStreamTests.WaitForAsync(() => _server.HasPath(destination.Destination.Hash));
            return destination;
        }

        public Task DialPeerAsync() => Phone.CallAsync(_clientIdentity);

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
    public async Task ReplacingCallerPolicyAfterDenialAllowsTheNextIncomingCall()
    {
        await using var rig = new Rig(new()
        {
            AutoAnswerDelay = TimeSpan.FromMilliseconds(20), CallerPolicy = new(allowedIdentityHashes: []),
        });
        await rig.StartAsync();
        await using (var denied = rig.Connect(out _))
            await denied.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Phone.SetCallerPolicy(new());
        await using var next = rig.Connect(out var established);
        await established.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CallState.Established, rig.Phone.State);
    }

    [Fact]
    public async Task ReceiveGainAndMuteApplyWithoutAnExternalSinkAndMuteResetsAfterHangup()
    {
        await using var rig = new Rig(new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(20), CodecFactory = new RawFactory() });
        await rig.StartAsync();
        await using var peer = rig.Connect(out var established);
        await established.WaitAsync(TimeSpan.FromSeconds(5));
        var frames = new System.Collections.Concurrent.ConcurrentQueue<AudioFrame>();
        rig.Phone.AudioReceived += frames.Enqueue;
        using var codec = new RawAudioCodec(32, 1);
        var data = codec.Encode(new(new float[] { .125f, -.25f, .5f }, 48000, 1));
        rig.Phone.ReceiveGainDb = 6.0206f;
        await peer.SendFrameAsync(new(CodecKind.Raw, data));
        await LiveStreamTests.WaitForAsync(() => frames.Count == 1);
        Assert.True(frames.TryDequeue(out var amplified));
        Assert.InRange(amplified.Samples.Span[0], .49999f, .50001f);
        Assert.InRange(amplified.Samples.Span[1], -1.00001f, -.99999f);
        Assert.Equal(1, amplified.Samples.Span[2]);
        rig.Phone.ReceiveMuted = true;
        rig.Phone.TransmitMuted = true;
        await peer.SendFrameAsync(new(CodecKind.Raw, data));
        await LiveStreamTests.WaitForAsync(() => frames.Count == 1);
        Assert.True(frames.TryDequeue(out var muted));
        Assert.All(muted.Samples.ToArray(), sample => Assert.Equal(0, sample));
        rig.Phone.Hangup();
        Assert.False(rig.Phone.ReceiveMuted);
        Assert.False(rig.Phone.TransmitMuted);
        Assert.Equal(6.0206f, rig.Phone.ReceiveGainDb);
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Phone.TransmitGainDb = float.NaN);
    }

    [NativeCodecFact]
    public async Task TransmitFiltersGainAndMuteReachThePeerThroughTheActualNativeCodec()
    {
        await using var rig = new Rig(new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(20) });
        await rig.StartAsync();
        await using var peer = rig.Connect(out var established, codecFactory: new NativeAudioCodecFactory());
        var frames = new System.Collections.Concurrent.ConcurrentQueue<AudioFrame>();
        peer.FrameReceived += frames.Enqueue;
        await established.WaitAsync(TimeSpan.FromSeconds(5));
        var samples = Enumerable.Range(0, 1440).Select(index => (float)(.1 * Math.Sin(index * 2 * Math.PI * 440 / 24000))).ToArray();
        rig.Phone.TransmitGainDb = -3;
        for (var index = 0; index < 3; index++) await rig.Phone.SendAudioAsync(new(samples, 24000, 1));
        await LiveStreamTests.WaitForAsync(() => frames.Count == 3);
        Assert.Contains(frames, frame => frame.Samples.ToArray().Any(sample => Math.Abs(sample) > .01));
        rig.Phone.TransmitMuted = true;
        while (frames.TryDequeue(out _)) { }
        for (var index = 0; index < 8; index++) await rig.Phone.SendAudioAsync(new(samples, 24000, 1));
        await LiveStreamTests.WaitForAsync(() => frames.Count == 8);
        Assert.True(frames.Last().Samples.ToArray().All(sample => Math.Abs(sample) < .001));
        rig.Phone.TransmitMuted = false;
        while (frames.TryDequeue(out _)) { }
        for (var index = 0; index < 3; index++) await rig.Phone.SendAudioAsync(new(samples, 24000, 1));
        await LiveStreamTests.WaitForAsync(() => frames.Count == 3);
        Assert.Contains(frames, frame => frame.Samples.ToArray().Any(sample => Math.Abs(sample) > .01));
    }

    [Fact]
    public async Task ASecondIncomingLinkIsBusyWithoutInterruptingTheEstablishedCall()
    {
        await using var rig = new Rig(new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(20) });
        await rig.StartAsync();
        await using var first = rig.Connect(out var established);
        await established.WaitAsync(TimeSpan.FromSeconds(5));
        var signals = new List<long>();
        await using var contender = rig.Connect(out _, signals.Add);
        await contender.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains((long)CallState.Busy, signals);
        Assert.Equal(CallState.Established, rig.Phone.State);
        rig.Phone.Hangup();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await using var next = rig.Connect(out established);
        await established.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LocalRejectIsSignalledButRingTimeoutOnlyTearsDownAndBothPermitTheNextCall()
    {
        await using var rig = new Rig(new() { RingTimeout = TimeSpan.FromMilliseconds(150) });
        await rig.StartAsync();
        var rejected = new List<long>();
        await using (var first = rig.Connect(out _, rejected.Add))
        {
            await LiveStreamTests.WaitForAsync(() => rig.Phone.State == CallState.Ringing);
            rig.Phone.Hangup();
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains((long)CallState.Rejected, rejected);
        }
        var timedOut = new List<long>();
        await using (var second = rig.Connect(out _, timedOut.Add))
        {
            await LiveStreamTests.WaitForAsync(() => rig.Phone.State == CallState.Ringing);
            await LiveStreamTests.WaitForAsync(() => rig.Phone.State == CallState.Available);
            await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain((long)CallState.Rejected, timedOut);
        }
        await using var next = rig.Connect(out _);
        await LiveStreamTests.WaitForAsync(() => rig.Phone.State == CallState.Ringing);
        await rig.Phone.AnswerAsync();
        Assert.Equal(CallState.Established, rig.Phone.State);
    }

    [Fact]
    public async Task OutgoingEstablishmentDeadlineClosesASilentActiveLinkBeforeTheLongerWaitDeadline()
    {
        await using var rig = new Rig(new()
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(150), WaitTimeout = TimeSpan.FromSeconds(10),
        });
        await rig.StartAsync();
        await rig.AnnounceSilentPeerAsync();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Phone.StateChanged += state => { if (state == CallState.Available) ended.TrySetResult(); };
        await rig.DialPeerAsync();
        Assert.Equal(CallState.Calling, rig.Phone.State);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MutableConnectTimeoutAppliesToFutureCallsWithoutRewritingAnExistingDeadline()
    {
        await using var rig = new Rig(new() { ConnectTimeout = TimeSpan.FromSeconds(10), WaitTimeout = TimeSpan.FromSeconds(20) });
        await rig.StartAsync();
        await rig.AnnounceSilentPeerAsync();
        rig.Phone.ConnectTimeout = TimeSpan.FromMilliseconds(150);
        await rig.DialPeerAsync();
        rig.Phone.ConnectTimeout = TimeSpan.FromSeconds(10);
        await LiveStreamTests.WaitForAsync(() => rig.Phone.State == CallState.Available);
        rig.Phone.ConnectTimeout = TimeSpan.FromMilliseconds(-1);
        await rig.DialPeerAsync();
        await LiveStreamTests.WaitForAsync(() => rig.Phone.State == CallState.Available);
        rig.Phone.ConnectTimeout = TimeSpan.FromSeconds(2);
        await rig.DialPeerAsync();
        Assert.Equal(CallState.Calling, rig.Phone.State);
        rig.Phone.Hangup();
    }

    [Fact]
    public async Task AutoAnswerIsNotCancelledByTheShorterManualRingDeadline()
    {
        await using var rig = new Rig(new()
        {
            RingTimeout = TimeSpan.FromMilliseconds(20), AutoAnswerDelay = TimeSpan.FromMilliseconds(120),
        });
        await rig.StartAsync();
        await using var session = rig.Connect(out var established);
        await established.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CallState.Established, rig.Phone.State);
    }

    [Fact]
    public async Task ForeignReceiverOwnershipAndRepeatedConcurrentDisposalCannotClobberTheNextOwner()
    {
        await using var rig = new Rig(new());
        await rig.StartAsync();
        var link = rig.CreateLink();
        using (link.RegisterPacketReceiver((_, _) => { }))
            Assert.Throws<InvalidOperationException>(() => new ReticulumStreamTransport(link));
        var transport = new ReticulumStreamTransport(link, receiveCapacity: 1);
        Assert.Throws<InvalidOperationException>(() => link.SetPacketReceiver(null));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(transport.Dispose)));
        using var nextOwner = link.RegisterPacketReceiver((_, _) => { });
        transport.Dispose();
        Assert.Throws<InvalidOperationException>(() => new ReticulumStreamTransport(link));
    }

    [Fact]
    public async Task DuplicateLinkAdapterCannotReplaceAnExistingStreamReceiver()
    {
        await using var rig = new Rig(new()
        {
            AutoAnswerDelay = TimeSpan.FromMilliseconds(20), CodecFactory = new RawFactory(),
        });
        await rig.StartAsync();
        await using var session = rig.Connect(out var established);
        Assert.Throws<InvalidOperationException>(() => new ReticulumStreamTransport(rig.LastLink!));
        await established.WaitAsync(TimeSpan.FromSeconds(5));
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
    public async Task BusyOverrideRejectsAnIncomingCallAndCanBeClearedForContinuedService()
    {
        await using var rig = new Rig(new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(20) });
        rig.Phone.SetBusy();
        await rig.StartAsync();
        await using (var first = rig.Connect(out _))
            await LiveStreamTests.WaitForAsync(() => rig.LastLink!.Status == LinkStatus.Closed);
        Assert.True(rig.Phone.IsBusy);
        using var remote = Identity.Generate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Phone.CallAsync(remote));
        rig.Phone.SetBusy(false);
        await using var next = rig.Connect(out var established);
        await established.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(rig.Phone.IsBusy);
        rig.Phone.Hangup();
        Assert.False(rig.Phone.IsBusy);
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
