using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace Lanstard.AvatarVariantSwitcher
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Lanstard/Avatar 装扮切换配置")]
    public class AvatarVariantSwitchConfig : MonoBehaviour, IEditorOnly
    {
        public VRCAvatarDescriptor avatarDescriptor;
        public string parameterName = "AvatarVariant";
        public string menuName = "Switch Variant";
        public int defaultValue;
        public ReleaseStatus releaseStatus = ReleaseStatus.Private;

        [FormerlySerializedAs("mappingFilePath")]
        public string outputMapPath = "Assets/AvatarVariantSwitcher/Generated/avatar-switch-map.json";

        public string uploadedAvatarNamePrefix = string.Empty;
        public List<AvatarVariantEntry> variants = new List<AvatarVariantEntry>();

        [HideInInspector]
        [FormerlySerializedAs("installTargetMenu")]
        public VRCExpressionsMenu legacyInstallTargetMenu;

        [HideInInspector]
        [FormerlySerializedAs("thumbnail")]
        public Texture2D legacyThumbnail;

        [HideInInspector]
        [FormerlySerializedAs("uploadedAvatarDescription")]
        [TextArea(2, 4)]
        public string legacyUploadedAvatarDescription = string.Empty;

        [NonSerialized]
        public GameObject generatedMenuRoot;

        public GameObject AvatarRoot
        {
            get { return avatarDescriptor != null ? avatarDescriptor.gameObject : gameObject; }
        }

        // null 表示装到 avatar 根 expressions menu。
        // 不要直接返回 avatarDescriptor.expressionsMenu —— MA 的 VirtualMenu 不会把根菜单加进
        // _visitedMenus，把 installer.installTargetMenu 显式指向根菜单会被判成"不属于此 Avatar"。
        public VRCExpressionsMenu InstallTargetMenu
        {
            get
            {
                if (legacyInstallTargetMenu == null) return null;
                if (avatarDescriptor != null && legacyInstallTargetMenu == avatarDescriptor.expressionsMenu)
                {
                    return null;
                }
                return legacyInstallTargetMenu;
            }
        }

        private void Reset()
        {
            avatarDescriptor = GetComponent<VRCAvatarDescriptor>();
        }
    }

    public enum ReleaseStatus
    {
        Private,
        Public
    }
}
