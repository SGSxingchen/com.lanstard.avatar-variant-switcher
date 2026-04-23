using System.Text.Json;

namespace AvatarVariantOscBridge;

internal sealed class AvatarVariantMap
{
    public int SchemaVersion { get; set; } = 1;
    public string GeneratedAtUtc { get; set; } = string.Empty;
    public string ParameterName { get; set; } = string.Empty;
    public string MenuName { get; set; } = string.Empty;
    public int DefaultValue { get; set; }
    public List<AvatarVariantMapEntry> Variants { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static AvatarVariantMap Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Mapping file not found.", path);

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Mapping file is empty.");

        var map = JsonSerializer.Deserialize<AvatarVariantMap>(json, Options);
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
    public string VariantKey { get; set; } = string.Empty;
    public int ParamValue { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string BlueprintId { get; set; } = string.Empty;
}
