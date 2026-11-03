using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotReticulum.Core;
using DotReticulum.Transport;
using LXMF;

namespace NomadNet.Tests;

[Trait("Category", "Unit")]
public sealed class NomadNetAttachmentTests
{
    [Fact]
    public void NamesAndMediaMatchExecutedPrimaryReference()
    {
        // Generated with GenerateNomadNetVectors.py executing primary 1.4.4 f6b56044.
        // Fixture records the complete revision, helper names and source SHA-256.
        using var fixture = JsonDocument.Parse(typeof(NomadNetAttachmentTests).Assembly.GetManifestResourceStream("NomadNet.Tests.NomadNetVectors.json")!);
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("commit").GetString());
        foreach (var item in fixture.RootElement.GetProperty("names").EnumerateArray())
            Assert.Equal(item.GetProperty("output").GetString(), NomadNetAttachments.SafeName(item.GetProperty("input").GetString()));
        foreach (var item in fixture.RootElement.GetProperty("media").EnumerateArray())
        {
            var format = item.GetProperty("format");
            object? value = format.ValueKind == JsonValueKind.String ? format.GetString() :
                format.ValueKind == JsonValueKind.Number ? format.GetInt64() : null;
            Assert.Equal(item.GetProperty("extension").GetString(), NomadNetAttachments.MediaExtension(value,
                Convert.FromHexString(item.GetProperty("data").GetString()!), item.GetProperty("audio").GetBoolean()));
        }
        foreach (var item in fixture.RootElement.GetProperty("inline").EnumerateArray())
        {
            var page = MicronText.Render(Encoding.UTF8.GetBytes(item.GetProperty("source").GetString()!));
            var plain = page.Text;
            for (var index = 0; index < page.Links.Count; index++) plain = plain.Replace($"[{index + 1}] ", "", StringComparison.Ordinal);
            Assert.Equal(item.GetProperty("text").GetString(), plain);
        }
    }

    [Fact]
    public void ExtractHandlesFilesImagesAudioAndBounds()
    {
        var attachments = NomadNetAttachments.Extract(new()
        {
            [LxmField.FileAttachments] = new object?[] { new object?[] { "../bad.txt", new byte[] { 1, 2 } }, "malformed" },
            [LxmField.Image] = new object?[] { "webp", new byte[] { 3 } },
            [LxmField.Audio] = new object?[] { 16L, new byte[] { 4 } },
        });
        Assert.Equal(new[] { "bad.txt", "image.webp", "audio.ogg" }, attachments.Select(item => item.Name));
        Assert.Throws<InvalidDataException>(() => NomadNetAttachments.Extract(new()
        {
            [LxmField.FileAttachments] = Enumerable.Range(0, 65).Select(_ => (object?)new object?[] { "file", Array.Empty<byte>() }).ToArray(),
        }));
        Assert.Throws<InvalidDataException>(() => NomadNetAttachments.Extract(new()
        {
            [LxmField.Image] = new byte[NomadNetAttachments.MaximumBytes + 1],
        }));
    }

    [Theory]
    [InlineData("#!c=0\nprivate", 0)]
    [InlineData("#!c=30\ncache", 30)]
    [InlineData("#!c=-1\nexpired", -1)]
    [InlineData("#!c=oops\ninvalid", NomadNetPageCache.DefaultLifetimeSeconds)]
    [InlineData("plain", NomadNetPageCache.DefaultLifetimeSeconds)]
    public void CacheLifetimeMatchesBrowserHeaders(string page, int expected) =>
        Assert.Equal(expected, NomadNetPageCache.Lifetime(Encoding.UTF8.GetBytes(page)));
}

[Trait("Category", "Integration")]
public sealed class NomadNetFunctionalTests
{
    [Fact]
    public async Task HostServesPagesFilesDefaultAndEnforcesIdentityLists()
    {
        var directory = CreateDirectory();
        using var identity = Identity.Generate();
        try
        {
            var host = new NomadNetContent(directory);
            var page = Path.Combine(directory, "storage", "pages", "private.mu");
            var file = Path.Combine(directory, "storage", "files", "test.bin");
            await File.WriteAllTextAsync(page, "private page");
            await File.WriteAllBytesAsync(file, new byte[] { 1, 2, 3 });
            Assert.Contains("/page/private.mu", host.RequestPaths());
            Assert.Contains("Create storage/pages/index.mu", Encoding.UTF8.GetString(Assert.IsType<byte[]>(
                await host.RespondAsync(Context("/page/index.mu"), default))));
            Assert.Equal("private page", Encoding.UTF8.GetString(Assert.IsType<byte[]>(await host.RespondAsync(Context("/page/private.mu"), default))));
            await File.WriteAllTextAsync(page + ".allowed", Convert.ToHexStringLower(identity.Hash));
            Assert.Contains("Not Allowed", Encoding.UTF8.GetString(Assert.IsType<byte[]>(await host.RespondAsync(Context("/page/private.mu"), default))));
            Assert.Equal("private page", Encoding.UTF8.GetString(Assert.IsType<byte[]>(
                await host.RespondAsync(Context("/page/private.mu") with { RemoteIdentity = identity }, default))));
            var response = Assert.IsType<FileResponse>(await host.RespondAsync(Context("/file/test.bin"), default));
            Assert.Equal(new byte[] { 1, 2, 3 }, response.Data);
            var metadata = Assert.IsType<Dictionary<object, object?>>(response.Metadata);
            Assert.Equal("test.bin", Encoding.UTF8.GetString(Assert.IsType<byte[]>(metadata["name"])));
            Assert.DoesNotContain("/page/private.mu.allowed", host.RequestPaths());
            foreach (var path in new[] { "/page/../outside", "/page/.hidden", "/file/a\\b", "/page/private.mu.allowed",
                "/page/private.mu.allowed.", "/page/private.mu.allowed " })
                Assert.Null(await host.RespondAsync(Context(path), default));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task HostBoundsContentCancellationAndNativeMedia()
    {
        var directory = CreateDirectory();
        try
        {
            var host = new NomadNetContent(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "storage", "pages", "large.mu"), new byte[MicronText.MaximumPageBytes + 1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => host.RespondAsync(Context("/page/large.mu"), default).AsTask());
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RespondAsync(Context("/page/large.mu"), cancel.Token).AsTask());
            var media = Path.Combine(directory, "storage", "pages", "image.webp");
            await File.WriteAllBytesAsync(media, "RIFF0000WEBP"u8.ToArray());
            var context = Context("/media") with { Data = new Dictionary<object, object?> { ["path"] = "/media/image.webp", ["key"] = "image" } };
            Assert.IsType<FileResponse>(await host.RespondAsync(context, default));
            Assert.False(Assert.IsType<bool>(await host.RespondAsync(context with
                { Data = new Dictionary<object, object?> { ["path"] = "/media/../image.webp", ["key"] = "image" } }, default)));
            Assert.False(Assert.IsType<bool>(await host.RespondAsync(context with { Data = null }, default)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CacheSurvivesRestartHonorsNoCacheAndEvictsExpiredEntries()
    {
        var directory = CreateDirectory();
        try
        {
            var clock = new TestClock();
            var cache = new NomadNetPageCache(directory, clock);
            await cache.StoreAsync("url", "#!c=1\ncontent"u8.ToArray());
            Assert.Equal("#!c=1\ncontent", Encoding.UTF8.GetString((await new NomadNetPageCache(directory, clock).ReadAsync("url"))!));
            clock.Now = clock.Now.AddSeconds(2);
            Assert.Null(await cache.ReadAsync("url"));
            await cache.StoreAsync("url", "cache me"u8.ToArray());
            await cache.StoreAsync("url", "#!c=0\nprivate"u8.ToArray());
            Assert.Null(await cache.ReadAsync("url"));
            await cache.StoreAsync("other", "content"u8.ToArray());
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ReadAsync("other", cancel.Token));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task AttachmentsPersistWithoutUsingRemoteNamesAsPaths()
    {
        var directory = CreateDirectory();
        try
        {
            var id = new string('a', 64);
            await NomadNetAttachments.SaveAsync(directory, id, new()
            {
                [LxmField.FileAttachments] = new object?[] { new object?[] { "../../outside.txt", new byte[] { 3, 4, 5 } } },
            });
            var path = Path.Combine(directory, "attachments", id, "0.bin");
            Assert.Equal(new byte[] { 3, 4, 5 }, await File.ReadAllBytesAsync(path));
            Assert.Equal("outside.txt", await File.ReadAllTextAsync(path + ".name"));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 3, 4, 5 })), await File.ReadAllTextAsync(path + ".sha256"));
            await Assert.ThrowsAsync<ArgumentException>(() => NomadNetAttachments.SaveAsync(directory, "../outside", new()));
            await using (var quota = File.Create(Path.Combine(directory, "attachments", "quota.bin"))) quota.SetLength(NomadNetAttachments.MaximumStoredBytes);
            await Assert.ThrowsAsync<InvalidDataException>(() => NomadNetAttachments.SaveAsync(directory, new string('b', 64), new()
            {
                [LxmField.Image] = new byte[] { 1 },
            }));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SettingsRoundTripAndRejectUnsafeConfigurations()
    {
        var directory = CreateDirectory();
        try
        {
            var settings = new NomadNetSettings { EnableNode = true, EnablePropagation = true, PropagationNode = new string('b', 32), NodeName = "Test" };
            await settings.SaveAsync(directory);
            Assert.Equivalent(settings, await NomadNetSettings.OpenAsync(directory), strict: true);
            await Assert.ThrowsAsync<InvalidDataException>(() => (settings with { NodeAnnounceIntervalMinutes = 0 }).SaveAsync(directory));
            await Assert.ThrowsAsync<InvalidDataException>(() => (settings with { EnableNode = false }).SaveAsync(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [LinuxFact]
    public async Task ExecutablePagesRequireOptInPassFormEnvironmentAndCancelChildren()
    {
        var directory = CreateDirectory();
        using var identity = Identity.Generate();
        try
        {
            var disabled = new NomadNetContent(directory);
            var page = Path.Combine(directory, "storage", "pages", "form.mu");
            await File.WriteAllTextAsync(page, "#!/bin/sh\nprintf '%s' \"$field_name|$var_test|$remote_identity|$link_id\"");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(page, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Contains("disabled", Encoding.UTF8.GetString(Assert.IsType<byte[]>(await disabled.RespondAsync(Context("/page/form.mu"), default))));
            var host = new NomadNetContent(directory, enableExecutablePages: true);
            var request = Context("/page/form.mu") with
            {
                RemoteIdentity = identity,
                Data = new Dictionary<object, object?> { ["field_name"] = "Alice", ["var_test"] = "value", ["NOT_ALLOWED"] = "secret" },
            };
            Assert.Equal($"Alice|value|{Convert.ToHexStringLower(identity.Hash)}|{new string('0', 32)}",
                Encoding.UTF8.GetString(Assert.IsType<byte[]>(await host.RespondAsync(request, default))));
            await File.WriteAllTextAsync(page + ".allowed", "#!/bin/sh\nprintf '%s\\n' '" + Convert.ToHexStringLower(identity.Hash) + "'");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(page + ".allowed", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Contains("Alice", Encoding.UTF8.GetString(Assert.IsType<byte[]>(await host.RespondAsync(request, default))));
            using var outsider = Identity.Generate();
            Assert.Contains("Not Allowed", Encoding.UTF8.GetString(Assert.IsType<byte[]>(await host.RespondAsync(request with { RemoteIdentity = outsider }, default))));
            File.Delete(page + ".allowed");
            await File.WriteAllTextAsync(page, "#!/bin/sh\nsleep 30");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RespondAsync(request, cancel.Token).AsTask());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [LinuxFact]
    public async Task HostedPathsCannotEscapeThroughSymlinks()
    {
        var directory = CreateDirectory();
        try
        {
            var host = new NomadNetContent(directory);
            var outside = Directory.CreateDirectory(Path.Combine(directory, "private")).FullName;
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.mu"), "secret");
            var link = Path.Combine(directory, "storage", "pages", "escape");
            Directory.CreateSymbolicLink(link, outside);
            Assert.DoesNotContain("/page/escape/secret.mu", host.RequestPaths());
            Assert.Null(await host.RespondAsync(Context("/page/escape/secret.mu"), default));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal sealed class LinuxFactAttribute : FactAttribute
    {
        public LinuxFactAttribute()
        {
            if (!OperatingSystem.IsLinux()) Skip = "Requires Linux executable files, shell processes and symbolic links.";
        }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "nomadnet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static RequestContext Context(string path) => new(path, null, new byte[16], new byte[16], null, 0);
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
