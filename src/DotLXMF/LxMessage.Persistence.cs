using DotReticulum.Core;

namespace DotLXMF;

public sealed partial class LxMessage
{
    private readonly SemaphoreSlim _persist = new(1, 1);

    public async Task<string> WriteToDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        await _persist.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? output = null;
        try
        {
            var container = PackedContainer();
            var path = Path.Combine(directoryPath, Convert.ToHexStringLower(Hash!));
            output = path + "." + Guid.NewGuid().ToString("N") + ".out";
            await using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(container, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(output, path, overwrite: true);
            return path;
        }
        finally
        {
            try { if (output is not null && File.Exists(output)) File.Delete(output); }
            finally { _persist.Release(); }
        }
    }

    public static async Task<LxMessage> UnpackFromFileAsync(string path, Func<byte[], Identity?> recallSource,
        int maximumBytes = 16 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(recallSource);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            4096, FileOptions.Asynchronous);
        if (stream.Length > maximumBytes) throw new InvalidDataException("LXMF message container exceeds the configured bound.");
        var container = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(container, cancellationToken).ConfigureAwait(false);
        return UnpackContainer(container, recallSource);
    }
}
