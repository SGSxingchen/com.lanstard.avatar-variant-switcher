using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    public sealed class AvatarVariantUploadSelectWindow : EditorWindow
    {
        private AvatarVariantSwitchConfig _config;
        private List<AvatarVariantEntry> _variantsSnapshot;
        private HashSet<int> _selected;
        private Dictionary<string, string> _mapBlueprintIds;
        private Action<List<int>> _onResult;
        private bool _resultDelivered;
        private Vector2 _scroll;

        public static void Prompt(AvatarVariantSwitchConfig cfg, Action<List<int>> onResult)
        {
            if (cfg == null || cfg.variants == null || cfg.variants.Count == 0)
            {
                try { onResult?.Invoke(null); } catch (Exception ex) { Debug.LogException(ex); }
                return;
            }

            var window = CreateInstance<AvatarVariantUploadSelectWindow>();
            window.titleContent = new GUIContent("选择要上传的装扮");
            window.minSize = new Vector2(440f, 340f);
            window.Init(cfg, onResult);
            window.ShowUtility();
            window.Focus();
        }

        private void Init(AvatarVariantSwitchConfig cfg, Action<List<int>> onResult)
        {
            _config = cfg;
            _variantsSnapshot = new List<AvatarVariantEntry>(cfg.variants);
            _selected = new HashSet<int>(Enumerable.Range(0, _variantsSnapshot.Count));
            _onResult = onResult;
            _resultDelivered = false;
            _mapBlueprintIds = LoadMapBlueprintIds(cfg, _variantsSnapshot);
        }

        private static Dictionary<string, string> LoadMapBlueprintIds(
            AvatarVariantSwitchConfig cfg,
            List<AvatarVariantEntry> variants)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var map = AvatarVariantMap.Read(cfg.outputMapPath);
                foreach (var v in variants)
                {
                    if (v == null || string.IsNullOrWhiteSpace(v.variantKey)) continue;
                    var existing = map.FindByKeyOrParam(v.variantKey, v.paramValue);
                    dict[v.variantKey] = existing != null ? (existing.blueprintId ?? string.Empty) : string.Empty;
                }
            }
            catch
            {
                // Map 读失败就全部当"未上传"显示；上传流程本身仍会走 Validate。
            }
            return dict;
        }

        private void OnGUI()
        {
            if (!IsConfigStillValid())
            {
                DeliverAndClose(null);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                "勾选本次要上传的装扮。未选中的装扮不会被上传，" +
                "但为了保证被选中装扮的 bundle 不带入其他装扮的对象，" +
                "它们在本次上传期间仍会被临时摆成 EditorOnly。",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (var i = 0; i < _variantsSnapshot.Count; i++)
            {
                DrawRow(i);
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            DrawFooter();
        }

        private bool IsConfigStillValid()
        {
            if (_config == null || _config.variants == null) return false;
            if (_config.variants.Count != _variantsSnapshot.Count) return false;
            for (var i = 0; i < _variantsSnapshot.Count; i++)
            {
                if (!ReferenceEquals(_variantsSnapshot[i], _config.variants[i])) return false;
            }
            return true;
        }

        private void DrawRow(int i)
        {
            var variant = _variantsSnapshot[i];
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                var wasSelected = _selected.Contains(i);
                var nowSelected = EditorGUILayout.Toggle(wasSelected, GUILayout.Width(18));
                if (nowSelected != wasSelected)
                {
                    if (nowSelected) _selected.Add(i);
                    else _selected.Remove(i);
                }

                var thumb = variant != null ? variant.thumbnail : null;
                var thumbRect = GUILayoutUtility.GetRect(
                    32, 32, GUILayout.Width(32), GUILayout.Height(32));
                if (thumb != null)
                {
                    GUI.DrawTexture(thumbRect, thumb, ScaleMode.ScaleToFit);
                }
                else
                {
                    EditorGUI.DrawRect(thumbRect, new Color(0.2f, 0.2f, 0.2f, 1f));
                }

                using (new EditorGUILayout.VerticalScope())
                {
                    var displayName = variant != null
                        ? (string.IsNullOrWhiteSpace(variant.displayName) ? "(未命名)" : variant.displayName)
                        : "(空条目)";
                    EditorGUILayout.LabelField(displayName, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(BuildStatusText(variant), EditorStyles.miniLabel);
                }
            }
        }

        private string BuildStatusText(AvatarVariantEntry variant)
        {
            if (variant == null || string.IsNullOrWhiteSpace(variant.variantKey))
            {
                return "未上传";
            }
            if (_mapBlueprintIds != null
                && _mapBlueprintIds.TryGetValue(variant.variantKey, out var bid)
                && !string.IsNullOrWhiteSpace(bid))
            {
                return "已上传：" + bid;
            }
            return "未上传";
        }

        private void DrawFooter()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_selected.Count == 0))
                {
                    if (GUILayout.Button(
                            string.Format("上传选中的 {0} 个", _selected.Count),
                            GUILayout.Width(160), GUILayout.Height(24)))
                    {
                        DeliverAndClose(_selected.OrderBy(g => g).ToList());
                        GUIUtility.ExitGUI();
                    }
                }
                if (GUILayout.Button("取消", GUILayout.Width(80), GUILayout.Height(24)))
                {
                    DeliverAndClose(null);
                    GUIUtility.ExitGUI();
                }
            }
        }

        private void OnDisable()
        {
            // 用户关了窗口但没按任何按钮——按"取消"处理
            DeliverAndClose(null);
        }

        private void DeliverAndClose(List<int> result)
        {
            if (_resultDelivered) return;
            _resultDelivered = true;

            try { _onResult?.Invoke(result); }
            catch (Exception ex) { Debug.LogException(ex); }

            // Close() 会触发 OnDisable 再次进入 DeliverAndClose，_resultDelivered 已 true
            // 所以是 no-op，安全。
            try { Close(); } catch { /* window already closing */ }
        }
    }
}
