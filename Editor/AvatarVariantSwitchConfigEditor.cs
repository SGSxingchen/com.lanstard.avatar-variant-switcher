using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    [CustomEditor(typeof(AvatarVariantSwitchConfig))]
    public class AvatarVariantSwitchConfigEditor : UnityEditor.Editor
    {
        // 顶层字段（除 variants 外）的中文标签与提示
        private static readonly FieldLabel[] ConfigFields = new[]
        {
            new FieldLabel("avatarDescriptor",        "主 Avatar Descriptor",   "指向当前 avatar 的 VRCAvatarDescriptor；会自动从本物体上抓取。"),
            new FieldLabel("parameterName",           "参数名",                 "Int 类型 Expression Parameter 的名称，所有变体共享这一个参数。"),
            new FieldLabel("menuName",                "子菜单标题",             "生成的 SubMenu 在 Expression Menu 里显示的名字。"),
            new FieldLabel("defaultValue",            "默认值",                 "avatar 进场时参数的初始值；必须等于某一个变体的 paramValue。"),
            new FieldLabel("releaseStatus",           "发布状态",               "Private / Public，应用于所有变体的上传。"),
            new FieldLabel("outputMapPath",           "映射文件输出路径",       "映射 JSON 的保存位置；建议放到 Assets/ 下，不要放到只读的 Packages/。"),
            new FieldLabel("uploadedAvatarNamePrefix","上传名称前缀",           "可选；当变体的 uploadedName 为空时，用 \"前缀 + avatarRoot 名 - 变体名\" 拼出上传名。"),
        };

        // 每个变体的中文标签
        private static readonly FieldLabel[] EntryFields = new[]
        {
            new FieldLabel("displayName",        "显示名称",      "菜单按钮文本，也是默认的上传名的一部分。"),
            new FieldLabel("variantKey",         "稳定标识",      "首次创建自动生成的 GUID，用来和映射文件对应；请不要手动修改。"),
            new FieldLabel("paramValue",         "参数值",        "整数且在所有变体里唯一；菜单按钮按下会把 parameterName 设成这个值。"),
            new FieldLabel("thumbnail",          "缩略图",        "首次上传必填；必须是项目资源里的 Texture2D。"),
            new FieldLabel("menuIcon",           "菜单图标 (可选)","Expression Menu 按钮上显示的图标。"),
            new FieldLabel("uploadedName",       "上传名称 (可选)","留空则自动拼接；填了就用这个作为 VRChat 上该 blueprint 的名字。"),
            new FieldLabel("uploadedDescription","上传描述 (可选)","显示在 avatar 页面的描述文本。"),
            new FieldLabel("includedRoots",      "保留的子物体",  "该变体需要保留为 Untagged 的装扮根节点；其他变体的受控 roots 会被设为 EditorOnly。不得包含 _AvatarSwitcherMenu 或其子物体。"),
        };

        private ReorderableList _variantsList;
        private SerializedProperty _variantsProperty;

        private void OnEnable()
        {
            _variantsProperty = serializedObject.FindProperty("variants");
            _variantsList = new ReorderableList(serializedObject, _variantsProperty, true, true, true, true);
            _variantsList.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "变体列表");
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
                foreach (var field in ConfigFields)
                {
                    var prop = serializedObject.FindProperty(field.Name);
                    if (prop == null) continue;
                    EditorGUILayout.PropertyField(prop, new GUIContent(field.Label, field.Tooltip), true);
                }

                EditorGUILayout.Space();
                _variantsList.DoLayoutList();
                serializedObject.ApplyModifiedProperties();

                EditorGUILayout.Space();
                DrawPruneUi(config, report);

                EditorGUILayout.Space();
                if (GUILayout.Button("生成 / 刷新菜单"))
                {
                    AvatarVariantSwitchWorkflow.GenerateMenu(config);
                }

                if (GUILayout.Button("批量上传所有变体"))
                {
                    AvatarVariantSwitchWorkflow.StartBatchUpload(config);
                }

                if (GUILayout.Button("写入映射文件"))
                {
                    AvatarVariantSwitchWorkflow.WriteMap(config);
                }
            }
        }

        private static void EnsureVariantKeys(AvatarVariantSwitchConfig config)
        {
            if (config == null || config.variants == null) return;

            var changed = false;
            for (var i = 0; i < config.variants.Count; i++)
            {
                var variant = config.variants[i];
                if (variant == null) continue;
                if (!string.IsNullOrWhiteSpace(variant.variantKey)) continue;
                variant.variantKey = Guid.NewGuid().ToString("N");
                changed = true;
            }

            if (changed) EditorUtility.SetDirty(config);
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
            if (report.StaleVariants.Count == 0) return;

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

            if (GUILayout.Button("清理旧映射记录"))
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
            element.FindPropertyRelative("displayName").stringValue = "新变体";
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
                if (value >= next) next = value + 1;
            }
            return next;
        }

        private float GetElementHeight(int index)
        {
            var element = _variantsProperty.GetArrayElementAtIndex(index);
            var spacing = EditorGUIUtility.standardVerticalSpacing;
            var total = spacing * (EntryFields.Length + 1);
            foreach (var field in EntryFields)
            {
                var prop = element.FindPropertyRelative(field.Name);
                if (prop == null) continue;
                total += EditorGUI.GetPropertyHeight(prop, true);
            }
            return total + 8f;
        }

        private void DrawElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            var element = _variantsProperty.GetArrayElementAtIndex(index);
            rect.y += 2f;
            foreach (var field in EntryFields)
            {
                var prop = element.FindPropertyRelative(field.Name);
                if (prop == null) continue;
                var height = EditorGUI.GetPropertyHeight(prop, true);
                var fieldRect = new Rect(rect.x, rect.y, rect.width, height);
                EditorGUI.PropertyField(fieldRect, prop, new GUIContent(field.Label, field.Tooltip), true);
                rect.y += height + EditorGUIUtility.standardVerticalSpacing;
            }
        }

        private readonly struct FieldLabel
        {
            public readonly string Name;
            public readonly string Label;
            public readonly string Tooltip;

            public FieldLabel(string name, string label, string tooltip)
            {
                Name = name;
                Label = label;
                Tooltip = tooltip;
            }
        }
    }
}
