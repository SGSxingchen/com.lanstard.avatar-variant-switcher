using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Lanstard.AvatarVariantSwitcher
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Lanstard/Avatar Variant Switch Config")]
    public class AvatarVariantSwitchConfig : MonoBehaviour
    {
        public VRCAvatarDescriptor avatarDescriptor;
        public string parameterName = "AvatarVariant";
        public string menuName = "Switch Variant";
        public int defaultValue;
        public ReleaseStatus releaseStatus = ReleaseStatus.Private;
        public string outputMapPath = "Assets/AvatarVariantSwitcher/Generated/avatar-switch-map.json";
        public string uploadedAvatarNamePrefix = string.Empty;
        public List<AvatarVariantEntry> variants = new List<AvatarVariantEntry>();

        [NonSerialized]
        public GameObject generatedMenuRoot;

        public GameObject AvatarRoot
        {
            get { return avatarDescriptor != null ? avatarDescriptor.gameObject : gameObject; }
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
