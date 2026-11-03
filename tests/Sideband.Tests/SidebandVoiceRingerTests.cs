using System.Collections.Concurrent;
using System.Diagnostics;
using LXST;
using static Sideband.Tests.SidebandVoiceLifecycleTests;

namespace Sideband.Tests;

// Original tone PCM is a native sound choice, not a vector or copy of Sideband soft1.opus.
[Trait("Category", "Integration")]
public sealed class SidebandVoiceRingerTests
{
    [Fact]
    public async Task OnlyIncomingCallsRingAfterReferenceDelayOnSelectedDeviceAndAnswerJoinsNotification()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await rig.Service.ConfigureAsync(new(new() { Ringer = "ring-A" }), TestContext.Current.CancellationToken);
        await rig.Service.CallAsync(rig.PeerIdentity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Peer.State == CallState.Ringing);
        await Task.Delay(650, TestContext.Current.CancellationToken);
        Assert.Equal(0, rig.Devices.Playbacks);
        Assert.Equal(0, rig.Devices.Captures);
        await rig.Service.HangupAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Peer.State == CallState.Available);

        await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.Ringing);
        var clock = Stopwatch.StartNew();
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.Equal(0, rig.Devices.Playbacks);
        await UntilAsync(() => rig.Devices.Written > 0);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(400));
        Assert.Equal("ring-A", Assert.Single(rig.Devices.PlaybackDevices));
        Assert.Equal(0, rig.Devices.Captures);
        var frame = Assert.IsType<AudioFrame>(Volatile.Read(ref rig.Devices.LastFrame));
        Assert.Equal(48000, frame.SampleRate);
        Assert.Equal(2, frame.Channels);
        Assert.Equal(2880, frame.SamplesPerChannel);
        Assert.Contains(frame.Samples.ToArray(), value => value != 0);
        Assert.All(frame.Samples.ToArray(), value => Assert.InRange(value, -.05001f, .05001f));

        await rig.Service.ConfigureAsync(new(new() { Ringer = "ring-B" }), TestContext.Current.CancellationToken);
        Assert.Equal(1, rig.Devices.PlaybackDisposals);
        await UntilAsync(() => rig.Devices.Playbacks == 2);
        Assert.Equal("ring-B", rig.Devices.PlaybackDevices.Last());
        await rig.Service.AnswerAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, rig.Devices.PlaybackDisposals);
        await UntilAsync(() => rig.Devices.Captures == 1);
        await rig.Service.HangupAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, rig.Devices.CaptureDisposals);
        Assert.Equal(2, rig.Devices.Playbacks);
    }

    [Theory]
    [InlineData("answer")]
    [InlineData("reject")]
    [InlineData("disconnect")]
    [InlineData("dispose")]
    public async Task EveryEndPathCancelsAndJoinsABlockedNotificationWrite(string action)
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        rig.Devices.BlockPlayback = true;
        await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
        await rig.Devices.PlaybackEntered.Task.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        switch (action)
        {
            case "answer":
                await rig.Service.AnswerAsync(TestContext.Current.CancellationToken);
                break;
            case "reject":
                await rig.Service.RejectAsync(TestContext.Current.CancellationToken);
                break;
            case "disconnect":
                await rig.Peer.HangupAsync(TestContext.Current.CancellationToken);
                await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.Available);
                break;
            default:
                await Task.WhenAll(rig.Service.DisposeAsync().AsTask(), rig.Service.DisposeAsync().AsTask());
                break;
        }
        Assert.Equal(1, rig.Devices.CancelledWrites);
        Assert.Equal(1, rig.Devices.Playbacks);
        Assert.Equal(1, rig.Devices.PlaybackDisposals);
        if (action == "answer") await rig.Service.HangupAsync(TestContext.Current.CancellationToken);
        else Assert.Equal(0, rig.Devices.Captures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAndWriteFailuresReportWithoutDisablingCallHandlingAndNextCallRings(bool writeFailure)
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        rig.Devices.FailPlaybackOpen = !writeFailure;
        rig.Devices.FailPlaybackWrite = writeFailure;
        var errors = new ConcurrentQueue<SidebandVoiceError>();
        rig.Service.Error += _ => throw new InvalidOperationException("notification error observer");
        rig.Service.Error += errors.Enqueue;
        await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => errors.Any(error => error.Context == "Voice ringer failed."));
        Assert.Equal(SidebandVoiceState.Ringing, rig.Service.Snapshot.State);
        Assert.Equal(0, rig.Devices.Captures);
        await rig.Service.RejectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(rig.Devices.Playbacks, rig.Devices.PlaybackDisposals);
        await UntilAsync(() => rig.Peer.State == CallState.Available);
        rig.Devices.FailPlaybackOpen = false;
        rig.Devices.FailPlaybackWrite = false;
        var before = rig.Devices.Written;
        await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Devices.Written > before);
        await rig.Service.RejectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(rig.Devices.Playbacks, rig.Devices.PlaybackDisposals);
    }

    [Fact]
    public async Task EarlyDirectPttAcceptanceSuppressesNotificationWithoutOpeningAnyOutput()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await rig.Service.ConfigureAsync(new(new() { DirectPushToTalk = true, Ringer = "never-opened" },
            [new(rig.PeerIdentity.HexHash, true, false, true)]), TestContext.Current.CancellationToken);
        await rig.Peer.CallAsync(rig.Identity, mode: TelephoneMode.HalfDuplex,
            cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Service.Snapshot.State == SidebandVoiceState.InCall);
        await Task.Delay(600, TestContext.Current.CancellationToken);
        Assert.Equal(0, rig.Devices.Playbacks);
        await rig.Service.HangupAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RealReferenceRingDeadlineStopsAndJoinsNotificationAndServiceCanRingAgain()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Devices.Playbacks == 1);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(SidebandVoiceService.RingTimeout + TimeSpan.FromSeconds(5));
        while (rig.Service.Snapshot.State != SidebandVoiceState.Available)
            await Task.Delay(25, deadline.Token);
        Assert.Equal(0, rig.Devices.Captures);
        Assert.Equal(1, rig.Devices.PlaybackDisposals);
        await UntilAsync(() => rig.Peer.State == CallState.Available);
        await rig.Peer.CallAsync(rig.Identity, cancellationToken: TestContext.Current.CancellationToken);
        await UntilAsync(() => rig.Devices.Playbacks == 2);
        await rig.Service.RejectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, rig.Devices.PlaybackDisposals);
    }
}
