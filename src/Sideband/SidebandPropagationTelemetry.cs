using System.Diagnostics;
using DotReticulum.Core;
using LXMF;

namespace Sideband;

public enum SidebandPropagationTelemetryFailure { None, EmptyResponse, NoIdentity, AccessDenied, Timeout, InvalidResponse, SourceFailed }

public sealed record SidebandPropagationTelemetrySnapshot(
    SidebandTelemetryReading Reading,
    double? LastAttemptUtcSeconds,
    double? LastSuccessUtcSeconds,
    SidebandPropagationTelemetryFailure Failure);

/// <summary>
/// Explicit opt-in status collection. The caller retains the source, runtime and identity and must keep them alive
/// until disposal completes. No secret is exported, copied, persisted or disposed by this provider.
/// </summary>
public sealed class SidebandPropagationTelemetry : IAsyncDisposable
{
    public static TimeSpan StaleInterval { get; } = TimeSpan.FromSeconds(300);
    public static TimeSpan WakeInterval { get; } = TimeSpan.FromSeconds(18);
    private readonly Identity _identity;
    private readonly ISidebandPropagationTelemetrySource _source;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private Task<SidebandPropagationTelemetrySnapshot>? _query;
    private Task? _disposal;
    private bool _disposed;
    private SidebandPropagationTelemetrySnapshot _snapshot = new(
        SidebandTelemetryCodec.FromWireValue((byte)SidebandTelemetrySensorId.LxmfPropagation, SidebandTelemetryValue.Nil),
        null, null, SidebandPropagationTelemetryFailure.None);

    public SidebandPropagationTelemetry(Identity identity, ISidebandPropagationTelemetrySource source, TimeProvider? timeProvider = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (!identity.HasPrivateKey) throw new ArgumentException("Supply an explicit signing identity.", nameof(identity));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Sanitized diagnostics only; one failing observer cannot disable collection or another observer.</summary>
    public event Action<string>? ErrorReported;

    public SidebandPropagationTelemetrySnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    /// <summary>Starts one immediate query and an eighteen-second wake loop. Repeated starts are idempotent.</summary>
    public void Start(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _worker ??= Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
        }
    }

    /// <summary>Throttled single-flight collection; failures also consume the five-minute stale interval and retain last success.</summary>
    public Task<SidebandPropagationTelemetrySnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_query is { IsCompleted: false }) return _query.WaitAsync(cancellationToken);
            var now = _clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000d;
            if (_snapshot.LastAttemptUtcSeconds is { } last && now - last < StaleInterval.TotalSeconds)
                return Task.FromResult(_snapshot);
            _snapshot = _snapshot with { LastAttemptUtcSeconds = now };
            // Schedule outside the lifecycle lock so even a synchronously completing source cannot invoke observers under it.
            _query = Task.Run(() => QueryAsync(cancellationToken), CancellationToken.None);
            return _query;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        try
        {
            while (true)
            {
                try { await CollectAsync(lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    // A concurrent manual refresh can own/cancel the shared attempt, not this worker's lifetime.
                }
                await Task.Delay(WakeInterval, _clock, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task<SidebandPropagationTelemetrySnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        deadline.CancelAfter(LxmPropagationStatusClient.DefaultTimeout);
        SidebandTelemetryReading? reading = null;
        SidebandPropagationTelemetryFailure failure;
        try
        {
            var response = await _source.QueryAsync(_identity, deadline.Token).ConfigureAwait(false);
            failure = response switch
            {
                { IsNil: true } => SidebandPropagationTelemetryFailure.EmptyResponse,
                { Integer: LxmPropagationStatusClient.ErrorNoIdentity } => SidebandPropagationTelemetryFailure.NoIdentity,
                { Integer: LxmPropagationStatusClient.ErrorNoAccess } => SidebandPropagationTelemetryFailure.AccessDenied,
                { Integer: LxmPropagationStatusClient.ErrorTimeout } => SidebandPropagationTelemetryFailure.Timeout,
                _ => SidebandPropagationTelemetryFailure.None,
            };
            if (failure == SidebandPropagationTelemetryFailure.None)
            {
                reading = SidebandTelemetryCodec.FromWireValue((byte)SidebandTelemetrySensorId.LxmfPropagation, response);
                if (reading.Status is not SidebandTelemetryStatus.Available)
                {
                    reading = null;
                    failure = SidebandPropagationTelemetryFailure.InvalidResponse;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested || cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { failure = SidebandPropagationTelemetryFailure.Timeout; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = SidebandPropagationTelemetryFailure.SourceFailed;
        }
        SidebandPropagationTelemetrySnapshot result;
        lock (_gate)
        {
            if (_disposed) return _snapshot;
            _snapshot = reading is null ? _snapshot with { Failure = failure } :
                _snapshot with { Reading = reading, LastSuccessUtcSeconds = _clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000d,
                    Failure = SidebandPropagationTelemetryFailure.None };
            result = _snapshot;
        }
        if (failure != SidebandPropagationTelemetryFailure.None) Report(failure);
        return result;
    }

    private void Report(SidebandPropagationTelemetryFailure failure)
    {
        var message = "LXMF propagation telemetry status query failed: " + failure + ".";
        if (ErrorReported is not { } callbacks) { Trace.TraceError("{0}", message); return; }
        foreach (Action<string> observer in callbacks.GetInvocationList())
        {
            try { observer(message); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { Trace.TraceError("LXMF propagation telemetry error observer failed ({0}).", error.GetType().Name); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            return new(_disposal = FinishDisposalAsync());
        }
    }

    private async Task FinishDisposalAsync()
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            if (_worker is { } worker) await worker.ConfigureAwait(false);
            if (_query is { } query) await query.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
                _snapshot = new(SidebandTelemetryCodec.FromWireValue((byte)SidebandTelemetrySensorId.LxmfPropagation,
                    SidebandTelemetryValue.Nil), null, null, SidebandPropagationTelemetryFailure.None);
            _stop.Dispose();
        }
    }
}
