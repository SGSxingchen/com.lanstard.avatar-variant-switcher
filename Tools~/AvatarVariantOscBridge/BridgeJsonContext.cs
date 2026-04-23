using System.Text.Json.Serialization;

namespace AvatarVariantOscBridge;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AvatarVariantMap))]
[JsonSerializable(typeof(AvatarVariantMapEntry))]
[JsonSerializable(typeof(BridgeSettings))]
[JsonSerializable(typeof(OscQueryNode))]
[JsonSerializable(typeof(OscQueryHostInfo))]
internal partial class BridgeJsonContext : JsonSerializerContext
{
}
