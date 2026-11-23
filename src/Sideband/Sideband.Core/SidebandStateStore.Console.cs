namespace Sideband;

public sealed partial class SidebandStateStore
{
    public Task CreateConversationAsync(string deliveryHash, string name = "", CancellationToken cancellationToken = default) =>
        WriteConversationNameAsync(deliveryHash, name, create: true, cancellationToken);

    public Task SetConversationNameAsync(string deliveryHash, string name, CancellationToken cancellationToken = default) =>
        WriteConversationNameAsync(deliveryHash, name, create: false, cancellationToken);

    private Task WriteConversationNameAsync(string deliveryHash, string name, bool create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var normalized))
            throw new ArgumentException("An LXMF delivery address must be exactly 32 hexadecimal characters.", nameof(deliveryHash));
        var savedName = name.Length == 0 ? null : SidebandContact.Create(name, normalized).DisplayName;
        return MutateAsync(state =>
        {
            if (create && state.OwnDeliveryHash == normalized)
                throw new InvalidOperationException("Cannot create a conversation with the local delivery destination.");
            var contact = state.Contacts.FirstOrDefault(item => item.DeliveryHash == normalized);
            if (create && contact is { ConversationKnown: true })
                throw new InvalidOperationException("Conversation already exists.");
            if (!create && contact is not { ConversationKnown: true })
                throw new InvalidOperationException("No conversation exists for this destination.");
            contact ??= SidebandContact.Create(normalized, normalized) with { NameIsUserDefined = false };
            var updated = contact with
            {
                DisplayName = savedName ?? AutomaticConversationName(state, contact),
                NameIsUserDefined = savedName is not null,
                ConversationKnown = true,
            };
            var contacts = state.Contacts.Where(item => item.DeliveryHash != normalized).Append(updated).ToArray();
            if (contacts.Length > 1_000) throw new InvalidOperationException("The contact list is full.");
            return state with { Contacts = contacts };
        }, cancellationToken);
    }

    private static string AutomaticConversationName(SidebandState state, SidebandContact contact)
    {
        var observed = state.Announces?.FirstOrDefault(item =>
            item.DestinationType == SidebandAnnounce.Delivery && item.DestinationHash == contact.DeliveryHash);
        if (observed is null) return contact.HasSavedName ? contact.DeliveryHash : contact.DisplayName;
        var name = observed.Name?.Trim();
        return name is { Length: > 0 and <= 80 } ? name : contact.DeliveryHash;
    }

    public Task ClearConversationAsync(string deliveryHash, bool deleteConversation = false,
        CancellationToken cancellationToken = default)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var normalized))
            throw new ArgumentException("An LXMF delivery address must be exactly 32 hexadecimal characters.", nameof(deliveryHash));
        return MutateAsync(state => state with
        {
            Messages = state.Messages.Where(message => message.PeerDeliveryHash != normalized).ToArray(),
            Contacts = deleteConversation ? state.Contacts.Where(contact => contact.DeliveryHash != normalized).ToArray() : state.Contacts,
        }, cancellationToken);
    }

    public Task DeleteMessageAsync(string deliveryHash, Guid id, CancellationToken cancellationToken = default)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var normalized))
            throw new ArgumentException("An LXMF delivery address must be exactly 32 hexadecimal characters.", nameof(deliveryHash));
        if (id == Guid.Empty) throw new ArgumentException("A saved message ID is required.", nameof(id));
        return MutateAsync(state =>
        {
            if (!state.Messages.Any(message => message.Id == id && message.PeerDeliveryHash == normalized))
                throw new InvalidOperationException("No saved message exists for this conversation and ID.");
            return state with
            {
                Messages = state.Messages.Where(message => message.Id != id || message.PeerDeliveryHash != normalized).ToArray(),
            };
        }, cancellationToken);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _state;
            _state = null;
            try { _ = await LoadUnderLockAsync(cancellationToken).ConfigureAwait(false); }
            catch { _state = previous; throw; }
        }
        finally { _gate.Release(); }
    }
}
