using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Core;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    public sealed class AvatarVariantTagGuard : IDisposable
    {
        private readonly Dictionary<GameObject, string> _tagSnapshot;
        private readonly Dictionary<GameObject, bool> _activeSelfSnapshot;
        private readonly PipelineManager _pm;
        private readonly string _originalBlueprintId;
        private bool _restored;

        private AvatarVariantTagGuard(
            PipelineManager pm,
            Dictionary<GameObject, string> tagSnapshot,
            Dictionary<GameObject, bool> activeSelfSnapshot)
        {
            _pm = pm;
            _tagSnapshot = tagSnapshot;
            _activeSelfSnapshot = activeSelfSnapshot;
            _originalBlueprintId = pm.blueprintId ?? string.Empty;
        }

        public static AvatarVariantTagGuard Capture(
            PipelineManager pm,
            IEnumerable<GameObject> controlledRoots,
            IEnumerable<GameObject> activeScopedRoots)
        {
            if (pm == null)
            {
                throw new ArgumentNullException("pm");
            }

            var tagSnapshot = new Dictionary<GameObject, string>();
            foreach (var root in controlledRoots ?? Enumerable.Empty<GameObject>())
            {
                if (root == null || tagSnapshot.ContainsKey(root))
                {
                    continue;
                }

                tagSnapshot.Add(root, root.tag);
            }

            var activeSnapshot = new Dictionary<GameObject, bool>();
            foreach (var root in activeScopedRoots ?? Enumerable.Empty<GameObject>())
            {
                if (root == null || activeSnapshot.ContainsKey(root))
                {
                    continue;
                }

                activeSnapshot.Add(root, root.activeSelf);
            }

            return new AvatarVariantTagGuard(pm, tagSnapshot, activeSnapshot);
        }

        public void ApplyActive(HashSet<GameObject> activeSet)
        {
            activeSet = activeSet ?? new HashSet<GameObject>();

            foreach (var root in _tagSnapshot.Keys)
            {
                if (root == null)
                {
                    continue;
                }

                Undo.RecordObject(root, "Set avatar variant tag");
                root.tag = activeSet.Contains(root) ? "Untagged" : "EditorOnly";
                EditorUtility.SetDirty(root);
            }

            foreach (var root in _activeSelfSnapshot.Keys)
            {
                if (root == null)
                {
                    continue;
                }

                var desired = activeSet.Contains(root);
                if (root.activeSelf == desired)
                {
                    continue;
                }

                Undo.RecordObject(root, "Set avatar variant active");
                root.SetActive(desired);
                EditorUtility.SetDirty(root);
            }
        }

        public void SetBlueprintId(string id)
        {
            Undo.RecordObject(_pm, "Set blueprint id");
            _pm.blueprintId = id ?? string.Empty;
            EditorUtility.SetDirty(_pm);
        }

        public void Restore()
        {
            if (_restored)
            {
                return;
            }

            foreach (var pair in _tagSnapshot)
            {
                var root = pair.Key;
                if (root == null)
                {
                    continue;
                }

                Undo.RecordObject(root, "Restore avatar variant tag");
                root.tag = pair.Value;
                EditorUtility.SetDirty(root);
            }

            Undo.RecordObject(_pm, "Restore blueprint id");
            _pm.blueprintId = _originalBlueprintId;
            EditorUtility.SetDirty(_pm);

            foreach (var pair in _activeSelfSnapshot)
            {
                var root = pair.Key;
                if (root == null)
                {
                    continue;
                }

                if (root.activeSelf == pair.Value)
                {
                    continue;
                }

                Undo.RecordObject(root, "Restore avatar variant active");
                root.SetActive(pair.Value);
                EditorUtility.SetDirty(root);
            }

            _restored = true;
        }

        public void Dispose()
        {
            Restore();
        }
    }
}
