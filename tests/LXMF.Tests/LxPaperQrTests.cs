using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LXMF;
using ZXing;
using ZXing.QrCode;
using FormatException = System.FormatException;

namespace LXMF.Tests;

/// <summary>
/// GenerateLxPaperQrVectors.py executes LXMessage.as_qr at LXMF 1.2.1
/// e8ba02483217af43be284a72f1c059c730b7d047; source SHA-256
/// 2fe8a1cb610e70f3d1c0b4f5086f660a45607484ce8dd53bd925943e95398aea.
/// Frozen software pixels are not live transport or physical scanning evidence.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LxPaperQrTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static string Uri
    {
        get
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Fixture("lx-paper-qr.json")));
            return fixture.RootElement.GetProperty("uri").GetString()!;
        }
    }

    [Fact]
    public void DecodesActualPinnedLxMessageQrPixelsInBothFormats()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Fixture("lx-paper-qr.json")));
        var root = fixture.RootElement;
        Assert.Equal("e8ba02483217af43be284a72f1c059c730b7d047", root.GetProperty("lxmf_sha").GetString());
        Assert.Equal("LXMF.LXMessage.as_qr", root.GetProperty("method").GetString());
        Assert.Equal("L", root.GetProperty("error_correction").GetString());
        Assert.Equal(1, root.GetProperty("border").GetInt32());
        Assert.Equal(10, root.GetProperty("box_size").GetInt32());
        var width = root.GetProperty("width").GetInt32();
        using var compressed = File.OpenRead(Fixture("lx-paper-qr.gray.zlib"));
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var pixels = new byte[width * width];
        zlib.ReadExactly(pixels);
        Assert.Equal(-1, zlib.ReadByte());
        Assert.Equal(root.GetProperty("gray_sha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(pixels)));
        Assert.Equal(root.GetProperty("png_sha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Fixture("lx-paper-qr.png")))));
        Assert.Equal(Uri, LxPaperQr.Decode(pixels, width, width));
        var rgb = new byte[pixels.Length * 3];
        for (var i = 0; i < pixels.Length; i++)
            rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = pixels[i];
        Assert.Equal(Uri, LxPaperQr.Decode(rgb, width, width, LxPaperQrPixelFormat.Rgb24));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 10)]
    [InlineData(4, 3)]
    [InlineData(16, 8)]
    public void EncodesImmutableBoundedSymbolAndOwnedPixels(int border, int scale)
    {
        var symbol = LxPaperQr.Encode(Uri, border);
        Assert.InRange(symbol.Version, 1, 40);
        Assert.Equal(border, symbol.QuietZone);
        Assert.Equal(17 + symbol.Version * 4 + border * 2, symbol.Size);
        for (var i = 0; i < symbol.Size; i++)
        {
            Assert.False(symbol[i, 0]);
            Assert.False(symbol[0, i]);
            Assert.False(symbol[i, symbol.Size - 1]);
            Assert.False(symbol[symbol.Size - 1, i]);
        }
        var pixels = symbol.ToGrayscale(scale);
        Assert.Equal(Uri, LxPaperQr.Decode(pixels, symbol.Size * scale, symbol.Size * scale));
        pixels.AsSpan().Fill(128);
        Assert.Equal(Uri, LxPaperQr.Decode(symbol.ToGrayscale(scale), symbol.Size * scale, symbol.Size * scale));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = symbol[-1, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = symbol[0, symbol.Size]);
    }

    [Fact]
    public void PngContainsStandardGrayscaleChunksAndExactRaster()
    {
        var symbol = LxPaperQr.Encode(Uri, 4);
        var png = symbol.ToPng(3);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var offset = 8;
        var chunks = new List<string>();
        byte[]? pixels = null;
        while (offset < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(png.AsSpan(offset + 4, 4));
            chunks.Add(type);
            var data = png.AsSpan(offset + 8, length);
            Assert.Equal(Crc(png.AsSpan(offset + 4, length + 4)),
                BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4)));
            if (type == "IHDR")
            {
                Assert.Equal((uint)(symbol.Size * 3), BinaryPrimitives.ReadUInt32BigEndian(data));
                Assert.Equal((uint)(symbol.Size * 3), BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                Assert.Equal(new byte[] { 8, 0, 0, 0, 0 }, data[8..].ToArray());
            }
            if (type == "IDAT")
            {
                using var input = new MemoryStream(data.ToArray());
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                var width = symbol.Size * 3;
                pixels = new byte[width * width];
                for (var y = 0; y < width; y++)
                {
                    Assert.Equal(0, zlib.ReadByte());
                    zlib.ReadExactly(pixels.AsSpan(y * width, width));
                }
                Assert.Equal(-1, zlib.ReadByte());
            }
            if (type == "IEND") Assert.Equal(0, length);
            offset += length + 12;
        }
        Assert.Equal(png.Length, offset);
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, chunks);
        Assert.Equal(symbol.ToGrayscale(3), pixels);
        Assert.Equal(Uri, LxPaperQr.Decode(pixels!, symbol.Size * 3, symbol.Size * 3));
        Assert.Equal(png, symbol.ToPng(3));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.invalid")]
    [InlineData("lxm://")]
    [InlineData("lxm://not%base64")]
    [InlineData("lxm://AA")]
    public void RejectsMalformedAndForeignEnvelopes(string uri)
    {
        Assert.Throws<FormatException>(() => LxPaperQr.Encode(uri));
        if (uri.Length == 0) return;
        var matrix = new QRCodeWriter().encode(uri, BarcodeFormat.QR_CODE, 240, 240);
        var pixels = new byte[matrix.Width * matrix.Height];
        for (var y = 0; y < matrix.Height; y++)
            for (var x = 0; x < matrix.Width; x++)
                pixels[y * matrix.Width + x] = matrix[x, y] ? (byte)0 : (byte)255;
        Assert.Throws<FormatException>(() => LxPaperQr.Decode(pixels, matrix.Width, matrix.Height));
    }

    [Fact]
    public void RejectsCapacityOverflowAndInvalidUnicodeBeforeUnboundedWork()
    {
        Assert.Throws<ArgumentNullException>(() => LxPaperQr.Encode(null!));
        Assert.Throws<FormatException>(() => LxPaperQr.Encode("lxm://" + new string('A', 100_000)));
        Assert.Throws<System.Text.EncoderFallbackException>(() => LxPaperQr.Encode("lxm://\ud800"));
        Assert.Throws<FormatException>(() => LxPaperQr.Encode("lxm://" + new string('界', 1100)));
        var bounded = "lxm://" + Convert.ToBase64String(new byte[LxMessage.PaperMdu]);
        _ = LxMessage.GetPaperDestinationHash(bounded);
        // A valid maximum envelope with base64 padding exceeds correction-L byte-mode capacity.
        Assert.Equal(LxPaperQr.MaxUriUtf8Bytes, bounded.Length);
        Assert.Throws<FormatException>(() => LxPaperQr.Encode(bounded));
        var unpadded = bounded.TrimEnd('=');
        var symbol = LxPaperQr.Encode(unpadded);
        Assert.Equal(40, symbol.Version);
        Assert.Equal(unpadded, LxPaperQr.Decode(symbol.ToGrayscale(2), symbol.Size * 2, symbol.Size * 2));
    }

    [Fact]
    public void ValidEnvelopeIsNotAuthenticatedMessage()
    {
        var uri = "lxm://" + Convert.ToBase64String(new byte[17]).TrimEnd('=');
        var symbol = LxPaperQr.Encode(uri);
        Assert.Equal(uri, LxPaperQr.Decode(symbol.ToGrayscale(), symbol.Size * 10, symbol.Size * 10));
        Assert.Equal(new byte[16], LxMessage.GetPaperDestinationHash(uri));
    }

    [Fact]
    public void ReadsRotatedEmbeddedAndLightlyDamagedSoftwarePixels()
    {
        var symbol = LxPaperQr.Encode(Uri, 4);
        const int scale = 4;
        var width = symbol.Size * scale;
        var pixels = symbol.ToGrayscale(scale);
        // Alter one data module, not the finder/timing patterns.
        for (var y = 40 * scale; y < 41 * scale; y++)
            for (var x = 40 * scale; x < 41 * scale; x++)
                pixels[y * width + x] ^= 255;
        Assert.Equal(Uri, DecodeObserved(pixels, width, width, "damaged"));
        var canvasWidth = width + 100;
        var canvasHeight = width + 60;
        var canvas = Enumerable.Repeat((byte)255, canvasWidth * canvasHeight).ToArray();
        for (var y = 0; y < width; y++)
            for (var x = 0; x < width; x++)
                canvas[(x + 30) * canvasWidth + width - 1 - y + 50] = pixels[y * width + x];
        Assert.Equal(Uri, DecodeObserved(canvas, canvasWidth, canvasHeight, "rotated embedded"));
        pixels.AsSpan().Fill(255);
        Assert.Throws<FormatException>(() => LxPaperQr.Decode(pixels, width, width));

        static string DecodeObserved(byte[] pixels, int width, int height, string context)
        {
            try { return LxPaperQr.Decode(pixels, width, height); }
            catch (FormatException error) { throw new InvalidOperationException(context, error); }
        }
    }

    [Fact]
    public void RejectsInvalidPixelDimensionsFormatsAndBlankPixels()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LxPaperQr.Decode([], 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LxPaperQr.Decode([], 1, 2049));
        Assert.Throws<ArgumentOutOfRangeException>(() => LxPaperQr.Decode([], int.MaxValue, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => LxPaperQr.Decode([], 1, 1, (LxPaperQrPixelFormat)12));
        Assert.Throws<ArgumentException>(() => LxPaperQr.Decode([0, 0], 1, 1));
        Assert.Throws<ArgumentException>(() => LxPaperQr.Decode([0], 1, 1, LxPaperQrPixelFormat.Rgb24));
        Assert.Throws<FormatException>(() => LxPaperQr.Decode(new byte[100 * 100], 100, 100));
        var symbol = LxPaperQr.Encode(Uri);
        Assert.Throws<ArgumentOutOfRangeException>(() => LxPaperQr.Encode(Uri, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LxPaperQr.Encode(Uri, 17));
        Assert.Throws<ArgumentOutOfRangeException>(() => symbol.ToGrayscale(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => symbol.ToPng(int.MaxValue));
        var maxScale = LxPaperQr.MaxImageDimension / symbol.Size;
        Assert.InRange(symbol.ToGrayscale(maxScale).Length, 1, LxPaperQr.MaxImagePixels);
        Assert.Throws<ArgumentOutOfRangeException>(() => symbol.ToGrayscale(maxScale + 1));
    }

    [Fact]
    public async Task StatelessConcurrentUseAndPreCancellation()
    {
        var uri = Uri;
        var symbol = LxPaperQr.Encode(uri);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => LxPaperQr.Encode(uri, cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => symbol.ToPng(cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => symbol.ToGrayscale(cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => LxPaperQr.Decode([], 1, 1, cancellationToken: cancellation.Token));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            var local = LxPaperQr.Encode(uri);
            Assert.Equal(uri, LxPaperQr.Decode(local.ToGrayscale(2), local.Size * 2, local.Size * 2));
            Assert.Equal(symbol.ToPng(2), local.ToPng(2));
        })));
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xedb88320u * (crc & 1));
        }
        return ~crc;
    }
}
