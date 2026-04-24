# 可选装扮子集上传 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在「批量上传」按钮点击后弹出一个模态窗口，让用户勾选本次要上传哪几个装扮；未选中的装扮不会被上传，但仍在本次上传期间被 tag 翻到 EditorOnly 以保证装扮隔离。

**Architecture:** 分三个改动——(1) 在 [AvatarVariantSwitchWorkflow](../../../Editor/AvatarVariantSwitchWorkflow.cs) 里给 `StartBatchUpload` / `RunBatchUploadAsync` / `BatchPlan` 加 `selectedIndices` 参数，主循环 `pendingIndices` 初始化按选中子集过滤；(2) 新建 `Editor/AvatarVariantUploadSelectWindow.cs`——一个带复选框的 `EditorWindow`，通过 `Prompt(cfg, Action<List<int>>)` 回调返回用户勾选结果；(3) [AvatarVariantSwitchConfigEditor](../../../Editor/AvatarVariantSwitchConfigEditor.cs) 的按钮改成先 Validate → 弹 Prompt → 调新签名的 StartBatchUpload。菜单生成、tag guard、map 写入、受控 roots 集合全部不变。

**Tech Stack:** C# (Unity Editor, .NET Framework/Standard)；Unity 2022.3+ Editor-only 代码；VRC SDK3A + Modular Avatar。无自动测试框架（CLAUDE.md 声明），验证靠 Unity Editor 重编译 + 手动场景验收。

**Spec:** [docs/superpowers/specs/2026-04-24-selective-variant-upload-design.md](../specs/2026-04-24-selective-variant-upload-design.md)

---

## File Structure

- **Modify**: [Editor/AvatarVariantSwitchWorkflow.cs](../../../Editor/AvatarVariantSwitchWorkflow.cs) — `StartBatchUpload` / `RunBatchUploadAsync` 加 `IList<int> selectedIndices` 参数；`BatchPlan` 加 `selectedIndices` 字段 + `Snapshot(cfg, selectedIndices)`；`RunBatchUploadAsync` 内加 `globalToProgress` 映射，`pendingIndices` 初始化用 selected，progress window 构造 + `MarkBegin/Success/Failure` + `PrepareRetry` 调用点都过一次转换。
- **Create**: `Editor/AvatarVariantUploadSelectWindow.cs` — 独立的 `EditorWindow`；`static Prompt(cfg, Action<List<int>>)` 打开窗口，用户点「上传选中的 N 个」或「取消」/关窗口触发 `onResult` 回调（`null` = 取消）。
- **Modify**: [Editor/AvatarVariantSwitchConfigEditor.cs](../../../Editor/AvatarVariantSwitchConfigEditor.cs) — 把「批量上传所有装扮」按钮文案和 onClick 改成走 Validate → Prompt → StartBatchUpload 三步。

**不修改**：`AvatarVariantTagGuard`、`AvatarVariantMenuBuilder`、`AvatarVariantThumbnailCapture`、`AvatarVariantMap`、`AvatarVariantUploadProgressWindow`（progress 窗口完全不动——通过在 workflow 内部做 global↔progress 索引映射来适配）、Runtime、OSC 桥、README、CHANGELOG。

---

### Task 1: 给 workflow 加 `selectedIndices` 参数（Inspector 先传"全集"保持行为等价）

这一步是**结构性重构**，不改用户可见行为。`StartBatchUpload` 和 `RunBatchUploadAsync` 签名多一个 `IList<int> selectedIndices`；Inspector 临时传 `Enumerable.Range(0, cfg.variants.Count).ToList()`，跑出来和旧版完全一样。做完 Unity 应该编译通过、点"批量上传所有装扮"行为不变。

**Files:**
- Modify: `Editor/AvatarVariantSwitchWorkflow.cs` — `StartBatchUpload` / `RunBatchUploadAsync` / `BatchPlan` / `pendingIndices` / progress 索引映射
- Modify: `Editor/AvatarVariantSwitchConfigEditor.cs` — 按钮 onClick 临时传全集

> **一个 commit** 完成：同一个 commit 里改 `StartBatchUpload` 签名和 Inspector 的调用点，否则 Unity 编译会断。

- [ ] **Step 1: 修改 `StartBatchUpload` 签名并校验 selectedIndices**

File: `Editor/AvatarVariantSwitchWorkflow.cs`，替换整个 `StartBatchUpload` 方法（当前在第 77–104 行）：

```csharp
public static async void StartBatchUpload(AvatarVariantSwitchConfig cfg, IList<int> selectedIndices)
{
    if (_busy)
    {
        EditorUtility.DisplayDialog(DialogTitle, "已有批量上传任务正在运行。", "确定");
        return;
    }

    if (!Validate(cfg, true, out var error))
    {
        EditorUtility.DisplayDialog(DialogTitle, error, "确定");
        return;
    }

    // selectedIndices 的 defensive check——非法调用（null/空/越界/重复）直接弹对话框，
    // 不让 RunBatchUploadAsync 在 EditorApplication.LockReloadAssemblies 之后才踩雷。
    var validatedSelection = ValidateSelectedIndices(cfg, selectedIndices, out var selectionError);
    if (validatedSelection == null)
    {
        EditorUtility.DisplayDialog(DialogTitle, selectionError, "确定");
        return;
    }

    try
    {
        await RunBatchUploadAsync(cfg, validatedSelection);
    }
    catch (OperationCanceledException)
    {
        Debug.Log("Batch upload cancelled.");
    }
    catch (Exception ex)
    {
        Debug.LogException(ex);
        EditorUtility.DisplayDialog(DialogTitle, "批量上传失败：" + ex.Message, "确定");
    }
}

private static HashSet<int> ValidateSelectedIndices(
    AvatarVariantSwitchConfig cfg,
    IList<int> selectedIndices,
    out string error)
{
    if (selectedIndices == null || selectedIndices.Count == 0)
    {
        error = "未选择任何装扮。";
        return null;
    }

    var count = cfg.variants != null ? cfg.variants.Count : 0;
    var set = new HashSet<int>();
    foreach (var idx in selectedIndices)
    {
        if (idx < 0 || idx >= count)
        {
            error = string.Format("选中装扮索引越界：{0}（合法范围 0..{1}）。", idx, count - 1);
            return null;
        }
        if (cfg.variants[idx] == null)
        {
            error = string.Format("选中装扮索引 {0} 对应的条目为空。", idx);
            return null;
        }
        set.Add(idx);
    }

    error = null;
    return set;
}
```

- [ ] **Step 2: 修改 `RunBatchUploadAsync` 签名，把 `selectedIndices` 传进 `BatchPlan.Snapshot`**

File: `Editor/AvatarVariantSwitchWorkflow.cs`，替换 `RunBatchUploadAsync` 方法签名（当前第 335 行）和第一行调用：

```csharp
private static async Task RunBatchUploadAsync(AvatarVariantSwitchConfig cfg, HashSet<int> selectedIndices)
{
    _busy = true;
    using var cts = new CancellationTokenSource();
    AvatarVariantTagGuard guard = null;
    AvatarVariantUploadProgressWindow progressWindow = null;
    EditorApplication.LockReloadAssemblies();

    try
    {
        var plan = BatchPlan.Snapshot(cfg, selectedIndices);
        AvatarVariantMenuBuilder.Generate(cfg);
        // ...（后续内容在 Step 4 改）
```

保留后续全部代码，只改这两行。

- [ ] **Step 3: 给 `BatchPlan` 加 `selectedIndices` 字段和构造参数**

File: `Editor/AvatarVariantSwitchWorkflow.cs`，替换 `BatchPlan` 类（当前第 1236–1299 行）的相关部分：

字段区新增一行 `public readonly HashSet<int> selectedIndices;`：

```csharp
private sealed class BatchPlan
{
    public readonly GameObject avatarRoot;
    public readonly string parameterName;
    public readonly string menuName;
    public readonly int defaultValue;
    public readonly ReleaseStatus releaseStatus;
    public readonly string outputMapPath;
    public readonly string uploadedAvatarNamePrefix;
    public readonly string legacyUploadedAvatarDescription;
    public readonly List<BatchVariantPlan> variants;
    public readonly HashSet<int> selectedIndices;
```

构造函数加参数（保持其他字段赋值不变）：

```csharp
    private BatchPlan(
        GameObject avatarRoot,
        string parameterName,
        string menuName,
        int defaultValue,
        ReleaseStatus releaseStatus,
        string outputMapPath,
        string uploadedAvatarNamePrefix,
        string legacyUploadedAvatarDescription,
        List<BatchVariantPlan> variants,
        HashSet<int> selectedIndices)
    {
        this.avatarRoot = avatarRoot;
        this.parameterName = parameterName;
        this.menuName = menuName;
        this.defaultValue = defaultValue;
        this.releaseStatus = releaseStatus;
        this.outputMapPath = outputMapPath;
        this.uploadedAvatarNamePrefix = uploadedAvatarNamePrefix;
        this.legacyUploadedAvatarDescription = legacyUploadedAvatarDescription;
        this.variants = variants;
        this.selectedIndices = selectedIndices;
    }
```

改 `Snapshot` 的签名和 return：

```csharp
    public static BatchPlan Snapshot(AvatarVariantSwitchConfig cfg, HashSet<int> selectedIndices)
    {
        var variants = new List<BatchVariantPlan>();
        foreach (var variant in cfg.variants)
        {
            var thumbnailAsset = ResolveVariantThumbnail(cfg, variant);
            variants.Add(new BatchVariantPlan(
                variant.displayName ?? string.Empty,
                variant.variantKey ?? string.Empty,
                variant.paramValue,
                ResolveThumbnailPath(thumbnailAsset, GetVariantLabel(variant, variants.Count)),
                thumbnailAsset,
                variant.uploadedName ?? string.Empty,
                variant.uploadedDescription ?? string.Empty,
                variant.legacyUploadedBlueprintId ?? string.Empty,
                new List<GameObject>(variant.includedRoots ?? new List<GameObject>())));
        }

        return new BatchPlan(
            cfg.AvatarRoot,
            (cfg.parameterName ?? string.Empty).Trim(),
            ResolveMenuName(cfg),
            cfg.defaultValue,
            cfg.releaseStatus,
            cfg.outputMapPath,
            cfg.uploadedAvatarNamePrefix ?? string.Empty,
            cfg.legacyUploadedAvatarDescription ?? string.Empty,
            variants,
            new HashSet<int>(selectedIndices));
    }
```

关键点：`Snapshot` 里的 `variants` 循环**仍然遍历全部** `cfg.variants`——只是 `selectedIndices` 额外记录子集。

- [ ] **Step 4: 在主循环内构造 `globalToProgress` 映射和 selected-only 的 plan items**

File: `Editor/AvatarVariantSwitchWorkflow.cs`，在 `RunBatchUploadAsync` 的 `AvatarVariantMenuBuilder.Generate(cfg);` 之后、`var planItems = new List<...>` 之前插入映射构造；然后**替换** `planItems` 的填充循环（当前第 348–353 行）：

把 这段：

```csharp
var planItems = new List<AvatarVariantUploadProgressWindow.VariantPlanItem>();
foreach (var v in plan.variants)
{
    planItems.Add(new AvatarVariantUploadProgressWindow.VariantPlanItem(
        v.displayName, v.variantKey, v.thumbnailAsset));
}
progressWindow = AvatarVariantUploadProgressWindow.ShowAndBegin(planItems);
```

替换为：

```csharp
// global→progress 索引映射：progress window 内部用 _rows[progressIdx] 连续数组，
// 但 pendingIndices / FailureRecord.OriginalIndex 以及 variant 数据都以 cfg.variants
// 全局索引为准。在每次调 MarkBegin / MarkSuccess / MarkFailure / PrepareRetry 时做转换。
var selectedSorted = plan.selectedIndices.OrderBy(g => g).ToList();
var globalToProgress = new Dictionary<int, int>(selectedSorted.Count);
for (var pi = 0; pi < selectedSorted.Count; pi++)
{
    globalToProgress[selectedSorted[pi]] = pi;
}

var planItems = new List<AvatarVariantUploadProgressWindow.VariantPlanItem>();
foreach (var globalIdx in selectedSorted)
{
    var v = plan.variants[globalIdx];
    planItems.Add(new AvatarVariantUploadProgressWindow.VariantPlanItem(
        v.displayName, v.variantKey, v.thumbnailAsset));
}
progressWindow = AvatarVariantUploadProgressWindow.ShowAndBegin(planItems);
```

- [ ] **Step 5: 在 `TryUploadOneVariantAsync` 里用映射转换 progress 索引**

File: `Editor/AvatarVariantSwitchWorkflow.cs`，定位到 `async Task<FailureRecord?> TryUploadOneVariantAsync(int index)` 的内部（当前第 405–489 行），**保持**入参 `index` 含义不变（= cfg.variants 全局索引），把全部 `progressWindow.MarkBegin(index)` / `MarkSuccess(index, ...)` / `MarkFailure(index, ...)` 改成 `globalToProgress[index]`：

- 第一行 `progressWindow.MarkBegin(index);` → `progressWindow.MarkBegin(globalToProgress[index]);`
- `progressWindow.MarkSuccess(index, blueprintId);` → `progressWindow.MarkSuccess(globalToProgress[index], blueprintId);`
- 两处 `progressWindow.MarkFailure(index, ...)` → 都改成 `progressWindow.MarkFailure(globalToProgress[index], ...)`

这是**唯一**的地方接触 progress index；别处对 `index` 的使用（`plan.variants[index]`、`variant.displayName` 等）保持全局索引语义不动。

- [ ] **Step 6: 把 `pendingIndices` 初始化成 selected 子集，并在 `PrepareRetry` 处做映射转换**

File: `Editor/AvatarVariantSwitchWorkflow.cs`，定位 `pendingIndices` 初始化（当前第 491 行）和重试循环内 `PrepareRetry` 调用（当前第 532 行）：

把：

```csharp
var pendingIndices = Enumerable.Range(0, plan.variants.Count).ToList();
```

替换为：

```csharp
// selectedSorted 已是按升序的全局索引，直接用它当初始 pending 列表。
var pendingIndices = new List<int>(selectedSorted);
```

把：

```csharp
pendingIndices = passFailures.Select(f => f.OriginalIndex).ToList();
progressWindow.PrepareRetry(pendingIndices);
```

替换为：

```csharp
pendingIndices = passFailures.Select(f => f.OriginalIndex).ToList();
progressWindow.PrepareRetry(pendingIndices.Select(g => globalToProgress[g]).ToList());
```

至此 workflow 的所有 progress 索引调用点都已过转换。

- [ ] **Step 7: 更新 `lastFailures` / `plan.variants.Count` 相关文案的分母**

File: `Editor/AvatarVariantSwitchWorkflow.cs`，定位末尾的汇总消息（当前第 543 行附近）：

```csharp
progressWindow.EndSessionFailed(string.Format(
    "部分失败：{0}/{1} 个装扮未能上传；成功的已写入映射，稍后可重新点批量上传继续处理失败装扮。",
    lastFailures.Count, plan.variants.Count));
```

把分母从"全部 variants"改成"本次选中的":

```csharp
progressWindow.EndSessionFailed(string.Format(
    "部分失败：{0}/{1} 个装扮未能上传；成功的已写入映射，稍后可重新点批量上传继续处理失败装扮。",
    lastFailures.Count, plan.selectedIndices.Count));
```

- [ ] **Step 8: 修改 Inspector 临时传"全集"，保持旧行为**

File: `Editor/AvatarVariantSwitchConfigEditor.cs`，第 109–112 行，把：

```csharp
if (GUILayout.Button("批量上传所有装扮"))
{
    AvatarVariantSwitchWorkflow.StartBatchUpload(config);
}
```

替换为（**临时**；Task 3 会再改成弹 selection window）：

```csharp
if (GUILayout.Button("批量上传所有装扮"))
{
    var allIndices = new List<int>();
    for (var i = 0; i < (config.variants != null ? config.variants.Count : 0); i++)
    {
        allIndices.Add(i);
    }
    AvatarVariantSwitchWorkflow.StartBatchUpload(config, allIndices);
}
```

这一步后 Unity 会重新编译。

- [ ] **Step 9: Unity 编译验证**

回到 Unity Editor，等它自动刷新。观察 Console：

Expected: 没有编译错误；`AvatarVariantSwitchWorkflow` / `AvatarVariantSwitchConfigEditor` 编译通过。

如果有错误，修完再继续。

- [ ] **Step 10: 烟测——点一次批量上传，确认行为和旧版一致**

手动（建议环境：有至少 2 个 variants、都已有映射记录的测试场景）：

1. 打开 AvatarVariantSwitchConfig 所在场景，Inspector 显示正常。
2. 点「批量上传所有装扮」。
3. 不需要真上传到 VRChat——看到 progress window 列出所有 variants、开始滚动、`MarkBegin` 日志按顺序打就算过。到 `AvatarVariantBuilderGate.AcquireAsync` 停住是正常的（需要登录/builder）。可以取消。
4. 观察：progress window 的行数 = `cfg.variants.Count`，和旧版一致。

Expected: 行为和重构前视觉上完全一致。

如果有回归（比如 progress window 行数错、MarkBegin 日志只跑到一半），回查 Step 4–7 的映射。

- [ ] **Step 11: Commit**

```bash
git add Editor/AvatarVariantSwitchWorkflow.cs Editor/AvatarVariantSwitchConfigEditor.cs
git commit -m "refactor: 批量上传 workflow 加 selectedIndices 参数

StartBatchUpload / RunBatchUploadAsync / BatchPlan 签名多一个
IList<int> selectedIndices；主循环 pendingIndices 按选中子集初始化，
progress window 的 plan items 和 MarkBegin/Success/Failure/PrepareRetry
全部在 workflow 内部做 global→progress 索引映射。

Inspector 临时传入全部 variants 的索引列表，用户可见行为不变。
下一步接入 AvatarVariantUploadSelectWindow。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: 新建 AvatarVariantUploadSelectWindow（不接入）

这一步写一个独立的 `EditorWindow`，暴露 `Prompt(cfg, Action<List<int>>)` API。UI 含每行复选框 + 缩略图 + 显示名 + map 状态；底部「上传选中的 N 个」和「取消」两个按钮。写完这步后**还没接入** Inspector——还是 Task 1 的老按钮"全集"路径。

**Files:**
- Create: `Editor/AvatarVariantUploadSelectWindow.cs` — 新建全文件
- Create: `Editor/AvatarVariantUploadSelectWindow.cs.meta` — Unity 自动生成，commit 时一起带上

- [ ] **Step 1: 新建空文件骨架**

Write file `Editor/AvatarVariantUploadSelectWindow.cs`:

```csharp
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
```

- [ ] **Step 2: Unity 编译验证**

回到 Unity Editor，等它自动刷新。

Expected: Console 无编译错误；新文件 meta 自动生成在同目录。

- [ ] **Step 3: 临时手测——从菜单或临时代码触发 Prompt**

因为 Inspector 还没接，没法点按钮测。建议做法之一：在 Inspector 里临时加一个**测试按钮**，只本地验证 UI（不 commit 这段测试代码）：

临时在 `AvatarVariantSwitchConfigEditor.cs` 的按钮区（第 109 行附近）上面临时加：

```csharp
if (GUILayout.Button("[DEV] 弹 SelectWindow"))
{
    AvatarVariantUploadSelectWindow.Prompt(config, selected =>
    {
        Debug.Log(selected == null
            ? "[AvatarVariantSwitcher] SelectWindow canceled"
            : "[AvatarVariantSwitcher] SelectWindow selected: " + string.Join(",", selected));
    });
}
```

验证点（每项对着 Console 或视觉）：

- 打开：窗口出现，所有装扮默认全部勾选。
- 取消勾选其中一个 → 「上传选中的 N-1 个」按钮的数字更新。
- 全部取消勾选 → 「上传选中的 0 个」disable。
- 点「上传选中的 N 个」→ 窗口关闭，Console 日志打 `selected: 0,1,...`。
- 重开窗口，点「取消」→ 窗口关闭，Console 日志打 `canceled`。
- 重开窗口，直接按 X → 窗口关闭，Console 日志打 `canceled`。
- 每行缩略图显示正确；已在 map 里的 variant 显示"已上传：avtr_xxx"，未上传显示"未上传"。

Expected: 全部通过，无异常日志。

**测试完删掉那段临时代码**——不要 commit 进去。

- [ ] **Step 4: Commit**

```bash
git add Editor/AvatarVariantUploadSelectWindow.cs Editor/AvatarVariantUploadSelectWindow.cs.meta
git commit -m "feat: 新增 AvatarVariantUploadSelectWindow

独立的 EditorWindow，提供 Prompt(cfg, onResult) 静态 API：弹一个
浮动窗口让用户勾选要上传的装扮子集，用户按「上传」/「取消」/关窗口
时通过回调把选中索引列表或 null 传回调用方。每行显示复选框、缩略图、
装扮名、以及从映射文件查出的该装扮当前 blueprintId（只读提示）。

目前还未接入 Inspector——下一步改按钮走 Validate→Prompt→StartBatchUpload。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Inspector 按钮接入 SelectWindow

**Files:**
- Modify: `Editor/AvatarVariantSwitchConfigEditor.cs`（第 109–112 行附近的按钮 block）

- [ ] **Step 1: 替换按钮 onClick 走 Validate → Prompt → StartBatchUpload**

File: `Editor/AvatarVariantSwitchConfigEditor.cs`，把 Task 1 Step 8 留下的：

```csharp
if (GUILayout.Button("批量上传所有装扮"))
{
    var allIndices = new List<int>();
    for (var i = 0; i < (config.variants != null ? config.variants.Count : 0); i++)
    {
        allIndices.Add(i);
    }
    AvatarVariantSwitchWorkflow.StartBatchUpload(config, allIndices);
}
```

替换为：

```csharp
if (GUILayout.Button("批量上传..."))
{
    StartBatchUploadFlow(config);
}
```

然后在这个 class 的任意合适位置（例如紧跟在 `OnInspectorGUI` 之后）新增 `StartBatchUploadFlow` 静态方法：

```csharp
private static void StartBatchUploadFlow(AvatarVariantSwitchConfig config)
{
    // Validate 内部会 requireThumbnails=true 覆盖全部 variants——即使用户本次只想
    // 上传一件，其他 variants 仍在 tag guard 和 menu builder 的作用范围内，必须全部合法。
    if (!AvatarVariantSwitchWorkflow.Validate(config, true, out var error))
    {
        EditorUtility.DisplayDialog("Avatar 装扮切换器", error, "确定");
        return;
    }

    AvatarVariantUploadSelectWindow.Prompt(config, selectedIndices =>
    {
        if (selectedIndices == null || selectedIndices.Count == 0)
        {
            return;
        }
        AvatarVariantSwitchWorkflow.StartBatchUpload(config, selectedIndices);
    });
}
```

- [ ] **Step 2: Unity 编译验证**

回到 Unity Editor，等自动刷新。

Expected: Console 无编译错误。

- [ ] **Step 3: 端到端烟测**

手动：

1. 打开 AvatarVariantSwitchConfig 所在场景。
2. 点「批量上传...」按钮。
3. Selection window 弹出，所有 variants 默认全选。
4. 取消勾选其中一个 → 按钮文案变成「上传选中的 N-1 个」。
5. 点「上传选中的 N-1 个」→ selection window 关闭；upload progress window 打开，**只**列出选中的 N-1 个 variants（未选的不出现在进度列表里）。
6. 取消 progress window / 等 `AcquireAsync` 超时都行，不需要真上传。
7. 批处理退出后，检查场景里未选 variant 的 includedRoots 的 tag / activeSelf / blueprintId 都回到了点"批量上传..."之前的状态。

Expected: 全部通过。

- [ ] **Step 4: 配置校验门槛回归**

手动：

1. 故意让某个 variant 的缩略图清空。
2. 点「批量上传...」。

Expected: 直接弹 Validate 错误对话框，**不打开** selection window。

做完把缩略图补回来。

- [ ] **Step 5: Commit**

```bash
git add Editor/AvatarVariantSwitchConfigEditor.cs
git commit -m "feat: 批量上传按钮接入 SelectWindow

按钮文案「批量上传所有装扮」→「批量上传...」，三个点提示弹框。
onClick 改成 Validate(true) → AvatarVariantUploadSelectWindow.Prompt
→ StartBatchUpload(config, selectedIndices)。Validate 仍对全部 variants
做（包括未选的），和设计 spec §7 一致。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: 手动验收场景

没有自动化测试；按 spec §11 的清单逐项跑。

**Files:** 无代码改动；如发现 bug 回到前面 Task 修。

- [ ] **Step 1: 全选路径回归**

操作：打开弹框，**不修改**勾选状态（默认全选），点「上传选中的 N 个」。

Expected: 行为和 Task 1 烟测一致——progress window 列出全部 N 个 variants。

- [ ] **Step 2: 只选一个**

操作：打开弹框，只勾第 2 个 variant（第 1、3、4... 全部取消勾选），点「上传选中的 1 个」。

Expected:
- progress window 只列 1 行。
- 如果真走完上传：VRChat 上只有第 2 个 variant 的 blueprint 被更新；第 1、3、... 的 blueprint 保持不动。
- 批处理退出（不论成功、失败、取消）后，场景里**全部** variants（包括未选的）的 tag / activeSelf / blueprintId 都恢复。
- map 文件里：第 2 个 variant 的 blueprintId 更新；其他 variants 的 map 记录**完全不变**。

- [ ] **Step 3: 断点续传**

操作：先全选上传一半，关窗口 / 重开 Unity；再次点「批量上传...」，手动取消勾选已经成功的那几个，只勾失败的。

Expected: 只跑选中的；之前成功的 map 记录没动，它们的 blueprint 也没被重新编译 / 重新上传。

- [ ] **Step 4: 空选取消**

操作：打开弹框 → 取消全部勾选 → 观察按钮。

Expected: 「上传选中的 0 个」disable。按 X / 「取消」 → 正常关闭，不进 upload 流程。

- [ ] **Step 5: 窗口关闭语义**

操作：打开弹框 → 直接按「取消」；再开一次 → 按 X。

Expected: 两种情况都不打开 progress window，场景无任何改动。

- [ ] **Step 6: Validate 门槛**

操作：让某个 variant 的缩略图或其他必填字段缺失 → 点「批量上传...」。

Expected: 弹 Validate 错误对话框，**不打开** selection window。

- [ ] **Step 7: 和失败重试交互**

操作：选 2–3 个 variants，断网（或用会 TLS 失败的网络环境），开始上传。

Expected:
- 跑完这批后出现「重试失败的 N 个」对话框。
- 点重试只跑失败的那些。
- 成功的不重跑。
- 整个过程进度显示的分母是"选中数"，而不是 cfg.variants 的总数。

- [ ] **Step 8: 中途取消**

操作：选几个，开始跑，按 progress window 的取消。

Expected:
- 所有选中的 variants 的 tag / activeSelf / blueprintId 都恢复到点"批量上传..."之前。
- **未选** variants 的状态也恢复（它们本来就被 tag guard 监管）。
- map 只写了已经成功的那些。

- [ ] **Step 9: Commit 任何 Task 4 中发现的修复**

如果前面 8 步发现 bug：

1. 回到对应 Task 修。
2. 单独起一个 commit，描述"fix: <bug 描述>"。

如果 8 步全部通过，**不需要 commit**——Task 4 本身没代码改动。

---

## Self-Review

**1. Spec coverage**：
- §4.1 触发 / §4.3 Prompt 调用形态 → Task 3 Step 1（`StartBatchUploadFlow`）。
- §4.2 Selection window UI → Task 2 Step 1。
- §4.4 两种取消语义 → Task 2 Step 1 的 `DeliverAndClose` + `OnDisable`；Task 3 Step 1 的 `selectedIndices == null` 分支。
- §5.1 签名变更 + defensive check → Task 1 Step 1。
- §5.2 BatchPlan 扩展 → Task 1 Step 3。
- §5.3 pendingIndices 初始化 → Task 1 Step 6。
- §5.4 Progress window + 索引映射 → Task 1 Step 4–6。
- §6 受控集合不变 → 不修改，Task 1 Step 4 只改 planItems 那一段；`includedRootsUnion`、`accessoryMenuGameObjects`、`accessoriesMenuRoot`、`controlledRoots`、`guard` 这一整块代码保持原样。
- §7 Validate 不变 → Task 3 Step 1 在 `Prompt` 之前调 `Validate(config, true, ...)`。
- §8 Map 写入不变 → 不修改 `Upsert` 调用点。
- §9 SelectWindow 实现要点 → Task 2 Step 1 的 `IsConfigStillValid`、`_resultDelivered`、`DeliverAndClose` 全部对应。
- §11 测试计划 → Task 4 Step 1–8。

**2. Placeholder scan**：Step 内容全部是实际代码/命令；无 TBD / TODO / "handle edge cases" 这类占位。

**3. Type consistency**：
- `IList<int> selectedIndices`（外部 API 参数） vs `HashSet<int> selectedIndices`（`BatchPlan` 内部字段、`RunBatchUploadAsync` 参数）——Task 1 Step 1 的 `ValidateSelectedIndices` 返回 `HashSet<int>`，Step 2 的 `RunBatchUploadAsync(...,  HashSet<int> selectedIndices)`，Step 3 的 `Snapshot(cfg, HashSet<int> selectedIndices)`，一致。
- `globalToProgress: Dictionary<int, int>`——Task 1 Step 4 定义，Step 5 / Step 6 使用，名字一致。
- `Prompt(AvatarVariantSwitchConfig cfg, Action<List<int>> onResult)`——Task 2 Step 1 定义，Task 3 Step 1 使用，签名一致。
- `DeliverAndClose(List<int> result)`——私有方法名在 Task 2 Step 1 里三处使用（按钮点击、OnDisable、自身内部），一致。
