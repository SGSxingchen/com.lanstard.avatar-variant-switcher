using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lanstard.AvatarVariantSwitcher
{
    [Serializable]
    public class AvatarVariantEntry
    {
        public string displayName = "New Variant";
        public string variantKey = string.Empty;
        public int paramValue;
        public Texture2D thumbnail;
        public Texture2D menuIcon;
        public string uploadedName = string.Empty;

        [TextArea(2, 4)]
        public string uploadedDescription = string.Empty;

        public List<GameObject> includedRoots = new List<GameObject>();
    }
}
