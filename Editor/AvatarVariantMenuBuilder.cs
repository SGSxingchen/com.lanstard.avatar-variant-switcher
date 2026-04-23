using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    public static class AvatarVariantMenuBuilder
    {
        private const string GeneratedMenuRootName = "_AvatarSwitcherMenu";

        public static void Generate(AvatarVariantSwitchConfig cfg)
        {
            var avatarRoot = cfg.AvatarRoot;
            var menuRoot = EnsureChild(avatarRoot.transform, GeneratedMenuRootName);
            ClearChildrenAndNonTransformComponents(menuRoot.gameObject);

            Undo.AddComponent<ModularAvatarMenuInstaller>(menuRoot.gameObject);

            var parameters = Undo.AddComponent<ModularAvatarParameters>(menuRoot.gameObject);
            parameters.parameters = new List<ParameterConfig>
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

            var subMenu = Undo.AddComponent<ModularAvatarMenuItem>(menuRoot.gameObject);
            subMenu.label = ResolveMenuName(cfg);
            subMenu.Control = new VRCExpressionsMenu.Control
            {
                name = subMenu.label,
                type = VRCExpressionsMenu.Control.ControlType.SubMenu
            };
            subMenu.MenuSource = SubmenuSource.Children;

            foreach (var variant in cfg.variants)
            {
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

            cfg.generatedMenuRoot = menuRoot.gameObject;
            EditorUtility.SetDirty(cfg);
            EditorUtility.SetDirty(menuRoot.gameObject);
            EditorSceneManager.MarkSceneDirty(cfg.gameObject.scene);
            AssetDatabase.SaveAssets();
        }

        private static Transform EnsureChild(Transform parent, string childName)
        {
            var child = parent.Find(childName);
            if (child != null)
            {
                return child;
            }

            var go = new GameObject(childName);
            Undo.RegisterCreatedObjectUndo(go, "Create avatar switcher menu root");
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
                if (component is Transform)
                {
                    continue;
                }

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
