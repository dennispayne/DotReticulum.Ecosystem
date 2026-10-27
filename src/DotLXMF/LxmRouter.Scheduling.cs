namespace DotLXMF;

public sealed partial class LxmRouter
{
    private readonly Dictionary<string, double> _pathRequestTimes = [];
    private readonly Dictionary<string, (bool Slow, double Cached)> _slowPaths = [];

    public double MediumPathTimeout => _router.GetMediumPathTimeout();
    public double PathRequestWait => Math.Max(_options.PathRequestWait.TotalSeconds, MediumPathTimeout);
    public bool SlowInterfaceOnline => _router.GetLowestInterfaceBitrate() is { } bitrate && bitrate < 2000;

    public double DestinationRoundTrip(ReadOnlySpan<byte> destination) => !_router.HasPath(destination)
        ? MediumPathTimeout : 2 * Math.Max(_router.FirstHopTimeout(destination) - 6, 0) + 6;

    public bool SlowPath(ReadOnlySpan<byte> destination)
    {
        var key = Convert.ToHexStringLower(destination);
        lock (_gate)
        {
            if (_slowPaths.TryGetValue(key, out var cached) && Now - cached.Cached < _options.ProcessingInterval.TotalSeconds)
                return cached.Slow;
            if (_slowPaths.Count > 256) _slowPaths.Clear();
            var slow = DestinationRoundTrip(destination) > _options.DeliveryRetryWait.TotalSeconds;
            _slowPaths[key] = (slow, Now);
            return slow;
        }
    }

    public bool PathRequestPending(ReadOnlySpan<byte> destination)
    {
        lock (_gate) return _pathRequestTimes.TryGetValue(Convert.ToHexStringLower(destination), out var requested) &&
            Now - requested < PathRequestWait;
    }

    public async Task<bool> RequestPathAsync(byte[] destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length != 16) throw new ArgumentException("Destination hash must be 16 bytes.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        var key = Convert.ToHexStringLower(destination);
        var window = SlowInterfaceOnline ? 60 : PathRequestWait;
        lock (_gate)
        {
            var now = Now;
            if (_pathRequestTimes.TryGetValue(key, out var requested) && now - requested < window) return false;
            foreach (var stale in _pathRequestTimes.Where(entry => now - entry.Value >= window).Select(entry => entry.Key).ToArray())
                _pathRequestTimes.Remove(stale);
            if (_pathRequestTimes.Count >= 65_536) throw new InvalidOperationException("LXMF path-request capacity reached.");
            _pathRequestTimes[key] = now;
        }
        await _router.RequestPathAsync(destination, cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }

    private byte[] OutboundTarget(LxMessage message) =>
        message.Method == LxmMethod.Propagated ? message.PropagationNodeHash ?? message.DestinationHash : message.DestinationHash;

    private void ScheduleAttempt(LxMessage message, byte[] destination, bool pathRequest = false)
    {
        var roundTrip = pathRequest ? MediumPathTimeout : DestinationRoundTrip(destination);
        var schedule = LxmDeliveryTiming.Schedule(Now, roundTrip, pathRequest, message.DeliveryAttempts,
            Random.Shared.NextDouble(), _options.MaxDeliveryAttempts, _options.DeliveryRetryWait.TotalSeconds,
            _options.PathRequestWait.TotalSeconds, _options.ProcessingInterval.TotalSeconds);
        message.NextAttempt = schedule.NextAttempt;
        message.MaxDeliveryAttempts = Math.Min(_options.MaxDeliveryAttempts, schedule.AttemptLimit);
        message.ReceiptTimeout = schedule.ReceiptTimeout is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
        message.AwaitingPath = pathRequest;
    }

    private void OutboundConcluded(LxMessage message)
    {
        if (message.State is not (LxmState.Delivered or LxmState.Failed or
            LxmState.Cancelled or LxmState.Rejected) && !(message.Method == LxmMethod.Propagated && message.State == LxmState.Sent))
            return;
        message.StateChanged -= OutboundConcluded;
        if (message.SentOnSlowPath && Volatile.Read(ref _disposed) == 0) _wake.Release();
    }
}
