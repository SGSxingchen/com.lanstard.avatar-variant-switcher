using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    public static class AvatarVariantMenuBuilder
    {
        public const string GeneratedMenuRootName = "_AvatarSwitcherMenu";
        public const string AccessoriesMenuRootName = "_AccessoriesMenu";
        public const string AccessoriesSubMenuLabel = "配件";
        private const string LegacyGeneratedMenuRootName = "_AvatarVariantMenu";
        private const string AccessoryGameObjectPrefix = "Acc_";
        private const int VariantKeyPrefixLength = 8;

        public static void Generate(AvatarVariantSwitchConfig cfg)
        {
            var avatarRoot = cfg.AvatarRoot;
            RemoveLegacyRoot(avatarRoot.transform);
            var menuRoot = EnsureChild(avatarRoot.transform, GeneratedMenuRootName);
            ClearChildrenAndNonTransformComponents(menuRoot.gameObject);

            var installer = Undo.AddComponent<ModularAvatarMenuInstaller>(menuRoot.gameObject);
            installer.installTargetMenu = cfg.InstallTargetMenu;

            // 主参数 + 每个 accessory 的 bool 参数全部集中在这里声明
            var parameters = Undo.AddComponent<ModularAvatarParameters>(menuRoot.gameObject);
            parameters.parameters = BuildAllParameters(cfg);

            // "切换装扮" 子菜单
            var switchSub = Undo.AddComponent<ModularAvatarMenuItem>(menuRoot.gameObject);
            switchSub.label = ResolveMenuName(cfg);
            switchSub.Control = new VRCExpressionsMenu.Control
            {
                name = switchSub.label,
                type = VRCExpressionsMenu.Control.ControlType.SubMenu
            };
            switchSub.MenuSource = SubmenuSource.Children;

            foreach (var variant in cfg.variants)
            {
                if (variant == null) continue;
                var itemName = string.IsNullOrWhiteSpace(variant.displayName) ? "Variant" : variant.displayName;
                var item = new GameObject(itemName);
                Undo.RegisterCreatedObjectUndo(item, "Create variant menu item");
                item.transform.SetParent(menuRoot, false);

                var menuItem = Undo.AddComponent<ModularAvatarMenuItem>(item);
                menuItem.label = variant.displayName;
                menuItem.automaticValue = false;
                menuItem.Control = new VRCExpressionsMenu.Control
                {
                    name = variant.displayName,
                    icon = variant.menuIcon,
                    type = VRCExpressionsMenu.Control.ControlType.Button,
                    parameter = new VRCExpressionsMenu.Control.Parameter
                    {
                        name = cfg.parameterName.Trim()
                    },
                    value = variant.paramValue
                };
            }

            // 配件主 SubMenu（所有装扮的配件 toggle 铺在它下面，靠 tag 开关区分哪个装扮露出）
            BuildAccessoryMenu(cfg, menuRoot);

            cfg.generatedMenuRoot = menuRoot.gameObject;
            EditorUtility.SetDirty(cfg);
            EditorUtility.SetDirty(menuRoot.gameObject);
            EditorSceneManager.MarkSceneDirty(cfg.gameObject.scene);
            AssetDatabase.SaveAssets();
        }

        private static void BuildAccessoryMenu(AvatarVariantSwitchConfig cfg, Transform menuRoot)
        {
            if (cfg.variants == null || !cfg.variants.Any(v => v != null && v.accessories != null && v.accessories.Count > 0))
                return;

            var accRoot = EnsureChild(menuRoot, AccessoriesMenuRootName);
            ClearChildrenAndNonTransformComponents(accRoot.gameObject);

            var accSub = Undo.AddComponent<ModularAvatarMenuItem>(accRoot.gameObject);
            accSub.label = AccessoriesSubMenuLabel;
            accSub.Control = new VRCExpressionsMenu.Control
            {
                name = AccessoriesSubMenuLabel,
                type = VRCExpressionsMenu.Control.ControlType.SubMenu
            };
            accSub.MenuSource = SubmenuSource.Children;

            foreach (var variant in cfg.variants)
            {
                if (variant == null || variant.accessories == null) continue;
                for (int i = 0; i < variant.accessories.Count; i++)
                {
                    var acc = variant.accessories[i];
                    if (acc == null || acc.target == null) continue;

                    var label = string.IsNullOrWhiteSpace(acc.displayName) ? acc.target.name : acc.displayName;
                    var goName = BuildAccessoryGameObjectName(variant.variantKey, i, label);
                    var go = new GameObject(goName);
                    Undo.RegisterCreatedObjectUndo(go, "Create accessory menu item");
                    go.transform.SetParent(accRoot, false);

                    var mi = Undo.AddComponent<ModularAvatarMenuItem>(go);
                    mi.label = label;
                    mi.automaticValue = false;
                    mi.isSynced = true;
                    mi.isSaved = true;
                    mi.isDefault = acc.defaultOn;
                    mi.Control = new VRCExpressionsMenu.Control
                    {
                        name = label,
                        icon = acc.icon,
                        type = VRCExpressionsMenu.Control.ControlType.Toggle,
                        parameter = new VRCExpressionsMenu.Control.Parameter
                        {
                            name = BuildAccessoryParameterName(variant.variantKey, i)
                        },
                        value = 1
                    };

                    var toggle = Undo.AddComponent<ModularAvatarObjectToggle>(go);
                    toggle.Objects = new List<ToggledObject>
                    {
                        new ToggledObject
                        {
                            Object = BuildAvatarReference(cfg.AvatarRoot, acc.target),
                            Active = true
                        }
                    };
                }
            }
        }

        public static List<ParameterConfig> BuildAllParameters(AvatarVariantSwitchConfig cfg)
        {
            var list = new List<ParameterConfig>
            {
                new ParameterConfig
                {
                    nameOrPrefix = cfg.parameterName.Trim(),
                    syncType = ParameterSyncType.Int,
                    localOnly = false,
                    saved = true,
                    hasExplicitDefaultValue = true,
                    defaultValue = cfg.defaultValue
                }
            };

            if (cfg.variants == null) return list;
            foreach (var variant in cfg.variants)
            {
                if (variant == null || variant.accessories == null) continue;
                for (int i = 0; i < variant.accessories.Count; i++)
                {
                    var acc = variant.accessories[i];
                    if (acc == null || acc.target == null) continue;
                    list.Add(new ParameterConfig
                    {
                        nameOrPrefix = BuildAccessoryParameterName(variant.variantKey, i),
                        syncType = ParameterSyncType.Bool,
                        localOnly = false,
                        saved = true,
                        hasExplicitDefaultValue = true,
                        defaultValue = acc.defaultOn ? 1f : 0f
                    });
                }
            }
            return list;
        }

        public static IEnumerable<GameObject> EnumerateAllAccessoryMenuGameObjects(AvatarVariantSwitchConfig cfg)
        {
            var accRoot = FindAccessoriesMenuRoot(cfg);
            if (accRoot == null) yield break;
            for (int i = 0; i < accRoot.childCount; i++)
            {
                var child = accRoot.GetChild(i);
                if (child != null && IsAccessoryGameObject(child.gameObject))
                    yield return child.gameObject;
            }
        }

        public static IEnumerable<GameObject> EnumerateAccessoryMenuGameObjectsFor(AvatarVariantSwitchConfig cfg, string variantKey)
        {
            if (string.IsNullOrEmpty(variantKey)) yield break;
            var prefix = BuildAccessoryGameObjectPrefix(variantKey);
            foreach (var go in EnumerateAllAccessoryMenuGameObjects(cfg))
            {
                if (go != null && go.name.StartsWith(prefix, StringComparison.Ordinal))
                    yield return go;
            }
        }

        public static Transform FindAccessoriesMenuRoot(AvatarVariantSwitchConfig cfg)
        {
            if (cfg == null || cfg.AvatarRoot == null) return null;
            var menu = cfg.AvatarRoot.transform.Find(GeneratedMenuRootName);
            if (menu == null) return null;
            return menu.Find(AccessoriesMenuRootName);
        }

        public static bool IsUnderMenuRoot(GameObject candidate, AvatarVariantSwitchConfig cfg)
        {
            if (candidate == null || cfg == null || cfg.AvatarRoot == null) return false;
            var menu = cfg.AvatarRoot.transform.Find(GeneratedMenuRootName);
            if (menu == null) return false;
            return candidate.transform == menu || candidate.transform.IsChildOf(menu);
        }

        public static string BuildAccessoryParameterName(string variantKey, int accessoryIndex)
        {
            var keyPart = SafePrefix(variantKey);
            return $"Acc_{keyPart}_{accessoryIndex}";
        }

        public static string BuildAccessoryGameObjectName(string variantKey, int accessoryIndex, string displayName)
        {
            var keyPart = SafePrefix(variantKey);
            var safeLabel = SanitizeForGameObjectName(displayName);
            return $"{AccessoryGameObjectPrefix}{keyPart}_{accessoryIndex}_{safeLabel}";
        }

        public static string BuildAccessoryGameObjectPrefix(string variantKey)
        {
            var keyPart = SafePrefix(variantKey);
            return $"{AccessoryGameObjectPrefix}{keyPart}_";
        }

        public static bool IsAccessoryGameObject(GameObject go)
        {
            return go != null && go.name.StartsWith(AccessoryGameObjectPrefix, StringComparison.Ordinal);
        }

        private static string SafePrefix(string variantKey)
        {
            if (string.IsNullOrEmpty(variantKey)) return "na";
            return variantKey.Length <= VariantKeyPrefixLength
                ? variantKey
                : variantKey.Substring(0, VariantKeyPrefixLength);
        }

        private static string SanitizeForGameObjectName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "acc";
            var trimmed = name.Trim();
            if (trimmed.Length > 24) trimmed = trimmed.Substring(0, 24);
            return trimmed;
        }

        private static AvatarObjectReference BuildAvatarReference(GameObject avatarRoot, GameObject target)
        {
            var reference = new AvatarObjectReference();
            reference.Set(target);
            return reference;
        }

        private static void RemoveLegacyRoot(Transform avatarRoot)
        {
            if (avatarRoot == null) return;
            var legacyRoot = avatarRoot.Find(LegacyGeneratedMenuRootName);
            if (legacyRoot == null) return;
            Undo.DestroyObjectImmediate(legacyRoot.gameObject);
        }

        private static Transform EnsureChild(Transform parent, string childName)
        {
            var child = parent.Find(childName);
            if (child != null) return child;
            var go = new GameObject(childName);
            Undo.RegisterCreatedObjectUndo(go, $"Create {childName}");
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        private static void ClearChildrenAndNonTransformComponents(GameObject root)
        {
            for (var i = root.transform.childCount - 1; i >= 0; i--)
            {
                Undo.DestroyObjectImmediate(root.transform.GetChild(i).gameObject);
            }

            var components = root.GetComponents<Component>();
            foreach (var component in components)
            {
                if (component is Transform) continue;
                Undo.DestroyObjectImmediate(component);
            }
        }

        private static string ResolveMenuName(AvatarVariantSwitchConfig cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.menuName))
            {
                return cfg.menuName.Trim();
            }
            return cfg.parameterName.Trim();
        }
    }
}
