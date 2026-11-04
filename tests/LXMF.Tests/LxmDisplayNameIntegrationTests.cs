namespace LXMF.Tests;

[Trait("Category", "Integration")]
public sealed class LxmDisplayNameIntegrationTests
{
    [Fact]
    public async Task PathResponseUsesCurrentNameWithoutAnExplicitAnnounce()
    {
        await using var pair = await LxmfPair.CreateAsync(serverOptions: new() { EnableRatchets = false, InboundStampCost = 13 });
        var destination = pair.Server.DeliveryHash.ToArray();
        pair.Server.SetDisplayName("Path response name");
        await Task.Delay(1100);
        Assert.True(pair.ClientRouter.DropPath(destination));
        await pair.ClientRouter.RequestPathAsync(destination);
        await pair.WaitAsync(() => pair.ClientRouter.HasPath(destination) &&
            LxmfAppData.DisplayName(pair.ClientRouter.Engine.Known.RecallAppData(destination)) == "Path response name");
        Assert.Equal(13, LxmfAppData.StampCost(pair.ClientRouter.Engine.Known.RecallAppData(destination)));
    }

    [Fact]
    public async Task ExplicitAnnounceAdvertisesChangedNameWithoutChangingIdentity()
    {
        await using var pair = await LxmfPair.CreateAsync(serverOptions: new() { EnableRatchets = false, InboundStampCost = 13 });
        var destination = pair.Server.DeliveryHash.ToArray();
        Assert.Equal("Server", LxmfAppData.DisplayName(pair.ClientRouter.Engine.Known.RecallAppData(destination)));
        pair.Server.SetDisplayName("Changed name");
        Assert.Equal("Server", LxmfAppData.DisplayName(pair.ClientRouter.Engine.Known.RecallAppData(destination)));
        // RNS accepts a strictly newer whole-second announcement, not a rewritten cached packet.
        await Task.Delay(1100);
        await pair.Server.AnnounceAsync();
        await pair.WaitAsync(() => LxmfAppData.DisplayName(pair.ClientRouter.Engine.Known.RecallAppData(destination)) == "Changed name");
        Assert.Equal(destination, pair.Server.DeliveryHash);
        Assert.Equal(13, LxmfAppData.StampCost(pair.ClientRouter.Engine.Known.RecallAppData(destination)));
    }
}
