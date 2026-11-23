namespace Sideband;

public sealed partial class SidebandStateStore
{
    public Task ConfigureFixedLocationsAsync(IReadOnlyList<SidebandFixedLocation> locations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var snapshot = locations.Take(SidebandFixedLocations.MaximumLocations + 1).ToArray();
        SidebandFixedLocations.Validate(snapshot);
        return MutateAsync(state => state with { MapFixedLocations = Array.AsReadOnly(snapshot) }, cancellationToken);
    }

    public Task SetConversationObjectAsync(string deliveryHash, bool isObject, CancellationToken cancellationToken = default)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var peer))
            throw new ArgumentException("An object requires a 32-character hexadecimal LXMF delivery address.", nameof(deliveryHash));
        return MutateAsync(state =>
        {
            if (!state.Contacts.Any(contact => contact.DeliveryHash == peer))
                throw new KeyNotFoundException("Add this LXMF address as a contact before changing its object classification.");
            return state with
            {
                Contacts = state.Contacts.Select(contact => contact.DeliveryHash == peer
                    ? contact with { IsObject = isObject, ConversationKnown = true } : contact).ToArray()
            };
        }, cancellationToken);
    }
}
