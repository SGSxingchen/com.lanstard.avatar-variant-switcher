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

        // CustomEditor 用来记住每个 includedRoots 已经被扫过一次，避免反复追加用户手动删掉的条目。
        // 被移出 includedRoots 后自动从这里清掉，重新加回则会再次触发一次扫描。
        [HideInInspector]
        public List<GameObject> autoScannedRoots = new List<GameObject>();

        [HideInInspector]
        [FormerlySerializedAs("uploadedBlueprintId")]
        public string legacyUploadedBlueprintId = string.Empty;
    }
}
