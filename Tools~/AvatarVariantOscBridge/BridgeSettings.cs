using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvatarVariantOscBridge;

internal sealed class BridgeSettings
{
    /// <summary>
    /// 桥 ↔ Inspector / 映射文件的协议版本。当前未使用，
    /// 未来映射文件 schema 或 OSC 协议真出现不兼容时用作门卫。
    /// </summary>
    public const int BridgeProtocolVersion = 1;

    [JsonPropertyName("lastMappingPath")]
    public string? LastMappingPath { get; set; }

    private static string SettingsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanstardAvatarVariantBridge");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static BridgeSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new BridgeSettings();
            var json = File.ReadAllText(SettingsPath);
            if (string.IsNullOrWhiteSpace(json)) return new BridgeSettings();
            return JsonSerializer.Deserialize(json, BridgeJsonContext.Default.BridgeSettings)
                ?? new BridgeSettings();
        }
        catch
        {
            // Corrupt / unreadable settings → start fresh, never crash on launch.
            return new BridgeSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(this, BridgeJsonContext.Default.BridgeSettings);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARN: failed to save settings: {ex.Message}");
        }
    }
}
