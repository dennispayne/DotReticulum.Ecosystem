using System.Net;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandRepositoryOptionsTests
{
    [Fact]
    public void SharingIsDisabledUnlessExplicitlyAcknowledgedAndBindsLoopbackByDefault()
    {
        var options = Valid();
        Assert.False(options.AllowPublicUnauthenticatedAccess);
        Assert.Equal(IPAddress.Loopback, options.Address);
        Assert.Equal(4444, options.Port);
        Assert.Equal(8, options.MaximumConnections);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidPortIsRejectedWithoutOpeningAnything(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SidebandRepositoryServer(Valid() with { Port = port }));

    [Fact]
    public void InvalidResourceLimitsAreRejectedWithoutOpeningAnything()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SidebandRepositoryServer(Valid() with { MaximumConnections = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SidebandRepositoryServer(Valid() with { MaximumConnections = 65 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SidebandRepositoryServer(Valid() with { MaximumHeaderBytes = 511 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SidebandRepositoryServer(Valid() with { MaximumHeaderBytes = 32769 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SidebandRepositoryServer(Valid() with { MaximumFileBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SidebandRepositoryServer(Valid() with { MaximumPackageEntries = 4097 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SidebandRepositoryServer(Valid() with { RequestTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SidebandRepositoryServer(Valid() with { RequestTimeout = TimeSpan.FromMinutes(6) }));
    }

    [Fact]
    public void PrivateCertificateStorageCannotBePublished()
    {
        Assert.Throws<ArgumentException>(() => new SidebandRepositoryServer(Valid() with { CertificateDirectory = "public" }));
        Assert.Throws<ArgumentException>(() => new SidebandRepositoryServer(Valid() with { CertificateDirectory = Path.Combine("public", "private") }));
    }

    private static SidebandRepositoryOptions Valid() => new()
    {
        RootDirectory = "public", CertificateDirectory = "private",
    };
}
