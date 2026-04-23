using System.Text.Json.Serialization;

namespace AvatarVariantOscBridge;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AvatarVariantMap))]
[JsonSerializable(typeof(AvatarVariantMapEntry))]
internal partial class BridgeJsonContext : JsonSerializerContext
{
}
