using DotReticulum.Core;
using DotReticulum.Transport;

namespace LXMF;

/// <summary>Native counterpart of LXMF 1.2.1 lxmd.query_status. Router and identities remain caller-owned.</summary>
public sealed class LxmPropagationStatusClient
{
    public const long ErrorNoIdentity = 0xf0;
    public const long ErrorNoAccess = 0xf1;
    public const long ErrorTimeout = 0xfe;
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(5);
    private readonly TransportRouter _router;

    public LxmPropagationStatusClient(TransportRouter router) =>
        _router = router ?? throw new ArgumentNullException(nameof(router));

    /// <summary>Returns the upstream map, nil or error integer. Cancellation is distinct from a query timeout.</summary>
    public async Task<object?> QueryAsync(Identity identity, Identity? remoteIdentity = null,
        TimeSpan? timeout = null, int maxResponseBytes = 262_144, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.HasPrivateKey) throw new ArgumentException("Status queries require an explicit signing identity.", nameof(identity));
        var duration = timeout ?? DefaultTimeout;
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxResponseBytes is < 1 or > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        Link? link = null;
        try
        {
            var destination = Destination.ComputeHash((remoteIdentity ?? identity).Hash, "lxmf", ["propagation", "control"]);
            link = await LxmPropagationTransport.ConnectAsync(_router, destination, duration, deadline.Token).ConfigureAwait(false);
            link.Identify(identity);
            return await LxmPropagationTransport.RequestAsync(link, "/pn/get/stats", null, duration,
                maxResponseBytes, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ErrorTimeout; }
        catch (TimeoutException) { return ErrorTimeout; }
        finally { link?.Teardown(); }
    }
}
