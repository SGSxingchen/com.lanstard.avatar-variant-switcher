using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lanstard.AvatarVariantSwitcher;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor.Api;

public static class AvatarVariantSwitchWorkflow
{
    private const string GeneratedMenuRootName = "_AvatarVariantMenu";
    private static bool _isBusy;

    public static bool IsBusy => _isBusy;

    public static void GenerateMenu(AvatarVariantSwitchConfig config)
    {
        if (!TryValidateConfig(config, requireThumbnail: false, requireUploadedBlueprintIds: false, out var error))
        {
            EditorUtility.DisplayDialog("Avatar Variant Switcher", error, "OK");
            return;
        }

        var root = EnsureGeneratedMenuRoot(config);
        ClearGeneratedMenuRoot(root);

        var installTarget = config.installTargetMenu != null
            ? config.installTargetMenu
            : config.avatarDescriptor.expressionsMenu;

        var installer = Undo.AddComponent<ModularAvatarMenuInstaller>(root);
        installer.installTargetMenu = installTarget;

        var parameters = Undo.AddComponent<ModularAvatarParameters>(root);
        parameters.parameters = new List<ParameterConfig>
        {
            new ParameterConfig
            {
                nameOrPrefix = config.parameterName.Trim(),
                saved = true,
                syncType = ParameterSyncType.Int,
                localOnly = false,
                defaultValue = config.defaultValue,
                hasExplicitDefaultValue = true
            }
        };

        var submenu = Undo.AddComponent<ModularAvatarMenuItem>(root);
        submenu.label = GetResolvedMenuName(config);
        submenu.Control = new VRCExpressionsMenu.Control
        {
            name = submenu.label,
            type = VRCExpressionsMenu.Control.ControlType.SubMenu
        };
        submenu.MenuSource = SubmenuSource.Children;

        foreach (var entry in config.variants)
        {
            var itemObject = new GameObject(string.IsNullOrWhiteSpace(entry.displayName) ? "Variant" : entry.displayName);
            Undo.RegisterCreatedObjectUndo(itemObject, "Create variant menu item");
            itemObject.transform.SetParent(root.transform, false);

            var menuItem = Undo.AddComponent<ModularAvatarMenuItem>(itemObject);
            menuItem.label = entry.displayName;
            menuItem.automaticValue = false;
            menuItem.Control = new VRCExpressionsMenu.Control
            {
                name = entry.displayName,
                icon = entry.menuIcon,
                type = VRCExpressionsMenu.Control.ControlType.Button,
                parameter = new VRCExpressionsMenu.Control.Parameter
                {
                    name = config.parameterName.Trim()
                },
                value = entry.value
            };
        }

        EditorUtility.SetDirty(root);
        EditorUtility.SetDirty(config);
        MarkSceneDirty(config);
        AssetDatabase.SaveAssets();
    }

    public static void ExportMappingFile(AvatarVariantSwitchConfig config)
    {
        if (!TryValidateConfig(config, requireThumbnail: false, requireUploadedBlueprintIds: true, out var error))
        {
            EditorUtility.DisplayDialog("Avatar Variant Switcher", error, "OK");
            return;
        }

        var outputPath = WriteMappingFile(config);
        EditorUtility.DisplayDialog("Avatar Variant Switcher", $"Mapping file exported to:\n{outputPath}", "OK");
    }

    public static async void StartBatchUpload(AvatarVariantSwitchConfig config)
    {
        if (_isBusy)
        {
            EditorUtility.DisplayDialog("Avatar Variant Switcher", "A batch upload is already running.", "OK");
            return;
        }

        if (!TryValidateConfig(config, requireThumbnail: true, requireUploadedBlueprintIds: false, out var error))
        {
            EditorUtility.DisplayDialog("Avatar Variant Switcher", error, "OK");
            return;
        }

        _isBusy = true;

        try
        {
            GenerateMenu(config);
            await RunBatchUploadInternal(config);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("Avatar Variant Switcher", $"Batch upload failed:\n{ex.Message}", "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            _isBusy = false;
        }
    }

    private static async Task RunBatchUploadInternal(AvatarVariantSwitchConfig config)
    {
        var avatarRoot = config.AvatarRoot;
        var pipelineManager = avatarRoot.GetComponent<PipelineManager>();
        if (pipelineManager == null)
        {
            throw new InvalidOperationException("The avatar root is missing a PipelineManager component.");
        }

        var uniqueRoots = CollectUniqueVariantRoots(config);
        var originalTags = uniqueRoots.ToDictionary(root => root, root => root.tag);
        var originalBlueprintId = pipelineManager.blueprintId;
        var thumbnailPath = ResolveThumbnailPath(config.thumbnail);

        try
        {
            var builder = await EnsureAvatarBuilderAsync();
            builder.SelectAvatar(avatarRoot);

            for (var index = 0; index < config.variants.Count; index++)
            {
                var entry = config.variants[index];
                var title = $"Uploading {index + 1}/{config.variants.Count}";
                EditorUtility.DisplayProgressBar(title, entry.displayName, (float)index / config.variants.Count);

                ApplyVariantTags(uniqueRoots, entry);

                Undo.RecordObject(pipelineManager, "Clear avatar blueprint ID");
                pipelineManager.blueprintId = string.Empty;
                EditorUtility.SetDirty(pipelineManager);
                MarkSceneDirty(config);

                await Task.Delay(200);

                var avatarRecord = CreateAvatarRecord(config, entry);
                await builder.BuildAndUpload(avatarRoot, avatarRecord, thumbnailPath);

                entry.uploadedBlueprintId = pipelineManager.blueprintId;
                EditorUtility.SetDirty(config);

                WriteMappingFile(config);
                await Task.Delay(200);
            }
        }
        finally
        {
            RestoreOriginalTags(originalTags);
            Undo.RecordObject(pipelineManager, "Restore avatar blueprint ID");
            pipelineManager.blueprintId = originalBlueprintId;
            EditorUtility.SetDirty(pipelineManager);
            EditorUtility.SetDirty(config);
            MarkSceneDirty(config);
            AssetDatabase.SaveAssets();
        }
    }

    private static VRCAvatar CreateAvatarRecord(AvatarVariantSwitchConfig config, AvatarVariantEntry entry)
    {
        var avatarName = string.IsNullOrWhiteSpace(entry.uploadedAvatarName)
            ? $"{config.uploadedAvatarNamePrefix}{config.AvatarRoot.name} - {entry.displayName}".Trim()
            : entry.uploadedAvatarName.Trim();

        if (string.IsNullOrWhiteSpace(avatarName))
        {
            avatarName = $"{config.AvatarRoot.name} - {entry.displayName}";
        }

        var description = string.IsNullOrWhiteSpace(entry.uploadedDescription)
            ? config.uploadedAvatarDescription ?? string.Empty
            : entry.uploadedDescription;

        return new VRCAvatar
        {
            ID = string.Empty,
            Name = avatarName,
            Description = description ?? string.Empty,
            Tags = new List<string>(),
            ReleaseStatus = config.releaseStatus == AvatarReleaseStatus.Public ? "public" : "private"
        };
    }

    private static async Task<IVRCSdkAvatarBuilderApi> EnsureAvatarBuilderAsync()
    {
        EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");

        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder))
            {
                if (!APIUser.IsLoggedIn)
                {
                    throw new InvalidOperationException("Please log in through the VRChat SDK panel before starting batch upload.");
                }

                return builder;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException("Could not access the VRChat avatar builder. Open the VRChat SDK panel first.");
    }

    private static GameObject EnsureGeneratedMenuRoot(AvatarVariantSwitchConfig config)
    {
        if (config.generatedMenuRoot != null && config.generatedMenuRoot.transform.parent == config.AvatarRoot.transform)
        {
            return config.generatedMenuRoot;
        }

        var menuRoot = new GameObject(GeneratedMenuRootName);
        Undo.RegisterCreatedObjectUndo(menuRoot, "Create avatar variant menu root");
        menuRoot.transform.SetParent(config.AvatarRoot.transform, false);
        config.generatedMenuRoot = menuRoot;
        EditorUtility.SetDirty(config);
        return menuRoot;
    }

    private static void ClearGeneratedMenuRoot(GameObject root)
    {
        for (var i = root.transform.childCount - 1; i >= 0; i--)
        {
            Undo.DestroyObjectImmediate(root.transform.GetChild(i).gameObject);
        }

        var components = root.GetComponents<Component>();
        foreach (var component in components)
        {
            if (component is Transform)
            {
                continue;
            }

            Undo.DestroyObjectImmediate(component);
        }
    }

    private static List<GameObject> CollectUniqueVariantRoots(AvatarVariantSwitchConfig config)
    {
        return config.variants
            .SelectMany(entry => entry.includedRoots ?? Enumerable.Empty<GameObject>())
            .Where(root => root != null)
            .Distinct()
            .ToList();
    }

    private static void ApplyVariantTags(IEnumerable<GameObject> uniqueRoots, AvatarVariantEntry activeEntry)
    {
        var activeSet = new HashSet<GameObject>((activeEntry.includedRoots ?? new List<GameObject>()).Where(root => root != null));

        foreach (var root in uniqueRoots)
        {
            Undo.RecordObject(root, "Set avatar variant tag");
            root.tag = activeSet.Contains(root) ? "Untagged" : "EditorOnly";
            EditorUtility.SetDirty(root);
        }
    }

    private static void RestoreOriginalTags(IReadOnlyDictionary<GameObject, string> originalTags)
    {
        foreach (var pair in originalTags)
        {
            if (pair.Key == null)
            {
                continue;
            }

            Undo.RecordObject(pair.Key, "Restore avatar variant tag");
            pair.Key.tag = pair.Value;
            EditorUtility.SetDirty(pair.Key);
        }
    }

    private static string ResolveThumbnailPath(Texture2D thumbnail)
    {
        var assetPath = AssetDatabase.GetAssetPath(thumbnail);
        if (string.IsNullOrWhiteSpace(assetPath))
        {
            throw new InvalidOperationException("Thumbnail must be an image asset inside the Unity project.");
        }

        var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new InvalidOperationException("Could not resolve the Unity project root.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Thumbnail file was not found.", fullPath);
        }

        return fullPath;
    }

    private static string WriteMappingFile(AvatarVariantSwitchConfig config)
    {
        var mapping = new AvatarVariantMapFile
        {
            version = 1,
            generatedAtUtc = DateTime.UtcNow.ToString("O"),
            avatarName = config.AvatarRoot.name,
            parameterName = config.parameterName.Trim(),
            menuName = GetResolvedMenuName(config),
            defaultValue = config.defaultValue,
            entries = config.variants.Select(entry => new AvatarVariantMapEntry
            {
                value = entry.value,
                name = entry.displayName,
                blueprintId = entry.uploadedBlueprintId
            }).ToList()
        };

        var outputPath = ResolveOutputPath(config.mappingFilePath);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(outputPath, JsonUtility.ToJson(mapping, true), new UTF8Encoding(false));

        if (outputPath.StartsWith(Application.dataPath, StringComparison.OrdinalIgnoreCase))
        {
            AssetDatabase.Refresh();
        }

        return outputPath;
    }

    private static string ResolveOutputPath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException("Mapping file output path is required.");
        }

        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new InvalidOperationException("Could not resolve the Unity project root.");
        }

        return Path.GetFullPath(Path.Combine(projectRoot, configuredPath));
    }

    private static bool TryValidateConfig(
        AvatarVariantSwitchConfig config,
        bool requireThumbnail,
        bool requireUploadedBlueprintIds,
        out string error)
    {
        if (config == null)
        {
            error = "Config object is missing.";
            return false;
        }

        if (config.avatarDescriptor == null)
        {
            config.avatarDescriptor = config.GetComponent<VRCAvatarDescriptor>();
            EditorUtility.SetDirty(config);
        }

        if (config.avatarDescriptor == null)
        {
            error = "Attach this component to the avatar root, or assign Avatar Descriptor manually.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.parameterName))
        {
            error = "Parameter name cannot be empty.";
            return false;
        }

        if (config.variants == null || config.variants.Count == 0)
        {
            error = "At least one variant entry is required.";
            return false;
        }

        var usedValues = new HashSet<int>();
        foreach (var entry in config.variants)
        {
            if (entry == null)
            {
                error = "The variants list contains a null entry.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(entry.displayName))
            {
                error = "Each variant must have a displayName.";
                return false;
            }

            if (entry.includedRoots == null)
            {
                error = $"Variant '{entry.displayName}' has a null includedRoots list.";
                return false;
            }

            if (!usedValues.Add(entry.value))
            {
                error = $"Duplicate parameter value found: {entry.value}";
                return false;
            }

            foreach (var root in entry.includedRoots.Where(root => root != null))
            {
                if (!root.transform.IsChildOf(config.AvatarRoot.transform))
                {
                    error = $"Object '{root.name}' is not under the current avatar root.";
                    return false;
                }
            }

            if (requireUploadedBlueprintIds && string.IsNullOrWhiteSpace(entry.uploadedBlueprintId))
            {
                error = $"Variant '{entry.displayName}' does not have an uploaded blueprint ID yet.";
                return false;
            }
        }

        var trackedRoots = CollectUniqueVariantRoots(config);
        foreach (var root in trackedRoots)
        {
            if (root == null)
            {
                error = "The variants list contains a missing object reference.";
                return false;
            }
        }

        for (var i = 0; i < trackedRoots.Count; i++)
        {
            for (var j = i + 1; j < trackedRoots.Count; j++)
            {
                if (trackedRoots[i].transform.IsChildOf(trackedRoots[j].transform)
                    || trackedRoots[j].transform.IsChildOf(trackedRoots[i].transform))
                {
                    error = $"Overlapping roots detected: {trackedRoots[i].name} / {trackedRoots[j].name}. Do not put parent and child objects into variant roots at the same time.";
                    return false;
                }
            }
        }

        if (requireThumbnail && config.thumbnail == null)
        {
            error = "A thumbnail is required before batch upload.";
            return false;
        }

        if (requireThumbnail)
        {
            try
            {
                ResolveThumbnailPath(config.thumbnail);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(config.mappingFilePath))
        {
            error = "Mapping file output path is required.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string GetResolvedMenuName(AvatarVariantSwitchConfig config)
    {
        return string.IsNullOrWhiteSpace(config.menuName) ? config.parameterName.Trim() : config.menuName.Trim();
    }

    private static void MarkSceneDirty(AvatarVariantSwitchConfig config)
    {
        if (config != null && config.gameObject.scene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(config.gameObject.scene);
        }
    }

    [Serializable]
    private class AvatarVariantMapFile
    {
        public int version;
        public string generatedAtUtc;
        public string avatarName;
        public string parameterName;
        public string menuName;
        public int defaultValue;
        public List<AvatarVariantMapEntry> entries;
    }

    [Serializable]
    private class AvatarVariantMapEntry
    {
        public int value;
        public string name;
        public string blueprintId;
    }
}
