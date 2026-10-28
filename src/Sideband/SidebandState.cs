namespace Sideband;

public sealed record SidebandChatMessage(
    Guid Id,
    string PeerDeliveryHash,
    bool Incoming,
    string Title,
    string Content,
    DateTimeOffset Timestamp,
    string DeliveryState);

public sealed record SidebandState(
    int SchemaVersion,
    string ReticulumConfigDirectory,
    string? OwnDeliveryHash,
    IReadOnlyList<SidebandContact> Contacts,
    IReadOnlyList<SidebandChatMessage> Messages)
{
    public const int CurrentSchemaVersion = 1;

    public static SidebandState Empty(string configDirectory) =>
        new(CurrentSchemaVersion, configDirectory, null, [], []);
}
