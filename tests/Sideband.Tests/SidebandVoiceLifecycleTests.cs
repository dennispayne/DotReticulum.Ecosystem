using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;
using LXST;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandVoiceLifecycleTests
{
    [Fact]
    public async Task ConstructionStartEnumerationAndDisposalNeverOpenDevicesAndLeaveOwnersUsable()
    {
        await using var rig = new Rig();
        Assert.Equal(SidebandVoiceState.Available, rig.Service.Snapshot.State);
        Assert.False(rig.Service.Snapshot.Started);
        Assert.Throws<InvalidOperationException>(() => rig.Service.MuteMicrophone(true));
        Assert.Throws<InvalidOperationException>(() => rig.Service.SetPushToTalk(true));
        await rig.StartAsync();
        rig.Service.Start();
        Assert.True(rig.Service.Snapshot.Started);
        Assert.Single(await rig.Service.ListInputsAsync(TestContext.Current.CancellationToken));
        Assert.Single(await rig.Service.ListOutputsAsync(TestContext.Current.CancellationToken));
        await rig.Service.ConfigureAsync(new(new() { Speaker = "selected", Microphone = "capture", Loudspeaker = "loud" }), TestContext.Current.CancellationToken);
        await rig.Service.EnableLoudspeakerAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(0, rig.Devices.Captures);
        Assert.Equal(0, rig.Devices.Playbacks);
        await Task.WhenAll(rig.Service.DisposeAsync().AsTask(), rig.Service.DisposeAsync().AsTask());
        Assert.Throws<ObjectDisposedException>(() => rig.Service.Start());
        Assert.False(rig.Service.Snapshot.Started);
        await using var replacement = new TelephoneEndpoint(rig.Server, rig.Identity);
        replacement.Start();
        await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => replacement.State == CallState.Ringing);
        Assert.Equal(16, rig.Identity.Hash.Length);
        Assert.Single(await rig.Devices.ListInputsAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HistoryAndStateTransitionsMatchActualCoordinatorCallbacks(bool outgoing, bool success)
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var history = new ConcurrentQueue<SidebandVoiceCallEvent>();
        rig.Service.CallHistory += history.Enqueue;
        if (outgoing)
        {
            await rig.Service.CallAsync(rig.PeerIdentity, cancellationToken: TestContext.Current.CancellationToken);
            await UntilAsync(() => rig.Peer.State == CallState.Ringing);
            Assert.Equal(SidebandVoiceState.Connecting, rig.Service.Snapshot.State);
            if (success) await rig.Peer.AnswerAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
            await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.Ringing);
            Assert.True(rig.Service.Snapshot.Incoming);
            if (success) await rig.Service.AnswerAsync(TestContext.Current.CancellationToken);
        }
        if (success)
        {
            await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.InCall);
            await UntilAsync(() => rig.Devices.Captures == 1);
        }
        else Assert.Equal(0, rig.Devices.Captures);
        await rig.Service.HangupAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Peer.State == CallState.Available);
        Assert.Equal(SidebandVoiceState.Available, rig.Service.Snapshot.State);
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "sideband-voice.json")));
        var name = outgoing ? success ? "outgoing-success" : "outgoing-failure"
            : success ? "incoming-success" : "incoming-missed";
        var row = fixture.RootElement.GetProperty("transitions").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == name);
        Assert.Equal(row.GetProperty("events").EnumerateArray().Select(value => value.GetString()),
            history.Select(value => value.Event));
        Assert.All(history, entry => Assert.Equal(rig.PeerIdentity.HexHash, entry.IdentityHash));
        Assert.Equal(rig.Devices.Captures, rig.Devices.CaptureDisposals);
        Assert.Equal(0, rig.Devices.MediaPlaybacks);
    }

    [Fact]
    public async Task NoSubscriberPathProcessesAndPeerFailureRecoversWhileOptionalObserversAreIsolated()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.Ringing);
        await rig.Service.AnswerAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.InCall);
        rig.Peer.Hangup();
        await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.Available);
        var errors = new ConcurrentQueue<SidebandVoiceError>();
        var history = new ConcurrentQueue<SidebandVoiceCallEvent>();
        rig.Service.StateChanged += _ => throw new InvalidOperationException("state observer");
        rig.Service.CallHistory += _ => throw new InvalidOperationException("history observer");
        rig.Service.CallHistory += history.Enqueue;
        rig.Service.Error += _ => throw new InvalidOperationException("error observer");
        rig.Service.Error += errors.Enqueue;
        await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.Ringing);
        await rig.Service.AnswerAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.InCall);
        await rig.Service.HangupAsync(TestContext.Current.CancellationToken);
        Assert.Contains(history, entry => entry.Event == "incoming-success");
        Assert.Contains(history, entry => entry.Event == "ongoing-ended");
        Assert.Contains(errors, error => error.Context.Contains("observer", StringComparison.Ordinal));
        Assert.Equal(rig.Devices.Captures, rig.Devices.CaptureDisposals);
    }

    [Fact]
    public async Task IncomingAuthorizationNeverTreatsVoiceTrustAsAnOverrideForUntrustedDeliveryAndCanRecover()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await rig.Service.ConfigureAsync(new(new() { TrustedOnly = true },
            [new(rig.PeerIdentity.HexHash, false, true)]), TestContext.Current.CancellationToken);
        await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Peer.State is CallState.Busy or CallState.Available);
        Assert.Equal(SidebandVoiceState.Available, rig.Service.Snapshot.State);
        Assert.Equal(0, rig.Devices.Captures);
        Assert.Equal(0, rig.Devices.Playbacks);
        await rig.Peer.HangupAsync(TestContext.Current.CancellationToken);
        await rig.Service.ConfigureAsync(new(new() { TrustedOnly = true },
            [new(rig.PeerIdentity.HexHash, null, true)]), TestContext.Current.CancellationToken);
        await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.Ringing);
        await rig.Service.RejectAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Peer.State is CallState.Rejected or CallState.Available);
        Assert.Equal(0, rig.Devices.Captures);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DirectPttRequiresAllPolicyInputsAndHalfDuplexThenPinsRemoteMode(bool trustedDelivery, bool halfDuplex)
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await rig.Service.ConfigureAsync(new(new() { DirectPushToTalk = true },
            [new(rig.PeerIdentity.HexHash, trustedDelivery, true, true)]), TestContext.Current.CancellationToken);
        await rig.Peer.CallAsync(rig.Identity, mode: halfDuplex ? TelephoneMode.HalfDuplex : TelephoneMode.FullDuplex,
            cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.Ringing ||
            rig.Service.Snapshot.State == SidebandVoiceState.InCall);
        if (trustedDelivery && halfDuplex)
        {
            await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.InCall);
            Assert.True(rig.Service.Snapshot.TransmitSquelched);
            Assert.True(rig.Service.Snapshot.LoudspeakerEnabled);
            rig.Service.SetPushToTalk(true);
            Assert.False(rig.Service.Snapshot.TransmitSquelched);
            rig.Service.SetPushToTalk(false);
            Assert.True(rig.Service.Snapshot.TransmitSquelched);
            rig.Service.MuteMicrophone(true);
            rig.Service.MuteSpeaker(true);
            Assert.True(rig.Service.Snapshot.TransmitMuted);
            Assert.True(rig.Service.Snapshot.ReceiveMuted);
            await rig.Peer.SwitchModeAsync(TelephoneMode.FullDuplex, TestContext.Current.CancellationToken);
            await rig.Peer.SwitchProfileAsync(TelephoneProfile.LowLatency, TestContext.Current.CancellationToken);
            await UntilAsync(() => rig.Service.Snapshot.Profile == TelephoneProfile.LowLatency);
            Assert.Equal(TelephoneMode.HalfDuplex, rig.Service.Snapshot.Mode);
        }
        else
        {
            await Task.Delay(1200, TestContext.Current.CancellationToken);
            Assert.Equal(SidebandVoiceState.Ringing, rig.Service.Snapshot.State);
            Assert.Equal(0, rig.Devices.Captures);
            Assert.Equal(1, rig.Devices.Playbacks);
        }
        await rig.Service.HangupAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancellationDisposalAndFreshServiceDoNotRetainPendingDialsOrDeviceLeases()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        using var unknown = Identity.Generate();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var dial = rig.Service.CallAsync(unknown, cancellationToken: cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dial);
        Assert.Equal(SidebandVoiceState.Available, rig.Service.Snapshot.State);
        await rig.Service.CallAsync(rig.PeerIdentity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Peer.State == CallState.Ringing);
        await rig.Peer.AnswerAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Devices.Captures == 1);
        await Task.WhenAll(rig.Service.DisposeAsync().AsTask(), rig.Service.DisposeAsync().AsTask());
        await UntilAsync(() => rig.Peer.State == CallState.Available);
        Assert.Equal(1, rig.Devices.CaptureDisposals);
        await using var fresh = new SidebandVoiceService(rig.Server, rig.Identity, rig.Devices);
        fresh.Start();
        await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => fresh.Snapshot.State == SidebandVoiceState.Ringing);
        await fresh.AnswerAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Devices.Captures == 2);
        await fresh.HangupAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, rig.Devices.CaptureDisposals);
    }

    [Fact]
    public async Task AudioBeforeAcceptanceCannotOpenPlaybackAndAcceptedRawAudioReleasesItsLeaseOnHangup()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var link = rig.Client.Links.Connect(rig.Identity, rig.Service.DestinationHash);
        using var transport = new ReticulumStreamTransport(link);
        await using var session = new StreamSession(transport, new NativeAudioCodecFactory());
        session.SignalReceived += signal =>
        {
            if (signal == (long)CallState.Available) link.Identify(rig.PeerIdentity);
        };
        session.Start();
        await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.Ringing);
        using var codec = new RawAudioCodec();
        var frame = new EncodedFrame(CodecKind.Raw, codec.Encode(new(new float[80], 48000, 1)));
        await session.SendFrameAsync(frame, TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Service.Snapshot.ReceivedFrames > 0);
        Assert.Equal(0, rig.Devices.MediaPlaybacks);
        Assert.Equal(0, rig.Devices.Captures);
        await rig.Service.AnswerAsync(TestContext.Current.CancellationToken);
        for (var index = 0; index < 4; index++)
            await session.SendFrameAsync(frame, TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Devices.MediaPlaybacks > 0);
        Assert.Equal(1, rig.Devices.MediaPlaybacks);
        await rig.Service.HangupAsync(TestContext.Current.CancellationToken);
        Assert.Equal(rig.Devices.Captures, rig.Devices.CaptureDisposals);
        Assert.Equal(rig.Devices.Playbacks, rig.Devices.PlaybackDisposals);
    }

    [Fact]
    public async Task CaptureOpenFailureIsReportedWithoutSubscribersAndTheNextCallStillSucceeds()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        rig.Devices.FailCapture = true;
        var errors = new ConcurrentQueue<SidebandVoiceError>();
        rig.Service.Error += errors.Enqueue;
        await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.Ringing);
        await rig.Service.AnswerAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.Available);
        Assert.Contains(errors, error => error.Context == "Voice capture failed.");
        await UntilAsync(() => rig.Peer.State == CallState.Available);
        rig.Devices.FailCapture = false;
        await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.Ringing);
        await rig.Service.AnswerAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Devices.Captures == 2);
        await rig.Service.HangupAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, rig.Devices.CaptureDisposals);
    }

    [Fact]
    public async Task DisposalJoinsPendingPathDiscoveryAndPrecancelledInvalidCallsNeverReserveVoice()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        using var remote = Identity.Generate();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.CallAsync(remote,
            cancellationToken: cancelled.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Service.CallAsync(remote,
            profile: (TelephoneProfile)0, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(rig.Service.IsBusy);
        var dial = rig.Service.CallAsync(remote, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(rig.Service.IsBusy);
        await rig.Service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.True(dial.IsCompleted);
        var exception = await Record.ExceptionAsync(() => dial);
        Assert.True(exception is OperationCanceledException or ObjectDisposedException, exception?.ToString());
        Assert.Equal(0, rig.Devices.Captures);
        Assert.Equal(0, rig.Devices.Playbacks);
    }

    [Fact]
    public async Task ConcurrentDisposersJoinTheExactPendingDialTaskAcrossRepeatedServices()
    {
        using var remote = Identity.Generate();
        for (var iteration = 0; iteration < 16; iteration++)
        {
            await using var rig = new Rig();
            await rig.StartAsync();
            var dial = rig.Service.CallAsync(remote, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(rig.Service.IsBusy);
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => rig.Service.DisposeAsync().AsTask()))
                .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.True(dial.IsCompleted);
            var exception = await Record.ExceptionAsync(() => dial);
            Assert.True(exception is OperationCanceledException or ObjectDisposedException, exception?.ToString());
            Assert.Equal(0, rig.Devices.Captures);
            Assert.Equal(0, rig.Devices.Playbacks);
        }
    }

    [Fact]
    public async Task HangupJoinsTheExactPendingDialBeforeAnotherCallCanUseTheEndpoint()
    {
        using var remote = Identity.Generate();
        for (var iteration = 0; iteration < 16; iteration++)
        {
            await using var rig = new Rig();
            await rig.StartAsync();
            var dial = rig.Service.CallAsync(remote, cancellationToken: TestContext.Current.CancellationToken);
            await rig.Service.HangupAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.True(dial.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dial);
            Assert.False(rig.Service.IsBusy);
            await rig.Service.CallAsync(rig.PeerIdentity, cancellationToken: TestContext.Current.CancellationToken);
            await UntilAsync(() => rig.Peer.State == CallState.Ringing);
            await rig.Service.HangupAsync(TestContext.Current.CancellationToken);
            await UntilAsync(() => rig.Peer.State == CallState.Available);
            Assert.Equal(0, rig.Devices.Captures);
        }
    }

    internal static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    internal sealed class Devices : IAudioDeviceBackend
    {
        public int Captures;
        public int CaptureDisposals;
        public int Playbacks;
        public int PlaybackDisposals;
        public int Written;
        public bool FailCapture;
        public bool FailPlaybackOpen;
        public bool FailPlaybackWrite;
        public bool BlockPlayback;
        public int CancelledWrites;
        public AudioFrame? LastFrame;
        public ConcurrentQueue<string?> PlaybackDevices { get; } = new();
        public int MediaPlaybacks => PlaybackDevices.Count(device => device != "fake-ringer");
        public TaskCompletionSource PlaybackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListInputsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AudioDeviceInfo>>([new("fake-input", "Fake input", true)]);
        public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListOutputsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AudioDeviceInfo>>([new("fake-output", "Fake output", true)]);
        public ValueTask<IAudioCapture> OpenCaptureAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Captures);
            if (FailCapture) throw new IOException("injected capture-open failure");
            return ValueTask.FromResult<IAudioCapture>(new Capture(this));
        }
        public ValueTask<IAudioPlayback> OpenPlaybackAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default)
        {
            if (FailPlaybackOpen) throw new IOException("injected ringer-open failure");
            Interlocked.Increment(ref Playbacks);
            PlaybackDevices.Enqueue(device);
            return ValueTask.FromResult<IAudioPlayback>(new Playback(this));
        }
        private sealed class Capture(Devices owner) : IAudioCapture
        {
            public async IAsyncEnumerable<AudioFrame> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.CaptureDisposals); return ValueTask.CompletedTask; }
        }
        private sealed class Playback(Devices owner) : IAudioPlayback
        {
            public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner.Written);
                Volatile.Write(ref owner.LastFrame, frame);
                owner.PlaybackEntered.TrySetResult();
                if (owner.FailPlaybackWrite) throw new IOException("injected ringer-write failure");
                if (owner.BlockPlayback)
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref owner.CancelledWrites);
                        throw;
                    }
                }
            }
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.PlaybackDisposals); return ValueTask.CompletedTask; }
        }
    }

    internal sealed class Rig : IAsyncDisposable
    {
        private readonly TcpServerPacketInterface _serverInterface;
        private readonly PacketInterfaceManager _serverManager;
        private readonly PacketInterfaceManager _clientManager;
        private readonly CancellationTokenSource _stop = new();
        private Task _serverRun = Task.CompletedTask;
        private Task _clientRun = Task.CompletedTask;
        public TransportRouter Server { get; }
        public TransportRouter Client { get; }
        public Identity Identity { get; } = Identity.Generate();
        public Identity PeerIdentity { get; } = Identity.Generate();
        public Devices Devices { get; } = new();
        public SidebandVoiceService Service { get; }
        public TelephoneEndpoint Peer { get; }
        public Rig()
        {
            _serverInterface = new(new IPEndPoint(IPAddress.Loopback, 0));
            _serverManager = new([_serverInterface]);
            _clientManager = new([], keepAlive: true);
            Server = new(_serverManager, new());
            Client = new(_clientManager, new());
            Service = new(Server, Identity, Devices, new(new() { Ringer = "fake-ringer" }));
            Peer = new(Client, PeerIdentity, new() { BusyToneDuration = TimeSpan.Zero });
        }
        public async Task StartAsync()
        {
            await _serverManager.StartAsync(_stop.Token);
            var endpoint = Assert.IsType<IPEndPoint>(_serverInterface.LocalEndpoint);
            Assert.InRange(endpoint.Port, 1, ushort.MaxValue);
            _serverRun = Server.RunAsync(_stop.Token);
            await _clientManager.StartAsync(_stop.Token);
            await _clientManager.AttachAsync(new TcpClientPacketInterface("127.0.0.1", endpoint.Port), _stop.Token);
            _clientRun = Client.RunAsync(_stop.Token);
            await UntilAsync(() => _serverInterface.ClientCount == 1);
            Service.Start();
            Peer.Start();
            await Service.AnnounceAsync();
            await Peer.AnnounceAsync();
            await UntilAsync(() => Server.HasPath(Peer.DestinationHash) && Client.HasPath(Service.DestinationHash));
        }
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Peer.DisposeAsync();
            await _stop.CancelAsync();
            try { await Task.WhenAll(_serverRun, _clientRun); } catch (OperationCanceledException) { }
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            await _clientManager.DisposeAsync();
            await _serverManager.DisposeAsync();
            Identity.Dispose();
            PeerIdentity.Dispose();
            _stop.Dispose();
        }
    }
}
