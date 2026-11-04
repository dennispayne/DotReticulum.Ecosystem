using System.Text;
using System.Text.Json;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace LXMF.Tests;

// Actual LXMRouter.get_announce_app_data at e8ba02483217af43be284a72f1c059c730b7d047;
// LXMRouter.py SHA-256 56b6a8e6b9f79256c4ae297340791eebcc1b6cda3cec04ab2dbe2f1efb53caa9.
[Trait("Category", "Unit")]
public sealed class LxmDisplayNameVectorTests
{
    [Fact]
    public async Task SequentialNamesMatchExecutedReferenceWithoutChangingDestinationOrStampCost()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "lxmf-display-name-vectors.json")));
        await using var manager = new PacketInterfaceManager([]);
        var transport = new TransportRouter(manager);
        using var identity = Identity.Generate();
        var router = new LxmRouter(transport, identity, "Before", new() { EnableRatchets = false, InboundStampCost = 13 });
        var destination = router.DeliveryHash.ToArray();
        try
        {
            foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
            {
                var name = vector.GetProperty("name").GetString();
                router.SetDisplayName(name);
                Assert.Equal(name, router.DisplayName);
                Assert.Equal(destination, router.DeliveryHash);
                Assert.Equal(13, router.InboundStampCost);
                Assert.Equal(Convert.FromHexString(vector.GetProperty("app_data").GetString()!), router.GetAnnounceAppData());
            }
            var previous = router.GetAnnounceAppData();
            Assert.Equal("UnicodeEncodeError", fixture.RootElement.GetProperty("invalid_name_error").GetString());
            Assert.Throws<EncoderFallbackException>(() => LxmfAppData.Build("\ud800"));
            Assert.Throws<EncoderFallbackException>(() => router.SetDisplayName("\ud800"));
            Assert.Equal(previous, router.GetAnnounceAppData());
            Assert.Throws<EncoderFallbackException>(() => new LxmRouter(transport, identity, "\ud800"));
        }
        finally { await router.DisposeAsync(); }
        Assert.Throws<ObjectDisposedException>(() => router.SetDisplayName("After disposal"));
    }
}
