namespace Sideband;

public sealed record SidebandChatMessage(
    Guid Id,
    string PeerDeliveryHash,
    bool Incoming,
    string Title,
    string Content,
    DateTimeOffset Timestamp,
    string DeliveryState,
    IReadOnlyList<SidebandAttachment>? Attachments = null);

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
    string? LastPropagationNode = null)
{
    public const int CurrentSchemaVersion = 1;

    public static SidebandState Empty(string configDirectory) =>
        new(CurrentSchemaVersion, configDirectory, null, [], []);
}
