using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Lanstard.AvatarVariantSwitcher
{
    public enum AvatarReleaseStatus
    {
        Private,
        Public
    }

    [Serializable]
    public class AvatarVariantEntry
    {
        public string displayName = "New Variant";
        public int value;
        public Texture2D menuIcon;
        public string uploadedAvatarName;

        [TextArea(2, 4)]
        public string uploadedDescription;

        public List<GameObject> includedRoots = new List<GameObject>();
        public string uploadedBlueprintId;
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Lanstard/Avatar Variant Switch Config")]
    public class AvatarVariantSwitchConfig : MonoBehaviour
    {
        public VRCAvatarDescriptor avatarDescriptor;
        public VRCExpressionsMenu installTargetMenu;
        public string parameterName = "AvatarVariant";
        public string menuName = "Avatar Switch";
        public int defaultValue;
        public Texture2D thumbnail;
        public string uploadedAvatarNamePrefix = "";

        [TextArea(2, 4)]
        public string uploadedAvatarDescription = "";

        public AvatarReleaseStatus releaseStatus = AvatarReleaseStatus.Private;
        public string mappingFilePath = "Packages/com.lanstard.avatar-variant-switcher/Generated/avatar-switch-map.json";
        public GameObject generatedMenuRoot;
        public List<AvatarVariantEntry> variants = new List<AvatarVariantEntry>();

        public GameObject AvatarRoot => avatarDescriptor != null ? avatarDescriptor.gameObject : gameObject;

        private void Reset()
        {
            avatarDescriptor = GetComponent<VRCAvatarDescriptor>();

            if (avatarDescriptor != null && avatarDescriptor.expressionsMenu != null)
            {
                installTargetMenu = avatarDescriptor.expressionsMenu;
            }
        }
    }
}
