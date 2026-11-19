using System.Net;
using System.Runtime.CompilerServices;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace LXST.Tests;

[Trait("Category", "Integration")]
public sealed partial class TelephoneLifecycleTests
{
    private sealed class RecordingPacketInterface : IPacketInterface
    {
        private readonly System.Threading.Channels.Channel<ReadOnlyMemory<byte>> _incoming =
            System.Threading.Channels.Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly System.Threading.Channels.Channel<byte[]> _sent =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

        public long Bitrate => 1_000_000;
        public int HardwareMtu => Packet.Mtu;
        public bool SupportsMtuDiscovery => false;
        public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
            _sent.Writer.WriteAsync(packet.ToArray(), cancellationToken);

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var packet in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return packet;
        }

        public async Task<byte[]> ReadSentAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await _sent.Reader.ReadAsync(timeout.Token);
        }

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            _sent.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

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

    private sealed class Routes : ITelephoneOutputControl
    {
        public string? SpeakerDevice { get; set; }
        public string? LoudspeakerDevice { get; set; }
        public bool BlockReset;
        public bool FailSelect;
        public bool BlockSelect;
        public readonly List<bool> Selected = [];
        public TaskCompletionSource ResetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReset { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SelectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSelect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask SelectLoudspeakerAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            if (enabled && FailSelect) throw new IOException("injected route failure");
            Selected.Add(enabled);
            SelectStarted.TrySetResult();
            if (BlockSelect) await ReleaseSelect.Task.WaitAsync(cancellationToken);
        }
        public async ValueTask ResetAsync()
        {
            ResetStarted.TrySetResult();
            if (BlockReset) await ReleaseReset.Task;
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
    public async Task ModeNegotiationPttAndRemoteFollowMatchActualPrimary054()
    {
        using var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-telephone-modes.json")));
        await using var rig = new Rig(new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(20) });
        await rig.StartAsync();
        var signals = new System.Collections.Concurrent.ConcurrentQueue<long>();
        await using var peer = rig.Connect(out var established, signals.Enqueue);
        await established.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(rig.Phone.EstablishedAt);
        foreach (var step in fixture.RootElement.GetProperty("steps").EnumerateArray())
        {
            while (signals.TryDequeue(out _)) { }
            switch (step.GetProperty("name").GetString())
            {
                case "initial-half":
                case "remote-half":
                    await peer.SendSignalAsync(242);
                    await LiveStreamTests.WaitForAsync(() => rig.Phone.ActiveMode == TelephoneMode.HalfDuplex);
                    break;
                case "press": rig.Phone.SetTransmitSquelched(false); break;
                case "release": rig.Phone.SetTransmitSquelched(true); break;
                case "disable-follow": Assert.True(rig.Phone.DisableRemoteModeFollow()); break;
                case "ignored-remote-full":
                    await peer.SendSignalsAsync([241L, 383L]);
                    await LiveStreamTests.WaitForAsync(() => rig.Phone.ActiveProfile == TelephoneProfile.LowLatency);
                    break;
                default: await rig.Phone.SwitchModeAsync(TelephoneMode.FullDuplex); break;
            }
            var expectedSignals = step.GetProperty("signals").EnumerateArray().Select(value => value.GetInt64()).ToArray();
            if (expectedSignals.Length != 0) await LiveStreamTests.WaitForAsync(() => signals.Count == expectedSignals.Length);
            Assert.Equal(expectedSignals, signals);
            Assert.Equal((TelephoneMode)step.GetProperty("mode").GetInt32(), rig.Phone.ActiveMode);
            Assert.Equal(step.GetProperty("squelched").GetBoolean(), rig.Phone.TransmitSquelched);
            Assert.Equal(step.GetProperty("agc_paused").GetBoolean(), rig.Phone.AutomaticGainControlPaused);
        }
        rig.Phone.Hangup();
        Assert.Null(rig.Phone.ActiveMode);
        Assert.Null(rig.Phone.EstablishedAt);
        Assert.False(rig.Phone.DisableRemoteModeFollow());
        Assert.False(rig.Phone.TransmitSquelched);
    }

    [Fact]
    public async Task BusyAndRejectedOutcomesFollowExecutedPrimaryToneCompletionAndReleaseState()
    {
        using var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "lxst-telephone-controls.json")));
        foreach (var row in fixture.RootElement.GetProperty("outcomes").EnumerateArray())
        {
            var clock = new ManualAudioClock();
            await using var rig = new Rig(new()
            {
                AutoAnswerDelay = TimeSpan.FromMilliseconds(20), TimeProvider = clock,
                BusyToneDuration = TimeSpan.FromSeconds(row.GetProperty("duration").GetDouble()),
            });
            await rig.StartAsync();
            await using var peer = rig.Connect(out var established);
            await established.WaitAsync(TimeSpan.FromSeconds(5));
            var reason = (CallState)row.GetProperty("reason").GetInt32();
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = new TaskCompletionSource<TelephoneCallEnded>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observedState = CallState.Calling;
            var callbackAt = TimeSpan.Zero;
            rig.Phone.StateChanged += state => { if (state == reason) seen.TrySetResult(); };
            rig.Phone.Error = (_, _) => { };
            rig.Phone.CallEnded += _ => throw new InvalidOperationException("outcome observer");
            rig.Phone.CallEnded += outcome =>
            {
                observedState = rig.Phone.State;
                callbackAt = clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(1000);
                ended.TrySetResult(outcome);
            };
            await peer.SendSignalAsync((long)reason);
            await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var expected = TimeSpan.FromTicks((long)Math.Round(row.GetProperty("events")[1][1].GetDouble() * TimeSpan.TicksPerSecond));
            if (expected > TimeSpan.Zero)
            {
                await clock.WaitForTimerAsync(expected);
                Assert.True(rig.Phone.IsBusy);
                clock.Advance(expected - TimeSpan.FromMilliseconds(1));
                Assert.False(ended.Task.IsCompleted);
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }
            Assert.Equal(reason, (await ended.Task.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
            Assert.Equal(expected, callbackAt);
            Assert.Equal(CallState.Available, observedState);
            Assert.False(rig.Phone.IsBusy);
        }
    }

    [Fact]
    public async Task LoudspeakerResetAndConcurrentHangupsWaitForOutputCleanupBeforeAllowingNextCall()
    {
        var routes = new Routes { BlockReset = true };
        await using var rig = new Rig(new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(20), OutputControl = routes });
        await rig.StartAsync();
        await using var peer = rig.Connect(out var established);
        await established.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Phone.Error = (_, _) => { };
        rig.Phone.LoudspeakerChanged += _ => throw new InvalidOperationException("route observer");
        await rig.Phone.EnableLoudspeakerAsync();
        await rig.Phone.EnableLoudspeakerAsync();
        Assert.True(rig.Phone.LoudspeakerEnabled);
        Assert.Equal(new[] { true }, routes.Selected);
        var first = rig.Phone.HangupAsync().AsTask();
        await routes.ResetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = rig.Phone.HangupAsync().AsTask();
        try
        {
            Assert.True(rig.Phone.IsBusy);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(rig.Phone.LoudspeakerEnabled);
        }
        finally { routes.ReleaseReset.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(rig.Phone.IsBusy);
        await using var next = rig.Connect(out var nextEstablished);
        await nextEstablished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(rig.Phone.LoudspeakerEnabled);
        await rig.Phone.HangupAsync();
    }

    [Fact]
    public async Task HangupWaitsForAnInFlightRouteBeforeResetAndAvailable()
    {
        var routes = new Routes { BlockSelect = true };
        await using var rig = new Rig(new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(20), OutputControl = routes });
        await rig.StartAsync();
        await using var peer = rig.Connect(out var established);
        await established.WaitAsync(TimeSpan.FromSeconds(5));
        var route = rig.Phone.EnableLoudspeakerAsync().AsTask();
        await routes.SelectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var hangup = rig.Phone.HangupAsync().AsTask();
        try
        {
            Assert.True(rig.Phone.IsBusy);
            Assert.False(routes.ResetStarted.Task.IsCompleted);
            Assert.False(hangup.IsCompleted);
        }
        finally { routes.ReleaseSelect.TrySetResult(); }
        await Task.WhenAll(route, hangup).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(routes.ResetStarted.Task.IsCompleted);
        Assert.False(rig.Phone.IsBusy);
        Assert.False(rig.Phone.LoudspeakerEnabled);
    }

    [Fact]
    public async Task RouteFailureIsReportedWithoutLosingCallService()
    {
        var routes = new Routes { FailSelect = true };
        await using var rig = new Rig(new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(20), OutputControl = routes });
        var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
        rig.Phone.Error = (context, _) => reports.Enqueue(context);
        await rig.StartAsync();
        await using var peer = rig.Connect(out var established);
        await established.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => rig.Phone.EnableLoudspeakerAsync().AsTask());
        Assert.Equal(CallState.Established, rig.Phone.State);
        await rig.Phone.HangupAsync();
        Assert.False(rig.Phone.LoudspeakerEnabled);
        Assert.False(rig.Phone.IsBusy);
        Assert.Contains(reports, context => context.Contains("route", StringComparison.Ordinal));
        await using var next = rig.Connect(out var nextEstablished);
        await nextEstablished.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [NativeCodecFact]
    public async Task DefaultEchoUsesReceivedAudioWithoutSubscribersAndAcceptsFullRateCapture()
    {
        await using var rig = new Rig(new()
        {
            AutoAnswerDelay = TimeSpan.FromMilliseconds(20),
            UseBandPass = false,
            UseAutomaticGainControl = false,
            EchoSuppression = new() { ComfortNoiseEnabled = false },
        });
        await rig.StartAsync();
        await using var peer = rig.Connect(out var established, codecFactory: new NativeAudioCodecFactory());
        await established.WaitAsync(TimeSpan.FromSeconds(5));
        using var encoder = new NativeOpusCodec(OpusProfile.VoiceMedium);
        using var decoder = new NativeAudioCodecFactory().Create(CodecKind.Opus);
        var reference = new List<AudioFrame>();
        var random = new Random(7824);
        for (var index = 0; index < 60; index++)
        {
            var input = new AudioFrame(Enumerable.Range(0, 240).Select(_ => (float)(random.NextDouble() - .5)).ToArray(), 24000, 1);
            var packet = encoder.Encode(input);
            reference.Add(decoder.Decode(packet));
            await peer.SendFrameAsync(new(CodecKind.Opus, packet));
            var referenceCount = Math.Min((index + 1) * 480, 42784);
            await LiveStreamTests.WaitForAsync(() => rig.Phone.EchoSuppressionState?.ReferenceSamples >= referenceCount);
            var delayed = reference[Math.Max(0, index - 3)];
            await rig.Phone.SendAudioAsync(new(delayed.Samples.ToArray().Select(value => value * .5f).ToArray(), 48000, 2));
        }
        Assert.NotNull(rig.Phone.EchoSuppressionState);
        Assert.NotNull(rig.Phone.EchoSuppressionState.DelaySamples);
        Assert.InRange(rig.Phone.EchoSuppressionState.DelaySamples!.Value, 1400, 1480);
        Assert.True(rig.Phone.EchoSuppressionState.Gain < .01, $"Unattenuated echo gain: {rig.Phone.EchoSuppressionState.Gain}");
        Assert.Equal(60, rig.Phone.TransmittedFrames);
        await rig.Phone.HangupAsync();
        Assert.Null(rig.Phone.EchoSuppressionState);
    }

    [NativeCodecFact]
    public async Task HalfDuplexSquelchDropsAudioRatherThanSendingSilentPacketsAndPttResumes()
    {
        await using var rig = new Rig(new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(20) });
        await rig.StartAsync();
        await using var peer = rig.Connect(out var established, codecFactory: new NativeAudioCodecFactory());
        var frames = new System.Collections.Concurrent.ConcurrentQueue<AudioFrame>();
        peer.FrameReceived += frames.Enqueue;
        await established.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.Phone.SwitchModeAsync(TelephoneMode.HalfDuplex);
        var frame = new AudioFrame(new float[1440], 24000, 1);
        for (var index = 0; index < 3; index++) await rig.Phone.SendAudioAsync(frame);
        Assert.Empty(frames);
        rig.Phone.SetTransmitSquelched(false);
        await rig.Phone.SendAudioAsync(frame);
        await LiveStreamTests.WaitForAsync(() => frames.Count == 1);
        rig.Phone.SetTransmitSquelched(true);
        for (var index = 0; index < 3; index++) await rig.Phone.SendAudioAsync(frame);
        await rig.Phone.SwitchModeAsync(TelephoneMode.FullDuplex);
        await rig.Phone.SendAudioAsync(frame);
        await LiveStreamTests.WaitForAsync(() => frames.Count == 2);
    }

    [Fact]
    public async Task DisposalCancelsAndAwaitsPendingPathDiscoveryWithoutSubscribers()
    {
        await using var rig = new Rig(new());
        await rig.StartAsync();
        using var remote = Identity.Generate();
        var pending = rig.Phone.CallAsync(remote);
        await Task.WhenAll(rig.Phone.DisposeAsync().AsTask(), rig.Phone.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pending.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task RepeatedPeerTeardownAndReconnectionKeepsTheTelephoneServingWithoutObservers()
    {
        await using var rig = new Rig(new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(20), CodecFactory = new RawFactory() });
        await rig.StartAsync();
        for (var index = 0; index < 12; index++)
        {
            await using var peer = rig.Connect(out var established);
            await established.WaitAsync(TimeSpan.FromSeconds(5));
            await peer.SendFrameAsync(new(CodecKind.Raw, new byte[] { 0, 0, 0 }));
            rig.LastLink!.Teardown();
            await LiveStreamTests.WaitForAsync(() => rig.Phone.State == CallState.Available);
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
    public async Task PeriodicAnnouncementsUseTheConfiguredTimeProvider()
    {
        var clock = new ManualAudioClock();
        var interval = TimeSpan.FromHours(4);
        using var stop = new CancellationTokenSource();
        using var identity = Identity.Generate();
        var packetInterface = new RecordingPacketInterface();
        await using var manager = new PacketInterfaceManager([packetInterface]);
        await using var router = new TransportRouter(manager, new());
        await using var phone = new TelephoneEndpoint(router, identity, new()
        {
            AnnounceInterval = interval,
            TimeProvider = clock,
        });
        await manager.StartAsync(stop.Token);
        var routerRun = router.RunAsync(stop.Token);
        phone.Start();
        var initial = await packetInterface.ReadSentAsync();
        AssertAnnounce(initial, packetInterface.HardwareMtu);
        await clock.WaitForTimerAsync(interval);

        clock.Advance(interval);

        var periodic = await packetInterface.ReadSentAsync();
        AssertAnnounce(periodic, packetInterface.HardwareMtu);
        await phone.DisposeAsync();
        await stop.CancelAsync();
        await routerRun.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void AssertAnnounce(byte[] bytes, int mtu)
    {
        Assert.True(Packet.TryParse(bytes, out var packet, mtu));
        Assert.Equal(PacketType.Announce, packet.PacketType);
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
        await rig.Phone.HangupAsync();
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
