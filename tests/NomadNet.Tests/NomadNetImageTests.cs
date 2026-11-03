using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotReticulum.Core;
using DotReticulum.Transport;
using NomadNet;

namespace NomadNet.Tests;

[Trait("Category", "Unit")]
public sealed class NomadNetImageHeaderTests
{
    [Fact]
    public void ActualPrimaryEncodedImagesExposeTheExecutedReferenceDimensions()
    {
        using var fixture = JsonDocument.Parse(typeof(NomadNetImageHeaderTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetImageVectors.json")!);
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("commit").GetString());
        foreach (var item in fixture.RootElement.GetProperty("vectors").EnumerateArray())
            Assert.Equal((item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32()),
                NomadNetImages.Dimensions(Convert.FromBase64String(item.GetProperty("referenceWebp").GetString()!)));
        Assert.Throws<InvalidDataException>(() => NomadNetImages.Dimensions("not webp"u8));
        var bomb = new byte[30];
        "RIFF"u8.CopyTo(bomb);
        "WEBPVP8X"u8.CopyTo(bomb.AsSpan(8));
        bomb.AsSpan(24).Fill(255);
        Assert.Throws<InvalidDataException>(() => NomadNetImages.Dimensions(bomb));
    }
}

[Trait("Category", "Integration")]
public sealed class NomadNetImageTests
{
    private static string Converter => Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_IMAGE_CONVERTER")!;

    [ImageBackendFact]
    public async Task AllPrimaryFormatsResizeWithoutUpscalingAndReferenceImagesDecodeForPreview()
    {
        using var fixture = Fixture();
        var images = new NomadNetImages(Converter);
        foreach (var item in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var source = Convert.FromBase64String(item.GetProperty("input").GetString()!);
            byte[] encoded;
            try { encoded = await images.ConvertAsync(source); }
            catch (Exception exception)
            { throw new Xunit.Sdk.XunitException($"Reference format {item.GetProperty("format")}: {exception}"); }
            Assert.Equal((item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32()), NomadNetImages.Dimensions(encoded));
            var preview = await images.PreviewAsync(Convert.FromBase64String(item.GetProperty("referenceWebp").GetString()!), 40);
            Assert.InRange(preview.Split('\n').Length, 1, 60);
            Assert.All(preview.Split('\n'), line => Assert.InRange(line.Length, 1, 40));
            Assert.All(preview, character => Assert.Contains(character, " .:-=+*#%@\n"));
        }
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => images.ConvertAsync([]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => images.ConvertAsync([1], quality: 101));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => images.ConvertAsync([1], cancellationToken: canceled.Token));
        var error = await Record.ExceptionAsync(() => images.ConvertAsync("malformed image"u8.ToArray()));
        Assert.True(error is IOException or InvalidDataException);
        var good = Convert.FromBase64String(fixture.RootElement.GetProperty("vectors")[0].GetProperty("input").GetString()!);
        var converted = await images.ConvertAsync(good);
        Assert.NotEmpty(converted);
        var colors = await images.PreviewAsync(converted, 40, color: true);
        Assert.Contains("\u001b[38;2;", colors);
        Assert.Contains("\u001b[48;2;", colors);
        Assert.EndsWith("\u001b[0m", colors);
    }

    [ImageBackendFact]
    public async Task HostedConversionsAreAuthorizedNamedCachedRestartedAndBounded()
    {
        using var fixture = Fixture();
        var directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomadnet-images-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            var content = new NomadNetContent(directory, mediaConverter: Converter);
            var source = Convert.FromBase64String(fixture.RootElement.GetProperty("vectors")[0].GetProperty("input").GetString()!);
            var file = Path.Combine(directory, "storage", "pages", "photo.png");
            await File.WriteAllBytesAsync(file, source);
            var request = new RequestContext("/media", new Dictionary<object, object?> { ["path"] = "/media/photo.png", ["key"] = null },
                new byte[16], new byte[16], null, 0);
            await File.WriteAllTextAsync(file + ".allowed", new string('a', 32));
            Assert.False(Assert.IsType<bool>(await content.RespondAsync(request, default)));
            using var identity = Identity.Generate();
            await File.WriteAllTextAsync(file + ".allowed", Convert.ToHexStringLower(identity.Hash));
            var allowed = request with { RemoteIdentity = identity };
            var response = Assert.IsType<FileResponse>(await content.RespondAsync(allowed, default));
            Assert.Equal("photo.webp", Encoding.UTF8.GetString(Assert.IsType<byte[]>(
                Assert.IsType<Dictionary<object, object?>>(response.Metadata)["name"])));
            var restored = new NomadNetContent(directory, mediaConverter: "missing-image-converter");
            Assert.Equal(response.Data, Assert.IsType<FileResponse>(await restored.RespondAsync(allowed, default)).Data);
            var cache = Path.Combine(directory, "storage", "cache", "conversions");
            var key = Convert.ToHexStringLower(SHA256.HashData(source)) + ".q85.d1200.webp";
            Assert.True(File.Exists(Path.Combine(cache, key)));
            await File.WriteAllTextAsync(Path.Combine(cache, key), "corrupted cache");
            Assert.Equal(NomadNetImages.Dimensions(response.Data),
                NomadNetImages.Dimensions(Assert.IsType<FileResponse>(await content.RespondAsync(allowed, default)).Data));
            for (var index = 0; index < 256; index++)
            {
                var path = Path.Combine(cache, index.ToString("x64") + ".webp");
                await File.WriteAllBytesAsync(path, response.Data);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));
            }
            await File.WriteAllBytesAsync(file, Convert.FromBase64String(fixture.RootElement.GetProperty("vectors")[1].GetProperty("input").GetString()!));
            Assert.IsType<FileResponse>(await content.RespondAsync(allowed, default));
            Assert.Equal(NomadNetImageCache.MaximumFiles, Directory.EnumerateFiles(cache, "*.webp").Count());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task MissingExternalCodecIsAnExplicitFailureNotAFalseConversion()
    {
        var images = new NomadNetImages(Path.Combine("artifacts", "builds", "missing-converter-" + Guid.NewGuid().ToString("N")));
        await Assert.ThrowsAsync<IOException>(() => images.ConvertAsync([1, 2, 3]));
    }

    private static JsonDocument Fixture() => JsonDocument.Parse(typeof(NomadNetImageTests).Assembly
        .GetManifestResourceStream("NomadNet.Tests.NomadNetImageVectors.json")!);
}

public sealed class ImageBackendFactAttribute : FactAttribute
{
    public ImageBackendFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_IMAGE_CONVERTER")))
            Skip = "Select the actual local FFmpeg binary with DOTRETICULUM_NOMADNET_IMAGE_CONVERTER.";
    }
}
