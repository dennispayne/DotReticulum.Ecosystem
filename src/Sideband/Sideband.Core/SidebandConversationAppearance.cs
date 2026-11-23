using System.Text.Json;

namespace Sideband;

public sealed record SidebandConversationBadge(string Icon, string? Foreground = null, string? Background = null);

public sealed record SidebandConversationAppearance(bool Enabled = true, bool FromAll = true)
{
    public static SidebandConversationAppearance FromState(SidebandState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Import(state.ImportedSettings);
    }

    public SidebandConversationBadge Resolve(SidebandState state, SidebandContact contact)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(contact);
        return Select(contact, SidebandTelemetryPolicy.AllowRequest(state.Telemetry.Policy, new()
        {
            Exists = contact.ConversationKnown, Trust = contact.Trusted ? 1 : 0, AllowRequests = contact.AllowTelemetryRequests
        }));
    }

    public SidebandConversationBadge Select(SidebandContact contact, bool requestsAllowed)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var appearance = contact.Appearance;
        if (appearance is not null) SidebandTelemetryConfiguration.ValidateAppearance(appearance);
        if (Enabled && (contact.Trusted || FromAll) && appearance is not null &&
            (appearance.Icon != "account" || !appearance.Foreground.Equals("000000", StringComparison.OrdinalIgnoreCase) ||
             !appearance.Background.Equals("ffffff", StringComparison.OrdinalIgnoreCase)))
            return new(contact.Unread ? "email" : appearance.Icon, appearance.Foreground, appearance.Background);
        return new(contact.Unread ? contact.Trusted ? "email-seal" : "email" :
            requestsAllowed ? "account-lock-open" : contact.Trusted ? "account-check" : "account-question");
    }

    internal static SidebandConversationAppearance Import(IReadOnlyDictionary<string, JsonElement>? settings,
        SidebandConversationAppearance? fallback = null)
    {
        var prior = fallback ?? new();
        return new(Boolean("display_style_in_contact_list", prior.Enabled), Boolean("display_style_from_all", prior.FromAll));

        bool Boolean(string key, bool previous) => settings?.TryGetValue(key, out var item) != true ? previous : item.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"{key} must be boolean.")
        };
    }

    internal void Export(IDictionary<string, JsonElement> settings)
    {
        settings["display_style_in_contact_list"] = JsonSerializer.SerializeToElement(Enabled, SidebandStateJsonContext.Default.Boolean);
        settings["display_style_from_all"] = JsonSerializer.SerializeToElement(FromAll, SidebandStateJsonContext.Default.Boolean);
    }
}
