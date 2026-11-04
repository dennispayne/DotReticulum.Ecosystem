using DotReticulum.Core;
using DotReticulum.Transport;

namespace LXMF.Tests;

[Trait("Category", "Integration")]
public class LxmRetryLifecycleTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(1700000300).AddTicks(Interlocked.Read(ref _ticks));
        public void Advance(TimeSpan span) => Interlocked.Add(ref _ticks, span.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new InertTimer();
        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ActiveBackchannel_IsFree_AndClosedBackchannelReschedulesBeforeChargingANewLink()
    {
        var clock = new Clock();
        var options = new LxmRouterOptions { ProcessingInterval = TimeSpan.FromMilliseconds(50) };
        await using var pair = await LxmfPair.CreateAsync(serverOptions: options, clientOptions: options, transportTimeProvider: clock);
        var first = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "open");
        await pair.Client.SendAsync(first);
        Assert.Equal(LxmState.Delivered, await first.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        pair.ServerRouter.DropPath(pair.Client.DeliveryHash);
        var backchannel = LxMessage.Create(pair.Client.DeliveryHash, pair.ServerIdentity, "backchannel");
        await pair.Server.SendAsync(backchannel);
        Assert.Equal(LxmState.Delivered, await backchannel.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, backchannel.DeliveryAttempts);
        var incoming = Assert.Single(pair.ServerRouter.Links.Links);
        Assert.False(incoming.IsInitiator);
        incoming.Teardown();
        await pair.WaitAsync(() => pair.ClientRouter.Links.Links.Count == 0);
        var retry = LxMessage.Create(pair.Client.DeliveryHash, pair.ServerIdentity, "after close");
        await pair.Server.SendAsync(retry);
        await pair.WaitAsync(() => retry.NextAttempt > 0 && pair.ServerRouter.HasPath(pair.Client.DeliveryHash));
        Assert.Equal(0, retry.DeliveryAttempts);
        Assert.False(retry.Completion.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(150);
        Assert.Equal(0, retry.DeliveryAttempts);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(LxmState.Delivered, await retry.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, retry.DeliveryAttempts);
    }

    [Fact]
    public async Task Disposal_DoesNotReinsertAnIdentifiedIncomingLinkFromItsClosedCallback()
    {
        var clock = new Clock();
        await using var pair = await LxmfPair.CreateAsync(transportTimeProvider: clock);
        var message = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "identified incoming link");
        await pair.Client.SendAsync(message);
        Assert.Equal(LxmState.Delivered, await message.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        var incoming = Assert.Single(pair.ServerRouter.Links.Links);
        Assert.NotNull(incoming.RemoteIdentity);
        await pair.Server.DisposeAsync();
        Assert.Equal(LinkStatus.Closed, incoming.Status);
        clock.Advance(TimeSpan.FromSeconds(601));
        Assert.Equal(0, pair.Server.CleanLinks());
    }

    [Fact]
    public async Task TerminalObserver_SeesFreedBoundedQueue_AndCanSubmitFreshDelivery()
    {
        await using var pair = await LxmfPair.CreateAsync(clientOptions: new LxmRouterOptions
        {
            ProcessingInterval = TimeSpan.FromMilliseconds(20),
            PathRequestWait = TimeSpan.FromMilliseconds(100),
            DeliveryRetryWait = TimeSpan.FromMilliseconds(100),
            MaxDeliveryAttempts = 2,
            OutboundQueueCapacity = 1,
        });
        var unknown = Enumerable.Repeat((byte)0xab, 16).ToArray();
        var failed = LxMessage.Create(unknown, pair.ClientIdentity, "unknown");
        var recovery = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "observer recovery");
        var queueAtFailure = -1;
        Task? submitted = null;
        failed.StateChanged += message =>
        {
            if (message.State != LxmState.Failed) return;
            queueAtFailure = pair.Client.PendingOutbound;
            submitted = pair.Client.SendAsync(recovery);
        };
        await pair.Client.SendAsync(failed);
        Assert.Equal(LxmState.Failed, await failed.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, queueAtFailure);
        Assert.Equal(2, failed.DeliveryAttempts);
        Assert.NotNull(submitted);
        await submitted;
        Assert.Equal(LxmState.Delivered, await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, recovery.DeliveryAttempts);
    }

    [Fact]
    public async Task CancellationWhilePending_AllowsFreshPeerDelivery_AndDisposalClosesThePendingLink()
    {
        await using var pair = await LxmfPair.CreateAsync();
        using var refusedIdentity = Identity.Generate();
        using var refused = new LocalDestination(refusedIdentity,
            Destination.Create(DestinationType.Single, "lxmf", ["delivery"], refusedIdentity)) { AcceptLinks = false };
        pair.ServerRouter.RegisterLocalDestination(refused);
        try
        {
            await pair.ServerRouter.AnnounceAsync(refused);
            await pair.WaitAsync(() => pair.ClientRouter.HasPath(refused.Destination.Hash));
            var message = LxMessage.Create(refused.Destination.Hash, pair.ClientIdentity, "cancel pending");
            await pair.Client.SendAsync(message);
            await pair.WaitAsync(() => message.DeliveryAttempts == 1 && pair.ClientRouter.Links.Links.Count == 1);
            var pending = Assert.Single(pair.ClientRouter.Links.Links);
            Assert.Equal(LinkStatus.Pending, pending.Status);
            Assert.True(pair.Client.CancelOutbound(message.Hash!));
            Assert.Equal(LxmState.Cancelled, await message.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, pair.Client.PendingOutbound);
            var recovery = LxMessage.Create(pair.Server.DeliveryHash, pair.ClientIdentity, "fresh healthy peer");
            await pair.Client.SendAsync(recovery);
            Assert.Equal(LxmState.Delivered, await recovery.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
            await pair.Client.DisposeAsync();
            Assert.Equal(LinkStatus.Closed, pending.Status);
            Assert.Equal(LxmState.Cancelled, message.State);
            Assert.Empty(pair.ClientRouter.Links.Links);
        }
        finally { pair.ServerRouter.DeregisterLocalDestination(refused); }
    }
}
