namespace Sideband;

public sealed partial class SidebandStateStore
{
    public Task ConfigureTelemetryAsync(SidebandTelemetryConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var validated = configuration.Validate();
        return MutateAsync(state => state with { Telemetry = validated }, cancellationToken);
    }

    public Task SetConversationTelemetryAsync(string deliveryHash, bool sendTelemetry, bool allowRequests,
        CancellationToken cancellationToken = default) =>
        MutateConversationAsync(deliveryHash, contact => contact with
        { SendTelemetry = sendTelemetry, AllowTelemetryRequests = allowRequests }, cancellationToken);

    internal Task UpdateTelemetryPeerAsync(string deliveryHash, Func<SidebandTelemetryPeerState, SidebandTelemetryPeerState> update,
        CancellationToken cancellationToken = default) => MutateAsync(state =>
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var hash)) throw new ArgumentException("Invalid telemetry peer.");
        var peers = state.TelemetryPeers ?? [];
        var peer = peers.SingleOrDefault(p => p.DeliveryHash == hash) ?? new(hash);
        return state with { TelemetryPeers = peers.Where(p => p.DeliveryHash != hash).Append(update(peer)).ToArray() };
    }, cancellationToken);

    internal Task SetTelemetryAppearanceAsync(string deliveryHash, SidebandTelemetryAppearance appearance, bool forwarded,
        CancellationToken cancellationToken = default)
    {
        SidebandTelemetryConfiguration.ValidateAppearance(appearance);
        return MutateAsync(state =>
        {
            if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var hash)) throw new ArgumentException("Invalid telemetry source.");
            var contact = state.Contacts.FirstOrDefault(c => c.DeliveryHash == hash) ?? SidebandContact.Create(hash, hash);
            if (forwarded && contact.Appearance is not null && contact.Appearance != new SidebandTelemetryAppearance()) return state;
            return state with { Contacts = state.Contacts.Where(c => c.DeliveryHash != hash).Append(contact with { Appearance = appearance }).ToArray() };
        }, cancellationToken);
    }

    internal Task RecordTelemetryAttemptAsync(string hash, bool request, bool live, bool scheduled, double now,
        CancellationToken cancellationToken) => MutateAsync(state =>
    {
        var peers = state.TelemetryPeers ?? [];
        var peer = peers.SingleOrDefault(p => p.DeliveryHash == hash) ?? new(hash);
        peer = request ? peer with { LastRequestAttemptUtcSeconds = now } : peer with
        {
            LastSendAttemptUtcSeconds = now,
            ScheduledSendAttempts = scheduled ? Math.Min(2, peer.ScheduledSendAttempts + 1) : peer.ScheduledSendAttempts
        };
        return state with
        {
            TelemetryPeers = peers.Where(p => p.DeliveryHash != hash).Append(peer).ToArray(),
            Telemetry = live ? state.Telemetry with
            {
                Tracking = state.Telemetry.Tracking.Select(t => t.DeliveryHash == hash ? t with { LastRequestUtcSeconds = now } : t).ToArray()
            } : state.Telemetry
        };
    }, cancellationToken);

    internal Task PruneTelemetryTrackingAsync(double now, CancellationToken cancellationToken) =>
        MutateAsync(state => state with
        {
            Telemetry = state.Telemetry with { Tracking = state.Telemetry.Tracking.Where(t => t.UntilUtcSeconds > now).ToArray() }
        }, cancellationToken);
}
