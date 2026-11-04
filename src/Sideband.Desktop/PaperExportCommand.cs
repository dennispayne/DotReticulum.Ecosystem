using LXMF;
using Sideband;

namespace Sideband.Desktop;

public static class PaperExportCommand
{
    public static async Task<int> RunAsync(string dataDirectory, Guid messageId, string path, bool qr,
        TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var store = new SidebandStateStore(dataDirectory);
        if (!File.Exists(store.StatePath)) throw new FileNotFoundException("No existing Sideband state was found.");
        await using var client = new SidebandClient(store);
        await ExportAsync(client, messageId, path, qr, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(qr ? "Paper QR PNG exported." : "Paper URI exported.").ConfigureAwait(false);
        return 0;
    }

    internal static async Task ExportAsync(SidebandClient client, Guid messageId, string path, bool qr,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!qr)
        {
            await client.ExportPaperMessageAsync(messageId, path, cancellationToken).ConfigureAwait(false);
            return;
        }
        var uri = await client.GetPaperMessageUriAsync(messageId, cancellationToken).ConfigureAwait(false);
        var png = LxPaperQr.Encode(uri, cancellationToken: cancellationToken).ToPng(cancellationToken: cancellationToken);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous);
        try
        {
            await stream.WriteAsync(png, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            File.Delete(path);
            throw;
        }
    }
}
