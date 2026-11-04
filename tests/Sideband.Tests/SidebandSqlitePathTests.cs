using Sideband;

namespace Sideband.Tests;

public sealed partial class SidebandProfileImportTests
{
    [Theory]
    [InlineData(300)]
    [InlineData(600)]
    public async Task PrivateProfileSnapshotsSupportLongUnicodePathsWithoutChangingSource(int minimumCharacters)
    {
        Assert.SkipWhen(minimumCharacters > 300 && !OperatingSystem.IsWindows(),
            "The 600-character case targets Windows long-path VFS; the exercised Linux system SQLite VFS has a 512-byte pathname budget.");
        var parent = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "sqlite-profile-" + Guid.NewGuid().ToString("N"));
        var root = LongPath(parent, minimumCharacters);
        try
        {
            using var profile = new Profile(root);
            Assert.True(profile.Target.Length > minimumCharacters);
            var before = profile.Fingerprint();
            var result = await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
            var state = await new SidebandStateStore(profile.Target).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(result.DeliveryHash, state.OwnDeliveryHash);
            Assert.Equal("Python Profile", state.DisplayName);
            Assert.Equal(before, profile.Fingerprint());
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
    }

    [Theory]
    [InlineData(300)]
    [InlineData(600)]
    public async Task ImmutableMapSnapshotsSupportLongEscapedPathsAndReopenWithoutChangingSource(int minimumCharacters)
    {
        Assert.SkipWhen(minimumCharacters > 300 && !OperatingSystem.IsWindows(),
            "The 600-character case targets Windows long-path VFS; the exercised Linux system SQLite VFS has a 512-byte pathname budget.");
        var parent = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "sqlite-map-" + Guid.NewGuid().ToString("N"));
        var root = LongPath(parent, minimumCharacters);
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-map.mbtiles");
        var token = TestContext.Current.CancellationToken;
        var original = await File.ReadAllBytesAsync(source, token);
        try
        {
            Assert.True(root.Length > minimumCharacters);
            byte[] expected;
            await using (var store = await SidebandMapStore.OpenAsync(root, cancellationToken: token))
            {
                await store.ImportMbTilesAsync(source, token);
                expected = Assert.IsType<byte[]>(await store.GetOfflineTileAsync(new(1, 0, 0), token));
            }
            await using (var reloaded = await SidebandMapStore.OpenAsync(root, cancellationToken: token))
                Assert.Equal(expected, await reloaded.GetOfflineTileAsync(new(1, 0, 0), token));
            Assert.Equal(original, await File.ReadAllBytesAsync(source, token));
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
    }

    private static string LongPath(string parent, int minimumCharacters)
    {
        var root = parent;
        while (root.Length <= minimumCharacters) root = Path.Combine(root, new string('p', 60));
        return Path.Combine(root, "escaped # % \u03bb");
    }
}
