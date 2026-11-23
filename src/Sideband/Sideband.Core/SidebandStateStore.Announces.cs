namespace Sideband;

public sealed partial class SidebandStateStore
{
    public Task ObserveAnnounceAsync(SidebandAnnounce announce, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(announce);
        SidebandAnnounce.Validate(announce);
        return MutateAsync(state => state with
        {
            Announces = SidebandAnnounce.Observe(state.Announces ?? [], announce)
        }, cancellationToken);
    }

    public Task DeleteAnnounceAsync(string destinationHash, CancellationToken cancellationToken = default)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(destinationHash, out var hash))
            throw new ArgumentException("An announce address must contain 32 hexadecimal characters.", nameof(destinationHash));
        return MutateAsync(state => state with
        {
            Announces = (state.Announces ?? []).Where(item => item.DestinationHash != hash).ToArray()
        }, cancellationToken);
    }
}
