namespace Sideband;

public sealed record SidebandChatMessage(
    Guid Id,
    string PeerDeliveryHash,
    bool Incoming,
    string Title,
    string Content,
    DateTimeOffset Timestamp,
    string DeliveryState,
    IReadOnlyList<SidebandAttachment>? Attachments = null,
    string DeliveryMethod = "Direct",
    SidebandImage? Image = null,
    SidebandAudio? Audio = null,
    string? PaperUri = null,
    bool ImportedPaperEnvelope = false);

public sealed record SidebandState(
    int SchemaVersion,
    string ReticulumConfigDirectory,
    string? OwnDeliveryHash,
    IReadOnlyList<SidebandContact> Contacts,
    IReadOnlyList<SidebandChatMessage> Messages,
    string DisplayName = "Sideband",
    string Theme = "Dark",
    string? PropagationNode = null,
    bool PropagationByDefault = false,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? ImportedSettings = null,
    string? LastPropagationNode = null,
    bool PeriodicSynchronization = false,
    int SynchronizationIntervalSeconds = 43_200,
    double? LastSynchronization = null,
    bool SynchronizationRetrying = false,
    bool TryPropagationOnFail = true,
    bool VoiceEnabled = false,
    SidebandVoiceConfiguration? Voice = null,
    IReadOnlyList<SidebandVoicePeer>? VoicePeers = null,
    IReadOnlyList<SidebandVoiceCallEvent>? Calls = null,
    IReadOnlyList<SidebandTelemetryPeerState>? TelemetryPeers = null,
    IReadOnlyList<SidebandFixedLocation>? MapFixedLocations = null,
    IReadOnlyList<SidebandAnnounce>? Announces = null)
{
    private readonly SidebandTelemetryConfiguration? _telemetry;
    public SidebandTelemetryConfiguration Telemetry { get => _telemetry ?? new(); init => _telemetry = value; }
    public const int CurrentSchemaVersion = 1;

    public static SidebandState Empty(string configDirectory) =>
        new(CurrentSchemaVersion, configDirectory, null, [], []);
}
