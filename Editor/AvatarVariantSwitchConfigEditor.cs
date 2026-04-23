using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    [CustomEditor(typeof(AvatarVariantSwitchConfig))]
    public class AvatarVariantSwitchConfigEditor : UnityEditor.Editor
    {
        private ReorderableList _variantsList;
        private SerializedProperty _variantsProperty;

        private void OnEnable()
        {
            _variantsProperty = serializedObject.FindProperty("variants");
            _variantsList = new ReorderableList(serializedObject, _variantsProperty, true, true, true, true);
            _variantsList.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Variants");
            _variantsList.elementHeightCallback = GetElementHeight;
            _variantsList.drawElementCallback = DrawElement;
            _variantsList.onAddCallback = AddVariant;
        }

        public override void OnInspectorGUI()
        {
            var config = (AvatarVariantSwitchConfig)target;
            EnsureVariantKeys(config);

            serializedObject.UpdateIfRequiredOrScript();

            var report = AvatarVariantSwitchWorkflow.BuildValidationReport(config, false);
            DrawValidation(report);

            if (AvatarVariantSwitchWorkflow.IsBusy)
            {
                EditorGUILayout.HelpBox("批量上传进行中，Inspector 已锁定。", MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(AvatarVariantSwitchWorkflow.IsBusy))
            {
                DrawPropertiesExcluding(serializedObject, "m_Script", "variants");
                EditorGUILayout.Space();
                _variantsList.DoLayoutList();
                serializedObject.ApplyModifiedProperties();

                EditorGUILayout.Space();
                DrawPruneUi(config, report);

                EditorGUILayout.Space();
                if (GUILayout.Button("Generate / Refresh Menu"))
                {
                    AvatarVariantSwitchWorkflow.GenerateMenu(config);
                }

                if (GUILayout.Button("Batch Upload All Variants"))
                {
                    AvatarVariantSwitchWorkflow.StartBatchUpload(config);
                }

                if (GUILayout.Button("Write Mapping File"))
                {
                    AvatarVariantSwitchWorkflow.WriteMap(config);
                }
            }
        }

        private static void EnsureVariantKeys(AvatarVariantSwitchConfig config)
        {
            if (config == null || config.variants == null)
            {
                return;
            }

            var changed = false;
            for (var i = 0; i < config.variants.Count; i++)
            {
                var variant = config.variants[i];
                if (variant == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(variant.variantKey))
                {
                    continue;
                }

                variant.variantKey = Guid.NewGuid().ToString("N");
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            EditorUtility.SetDirty(config);
        }

        private void DrawValidation(AvatarVariantSwitchWorkflow.ValidationReport report)
        {
            if (report.Errors.Count > 0)
            {
                EditorGUILayout.HelpBox(string.Join("\n", report.Errors.ToArray()), MessageType.Error);
            }

            if (report.Warnings.Count > 0)
            {
                EditorGUILayout.HelpBox(string.Join("\n", report.Warnings.ToArray()), MessageType.Warning);
            }
        }

        private void DrawPruneUi(AvatarVariantSwitchConfig config, AvatarVariantSwitchWorkflow.ValidationReport report)
        {
            if (report.StaleVariants.Count == 0)
            {
                return;
            }

            EditorGUILayout.LabelField(
                string.Format("映射文件里还有 {0} 条当前配置已不存在的旧记录。", report.StaleVariants.Count),
                EditorStyles.miniLabel);

            if (report.StaleVariants.Count <= 3)
            {
                foreach (var stale in report.StaleVariants)
                {
                    var label = string.IsNullOrWhiteSpace(stale.displayName) ? stale.variantKey : stale.displayName;
                    EditorGUILayout.LabelField("• " + label, EditorStyles.miniLabel);
                }
            }

            if (GUILayout.Button("Prune Stale Map Keys"))
            {
                AvatarVariantSwitchWorkflow.PruneStaleMapKeys(config);
            }
        }

        private void AddVariant(ReorderableList list)
        {
            var index = _variantsProperty.arraySize;
            _variantsProperty.arraySize++;
            serializedObject.ApplyModifiedProperties();
            serializedObject.UpdateIfRequiredOrScript();

            var element = _variantsProperty.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("displayName").stringValue = "New Variant";
            element.FindPropertyRelative("variantKey").stringValue = Guid.NewGuid().ToString("N");
            element.FindPropertyRelative("paramValue").intValue = FindNextParamValue();
            element.FindPropertyRelative("thumbnail").objectReferenceValue = null;
            element.FindPropertyRelative("menuIcon").objectReferenceValue = null;
            element.FindPropertyRelative("uploadedName").stringValue = string.Empty;
            element.FindPropertyRelative("uploadedDescription").stringValue = string.Empty;
            element.FindPropertyRelative("includedRoots").arraySize = 0;

            serializedObject.ApplyModifiedProperties();
        }

        private int FindNextParamValue()
        {
            var next = 0;
            for (var i = 0; i < _variantsProperty.arraySize; i++)
            {
                var value = _variantsProperty.GetArrayElementAtIndex(i).FindPropertyRelative("paramValue").intValue;
                if (value >= next)
                {
                    next = value + 1;
                }
            }

            return next;
        }

        private float GetElementHeight(int index)
        {
            var element = _variantsProperty.GetArrayElementAtIndex(index);
            var spacing = EditorGUIUtility.standardVerticalSpacing;
            var total = spacing * 9f;

            total += EditorGUI.GetPropertyHeight(element.FindPropertyRelative("displayName"), true);
            total += EditorGUI.GetPropertyHeight(element.FindPropertyRelative("variantKey"), true);
            total += EditorGUI.GetPropertyHeight(element.FindPropertyRelative("paramValue"), true);
            total += EditorGUI.GetPropertyHeight(element.FindPropertyRelative("thumbnail"), true);
            total += EditorGUI.GetPropertyHeight(element.FindPropertyRelative("menuIcon"), true);
            total += EditorGUI.GetPropertyHeight(element.FindPropertyRelative("uploadedName"), true);
            total += EditorGUI.GetPropertyHeight(element.FindPropertyRelative("uploadedDescription"), true);
            total += EditorGUI.GetPropertyHeight(element.FindPropertyRelative("includedRoots"), true);

            return total + 8f;
        }

        private void DrawElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            var element = _variantsProperty.GetArrayElementAtIndex(index);
            rect.y += 2f;

            DrawProperty(ref rect, element.FindPropertyRelative("displayName"));
            DrawProperty(ref rect, element.FindPropertyRelative("variantKey"));
            DrawProperty(ref rect, element.FindPropertyRelative("paramValue"));
            DrawProperty(ref rect, element.FindPropertyRelative("thumbnail"));
            DrawProperty(ref rect, element.FindPropertyRelative("menuIcon"));
            DrawProperty(ref rect, element.FindPropertyRelative("uploadedName"));
            DrawProperty(ref rect, element.FindPropertyRelative("uploadedDescription"));
            DrawProperty(ref rect, element.FindPropertyRelative("includedRoots"));
        }

        private static void DrawProperty(ref Rect rect, SerializedProperty property)
        {
            var height = EditorGUI.GetPropertyHeight(property, true);
            var fieldRect = new Rect(rect.x, rect.y, rect.width, height);
            EditorGUI.PropertyField(fieldRect, property, true);
            rect.y += height + EditorGUIUtility.standardVerticalSpacing;
        }
    }
}
