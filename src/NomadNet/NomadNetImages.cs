using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace NomadNet;

/// <summary>Bounded local FFmpeg adapter; no codec implementation or Python runtime is embedded.</summary>
public sealed class NomadNetImages(string executable = "ffmpeg", string? workDirectory = null)
{
    public const int DefaultQuality = 85;
    public const int DefaultMaximumDimension = 1200;

    public async Task<byte[]> ConvertAsync(byte[] source, int quality = DefaultQuality,
        int maximumDimension = DefaultMaximumDimension, CancellationToken cancellationToken = default)
    {
        if (source.Length is 0 or > NomadNetHost.MaximumFileBytes || quality is < 1 or > 100 || maximumDimension is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(source), "Invalid image size, quality or dimension bound.");
        string? staged = null;
        byte[] result;
        try
        {
            if (source.AsSpan().StartsWith("II*\0"u8) || source.AsSpan().StartsWith("MM\0*"u8))
            {
                var directory = Path.GetFullPath(workDirectory ?? Path.Combine("artifacts", "builds", "nomadnet-image-work"));
                Directory.CreateDirectory(directory);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Image work directory cannot be a link.");
                staged = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tiff");
                await File.WriteAllBytesAsync(staged, source, cancellationToken).ConfigureAwait(false);
            }
            result = await RunAsync(source, ["-quality", quality.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-vf", $"scale='min(iw,{maximumDimension})':'min(ih,{maximumDimension})':force_original_aspect_ratio=decrease",
                "-f", "webp", "pipe:1"], NomadNetHost.MaximumFileBytes, cancellationToken, staged).ConfigureAwait(false);
        }
        finally { if (staged is not null && File.Exists(staged)) File.Delete(staged); }
        var (width, height) = Dimensions(result);
        if (width > maximumDimension || height > maximumDimension) throw new InvalidDataException("Image converter exceeded requested dimensions.");
        return result;
    }

    public async Task<string> PreviewAsync(byte[] webp, int columns = 80, bool color = false, CancellationToken cancellationToken = default)
    {
        if (columns is < 8 or > 160) throw new ArgumentOutOfRangeException(nameof(columns));
        var (width, height) = Dimensions(webp);
        var scale = Math.Min(1d, Math.Min(columns / (double)width, 120d / height));
        var outputWidth = Math.Max(1, (int)Math.Round(width * scale));
        var outputHeight = Math.Max(1, (int)Math.Round(height * scale / (color ? 1 : 2)));
        var expected = outputWidth * outputHeight * 3;
        var pixels = await RunAsync(webp, ["-vf", $"scale={outputWidth}:{outputHeight}", "-frames:v", "1",
            "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"], expected, cancellationToken).ConfigureAwait(false);
        if (pixels.Length != expected) throw new InvalidDataException("Image converter returned an incomplete preview.");
        const string shades = " .:-=+*#%@";
        var text = new StringBuilder((outputWidth + 1) * outputHeight);
        for (var row = 0; row < outputHeight; row += color ? 2 : 1)
        {
            for (var column = 0; column < outputWidth; column++)
            {
                var index = (row * outputWidth + column) * 3;
                if (color)
                {
                    var below = (Math.Min(row + 1, outputHeight - 1) * outputWidth + column) * 3;
                    text.Append($"\u001b[38;2;{pixels[index]};{pixels[index + 1]};{pixels[index + 2]}m");
                    text.Append($"\u001b[48;2;{pixels[below]};{pixels[below + 1]};{pixels[below + 2]}m▀");
                }
                else
                {
                    var brightness = (pixels[index] * 299 + pixels[index + 1] * 587 + pixels[index + 2] * 114) / 1000;
                    text.Append(shades[brightness * (shades.Length - 1) / 255]);
                }
            }
            if (color) text.Append("\u001b[0m");
            if (row + (color ? 2 : 1) < outputHeight) text.Append('\n');
        }
        return text.ToString();
    }

    public static (int Width, int Height) Dimensions(ReadOnlySpan<byte> webp)
    {
        if (webp.Length is < 30 or > NomadNetHost.MaximumFileBytes || !webp[..4].SequenceEqual("RIFF"u8) ||
            !webp.Slice(8, 4).SequenceEqual("WEBP"u8)) throw new InvalidDataException("Invalid bounded WebP image.");
        var fourcc = webp.Slice(12, 4);
        int width, height;
        if (fourcc.SequenceEqual("VP8X"u8))
        {
            width = (webp[24] | webp[25] << 8 | webp[26] << 16) + 1;
            height = (webp[27] | webp[28] << 8 | webp[29] << 16) + 1;
        }
        else if (fourcc.SequenceEqual("VP8 "u8))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(webp[26..]) & 0x3fff;
            height = BinaryPrimitives.ReadUInt16LittleEndian(webp[28..]) & 0x3fff;
        }
        else if (fourcc.SequenceEqual("VP8L"u8))
        {
            var packed = BinaryPrimitives.ReadUInt32LittleEndian(webp[21..]);
            width = (int)(packed & 0x3fff) + 1;
            height = (int)((packed >> 14) & 0x3fff) + 1;
        }
        else throw new InvalidDataException("Unsupported WebP image header.");
        if (width < 1 || height < 1 || (long)width * height > 16_000_000)
            throw new InvalidDataException("Decoded image exceeds the sixteen-megapixel safety bound.");
        return (width, height);
    }

    private async Task<byte[]> RunAsync(byte[] source, string[] arguments, int outputLimit, CancellationToken cancellationToken,
        string? inputFile = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            },
        };
        var inputArguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-max_alloc", "67108864" };
        inputArguments.AddRange(["-i", inputFile ?? "pipe:0"]);
        foreach (var argument in inputArguments.Concat(arguments))
            process.StartInfo.ArgumentList.Add(argument);
        try { process.Start(); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        { throw new IOException("Image conversion requires the configured local FFmpeg executable.", exception); }
        var output = ReadAsync(process.StandardOutput.BaseStream, outputLimit, lifetime.Token);
        var errors = ReadAsync(process.StandardError.BaseStream, 16384, lifetime.Token);
        var input = WriteAsync();
        var exited = process.WaitForExitAsync(lifetime.Token);
        var tasks = new List<Task> { output, errors, input, exited };
        try
        {
            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                tasks.Remove(completed);
            }
            if (process.ExitCode != 0) throw new InvalidDataException("Local image conversion failed: " +
                NomadNetTerminalText.Safe(Encoding.UTF8.GetString(await errors.ConfigureAwait(false))));
            return await output.ConfigureAwait(false);
        }
        catch
        {
            var timedOut = lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            lifetime.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            try { await Task.WhenAll(input, output, errors, exited).ConfigureAwait(false); }
            catch { /* Preserve the original conversion/cancellation error after joining all I/O. */ }
            if (!process.HasExited) await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (timedOut)
                throw new IOException("Local image conversion exceeded its eight-second or output safety bound.");
            throw;
        }

        async Task WriteAsync()
        {
            try
            {
                if (inputFile is null) await process.StandardInput.BaseStream.WriteAsync(source, lifetime.Token).ConfigureAwait(false);
            }
            finally { process.StandardInput.Close(); }
        }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (read > maximumBytes - result.Length) throw new InvalidDataException("Image converter output exceeds its safety bound.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
}
