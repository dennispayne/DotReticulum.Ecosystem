using DotReticulum.Applications;
using DotReticulum.Core;

namespace Sideband;

public enum SidebandPropagationTelemetryClientState { Disabled, NotConnected, IdentityUnavailable, Active }

public sealed record SidebandPropagationTelemetryClientStatus(
    SidebandPropagationTelemetryClientState State,
    SidebandPropagationTelemetrySnapshot? Snapshot,
    string? Diagnostic);

public sealed partial class SidebandClient
{
    private readonly Func<ReticulumNode, ISidebandPropagationTelemetrySource>? _propagationTelemetrySourceFactory;
    private ReticulumNode? _propagationTelemetryNode;
    private CancellationToken _propagationTelemetryRuntimeToken;
    private SidebandPropagationTelemetry? _propagationTelemetry;
    private Identity? _propagationTelemetryIdentity;
    private string? _propagationTelemetryIdentityPath;
    private bool _propagationTelemetryConfigured;
    private SidebandPropagationTelemetryClientStatus _propagationTelemetryStatus = new(
        SidebandPropagationTelemetryClientState.Disabled, null, null);

    public SidebandPropagationTelemetrySnapshot? PropagationTelemetrySnapshot => PropagationTelemetryStatus.Snapshot;

    /// <summary>Local sanitized diagnostics; successful data remains available after a failed refresh.</summary>
    public SidebandPropagationTelemetryClientStatus PropagationTelemetryStatus
    {
        get
        {
            var status = Volatile.Read(ref _propagationTelemetryStatus);
            if (status.State != SidebandPropagationTelemetryClientState.Active) return status;
            if (!IsConnected)
                return new(SidebandPropagationTelemetryClientState.NotConnected, null, "Connect before collecting propagation telemetry.");
            var snapshot = Volatile.Read(ref _propagationTelemetry)?.Snapshot;
            var diagnostic = snapshot is null || snapshot.LastAttemptUtcSeconds is null ? "Awaiting propagation status." :
                snapshot.Failure != SidebandPropagationTelemetryFailure.None
                    ? "Propagation status query failed: " + snapshot.Failure +
                        (snapshot.LastSuccessUtcSeconds is null ? "." : "; retaining last successful status.")
                    : snapshot.LastSuccessUtcSeconds is null ? "Propagation status query is in progress." : null;
            return status with { Snapshot = snapshot, Diagnostic = diagnostic };
        }
    }

    // Called only under the telemetry refresh gate; the worker never takes that gate.
    private async Task ReconcilePropagationTelemetryAsync(SidebandTelemetryConfiguration config, CancellationToken token)
    {
        _propagationTelemetryConfigured = config.Enabled && config.PropagationEnabled;
        if (!_propagationTelemetryConfigured || _propagationTelemetryNode is null)
        {
            await StopPropagationTelemetryAsync(_propagationTelemetryConfigured
                ? SidebandPropagationTelemetryClientState.NotConnected : SidebandPropagationTelemetryClientState.Disabled).ConfigureAwait(false);
            return;
        }
        if (_propagationTelemetry is not null && _propagationTelemetryIdentityPath == config.PropagationIdentityPath) return;
        var previous = Volatile.Read(ref _propagationTelemetryStatus);
        await StopPropagationTelemetryAsync(SidebandPropagationTelemetryClientState.IdentityUnavailable).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        Identity? identity = null;
        try
        {
            if (config.PropagationIdentityPath is null) throw new InvalidDataException();
            // Hold a read-only sharing lease while IdentityFile checks/imports and zeros the private bytes.
            using (var lease = new FileStream(config.PropagationIdentityPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (lease.Length != Identity.KeySize) throw new InvalidDataException();
                identity = IdentityFile.Load(config.PropagationIdentityPath);
            }
            token.ThrowIfCancellationRequested();
            var source = _propagationTelemetrySourceFactory?.Invoke(_propagationTelemetryNode) ??
                new SidebandPropagationTelemetrySource(_propagationTelemetryNode.Router);
            var provider = new SidebandPropagationTelemetry(identity, source);
            provider.ErrorReported += Report;
            _propagationTelemetryIdentity = identity;
            identity = null;
            _propagationTelemetryIdentityPath = config.PropagationIdentityPath;
            Volatile.Write(ref _propagationTelemetry, provider);
            Volatile.Write(ref _propagationTelemetryStatus, new(SidebandPropagationTelemetryClientState.Active, null, null));
            provider.Start(_propagationTelemetryRuntimeToken);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await StopPropagationTelemetryAsync(SidebandPropagationTelemetryClientState.IdentityUnavailable).ConfigureAwait(false);
            const string diagnostic = "Propagation telemetry could not load/use the explicitly configured identity file.";
            Volatile.Write(ref _propagationTelemetryStatus, new(SidebandPropagationTelemetryClientState.IdentityUnavailable, null, diagnostic));
            if (previous.State != SidebandPropagationTelemetryClientState.IdentityUnavailable) Report(diagnostic);
        }
        finally { identity?.Dispose(); }
    }

    private SidebandTelemetryReading ReadPropagationTelemetry()
    {
        var status = PropagationTelemetryStatus;
        var reading = status.Snapshot?.Reading ?? SidebandTelemetryCodec.FromWireValue(
            (byte)SidebandTelemetrySensorId.LxmfPropagation, SidebandTelemetryValue.Nil);
        return new(reading.Id, reading.Name, reading.Status, reading.WireValue, reading.Data,
            reading.Fields, reading.Entries, reading.Location, status.Diagnostic ?? reading.Error);
    }

    private async Task StopPropagationTelemetryAsync(SidebandPropagationTelemetryClientState state)
    {
        Volatile.Write(ref _propagationTelemetryStatus, new(state, null,
            state == SidebandPropagationTelemetryClientState.NotConnected ? "Connect before collecting propagation telemetry." : null));
        var provider = Interlocked.Exchange(ref _propagationTelemetry, null);
        if (provider is not null)
        {
            _localTelemetry = null;
            await provider.DisposeAsync().ConfigureAwait(false);
            provider.ErrorReported -= Report;
        }
        _propagationTelemetryIdentity?.Dispose();
        _propagationTelemetryIdentity = null;
        _propagationTelemetryIdentityPath = null;
    }
}
