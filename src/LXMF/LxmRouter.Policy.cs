namespace LXMF;

public sealed partial class LxmRouter
{
    private int _enforceStamps;
    public bool StampsEnforced => Volatile.Read(ref _enforceStamps) != 0;
    public void EnforceStamps() => Volatile.Write(ref _enforceStamps, 1);
    public void IgnoreStamps() => Volatile.Write(ref _enforceStamps, 0);

    public bool CancelOutbound(ReadOnlySpan<byte> messageId, LxmState cancelState = LxmState.Cancelled)
    {
        if (cancelState is not (LxmState.Cancelled or LxmState.Rejected or LxmState.Failed))
            throw new ArgumentOutOfRangeException(nameof(cancelState));
        var message = FindOutbound(messageId);
        if (message is null) return false;
        message.State = cancelState;
        lock (_gate)
        {
            _outbound.Remove(message);
            if (_preparing.TryGetValue(message, out var preparation)) preparation.Cancel();
        }
        message.ActiveResource?.Cancel();
        return true;
    }

    public double? GetOutboundProgress(ReadOnlySpan<byte> messageId) => FindOutbound(messageId)?.Progress;
    public int? GetOutboundMessageStampCost(ReadOnlySpan<byte> messageId) => FindOutbound(messageId)?.StampCost;
    public int? GetOutboundPropagationStampCost(ReadOnlySpan<byte> messageId) => FindOutbound(messageId)?.PropagationTargetCost;

    private LxMessage? FindOutbound(ReadOnlySpan<byte> messageId)
    {
        if (messageId.Length != 32) return null;
        lock (_gate)
        {
            foreach (var message in _outbound.Concat(_preparing.Keys))
            {
                var hash = message.Hash ?? message.ComputeHash();
                if (messageId.SequenceEqual(hash)) return message;
            }
        }
        return null;
    }
}
