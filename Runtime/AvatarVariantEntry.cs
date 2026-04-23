using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Lanstard.AvatarVariantSwitcher
{
    [Serializable]
    public class AvatarVariantEntry
    {
        public string displayName = "New Variant";
        public string variantKey = string.Empty;

        [FormerlySerializedAs("value")]
        public int paramValue;

        public Texture2D thumbnail;
        public Texture2D menuIcon;

        [FormerlySerializedAs("uploadedAvatarName")]
        public string uploadedName = string.Empty;

        [TextArea(2, 4)]
        public string uploadedDescription = string.Empty;

        public List<GameObject> includedRoots = new List<GameObject>();

        public List<AvatarVariantAccessory> accessories = new List<AvatarVariantAccessory>();

        [HideInInspector]
        [FormerlySerializedAs("uploadedBlueprintId")]
        public string legacyUploadedBlueprintId = string.Empty;
    }
}
