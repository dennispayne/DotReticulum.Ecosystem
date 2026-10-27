using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotLXMF;

public sealed partial class LxmRouter
{
    private LocalDestination? _plain;

    /// <summary>Hosts unencrypted lxmf.delivery packets. Plain packets have no delivery receipts.</summary>
    public byte[] RegisterPlainDelivery()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_plain is not null) return _plain.Destination.Hash.ToArray();
            var destination = Destination.Create(DestinationType.Plain, AppName, [DeliveryAspect]);
            _plain = new LocalDestination(_identity, destination)
            {
                AcceptLinks = false,
                PacketDecrypted = (plain, _) =>
                {
                    var wire = new byte[destination.Hash.Length + plain.Length];
                    destination.Hash.CopyTo(wire, 0);
                    plain.CopyTo(wire, destination.Hash.Length);
                    if (!_inbound.Writer.TryWrite(new InboundWork(wire, LxmMethod.Opportunistic,
                        ExpectedDestination: destination.Hash, DestinationType: DestinationType.Plain)))
                        Report("LXMF inbound queue is full; dropping plain message.");
                    return ValueTask.CompletedTask;
                },
            };
            _router.RegisterLocalDestination(_plain);
            Start();
            return destination.Hash.ToArray();
        }
    }
}
