using System;
using System.Collections.Generic;
using System.Linq;
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
            new FieldLabel("parameterName",           "参数名",                 "Int 类型 Expression Parameter 的名称，所有装扮共享这一个参数。"),
            new FieldLabel("menuName",                "子菜单标题",             "生成的 SubMenu 在 Expression Menu 里显示的名字。"),
            new FieldLabel("defaultValue",            "默认值",                 "avatar 进场时参数的初始值；必须等于某一个装扮的 paramValue。"),
            new FieldLabel("releaseStatus",           "发布状态",               "Private / Public，应用于所有装扮的上传。"),
            new FieldLabel("outputMapPath",           "映射文件输出路径",       "映射 JSON 的保存位置；建议放到 Assets/ 下，不要放到只读的 Packages/。"),
            new FieldLabel("uploadedAvatarNamePrefix","上传名称前缀",           "可选；当装扮的 uploadedName 为空时，用 \"前缀 + avatarRoot 名 - 装扮名\" 拼出上传名。"),
        };

        // 每个装扮的中文标签
        private static readonly FieldLabel[] EntryFields = new[]
        {
            new FieldLabel("displayName",        "显示名称",      "菜单按钮文本，也是默认的上传名的一部分。"),
            new FieldLabel("variantKey",         "稳定标识",      "首次创建自动生成的 GUID，用来和映射文件对应；请不要手动修改。"),
            new FieldLabel("paramValue",         "参数值",        "整数且在所有装扮里唯一；菜单按钮按下会把 parameterName 设成这个值。"),
            new FieldLabel("thumbnail",          "缩略图",        "首次上传必填；必须是项目资源里的 Texture2D。"),
            new FieldLabel("menuIcon",           "菜单图标 (可选)","Expression Menu 按钮上显示的图标。"),
            new FieldLabel("uploadedName",       "上传名称 (可选)","留空则自动拼接；填了就用这个作为 VRChat 上该 blueprint 的名字。"),
            new FieldLabel("uploadedDescription","上传描述 (可选)","显示在 avatar 页面的描述文本。"),
            new FieldLabel("includedRoots",      "在这里拖入这个装扮包含的衣服/配件",  "本装扮上传时要保留的衣服、配件根物体（会被设为 Untagged 进入打包）。\n其他装扮的衣服会被本插件设为 EditorOnly 从这次包里排除——不是从场景里删除，只是这一轮上传不带它。\n主体（Body / Hair / 面部 / 骨骼等）【不要】拖进来，不放就对了，插件完全不碰它们，它们会跟随每一次上传。\n也不要放 _AvatarSwitcherMenu 或它的子物体。"),
            new FieldLabel("accessories",        "这个装扮的配件菜单（可选）",      "给这套装扮生成一组 Toggle 菜单项（帽子 / 眼镜 / 项链…）。每项：\n • target: 要开关的 GameObject（通常是 includedRoots 里物体的子物体）。\n • displayName: 菜单按钮文本；留空则用 target 的名字。\n • icon: 可选图标。\n • defaultOn: 打开此装扮时默认开还是关。\n\n拖一个新的 root 到 includedRoots 后，它的直接子物体会被自动追加进来（首次一次性扫描）。不想要的手动删掉，删掉后不会再自动加回。如果后来你在场景里给某个 root 加了新的子物体，点下面的「强制重扫」手动拾取。"),
        };

        private ReorderableList _variantsList;
        private SerializedProperty _variantsProperty;

        private void OnEnable()
        {
            _variantsProperty = serializedObject.FindProperty("variants");
            _variantsList = new ReorderableList(serializedObject, _variantsProperty, true, true, true, true);
            _variantsList.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "装扮列表");
            _variantsList.elementHeightCallback = GetElementHeight;
            _variantsList.drawElementCallback = DrawElement;
            _variantsList.onAddCallback = AddVariant;
        }

        private const string HelpFoldoutKey = "Lanstard.AvatarVariantSwitcher.ShowHelp";

        public override void OnInspectorGUI()
        {
            var config = (AvatarVariantSwitchConfig)target;
            EnsureVariantKeys(config);
            AutoScanNewRoots(config);

            serializedObject.UpdateIfRequiredOrScript();

            DrawHelp();

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

                if (GUILayout.Button("批量上传所有装扮"))
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

        // 每次 Inspector 刷新时比较每个装扮的 includedRoots 与 autoScannedRoots:
        //   - 从 includedRoots 新加入、还没扫过的 root → 自动把它的直接子物体追加到 accessories
        //   - 已经从 includedRoots 移除的 root → 从 autoScannedRoots 里剔除（下次再加回去可以重扫）
        // accessories 本身永远非破坏性追加；用户手动删掉的不会被自动加回来。
        private static void AutoScanNewRoots(AvatarVariantSwitchConfig config)
        {
            if (config == null || config.variants == null) return;

            var changed = false;
            foreach (var entry in config.variants)
            {
                if (entry == null) continue;
                entry.includedRoots ??= new List<GameObject>();
                entry.accessories ??= new List<AvatarVariantAccessory>();
                entry.autoScannedRoots ??= new List<GameObject>();

                // 剔除 autoScannedRoots 里已经不在 includedRoots 里的条目（被用户删掉的 root）
                var includedSet = new HashSet<GameObject>(entry.includedRoots.Where(r => r != null));
                var beforeCount = entry.autoScannedRoots.Count;
                entry.autoScannedRoots.RemoveAll(r => r == null || !includedSet.Contains(r));
                if (entry.autoScannedRoots.Count != beforeCount) changed = true;

                // 找出新加入还没扫过的 root，扫它们的直接子物体
                var scannedSet = new HashSet<GameObject>(entry.autoScannedRoots.Where(r => r != null));
                var existingTargets = new HashSet<GameObject>();
                foreach (var acc in entry.accessories)
                {
                    if (acc != null && acc.target != null) existingTargets.Add(acc.target);
                }

                foreach (var root in entry.includedRoots)
                {
                    if (root == null) continue;
                    if (scannedSet.Contains(root)) continue;

                    for (int ci = 0; ci < root.transform.childCount; ci++)
                    {
                        var child = root.transform.GetChild(ci).gameObject;
                        if (child == null) continue;
                        if (existingTargets.Contains(child)) continue;
                        entry.accessories.Add(new AvatarVariantAccessory
                        {
                            target = child,
                            displayName = child.name,
                            defaultOn = true
                        });
                        existingTargets.Add(child);
                        changed = true;
                    }

                    entry.autoScannedRoots.Add(root);
                    scannedSet.Add(root);
                    changed = true;
                }
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

        private static void DrawHelp()
        {
            var open = EditorPrefs.GetBool(HelpFoldoutKey, true);
            var newOpen = EditorGUILayout.Foldout(open, "这个插件怎么用？（点我展开/折叠）", true, EditorStyles.foldoutHeader);
            if (newOpen != open)
            {
                EditorPrefs.SetBool(HelpFoldoutKey, newOpen);
            }
            if (!newOpen) return;

            EditorGUILayout.HelpBox(
                "你现在大概有一个 avatar，里面塞了很多套衣服/配件。这个插件把「一次只上传一套衣服」的流程自动化：\n\n" +
                "  ▸ 你只配一次【装扮列表】——每个「装扮」= 一套搭配，把这套要包含的衣服/配件拖进对应条目里。\n" +
                "  ▸ 点【批量上传】，插件会对同一个 avatar 反复上传 N 次：\n" +
                "      - 每次把当前装扮下拖入的衣服/配件设成 Untagged（进包）\n" +
                "      - 其他装扮下拖入的衣服/配件设成 EditorOnly（这次不进包，但场景不动）\n" +
                "      - 得到一个独立的 blueprint id，写进映射文件\n" +
                "  ▸ 批量结束后，场景里的 tag 和 blueprintId 会完整恢复，不会污染你的工程。\n" +
                "  ▸ 游戏里把每个 blueprint 加入收藏夹，OSC 工具会监听参数变化自动切 avatar。\n\n" +
                "举例：avatar 下面有 Body / Hair / 日常服 / 战斗服 / 泳装 四类物体，你想做 3 套切换，配置大概是：\n" +
                "  • 装扮 A：显示名=「日常」，参数值=0，保留=「日常服」\n" +
                "  • 装扮 B：显示名=「战斗」，参数值=1，保留=「战斗服」\n" +
                "  • 装扮 C：显示名=「泳装」，参数值=2，保留=「泳装」\n" +
                "Body / Hair / 面部 / 骨骼 这些主体物体【不要】拖进任何装扮——插件只会管拖进来的物体的 tag，没拖的会跟着每一次上传一起进包，不受影响。",
                MessageType.Info);
            EditorGUILayout.Space();
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
            element.FindPropertyRelative("displayName").stringValue = "新装扮";
            element.FindPropertyRelative("variantKey").stringValue = Guid.NewGuid().ToString("N");
            element.FindPropertyRelative("paramValue").intValue = FindNextParamValue();
            element.FindPropertyRelative("thumbnail").objectReferenceValue = null;
            element.FindPropertyRelative("menuIcon").objectReferenceValue = null;
            element.FindPropertyRelative("uploadedName").stringValue = string.Empty;
            element.FindPropertyRelative("uploadedDescription").stringValue = string.Empty;
            element.FindPropertyRelative("includedRoots").arraySize = 0;
            var accProp = element.FindPropertyRelative("accessories");
            if (accProp != null) accProp.arraySize = 0;
            var scannedProp = element.FindPropertyRelative("autoScannedRoots");
            if (scannedProp != null) scannedProp.arraySize = 0;

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

        private const float ScanButtonHeight = 22f;

        private float GetElementHeight(int index)
        {
            var element = _variantsProperty.GetArrayElementAtIndex(index);
            var spacing = EditorGUIUtility.standardVerticalSpacing;
            var total = spacing * (EntryFields.Length + 2);
            foreach (var field in EntryFields)
            {
                var prop = element.FindPropertyRelative(field.Name);
                if (prop == null) continue;
                total += EditorGUI.GetPropertyHeight(prop, true);
            }
            total += ScanButtonHeight;
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

            // 扫描按钮：从当前 entry 的 includedRoots 的直接子物体自动追加到 accessories（不覆盖已有）
            var scanRect = new Rect(rect.x, rect.y, rect.width, ScanButtonHeight);
            if (GUI.Button(scanRect, "强制重扫所有 root 的子物体（拖入 root 时已自动扫过，此按钮用于手动拾取后加的子物体）"))
            {
                ScanAccessoriesForEntry(index);
            }
            rect.y += ScanButtonHeight + EditorGUIUtility.standardVerticalSpacing;
        }

        private void ScanAccessoriesForEntry(int entryIndex)
        {
            var config = (AvatarVariantSwitchConfig)target;
            if (config == null || config.variants == null) return;
            if (entryIndex < 0 || entryIndex >= config.variants.Count) return;

            var entry = config.variants[entryIndex];
            if (entry == null || entry.includedRoots == null) return;

            entry.accessories ??= new List<AvatarVariantAccessory>();
            var existingTargets = new HashSet<GameObject>();
            foreach (var acc in entry.accessories)
            {
                if (acc != null && acc.target != null) existingTargets.Add(acc.target);
            }

            var added = 0;
            foreach (var root in entry.includedRoots)
            {
                if (root == null) continue;
                for (int ci = 0; ci < root.transform.childCount; ci++)
                {
                    var child = root.transform.GetChild(ci).gameObject;
                    if (child == null) continue;
                    if (existingTargets.Contains(child)) continue;
                    entry.accessories.Add(new AvatarVariantAccessory
                    {
                        target = child,
                        displayName = child.name,
                        defaultOn = true
                    });
                    existingTargets.Add(child);
                    added++;
                }
            }

            if (added > 0)
            {
                EditorUtility.SetDirty(config);
                serializedObject.Update();
            }
            else
            {
                EditorUtility.DisplayDialog("Avatar 装扮切换器", "没有发现新的子物体可以加入配件列表。", "确定");
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
