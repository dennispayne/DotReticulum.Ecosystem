using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotLXMF;

public sealed partial class LxmRouter
{
    private readonly HashSet<Link> _propagationLinks = [];
    private readonly Dictionary<Link, LxMessage> _propagationMessages = [];
    private double _lastLinkCleanup;
    private double _lastResourceCleanup;

    private void ConfigurePropagationSignals(Link link)
    {
        link.PacketReceived = (data, _) =>
        {
            try
            {
                if (MsgPack.UnpackApplicationValue(data) is not object?[] { Length: > 0 } values ||
                    MsgPack.ToInt64(values[0]) != 0xf5) return;
                LxMessage? message;
                lock (_gate)
                {
                    if (_disposed != 0 || !_propagationMessages.TryGetValue(link, out message) ||
                        message.State is LxmState.Delivered or LxmState.Cancelled or LxmState.Failed) return;
                    message.State = LxmState.Rejected;
                }
                message.ActiveResource?.Cancel();
                _wake.Release();
            }
            catch (Exception ex) { Report("Could not process propagation transfer signalling: " + ex.Message); }
        };
        link.Closed += closed => { lock (_gate) _propagationMessages.Remove(closed); };
    }

    private Link? FindBackchannel(ReadOnlySpan<byte> destination)
    {
        Link[] links;
        lock (_gate) links = [.. _incomingLinks];
        foreach (var link in links)
        {
            if (link.IsInitiator || link.Status != LinkStatus.Active || link.RemoteIdentity is not { } identity) continue;
            var hash = Destination.Create(DestinationType.Single, AppName, [DeliveryAspect], identity).Hash;
            if (hash.AsSpan().SequenceEqual(destination)) return link;
        }
        return null;
    }

    public int CleanLinks()
    {
        Link[] expired;
        lock (_gate)
        {
            var entries = _links.Where(entry => entry.Value.NoDataFor >
                (_propagationLinks.Contains(entry.Value) ? 180 : 600)).ToArray();
            var links = entries.Select(entry => entry.Value);
            if (_retrievalLink is { } retrieval && retrieval.NoDataFor > 180) links = links.Append(retrieval);
            expired = links.Distinct().ToArray();
            foreach (var (key, link) in entries)
            {
                _links.Remove(key);
                _identified.Remove(key);
                _propagationLinks.Remove(link);
                _propagationMessages.Remove(link);
            }
        }
        foreach (var link in expired)
        {
            ForgetRetrievalLink(link);
            try { link.Teardown(); }
            catch (Exception ex) { Report("Could not close idle LXMF link: " + ex.Message); }
        }
        return expired.Length;
    }

    public int CleanResourceTracking()
    {
        lock (_gate)
        {
            var completed = _incomingResources.Where(entry => entry.Value.Status >= ResourceStatus.Complete)
                .Select(entry => entry.Key).ToArray();
            foreach (var key in completed) _incomingResources.Remove(key);
            return completed.Length;
        }
    }

    private void MaintainDeliveryLinks()
    {
        var now = Now;
        if (now - _lastLinkCleanup >= 4) { _lastLinkCleanup = now; CleanLinks(); }
        if (now - _lastResourceCleanup >= 8) { _lastResourceCleanup = now; CleanResourceTracking(); }
        if (now - _lastTransientCleanup >= 240) { _lastTransientCleanup = now; CleanTransientIdCaches(); }
    }
}
