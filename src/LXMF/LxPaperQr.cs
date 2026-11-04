using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;
using FormatException = System.FormatException;

namespace LXMF;

/// <summary>
/// Optional, stateless software QR support. QR/envelope validity does not authenticate
/// a signature, prove decryptability, or establish successful delivery.
/// </summary>
public static class LxPaperQr
{
    public const int MaxUriUtf8Bytes = 2954;
    public const int MaxSymbolModules = 177;
    public const int MaxImageDimension = 2048;
    public const int MaxImagePixels = MaxImageDimension * MaxImageDimension;
    public const int DefaultQuietZone = 1;
    public const int DefaultScale = 10;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>
    /// Encodes an existing paper envelope with correction L, matching LXMessage.as_qr.
    /// The immutable matrix includes the quiet zone (upstream default one module).
    /// Use four or more quiet modules when the surrounding UI is not white.
    /// </summary>
    public static LxPaperQrSymbol Encode(string uri, int quietZone = DefaultQuietZone,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateUri(uri);
        if (quietZone is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(quietZone));
        try
        {
            var matrix = new QRCodeWriter().encode(uri, BarcodeFormat.QR_CODE, 0, 0,
                new Dictionary<EncodeHintType, object>
                {
                    [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.L,
                    [EncodeHintType.CHARACTER_SET] = "UTF-8",
                    [EncodeHintType.DISABLE_ECI] = true,
                    [EncodeHintType.MARGIN] = quietZone,
                });
            cancellationToken.ThrowIfCancellationRequested();
            var size = matrix.Width;
            if (size != matrix.Height || size - quietZone * 2 > MaxSymbolModules)
                throw new FormatException("Paper QR exceeds the maximum symbol size.");
            var modules = new byte[size * size];
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                    modules[y * size + x] = matrix[x, y] ? (byte)1 : (byte)0;
            return new LxPaperQrSymbol(modules, size, quietZone);
        }
        catch (WriterException error)
        {
            throw new FormatException("Paper envelope does not fit a QR symbol at correction L.", error);
        }
    }

    /// <summary>
    /// Reads one QR from tightly packed caller-decoded Gray8 or RGB24 pixels.
    /// Each dimension is at most 2048; input is at most 4 Mi pixels / 12 MiB RGB.
    /// Throws FormatException for unreadable, foreign or malformed envelopes.
    /// At most four orientations each try detection and pure-symbol extraction;
    /// every luminance raster is bounded by MaxImagePixels.
    /// Cancellation is checked before/after the bounded synchronous QR operation,
    /// not within ZXing's detector. No PNG/image parser or device access is performed.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> pixels, int width, int height,
        LxPaperQrPixelFormat format = LxPaperQrPixelFormat.Gray8,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width is < 1 or > MaxImageDimension) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > MaxImageDimension) throw new ArgumentOutOfRangeException(nameof(height));
        var channels = format switch
        {
            LxPaperQrPixelFormat.Gray8 => 1,
            LxPaperQrPixelFormat.Rgb24 => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        if (pixels.Length != width * height * channels)
            throw new ArgumentException("Pixel buffer must exactly match tightly packed dimensions and format.", nameof(pixels));
        LuminanceSource source = new RGBLuminanceSource(pixels.ToArray(), width, height,
            channels == 1 ? RGBLuminanceSource.BitmapFormat.Gray8 : RGBLuminanceSource.BitmapFormat.RGB24);
        var reader = new QRCodeReader();
        var hints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.CHARACTER_SET] = "UTF-8",
        };
        var pureHints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.PURE_BARCODE] = true,
            [DecodeHintType.CHARACTER_SET] = "UTF-8",
        };
        Result? result = null;
        for (var orientation = 0; orientation < 4 && result is null; orientation++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = new BinaryBitmap(new HybridBinarizer(source));
            result = reader.decode(bitmap, hints);
            if (result is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = reader.decode(bitmap, pureHints);
            }
            if (result is null && orientation < 3) source = source.rotateCounterClockwise();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (result?.Text is not { } uri) throw new FormatException("No readable paper QR symbol was found.");
        ValidateUri(uri);
        return uri;
    }

    private static void ValidateUri(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Length > MaxUriUtf8Bytes || StrictUtf8.GetByteCount(uri) > MaxUriUtf8Bytes)
            throw new FormatException("Paper QR URI exceeds the bounded UTF-8 capacity.");
        _ = LxMessage.GetPaperDestinationHash(uri);
    }
}

public enum LxPaperQrPixelFormat
{
    Gray8,
    Rgb24,
}

/// <summary>An immutable square dark-module matrix including its white quiet zone.</summary>
public sealed class LxPaperQrSymbol
{
    private readonly byte[] _modules;
    public int Size { get; }
    public int QuietZone { get; }
    public int Version => (Size - QuietZone * 2 - 17) / 4;

    internal LxPaperQrSymbol(byte[] modules, int size, int quietZone)
    {
        _modules = modules;
        Size = size;
        QuietZone = quietZone;
    }

    public bool this[int x, int y]
    {
        get
        {
            if ((uint)x >= (uint)Size) throw new ArgumentOutOfRangeException(nameof(x));
            if ((uint)y >= (uint)Size) throw new ArgumentOutOfRangeException(nameof(y));
            return _modules[y * Size + x] != 0;
        }
    }

    /// <summary>Returns an owned tightly packed grayscale buffer; black=0, white=255.</summary>
    public byte[] ToGrayscale(int scale = LxPaperQr.DefaultScale, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var width = GetWidth(scale);
        var pixels = new byte[width * width];
        for (var y = 0; y < width; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = _modules[y / scale * Size + x / scale] == 0 ? (byte)255 : (byte)0;
        }
        return pixels;
    }

    /// <summary>
    /// Returns original PNG-standard noninterlaced 8-bit grayscale output (IHDR/IDAT/IEND).
    /// Maximum raster allocation is 4 MiB; compression uses one scanline plus a bounded
    /// output stream (at most raster bytes + 64 KiB). No image-library/native dependency.
    /// </summary>
    public byte[] ToPng(int scale = LxPaperQr.DefaultScale, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var width = GetWidth(scale);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[width + 1];
            for (var y = 0; y < width; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                    row[x + 1] = _modules[y / scale * Size + x / scale] == 0 ? (byte)255 : (byte)0;
                zlib.Write(row);
            }
        }
        if (compressed.Length > (long)width * width + 65536)
            throw new InvalidOperationException("PNG compressed data exceeds its allocation bound.");
        cancellationToken.ThrowIfCancellationRequested();
        using var png = new MemoryStream(checked((int)compressed.Length + 57));
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)width);
        header[8] = 8;
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private int GetWidth(int scale)
    {
        if (scale < 1 || scale > LxPaperQr.MaxImageDimension / Size)
            throw new ArgumentOutOfRangeException(nameof(scale));
        return Size * scale;
    }

    // W3C PNG 3 §§5.3/5.5/10/11: network-order length, type+data CRC, zlib scanlines.
    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(integer, (uint)data.Length);
        output.Write(integer);
        output.Write(type);
        output.Write(data);
        var crc = uint.MaxValue;
        foreach (var value in type) UpdateCrc(ref crc, value);
        foreach (var value in data) UpdateCrc(ref crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
        output.Write(integer);
    }

    private static void UpdateCrc(ref uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
    }
}
