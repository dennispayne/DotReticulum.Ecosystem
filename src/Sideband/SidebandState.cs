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
    string DeliveryMethod = "Direct");

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
    bool TryPropagationOnFail = true)
{
    public const int CurrentSchemaVersion = 1;

    public static SidebandState Empty(string configDirectory) =>
        new(CurrentSchemaVersion, configDirectory, null, [], []);
}
