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

        public VRCExpressionsMenu InstallTargetMenu
        {
            get
            {
                if (legacyInstallTargetMenu != null)
                {
                    return legacyInstallTargetMenu;
                }

                return avatarDescriptor != null ? avatarDescriptor.expressionsMenu : null;
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
