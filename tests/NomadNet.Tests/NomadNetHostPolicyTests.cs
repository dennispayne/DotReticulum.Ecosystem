using DotReticulum.Applications;
using DotReticulum.Core;

namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class NomadNetHostPolicyTests
{
    [Fact]
    public async Task HostPathsRemainAtomicAtQuotaAndSelectiveEnumerationDoesNotMixRoots()
    {
        var directory = CreateDirectory();
        try
        {
            var pages = Path.Combine(directory, "storage", "pages");
            Directory.CreateDirectory(pages);
            for (var index = 0; index < NomadNetHost.MaximumPaths - 2; index++)
                File.WriteAllBytes(Path.Combine(pages, index + ".mu"), []);
            await using var node = ReticulumNode.Open(directory);
            using var identity = Identity.Generate();
            await using var host = new NomadNetHost(node.Router, identity, directory, new());
            Assert.Equal(NomadNetHost.MaximumPaths, host.RegisteredPaths.Count);
            await File.WriteAllTextAsync(Path.Combine(pages, "overflow.mu"), "one too many");
            Assert.Throws<InvalidDataException>(host.Refresh);
            Assert.Equal(NomadNetHost.MaximumPaths, host.RegisteredPaths.Count);
            Assert.DoesNotContain("/page/overflow.mu", host.RegisteredPaths);
            var content = new NomadNetContent(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "storage", "files", "file.txt"), "file");
            Assert.All(content.RequestPaths(includeFiles: false), path => Assert.False(path.StartsWith("/file/", StringComparison.Ordinal)));
            Assert.Equal(["/file/file.txt"], content.RequestPaths(includePages: false));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AnnounceChainsPropagationWithoutLogSubscriberAndDisposalIsRepeatable()
    {
        var directory = CreateDirectory();
        try
        {
            await using var node = ReticulumNode.Open(directory);
            using var identity = Identity.Generate();
            var calls = 0;
            var host = new NomadNetHost(node.Router, identity, directory, new(), _ =>
            {
                calls++;
                return ValueTask.CompletedTask;
            });
            await node.StartAsync();
            try
            {
                await host.AnnounceAsync();
                Assert.Equal(1, calls);
            }
            finally { await host.DisposeAsync(); }
            await host.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => host.RegisteredPaths);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => host.AnnounceAsync().AsTask());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(10081, 0)]
    public async Task InvalidRefreshIntervalsDoNotCreatePartialSettings(double pages, double files)
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomadnet-invalid-host-" + Guid.NewGuid().ToString("N")));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new NomadNetSettings {
                NodePageRefreshIntervalMinutes = pages, NodeFileRefreshIntervalMinutes = files }.SaveAsync(directory));
            Assert.False(Directory.Exists(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static string CreateDirectory()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomadnet-host-policy-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "config"), "[reticulum]\n share_instance = No\n[interfaces]\n");
        return directory;
    }
}
