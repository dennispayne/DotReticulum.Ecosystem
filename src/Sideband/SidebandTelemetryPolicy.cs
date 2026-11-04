using LXMF;

namespace Sideband;

public sealed record SidebandTelemetryPolicySettings
{
    public bool Enabled { get; init; }
    public bool SendToTrusted { get; init; }
    public bool SendToCollector { get; init; }
    public bool RequestFromCollector { get; init; }
    public bool CollectorEnabled { get; init; }
    public bool SendAllToCollector { get; init; }
    public bool AllowRequestsFromAnyone { get; init; }
    public bool AllowRequestsFromTrusted { get; init; }
    public bool ReceiveTrustedOnly { get; init; }
    public bool DisplayTrustedOnly { get; init; }
    public bool SendAppearance { get; init; } = true;
    public bool UsePropagationOnly { get; init; }
    public bool TryPropagationOnFail { get; init; } = true;
    public bool RequestsOnlySendLatest { get; init; } = true;
    public const int DefaultSendIntervalSeconds = 43_200;
    public const int DefaultRequestIntervalSeconds = 43_200;
    public const int DefaultRequestHistorySeconds = 604_800;
}

public sealed record SidebandTelemetryConversationPolicy(bool Exists = false, int Trust = 0,
    bool SendTelemetry = false, bool AllowRequests = false)
{
    public bool IsTrusted => Exists && Trust == 1;
}

public sealed record SidebandTelemetryFieldContext(bool HasPackedTelemetry, double OwnTimestamp,
    double LastSuccessTimestamp = 0, bool TelemetryUpdate = false, bool AuthorizedRequest = false,
    bool CollectorResponse = false, bool OwnSourceExcluded = false, bool SignalAlreadySent = false);
public sealed record SidebandTelemetryFieldDecision(bool IncludeTelemetry, bool IncludeAppearance, bool SuppressFields);

public sealed record SidebandTelemetrySendContext(double NowUtcSeconds)
{
    public bool HasAddress { get; init; } = true;
    public bool IsSelf { get; init; }
    public bool TargetIsCollector { get; init; }
    public bool InProgress { get; init; }
    public bool IdentityKnown { get; init; } = true;
    public bool HasPackedTelemetry { get; init; } = true;
    public bool HasReadings { get; init; } = true;
    public double OwnTimestamp { get; init; }
    public double LastSuccessTimestamp { get; init; }
    public bool AuthorizedRequest { get; init; }
    public bool CollectorResponse { get; init; }
    public bool OwnSourceExcluded { get; init; }
    /// <summary>Null is no stream; empty is an explicitly supplied empty stream. Ownership remains with the caller.</summary>
    public IReadOnlyList<double>? StreamTimebases { get; init; }
    public bool LinkAvailable { get; init; }
    public bool RatchetAvailable { get; init; }
    public bool PropagationNodeAvailable { get; init; }
}

public sealed record SidebandTelemetryRequestContext(double NowUtcSeconds)
{
    public bool HasAddress { get; init; } = true;
    public bool IsSelf { get; init; }
    public bool InProgress { get; init; }
    public bool IdentityKnown { get; init; } = true;
    public double? LastReceivedTimebase { get; init; }
    public bool IsCollectorRequest { get; init; }
    public bool IsLiveTrack { get; init; }
    public bool LinkAvailable { get; init; }
    public bool RatchetAvailable { get; init; }
    public bool PropagationNodeAvailable { get; init; }
}

public enum SidebandTelemetryAdmission { Sent, NoAddress, InProgress, DestinationUnknown, NothingToSend, AlreadySent }
public enum SidebandTelemetryDeliveryDisposition { Drop, IngestMessage, HandleCommands }
public sealed record SidebandTelemetryOutboundDecision(SidebandTelemetryAdmission Status, LxmMethod Method = LxmMethod.Direct,
    bool IncludeTicket = false, bool TryPropagationOnFail = false, double? Timebase = null,
    bool IncludeOwnTelemetry = false, bool IncludeStream = false, bool IncludeAppearance = false, bool CollectorRequest = false)
{
    public bool RequestPath => Status == SidebandTelemetryAdmission.DestinationUnknown;
    public string PrimaryStatus => Status switch
    {
        SidebandTelemetryAdmission.Sent => "sent", SidebandTelemetryAdmission.NoAddress => "no_address",
        SidebandTelemetryAdmission.InProgress => "in_progress", SidebandTelemetryAdmission.DestinationUnknown => "destination_unknown",
        SidebandTelemetryAdmission.NothingToSend => "nothing_to_send", _ => "already_sent"
    };
}

/// <summary>
/// Pure primary d069122 policy observations, NOT authentication, transmission, provenance attestation or scheduling.
/// Ordinary receipt in the reference lacks a signature gate; callers may enforce stricter authentication before this helper.
/// </summary>
public static class SidebandTelemetryPolicy
{
    public const int MaximumStreamEntries = 256;

    public static bool ShouldSendTelemetry(SidebandTelemetryPolicySettings settings, SidebandTelemetryConversationPolicy conversation) =>
        settings.Enabled && conversation.Exists && (conversation.SendTelemetry || settings.SendToTrusted && conversation.IsTrusted);

    /// <summary>Primary trusted-only request mode bypasses the per-conversation fallback, including explicit permissions for untrusted peers.</summary>
    public static bool AllowRequest(SidebandTelemetryPolicySettings settings, SidebandTelemetryConversationPolicy conversation) =>
        settings.AllowRequestsFromAnyone || (settings.AllowRequestsFromTrusted
            ? conversation.IsTrusted : conversation.Exists && conversation.AllowRequests);

    /// <summary>Applies to the relay/sender conversation, not an asserted inner stream source. Global enable is not consulted.</summary>
    public static bool AcceptReceivedTelemetry(SidebandTelemetryPolicySettings settings,
        SidebandTelemetryConversationPolicy sender, bool originator = false) =>
        !originator && (!settings.ReceiveTrustedOnly || sender.IsTrusted);

    /// <summary>hasCommands means field presence, including an empty command array; it does not mean nonempty content.</summary>
    public static SidebandTelemetryDeliveryDisposition DeliveryDisposition(SidebandTelemetryPolicySettings settings,
        SidebandTelemetryConversationPolicy sender, bool signatureValidated, bool hasCommands, bool ignoreUnknown)
    {
        if (ignoreUnknown && !sender.Exists) return SidebandTelemetryDeliveryDisposition.Drop;
        if (signatureValidated && hasCommands) return AllowRequest(settings, sender)
            ? SidebandTelemetryDeliveryDisposition.HandleCommands : SidebandTelemetryDeliveryDisposition.Drop;
        return SidebandTelemetryDeliveryDisposition.IngestMessage;
    }

    public static bool IncludeCollectorSource(bool sourceIsRecipient, bool sourceIsExcluded, bool latestOnly, bool alreadyAdded) =>
        !sourceIsRecipient && !sourceIsExcluded && (!latestOnly || !alreadyAdded);

    /// <summary>Preserves the primary all-zero/nil omission, but additionally excludes nonfinite native observations.</summary>
    public static bool UsePhysicalLinkObservation(double? rssi, double? snr, double? quality) =>
        rssi is { } r && double.IsFinite(r) && r != 0 ||
        snr is { } s && double.IsFinite(s) && s != 0 ||
        quality is { } q && double.IsFinite(q) && q != 0;

    public static SidebandTelemetryFieldDecision EvaluateFields(SidebandTelemetryPolicySettings settings,
        SidebandTelemetryConversationPolicy conversation, SidebandTelemetryFieldContext context)
    {
        Finite(context.OwnTimestamp); Finite(context.LastSuccessTimestamp);
        var include = context.TelemetryUpdate || context.AuthorizedRequest || ShouldSendTelemetry(settings, conversation);
        var appearance = settings.SendAppearance || include;
        if (include && context.HasPackedTelemetry)
        {
            if (context.CollectorResponse && context.OwnSourceExcluded) include = false;
            else if (context.OwnTimestamp <= context.LastSuccessTimestamp)
                return new(false, false, context.SignalAlreadySent);
        }
        else include = false;
        return new(include, appearance, false);
    }

    public static LxmMethod DeliveryMethod(SidebandTelemetryPolicySettings settings, bool linkAvailable, bool ratchetAvailable) =>
        settings.UsePropagationOnly ? LxmMethod.Propagated :
        !linkAvailable && ratchetAvailable ? LxmMethod.Opportunistic : LxmMethod.Direct;

    public static SidebandTelemetryOutboundDecision EvaluateSend(SidebandTelemetryPolicySettings settings,
        SidebandTelemetryConversationPolicy conversation, SidebandTelemetrySendContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Finite(context.NowUtcSeconds); Finite(context.OwnTimestamp); Finite(context.LastSuccessTimestamp);
        if (context.StreamTimebases is { Count: > MaximumStreamEntries })
            throw new ArgumentOutOfRangeException(nameof(context), "Telemetry policy stream accounting is bounded to 256 entries.");
        if (!context.HasAddress || context.IsSelf) return new(SidebandTelemetryAdmission.NoAddress);
        if (context.InProgress) return new(SidebandTelemetryAdmission.InProgress);
        if (!(context.HasPackedTelemetry && context.HasReadings) && context.StreamTimebases is null)
            return new(SidebandTelemetryAdmission.NothingToSend);
        if (!context.IdentityKnown) return new(SidebandTelemetryAdmission.DestinationUnknown);
        var fields = EvaluateFields(settings, conversation, new(context.HasPackedTelemetry, context.OwnTimestamp,
            context.LastSuccessTimestamp, AuthorizedRequest: context.AuthorizedRequest || context.TargetIsCollector,
            CollectorResponse: context.CollectorResponse, OwnSourceExcluded: context.OwnSourceExcluded, SignalAlreadySent: true));
        if (fields.SuppressFields && context.StreamTimebases is null) return new(SidebandTelemetryAdmission.AlreadySent);
        var stream = context.StreamTimebases is { Count: > 0 };
        if (!fields.IncludeTelemetry && !stream) return new(SidebandTelemetryAdmission.NothingToSend);
        var timebase = fields.IncludeTelemetry ? context.OwnTimestamp : 0;
        if (!fields.IncludeTelemetry && context.StreamTimebases is { } timestamps)
            foreach (var timestamp in timestamps)
                if (SidebandTelemetryCodec.IsValidTimebase(timestamp, context.NowUtcSeconds)) timebase = Math.Max(timebase, timestamp);
        if (timebase <= context.LastSuccessTimestamp) return new(SidebandTelemetryAdmission.AlreadySent);
        return new(SidebandTelemetryAdmission.Sent, DeliveryMethod(settings, context.LinkAvailable, context.RatchetAvailable),
            conversation.IsTrusted, settings.TryPropagationOnFail && context.PropagationNodeAvailable, timebase,
            fields.IncludeTelemetry, stream, fields.IncludeAppearance);
    }

    public static SidebandTelemetryOutboundDecision EvaluateRequest(SidebandTelemetryPolicySettings settings,
        SidebandTelemetryRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Finite(context.NowUtcSeconds);
        if (context.LastReceivedTimebase is { } received) Finite(received);
        if (!context.HasAddress || context.IsSelf) return new(SidebandTelemetryAdmission.NoAddress);
        if (context.InProgress) return new(SidebandTelemetryAdmission.InProgress);
        if (!context.IdentityKnown) return new(SidebandTelemetryAdmission.DestinationUnknown);
        var timebase = context.LastReceivedTimebase is null or 0
            ? context.NowUtcSeconds - SidebandTelemetryPolicySettings.DefaultRequestHistorySeconds : context.LastReceivedTimebase.Value;
        return new(SidebandTelemetryAdmission.Sent, DeliveryMethod(settings, context.LinkAvailable, context.RatchetAvailable), true,
            settings.TryPropagationOnFail && context.PropagationNodeAvailable && !context.IsLiveTrack, timebase,
            CollectorRequest: context.IsCollectorRequest);
    }

    private static void Finite(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Policy timebases must be finite.");
    }
}
