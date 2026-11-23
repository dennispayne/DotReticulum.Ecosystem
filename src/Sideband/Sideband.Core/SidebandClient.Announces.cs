using DotReticulum.Core;

namespace Sideband;

public sealed partial class SidebandClient
{
    public async Task AnnounceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var cancellation = RuntimeOperationCancellation(cancellationToken);
        await _lifecycle.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            var router = _router ?? throw new InvalidOperationException("Connect to Reticulum before announcing.");
            await router.AnnounceAsync(cancellation.Token).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<IReadOnlyList<SidebandAnnounce>> GetAnnouncesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var state = await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var router = _router;
        return (state.Announces ?? []).Select(item => router is not null && item.DestinationType == SidebandAnnounce.Delivery
            ? item with { StampCost = router.GetOutboundStampCost(Convert.FromHexString(item.DestinationHash)) }
            : item).ToArray();
    }

    public async Task DeleteAnnounceAsync(string destinationHash, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.DeleteAnnounceAsync(destinationHash, cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    private async Task ProcessAnnounceAsync(SidebandAnnounce announce, byte[]? packetHash)
    {
        var node = _node;
        using var cancellation = RuntimeOperationCancellation(CancellationToken.None);
        if (node is not null && packetHash is not null)
        {
            try
            {
                async Task<double?> QueryAsync(string path)
                {
                    var value = await node.CallManagementAsync(new()
                    {
                        ["get"] = path, ["packet_hash"] = packetHash
                    }, cancellation.Token).ConfigureAwait(false);
                    return value is null ? null : MsgPack.ToDouble(value);
                }
                var signal = new SidebandAnnounceSignal(await QueryAsync("packet_rssi").ConfigureAwait(false),
                    await QueryAsync("packet_snr").ConfigureAwait(false), await QueryAsync("packet_q").ConfigureAwait(false));
                var measured = announce with { Signal = signal };
                SidebandAnnounce.Validate(measured);
                announce = measured;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { Report("Announce signal lookup cancelled during disconnect; the announce is retained without measurements."); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or
                FormatException or ArgumentException or OverflowException or TimeoutException or System.Net.Sockets.SocketException)
            { Report("Announce signal lookup failed; the announce is retained without measurements: " + ex.Message); }
        }
        await _store.ObserveAnnounceAsync(announce).ConfigureAwait(false);
    }
}
