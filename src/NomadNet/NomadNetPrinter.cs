using System.Text;
using SharedPrinting;

namespace NomadNet;

public sealed record NomadNetPrintResult(bool Success, string Detail);

/// <summary>NomadNet presentation and policy over the shared bounded printing worker.</summary>
public sealed class NomadNetPrinter : IAsyncDisposable
{
    public const int QueueCapacity = PrintWorker.QueueCapacity;
    public const int MaximumProcessOutputBytes = PrintWorker.MaximumProcessOutputBytes;
    private readonly PrintWorker _worker;
    public Action<string>? Log { get => _worker.Log; set => _worker.Log = value; }

    public NomadNetPrinter(string profile, TimeSpan? timeout = null) => _worker = new(profile, timeout);
    public void Start() => _worker.Start();

    public bool TryPrintIncoming(NomadNetSettings settings, NomadNetMessage message, NomadNetContact? contact,
        DateTimeOffset received)
    {
        if (!NomadNetPrintingPolicy.ShouldPrint(settings, message.PeerHash, contact?.TrustLevel ?? 2)) return false;
        try { return QueueMessage(settings, message, contact, received, CancellationToken.None).Accepted; }
        catch (Exception exception)
        {
            // Use the worker's observer isolation for preparation errors as well as running jobs.
            _worker.ReportPreparationFailure(message.Id, exception);
            return false;
        }
    }

    public async Task<NomadNetPrintResult> PrintMessageAsync(NomadNetSettings settings, NomadNetMessage message,
        NomadNetContact? contact, DateTimeOffset received, CancellationToken cancellationToken = default)
    {
        var result = await QueueMessage(settings, message, contact, received, cancellationToken).Completion.ConfigureAwait(false);
        return new(result.Success, result.Detail);
    }

    public async Task<NomadNetPrintResult> PrintPaperAsync(NomadNetSettings settings, string id, string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _worker.Queue(settings.PrintCommand, id, NomadNetPaperQr.Png(uri, cancellationToken),
            ".png", cancellationToken).Completion.ConfigureAwait(false);
        return new(result.Success, result.Detail);
    }

    private (bool Accepted, Task<PrintResult> Completion) QueueMessage(NomadNetSettings settings,
        NomadNetMessage message, NomadNetContact? contact, DateTimeOffset received, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = NomadNetPrintingPolicy.Render(settings.PrintingMessageTemplate, message,
            NomadNetPrintingPolicy.Origin(message.PeerHash, contact), received);
        return _worker.Queue(settings.PrintCommand, message.Id, new UTF8Encoding(false, true).GetBytes(text),
            ".txt", cancellationToken);
    }

    public ValueTask DisposeAsync() => _worker.DisposeAsync();
}
