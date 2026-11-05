using LXMF;
using SharedPrinting;

namespace Sideband;

public sealed record SidebandPrintResult(bool Success, string Detail);

/// <summary>Explicitly started, profile-owned software printing; never prints from inbound callbacks.</summary>
public sealed class SidebandPrintingService : IAsyncDisposable
{
    private readonly SidebandStateStore _store;
    private readonly PrintWorker _worker;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _disposed;
    private Task? _disposal;
    public Action<string>? Log { get => _worker.Log; set => _worker.Log = value; }
    public const int QueueCapacity = PrintWorker.QueueCapacity;

    public SidebandPrintingService(SidebandStateStore store, TimeSpan? timeout = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _worker = new(store.DataDirectory, timeout, "sideband");
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worker.Start();
        }
    }

    /// <summary>Consent names the exact saved message and reviewed command; changed policy refuses the job.</summary>
    public async Task<SidebandPrintResult> PrintPaperAsync(Guid messageId, string confirmedCommand,
        bool confirmed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!confirmed) throw new InvalidOperationException("Explicit consent is required for each paper print.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active == QueueCapacity) throw new InvalidOperationException("Paper print operation capacity reached.");
            _active++;
        }
        try
        {
            using var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            return await PrintCoreAsync(messageId, confirmedCommand, owned.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _active--;
                if (_disposed && _active == 0) _drained.TrySetResult();
            }
        }
    }

    private async Task<SidebandPrintResult> PrintCoreAsync(Guid messageId, string confirmedCommand,
        CancellationToken cancellationToken)
    {
        var state = await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var policy = state.Printing.Validate();
        if (!policy.Enabled) throw new InvalidOperationException("Paper printing is disabled. Review and activate a local command first.");
        if (!string.Equals(confirmedCommand, policy.Command, StringComparison.Ordinal))
            throw new InvalidOperationException("The print command changed. Review it and confirm again.");
        var message = state.Messages.SingleOrDefault(item => item.Id == messageId)
            ?? throw new InvalidOperationException("The selected saved message no longer exists.");
        if (!SidebandPrintingConfiguration.CanPrint(message))
            throw new InvalidOperationException("Only original saved outgoing paper messages can be printed.");
        var bytes = LxPaperQr.Encode(message.PaperUri!, cancellationToken: cancellationToken)
            .ToPng(cancellationToken: cancellationToken);
        var result = await _worker.Queue(policy.Command, messageId.ToString("N"), bytes, ".png",
            cancellationToken).Completion.ConfigureAwait(false);
        return new(result.Success, result.Detail);
    }

    /// <summary>Must run before ordinary application startup so Linux descendants have an independent owner.</summary>
    public static Task<int?> TryRunSupervisorAsync(string[] arguments) =>
        LinuxPrintProcess.TryRunSupervisorAsync(arguments);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            _stop.Cancel();
            if (_active == 0) _drained.TrySetResult();
            _disposal = DisposeCoreAsync();
            return new(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _worker.DisposeAsync().ConfigureAwait(false);
        await _drained.Task.ConfigureAwait(false);
        _stop.Dispose();
    }
}
