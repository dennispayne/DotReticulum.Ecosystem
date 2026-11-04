namespace Sideband;

public sealed record SidebandTelemetryAppearance(string Icon = "account", string Foreground = "000000", string Background = "ffffff");
public sealed record SidebandTelemetryManualSensor(byte SensorId, string WireHex);
public sealed record SidebandTelemetryTracking(string DeliveryHash, int IntervalSeconds, double UntilUtcSeconds, double LastRequestUtcSeconds = 0);
public sealed record SidebandTelemetryPeerState(string DeliveryHash, double LastSendSuccessTimebase = 0,
    double LastReceivedTimebase = 0, double LastSendAttemptUtcSeconds = 0, double LastRequestAttemptUtcSeconds = 0,
    double LastRequestSuccessTimebase = 0, int ScheduledSendAttempts = 0, double ScheduledSendBlockedUntil = 0);

/// <summary>Explicit manual data and opt-in connected runtime collection. No option opens an audio/location device.</summary>
public sealed record SidebandTelemetryConfiguration
{
    public bool Enabled { get; init; }
    public bool RnsTransportEnabled { get; init; }
    public bool RnsTransportIncludeTables { get; init; }
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
    public string? Collector { get; init; }
    public int SendIntervalSeconds { get; init; } = 43_200;
    public int RequestIntervalSeconds { get; init; } = 43_200;
    public bool InformationEnabled { get; init; }
    public string Information { get; init; } = "";
    public bool FixedLocationEnabled { get; init; }
    public SidebandGeoCoordinate FixedLocation { get; init; } = new(0, 0, 0);
    public SidebandTelemetryAppearance Appearance { get; init; } = new();
    public IReadOnlyList<SidebandTelemetryManualSensor> ManualSensors { get; init; } = [];
    public IReadOnlyList<string> ExcludedSources { get; init; } = [];
    public IReadOnlyList<SidebandTelemetryTracking> Tracking { get; init; } = [];

    internal SidebandTelemetryPolicySettings Policy => new()
    {
        Enabled = Enabled, SendToTrusted = SendToTrusted, SendToCollector = SendToCollector,
        RequestFromCollector = RequestFromCollector, CollectorEnabled = CollectorEnabled,
        SendAllToCollector = SendAllToCollector, AllowRequestsFromAnyone = AllowRequestsFromAnyone,
        AllowRequestsFromTrusted = AllowRequestsFromTrusted, ReceiveTrustedOnly = ReceiveTrustedOnly,
        DisplayTrustedOnly = DisplayTrustedOnly, SendAppearance = SendAppearance, UsePropagationOnly = UsePropagationOnly,
        TryPropagationOnFail = TryPropagationOnFail, RequestsOnlySendLatest = RequestsOnlySendLatest
    };

    internal SidebandTelemetryConfiguration Validate()
    {
        if (SendIntervalSeconds is < 1 or > 31_536_000 || RequestIntervalSeconds is < 1 or > 31_536_000 ||
            Information is null || Information.Length > 16_384 || ManualSensors is null || ManualSensors.Count > 64 ||
            ExcludedSources is null || ExcludedSources.Count > 1_000 || Tracking is null || Tracking.Count > 64)
            throw new InvalidDataException("Invalid bounded telemetry configuration.");
        if (Collector is not null && !SidebandContact.TryNormalizeDeliveryHash(Collector, out _))
            throw new InvalidDataException("Invalid telemetry collector hash.");
        ValidateAppearance(Appearance);
        _ = SidebandGeo.ToCartesian(FixedLocation);
        if (FixedLocationEnabled)
            _ = SidebandTelemetryCodec.FromLocation(new(FixedLocation.Latitude, FixedLocation.Longitude, FixedLocation.Altitude, 0, 0, 0, 0));
        if (ManualSensors.Select(s => s?.SensorId).Distinct().Count() != ManualSensors.Count)
            throw new InvalidDataException("Duplicate manual sensor IDs.");
        foreach (var sensor in ManualSensors)
        {
            if (sensor is null || sensor.SensorId is 1 or 16 || sensor.WireHex is null ||
                sensor.WireHex.Length > SidebandTelemetryCodec.MaxValueBytes * 2)
                throw new InvalidDataException("Manual sensors cannot supply time/received provenance or oversized values.");
            if (RnsTransportEnabled && sensor.SensorId == (byte)SidebandTelemetrySensorId.RnsTransport)
                throw new InvalidDataException("Automatic and manual Reticulum transport sensors cannot both be enabled.");
            var reading = SidebandTelemetryCodec.FromWireValue(sensor.SensorId, SidebandTelemetryValue.Decode(Convert.FromHexString(sensor.WireHex)));
            if (reading.Status == SidebandTelemetryStatus.Malformed) throw new InvalidDataException("Malformed manual sensor.");
        }
        foreach (var source in ExcludedSources)
            if (!SidebandContact.TryNormalizeDeliveryHash(source, out _)) throw new InvalidDataException("Invalid excluded telemetry source.");
        foreach (var entry in Tracking)
            if (entry is null || !SidebandContact.TryNormalizeDeliveryHash(entry.DeliveryHash, out _) ||
                entry.IntervalSeconds is < 1 or > 31_536_000 || !double.IsFinite(entry.UntilUtcSeconds) || !double.IsFinite(entry.LastRequestUtcSeconds))
                throw new InvalidDataException("Invalid telemetry tracking schedule.");
        return this with
        {
            Collector = Collector?.ToLowerInvariant(), ManualSensors = Array.AsReadOnly(ManualSensors.ToArray()),
            ExcludedSources = Array.AsReadOnly(ExcludedSources.Select(s => s.ToLowerInvariant()).Distinct().ToArray()),
            Tracking = Array.AsReadOnly(Tracking.Select(t => t with { DeliveryHash = t.DeliveryHash.ToLowerInvariant() }).ToArray())
        };
    }

    internal static void ValidateAppearance(SidebandTelemetryAppearance? appearance)
    {
        if (appearance is null || string.IsNullOrWhiteSpace(appearance.Icon) || appearance.Icon.Length > 64 ||
            appearance.Icon.Any(char.IsControl) || appearance.Foreground is not { Length: 6 } || appearance.Background is not { Length: 6 } ||
            !appearance.Foreground.All(Uri.IsHexDigit) || !appearance.Background.All(Uri.IsHexDigit))
            throw new InvalidDataException("Appearance requires a bounded icon name and two six-digit RGB values.");
    }
}

public enum SidebandTelemetryQueueStatus { Queued, NotConnected, QueueFull, NoAddress, InProgress, NothingToSend, AlreadySent }
public sealed record SidebandTelemetryQueueResult(SidebandTelemetryQueueStatus Status)
{
    public bool Queued => Status == SidebandTelemetryQueueStatus.Queued;
}
