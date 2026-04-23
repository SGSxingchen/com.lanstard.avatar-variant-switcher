using System.Text.Json.Serialization;

namespace AvatarVariantOscBridge;

internal static class OscQueryAccess
{
    public const int NoAccess = 0;
    public const int ReadOnly = 1;
    public const int WriteOnly = 2;
    public const int ReadWrite = 3;
}

internal sealed class OscQueryNode
{
    [JsonPropertyName("FULL_PATH")]
    public string FullPath { get; set; } = string.Empty;

    [JsonPropertyName("ACCESS")]
    public int Access { get; set; } = OscQueryAccess.NoAccess;

    [JsonPropertyName("TYPE")]
    public string? Type { get; set; }

    [JsonPropertyName("DESCRIPTION")]
    public string? Description { get; set; }

    [JsonPropertyName("CONTENTS")]
    public Dictionary<string, OscQueryNode>? Contents { get; set; }
}

internal sealed class OscQueryHostInfo
{
    [JsonPropertyName("NAME")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("OSC_IP")]
    public string OscIp { get; set; } = "127.0.0.1";

    [JsonPropertyName("OSC_PORT")]
    public int OscPort { get; set; }

    [JsonPropertyName("OSC_TRANSPORT")]
    public string OscTransport { get; set; } = "UDP";

    [JsonPropertyName("EXTENSIONS")]
    public Dictionary<string, bool> Extensions { get; set; } = new()
    {
        ["ACCESS"] = true,
        ["VALUE"] = false,
        ["DESCRIPTION"] = true,
        ["TAGS"] = false,
        ["EXTENDED_TYPE"] = false,
        ["UNIT"] = false,
        ["CRITICAL"] = false,
        ["CLIPMODE"] = false,
        ["LISTEN"] = false,
        ["PATH_CHANGED"] = false,
    };
}
