using System.Text.Json.Serialization;

namespace Sideband;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SidebandState))]
[JsonSerializable(typeof(SidebandContact))]
[JsonSerializable(typeof(SidebandChatMessage))]
internal sealed partial class SidebandStateJsonContext : JsonSerializerContext;
