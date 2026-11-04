namespace Sideband;

public sealed record SidebandLinkInformation(string DestinationHash, string State, double RoundTripSeconds, int Mtu);

public sealed partial class SidebandClient
{
    public bool TransportEnabled => _node?.Config.EnableTransport ?? false;

    public IReadOnlyList<SidebandLinkInformation> GetOutboundLinks() =>
        _node?.Router.Links.Links.Where(link => link.IsInitiator)
            .Select(link => new SidebandLinkInformation(Convert.ToHexStringLower(link.DestinationHash),
                link.Status.ToString(), link.Rtt ?? 0, link.Mtu)).ToArray() ?? [];

    public Task CreateConversationAsync(string deliveryHash, string name = "", CancellationToken cancellationToken = default) =>
        ChangeConversationNameAsync(deliveryHash, name, create: true, cancellationToken);

    public Task SetConversationNameAsync(string deliveryHash, string name, CancellationToken cancellationToken = default) =>
        ChangeConversationNameAsync(deliveryHash, name, create: false, cancellationToken);

    private async Task ChangeConversationNameAsync(string deliveryHash, string name, bool create, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (create) await _store.CreateConversationAsync(deliveryHash, name, cancellationToken).ConfigureAwait(false);
            else await _store.SetConversationNameAsync(deliveryHash, name, cancellationToken).ConfigureAwait(false);
            await RefreshVoicePolicyCoreAsync(cancellationToken).ConfigureAwait(false);
            PublishState(await LoadAsync(cancellationToken).ConfigureAwait(false));
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ConfigurePreferencesAsync(string displayName, string theme, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _store.SetPreferencesAsync(displayName, theme, cancellationToken).ConfigureAwait(false);
            _router?.SetDisplayName(displayName);
            PublishState(await LoadAsync(cancellationToken).ConfigureAwait(false));
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ClearConversationAsync(string deliveryHash, bool deleteConversation = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (deleteConversation) await ClearTelemetryAsync(deliveryHash, cancellationToken).ConfigureAwait(false);
            await _store.ClearConversationAsync(deliveryHash, deleteConversation, cancellationToken).ConfigureAwait(false);
            await RefreshVoicePolicyCoreAsync(cancellationToken).ConfigureAwait(false);
            PublishState(await LoadAsync(cancellationToken).ConfigureAwait(false));
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ReloadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _store.ReloadAsync(cancellationToken).ConfigureAwait(false);
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (_router is { } router)
            {
                router.SetDisplayName(state.DisplayName);
                router.OutboundPropagationNode = (state.PropagationNode ?? state.LastPropagationNode) is { } selected
                    ? Convert.FromHexString(selected) : null;
                var stamps = SidebandStampConfiguration.FromState(state);
                if (!router.SetInboundStampCost(stamps.EffectiveCost))
                    throw new InvalidOperationException("The router refused the reloaded stamp cost.");
                if (stamps.RejectInvalidStamps) router.EnforceStamps();
                else router.IgnoreStamps();
                router.DeliveryPerTransferLimit = SidebandDeliveryConfiguration.FromState(state).MaximumTransferBytes;
                await ApplyVoiceConfigurationAsync(state, cancellationToken).ConfigureAwait(false);
            }
            await RefreshLocalTelemetryAsync(cancellationToken).ConfigureAwait(false);
            PublishState(state);
        }
        finally { _lifecycle.Release(); }
    }
}
