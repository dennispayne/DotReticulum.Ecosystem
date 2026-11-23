using System.Text.Json;

namespace Sideband;

public sealed partial class SidebandStateStore
{
    internal Task ObserveDeliveryAnnounceAsync(SidebandContact contact, CancellationToken cancellationToken = default)
    {
        var validated = SidebandContact.Create(contact.DisplayName, contact.DeliveryHash)
            with { ConversationKnown = false, NameIsUserDefined = false };
        return MutateAsync(state =>
        {
            var existing = state.Contacts.FirstOrDefault(item => item.DeliveryHash == validated.DeliveryHash);
            var observed = existing is null ? validated : existing with
            {
                DisplayName = existing.HasSavedName ? existing.DisplayName : validated.DisplayName,
                NameIsUserDefined = existing.HasSavedName,
            };
            var contacts = state.Contacts.Where(item => item.DeliveryHash != observed.DeliveryHash).Append(observed).ToArray();
            if (contacts.Length > 1_000) throw new InvalidOperationException("The contact list is full.");
            return state with { Contacts = contacts };
        }, cancellationToken);
    }

    internal Task PromoteConversationAsync(string hash, CancellationToken cancellationToken = default) =>
        MutateAsync(state =>
        {
            var contact = state.Contacts.FirstOrDefault(item => item.DeliveryHash == hash) ?? SidebandContact.Create(hash, hash);
            var contacts = state.Contacts.Where(item => item.DeliveryHash != hash)
                .Append(contact with { ConversationKnown = true }).ToArray();
            if (contacts.Length > 1_000) throw new InvalidOperationException("The contact list is full.");
            return state with { Contacts = contacts };
        }, cancellationToken);

    internal Task RecordCommandMethodAsync(Guid id, LXMF.LxmMethod method, CancellationToken cancellationToken = default) =>
        MutateAsync(state => state with
        {
            Messages = state.Messages.Select(message => message.Id == id
                ? message with { DeliveryMethod = method.ToString() } : message).ToArray()
        }, cancellationToken);

    public Task ConfigureCommandAuthorizationAsync(SidebandCommandAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return MutateAsync(state =>
        {
            var settings = state.ImportedSettings?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            settings["lxmf_ignore_unknown"] = JsonSerializer.SerializeToElement(authorization.IgnoreUnknown,
                SidebandStateJsonContext.Default.Boolean);
            return state with
            {
                ImportedSettings = settings,
                Telemetry = state.Telemetry with
                {
                    AllowRequestsFromAnyone = authorization.AllowRequestsFromAnyone,
                    AllowRequestsFromTrusted = authorization.AllowRequestsFromTrusted
                }
            };
        }, cancellationToken);
    }
}
