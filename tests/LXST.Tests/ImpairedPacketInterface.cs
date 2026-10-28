using System.Runtime.CompilerServices;
using DotReticulum.Core;

namespace LXST.Tests;

internal sealed class ImpairedPacketInterface(IPacketInterface inner) : IPacketInterface
{
    private int _outbound;
    private int _inbound;
    private int _outboundDrops;
    private int _inboundDrops;
    private TaskCompletionSource? _linkRequestGate;
    public volatile bool Enabled;
    public int OutboundDrops => Volatile.Read(ref _outboundDrops);
    public int InboundDrops => Volatile.Read(ref _inboundDrops);
    public void HoldLinkRequests() => _linkRequestGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void ReleaseLinkRequests() => _linkRequestGate?.TrySetResult();
    public long Bitrate => inner.Bitrate;
    public int HardwareMtu => inner.HardwareMtu;
    public bool SupportsMtuDiscovery => inner.SupportsMtuDiscovery;
    public ValueTask StartAsync(CancellationToken cancellationToken) => inner.StartAsync(cancellationToken);

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        await WaitForLinkRequestGateAsync(packet, cancellationToken);
        if (Enabled && IsStreamData(packet))
        {
            await Task.Delay(5, cancellationToken);
            if (Interlocked.Increment(ref _outbound) % 5 == 0)
            {
                Interlocked.Increment(ref _outboundDrops);
                return;
            }
        }
        await inner.SendAsync(packet, cancellationToken);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var packet in inner.ReceiveAsync(cancellationToken))
        {
            await WaitForLinkRequestGateAsync(packet, cancellationToken);
            if (Enabled && IsStreamData(packet))
            {
                await Task.Delay(5, cancellationToken);
                if (Interlocked.Increment(ref _inbound) % 5 == 0)
                {
                    Interlocked.Increment(ref _inboundDrops);
                    continue;
                }
            }
            yield return packet;
        }
    }

    private bool IsStreamData(ReadOnlyMemory<byte> bytes) =>
        Packet.TryParse(bytes, out var packet, HardwareMtu) &&
        packet.DestinationType == DestinationType.Link && packet.PacketType == PacketType.Data && packet.Context == 0;

    private async ValueTask WaitForLinkRequestGateAsync(ReadOnlyMemory<byte> bytes, CancellationToken stop)
    {
        if (_linkRequestGate is { } gate && Packet.TryParse(bytes, out var packet, HardwareMtu) &&
            packet.PacketType == PacketType.LinkRequest)
            await gate.Task.WaitAsync(stop);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
