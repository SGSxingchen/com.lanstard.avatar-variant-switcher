using System;
using UnityEngine;

namespace Lanstard.AvatarVariantSwitcher
{
    [Serializable]
    public class AvatarVariantAccessory
    {
        public GameObject target;
        public string displayName = string.Empty;
        public Texture2D icon;
        public bool defaultOn = true;
    }
}
