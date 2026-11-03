using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LXST;
using SkiaSharp;

namespace Sideband;

/// <summary>
/// Bounded media preparation for Sideband fields 0x06/0x07, independent of devices and storage.
/// Native codec libraries are loaded only by the operation that needs them.
/// </summary>
public static class SidebandMediaCodec
{
    public const int MaximumEncodedBytes = 2 * 1024 * 1024;
    public const int MaximumImageDimension = 8192;
    public const int MaximumImagePixels = 16 * 1024 * 1024;
    public const int MaximumAudioDurationSeconds = 10;
    public const int OpusOggMode = 0x10;

    public static SidebandEncodedImage PrepareImage(ReadOnlySpan<byte> input,
        SidebandImageQuality quality = SidebandImageQuality.Medium, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var data = ImageData(input);
        using var codec = OpenImage(data);
        var (limit, encoderQuality) = quality switch
        {
            SidebandImageQuality.LowBandwidth => (320, 22),
            SidebandImageQuality.Medium => (640, 66),
            SidebandImageQuality.HighResolution => (1280, 75),
            _ => throw new ArgumentOutOfRangeException(nameof(quality)),
        };
        using var bitmap = DecodeImage(codec);
        cancellationToken.ThrowIfCancellationRequested();
        var (width, height) = ThumbnailSize(bitmap.Width, bitmap.Height, limit);
        using var resized = width == bitmap.Width && height == bitmap.Height ? null :
            bitmap.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if ((width != bitmap.Width || height != bitmap.Height) && resized is null)
            throw new InvalidDataException("Sideband image resizing failed.");
        using var image = SKImage.FromBitmap(resized ?? bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, encoderQuality);
        if (encoded is null || encoded.Size is <= 0 or > MaximumEncodedBytes)
            throw new InvalidDataException("The prepared Sideband WebP exceeds the 2 MiB limit or could not be encoded.");
        cancellationToken.ThrowIfCancellationRequested();
        return new("webp", encoded.ToArray(), new("webp", width, height, checked((int)encoded.Size)));
    }

    /// <summary>Fully decodes once after checking metadata, so malformed payloads are not handed to a UI decoder.</summary>
    public static SidebandEncodedImage ValidateImage(string format, ReadOnlySpan<byte> input,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var data = ImageData(input);
        using var codec = OpenImage(data);
        var actual = ImageFormat(codec.EncodedFormat);
        if (!string.Equals(format, actual, StringComparison.OrdinalIgnoreCase) &&
            !(actual == "jpeg" && string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The declared Sideband image format does not match its header.");
        using var decoded = DecodeImage(codec);
        cancellationToken.ThrowIfCancellationRequested();
        return new(actual, input.ToArray(), new(actual, codec.Info.Width, codec.Info.Height, input.Length));
    }

    public static SidebandEncodedAudio EncodeCodec2(AudioFrame audio, int mode = 0x08,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePcm(audio, 8000);
        var (bitrate, samplesPerFrame, _) = Codec2Parameters(mode);
        if (audio.Samples.Length < samplesPerFrame)
            throw new ArgumentException("Sideband Codec2 encoding requires at least one complete frame.", nameof(audio));
        using var codec = new NativeCodec2(bitrate);
        var framed = codec.Encode(audio);
        cancellationToken.ThrowIfCancellationRequested();
        // LXST's adapter adds its own mode byte. Sideband carries the LXMF mode separately.
        return new(mode, framed.AsSpan(1).ToArray());
    }

    public static AudioFrame DecodeCodec2(ReadOnlySpan<byte> encoded, int mode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEncoded(encoded);
        var (bitrate, samplesPerFrame, bytesPerFrame) = Codec2Parameters(mode);
        var frames = encoded.Length / bytesPerFrame;
        if ((long)frames * samplesPerFrame > 8000 * MaximumAudioDurationSeconds)
            throw new InvalidDataException("Sideband Codec2 audio exceeds the duration limit.");
        // Like actual audioproc.decode_codec2, ignore an incomplete final frame.
        var input = new byte[1 + frames * bytesPerFrame];
        input[0] = checked((byte)(mode - 3));
        encoded[..(frames * bytesPerFrame)].CopyTo(input.AsSpan(1));
        using var codec = new NativeCodec2(bitrate);
        var audio = codec.Decode(input);
        cancellationToken.ThrowIfCancellationRequested();
        return audio;
    }

    /// <summary>
    /// Produces mono 12 kHz, 60 ms AUDIO/automatic-bitrate packets matching actual Sideband PyOgg.
    /// The Ogg container uses standard 48 kHz granules, unlike the reference helper's unscaled
    /// 12 kHz counters, so duration is preserved rather than quartered. No scratch file is needed.
    /// </summary>
    public static Task<SidebandEncodedAudio> EncodeOggOpusAsync(AudioFrame audio, string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        return EncodeOggOpusAsync(audio, cancellationToken);
    }

    public static Task<SidebandEncodedAudio> EncodeOggOpusAsync(AudioFrame audio,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePcm(audio, 12000);
        return Task.Run(() => EncodeOggOpus(audio, cancellationToken), cancellationToken);
    }

    private static SidebandEncodedAudio EncodeOggOpus(AudioFrame audio, CancellationToken cancellationToken)
    {
        using var codec = new NativeOpusCodec(OpusProfile.AudioLow, maximumBytesPerFrame: 4000);
        var delay = codec.AlgorithmicDelaySamples;
        var warmup = new[] { 30, 60, 120, 240, 480, 720 }.First(samples => samples > delay + 120);
        using var output = new MemoryStream();
        Span<byte> serialBytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(serialBytes);
        var serial = BinaryPrimitives.ReadUInt32LittleEndian(serialBytes);
        uint sequence = 0;
        var identification = new byte[19];
        "OpusHead"u8.CopyTo(identification);
        identification[8] = 1;
        identification[9] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(identification.AsSpan(10), checked((ushort)(warmup * 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(identification.AsSpan(12), 12000);
        WriteOggPage(output, identification, serial, sequence++, 0, 0x02);
        var vendor = Encoding.UTF8.GetBytes("DotReticulum Sideband");
        var comments = new byte[16 + vendor.Length];
        "OpusTags"u8.CopyTo(comments);
        BinaryPrimitives.WriteUInt32LittleEndian(comments.AsSpan(8), checked((uint)vendor.Length));
        vendor.CopyTo(comments.AsSpan(12));
        WriteOggPage(output, comments, serial, sequence++, 0, 0);
        var total = warmup + audio.Samples.Length;
        for (var offset = 0; offset < total; offset += 720)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pcm = new float[720];
            var start = Math.Max(offset, warmup);
            var end = Math.Min(offset + pcm.Length, total);
            if (end > start)
            {
                audio.Samples.Span.Slice(start - warmup, end - start).CopyTo(pcm.AsSpan(start - offset));
            }
            var packet = codec.Encode(new(pcm, 12000, 1));
            var last = offset + pcm.Length >= total;
            WriteOggPage(output, packet, serial, sequence++, Math.Min(offset + pcm.Length, total) * 4L,
                last ? (byte)0x04 : (byte)0);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(OpusOggMode, output.ToArray());
    }

    /// <summary>Returns normalized mono PCM at 48 kHz; does not apply the reference UI's peak normalization/resampling.</summary>
    public static async Task<AudioFrame> DecodeOggOpusAsync(ReadOnlyMemory<byte> encoded, string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateEncoded(encoded.Span);
        if (encoded.Length < 4 || !encoded.Span[..4].SequenceEqual("OggS"u8))
            throw new InvalidDataException("Sideband Ogg Opus data has no Ogg capture header.");
        var path = ScratchPath(workingDirectory);
        try
        {
            await using (var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None,
                16_384, FileOptions.Asynchronous))
                await output.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
            await using var source = new OpusFileSource(path, frameDurationMilliseconds: 60);
            if (source.Channels is < 1 or > 2 || source.SamplesPerChannel > 48000 * MaximumAudioDurationSeconds)
                throw new InvalidDataException("Sideband Ogg Opus exceeds the mono/stereo channel or duration limit.");
            var samples = new List<float>(checked((int)source.SamplesPerChannel));
            await foreach (var frame in source.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                if (samples.Count + frame.SamplesPerChannel > 48000 * MaximumAudioDurationSeconds)
                    throw new InvalidDataException("Decoded Sideband Ogg Opus exceeds the duration limit.");
                for (var index = 0; index < frame.Samples.Length; index += frame.Channels)
                    samples.Add(frame.Samples.Span[index]);
            }
            return new(samples.ToArray(), 48000, 1);
        }
        finally { File.Delete(path); }
    }

    public static Task<AudioFrame> DecodeAudioAsync(int mode, ReadOnlyMemory<byte> encoded,
        string workingDirectory, CancellationToken cancellationToken = default) =>
        mode == OpusOggMode ? DecodeOggOpusAsync(encoded, workingDirectory, cancellationToken) :
        Task.FromResult(DecodeCodec2(encoded.Span, mode, cancellationToken));

    private static SKData ImageData(ReadOnlySpan<byte> input)
    {
        ValidateEncoded(input);
        return SKData.CreateCopy(input);
    }

    private static SKCodec OpenImage(SKData data)
    {
        var codec = SKCodec.Create(data);
        if (codec is null) throw new InvalidDataException("The Sideband image header is invalid or unsupported.");
        try
        {
            _ = ImageFormat(codec.EncodedFormat);
            var info = codec.Info;
            if (info.Width is < 1 or > MaximumImageDimension || info.Height is < 1 or > MaximumImageDimension ||
                (long)info.Width * info.Height > MaximumImagePixels)
                throw new InvalidDataException("Sideband image dimensions exceed the bounded decode limit.");
            if (codec.FrameCount > 1)
                throw new InvalidDataException("Animated images are not supported by the Sideband media utility.");
            return codec;
        }
        catch { codec.Dispose(); throw; }
    }

    private static string ImageFormat(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Png => "png",
        SKEncodedImageFormat.Jpeg => "jpeg",
        SKEncodedImageFormat.Webp => "webp",
        _ => throw new InvalidDataException("Sideband media accepts only static PNG, JPEG and WebP images."),
    };

    private static SKBitmap DecodeImage(SKCodec codec)
    {
        var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) == SKCodecResult.Success) return bitmap;
        bitmap.Dispose();
        throw new InvalidDataException("Sideband image pixel data is malformed or incomplete.");
    }

    private static (int Width, int Height) ThumbnailSize(int width, int height, int maximum)
    {
        if (width <= maximum && height <= maximum) return (width, height);
        // Pillow's thumbnail chooses the nearest integer that best preserves the aspect ratio.
        var aspect = (double)width / height;
        if (aspect >= 1)
            return (maximum, AspectRound(maximum / aspect, value => Math.Abs(aspect - (double)maximum / value)));
        return (AspectRound(maximum * aspect, value => Math.Abs(aspect - (double)value / maximum)), maximum);
    }

    private static int AspectRound(double value, Func<int, double> error)
    {
        var lower = Math.Max(1, (int)Math.Floor(value));
        var upper = Math.Max(1, (int)Math.Ceiling(value));
        return error(lower) <= error(upper) ? lower : upper;
    }

    private static (int Bitrate, int SamplesPerFrame, int BytesPerFrame) Codec2Parameters(int mode) => mode switch
    {
        0x03 => (700, 320, 4), 0x04 => (1200, 320, 6), 0x05 => (1300, 320, 7),
        0x06 => (1400, 320, 7), 0x07 => (1600, 320, 8), 0x08 => (2400, 160, 6),
        0x09 => (3200, 160, 8),
        _ => throw new NotSupportedException($"Sideband audio mode 0x{mode:x2} is unsupported; use Ogg Opus or Codec2 700C–3200."),
    };

    private static void ValidatePcm(AudioFrame audio, int rate)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.SampleRate != rate || audio.Channels != 1)
            throw new ArgumentException($"Sideband encoding requires mono PCM at {rate} Hz.", nameof(audio));
        if (audio.Samples.IsEmpty || audio.Samples.Length > rate * MaximumAudioDurationSeconds ||
            audio.Samples.Length > MaximumEncodedBytes / sizeof(float))
            throw new ArgumentException("Sideband PCM is empty or exceeds the bounded size/duration limit.", nameof(audio));
        foreach (var sample in audio.Samples.Span)
            if (!float.IsFinite(sample) || sample is < -1 or > 1)
                throw new ArgumentException("Sideband PCM samples must be finite and normalized to [-1, 1].", nameof(audio));
    }

    private static void ValidateEncoded(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty || encoded.Length > MaximumEncodedBytes)
            throw new InvalidDataException("Sideband media must contain between 1 byte and 2 MiB.");
    }

    private static string ScratchPath(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var directory = Path.GetFullPath(workingDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"sideband-media-{Guid.NewGuid():N}.ogg");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var reserved = new FileStream(path, options);
        return path;
    }

    // Ogg framing follows Xiph's framing specification; Opus timing/header fields follow RFC 7845.
    // One bounded packet per page avoids buffering, continuation and multiplexing machinery.
    private static void WriteOggPage(Stream output, ReadOnlySpan<byte> packet, uint serial, uint sequence,
        long granule, byte flags)
    {
        var segments = packet.Length / 255 + 1;
        var page = new byte[27 + segments + packet.Length];
        "OggS"u8.CopyTo(page);
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = checked((byte)segments);
        page.AsSpan(27, segments - 1).Fill(255);
        page[26 + segments] = checked((byte)(packet.Length % 255));
        packet.CopyTo(page.AsSpan(27 + segments));
        uint checksum = 0;
        foreach (var value in page)
        {
            checksum ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
                checksum = (checksum << 1) ^ ((checksum & 0x80000000) == 0 ? 0u : 0x04c11db7u);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), checksum);
        if (output.Length + page.Length > MaximumEncodedBytes)
            throw new InvalidDataException("Sideband Ogg Opus exceeds the 2 MiB output limit.");
        output.Write(page);
    }
}
