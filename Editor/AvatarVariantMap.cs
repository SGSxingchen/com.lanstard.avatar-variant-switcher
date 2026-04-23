using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    [Serializable]
    public class AvatarVariantMap
    {
        public int schemaVersion = 1;
        public string generatedAtUtc = string.Empty;
        public string parameterName = string.Empty;
        public string menuName = string.Empty;
        public int defaultValue;
        public List<AvatarVariantMapVariant> variants = new List<AvatarVariantMapVariant>();

        public static AvatarVariantMap Read(string path)
        {
            var resolvedPath = ResolvePath(path);
            if (!File.Exists(resolvedPath))
            {
                return new AvatarVariantMap();
            }

            var json = File.ReadAllText(resolvedPath, new UTF8Encoding(false));
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AvatarVariantMap();
            }

            var map = JsonUtility.FromJson<AvatarVariantMap>(json);
            if (map == null)
            {
                throw new InvalidOperationException("映射文件读取失败。");
            }

            map.EnsureInitialized();
            return map;
        }

        public static void WriteAtomic(string path, AvatarVariantMap map)
        {
            if (map == null)
            {
                throw new ArgumentNullException("map");
            }

            var resolvedPath = ResolvePath(path);
            var directory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            map.EnsureInitialized();
            map.schemaVersion = 1;
            map.generatedAtUtc = DateTime.UtcNow.ToString("O");

            var json = JsonUtility.ToJson(map, true);
            var tmpPath = resolvedPath + ".tmp";
            File.WriteAllText(tmpPath, json, new UTF8Encoding(false));
            // .NET Standard 2.0 lacks File.Move(src, dst, overwrite); emulate with atomic Replace / delete-then-move.
            if (File.Exists(resolvedPath))
            {
                File.Replace(tmpPath, resolvedPath, null);
            }
            else
            {
                File.Move(tmpPath, resolvedPath);
            }

            if (StartsWithProjectAssetRoot(path))
            {
                AssetDatabase.Refresh();
            }
        }

        public void Upsert(string variantKey, int paramValue, string displayName, string blueprintId)
        {
            EnsureInitialized();

            var existing = FindByKey(variantKey);
            if (existing == null)
            {
                variants.Add(new AvatarVariantMapVariant
                {
                    variantKey = variantKey ?? string.Empty,
                    paramValue = paramValue,
                    displayName = displayName ?? string.Empty,
                    blueprintId = blueprintId ?? string.Empty
                });
                return;
            }

            existing.paramValue = paramValue;
            existing.displayName = displayName ?? string.Empty;
            existing.blueprintId = blueprintId ?? string.Empty;
        }

        public AvatarVariantMapVariant FindByKey(string variantKey)
        {
            EnsureInitialized();
            return variants.FirstOrDefault(variant => string.Equals(variant.variantKey, variantKey, StringComparison.Ordinal));
        }

        public void PruneKeysNotIn(ICollection<string> validKeys)
        {
            EnsureInitialized();

            var keySet = new HashSet<string>(validKeys ?? Array.Empty<string>());
            variants.RemoveAll(variant => string.IsNullOrWhiteSpace(variant.variantKey) || !keySet.Contains(variant.variantKey));
        }

        private void EnsureInitialized()
        {
            if (schemaVersion <= 0)
            {
                schemaVersion = 1;
            }

            if (generatedAtUtc == null)
            {
                generatedAtUtc = string.Empty;
            }

            if (parameterName == null)
            {
                parameterName = string.Empty;
            }

            if (menuName == null)
            {
                menuName = string.Empty;
            }

            if (variants == null)
            {
                variants = new List<AvatarVariantMapVariant>();
            }
        }

        private static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("映射文件输出路径不能为空。");
            }

            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            var projectRoot = Directory.GetParent(Application.dataPath);
            if (projectRoot == null)
            {
                throw new InvalidOperationException("无法定位 Unity 项目根目录。");
            }

            return Path.GetFullPath(Path.Combine(projectRoot.FullName, path));
        }

        private static bool StartsWithProjectAssetRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Serializable]
    public class AvatarVariantMapVariant
    {
        public string variantKey = string.Empty;
        public int paramValue;
        public string displayName = string.Empty;
        public string blueprintId = string.Empty;
    }
}
