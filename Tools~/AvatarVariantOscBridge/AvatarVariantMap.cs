using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvatarVariantOscBridge;

internal sealed class AvatarVariantMap
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("generatedAtUtc")]
    public string GeneratedAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("parameterName")]
    public string ParameterName { get; set; } = string.Empty;

    [JsonPropertyName("menuName")]
    public string MenuName { get; set; } = string.Empty;

    [JsonPropertyName("defaultValue")]
    public int DefaultValue { get; set; }

    [JsonPropertyName("variants")]
    public List<AvatarVariantMapEntry> Variants { get; set; } = new();

    public static AvatarVariantMap Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Mapping file not found.", path);

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Mapping file is empty.");

        var map = JsonSerializer.Deserialize(json, BridgeJsonContext.Default.AvatarVariantMap);
        if (map == null)
            throw new InvalidDataException("Failed to deserialize mapping file.");

        map.Variants ??= new List<AvatarVariantMapEntry>();
        if (string.IsNullOrWhiteSpace(map.ParameterName))
            throw new InvalidDataException("Mapping file is missing parameterName.");

        return map;
    }
}

internal sealed class AvatarVariantMapEntry
{
    [JsonPropertyName("variantKey")]
    public string VariantKey { get; set; } = string.Empty;

    [JsonPropertyName("paramValue")]
    public int ParamValue { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("blueprintId")]
    public string BlueprintId { get; set; } = string.Empty;
}
