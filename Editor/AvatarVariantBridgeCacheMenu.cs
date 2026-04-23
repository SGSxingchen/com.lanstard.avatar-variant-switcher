using System.IO;
using UnityEditor;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    internal static class AvatarVariantBridgeCacheMenu
    {
        [MenuItem("Tools/Lanstard Avatar Variant Switcher/清除桥缓存", priority = 1000)]
        private static void ClearBridgeCache()
        {
            var dir = AvatarVariantBridgeLauncher.GetCacheDirectoryForDisplay();
            if (!Directory.Exists(dir))
            {
                EditorUtility.DisplayDialog(
                    "清除桥缓存",
                    $"缓存目录不存在，无需清理：\n{dir}",
                    "确定");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "清除桥缓存",
                    $"将清除下列文件（保留 settings.json 不动）：\n\n{dir}\\bridge.exe\n{dir}\\version.txt\n\n继续？",
                    "清除",
                    "取消"))
            {
                return;
            }

            AvatarVariantBridgeLauncher.ClearCache();
            EditorUtility.DisplayDialog("清除桥缓存", "完成。下次启动桥时会重新下载。", "确定");
        }
    }
}
