# 批量上传：可选装扮子集（断点续传 / 单件更新）— 设计文档

- **日期**：2026-04-24
- **作者**：Claude + 用户协作
- **背景仓库**：`com.lanstard.avatar-variant-switcher`（VPM 包）
- **worktree**：`fix-selective-upload`

## 1. 背景与目标

目前 [AvatarVariantSwitchWorkflow.StartBatchUpload](../../../Editor/AvatarVariantSwitchWorkflow.cs) 是一个"无脑全量"的入口：点一下，`cfg.variants` 里的所有装扮全都会被上传一遍。两个常见场景因此变得痛苦：

- **断点续传**：上次会话里批量跑了一半，关了 Unity / 退出了 editor，下次想只跑"还没成功那几件"，现在做不到——只能再点一次"批量上传"，把已经成功、CDN 上完好的那几件又全量重跑、bundle 重编、版本号 +1。
- **单件更新**：改了某一个装扮（比如换了一件头发），想只把那一件的 blueprint 更新上去。现在做不到，同样要走全量。

**目标**：让用户在点"批量上传"后，可以勾选本次要上传哪几个装扮；未选的装扮既不会被上传，也不会影响选中装扮的 bundle 隔离。

## 2. 非目标（YAGNI）

- **Inspector 行内"只上传这一个"按钮**：不做。只有一个弹框入口，和现有按钮文案统一。
- **"仅选未上传"快捷钮 / 智能默认勾选**：不做。默认全选 + 复选框就够。这是用户明确选的"最简"方案。
- **跨会话记住上次选择**：不做。每次开弹框默认全选。
- **部分 variants 数据不全时允许只上传其他的**：不做。验证仍然对**全部** variants 做（见 §4）。
- **Selection window 里修改装扮字段**（缩略图、参数值等）：不做，改配置仍然走 Inspector。
- **并行上传 / 自动重试 / 细分错误处理**：沿用 [2026-04-23-batch-upload-continue-on-failure](2026-04-23-batch-upload-continue-on-failure-design.md) 已有的语义，本次不改。

## 3. 总体思路

**切分清楚**：批处理的三件事是各自独立的维度。

| 维度 | 范围 | 是否受 "用户选择子集" 影响 |
|---|---|---|
| 菜单生成（`Generate`） | **全部** variants | 不受影响 |
| 受控 roots 集合（tag 翻转范围） | **全部** variants | 不受影响 |
| 每轮激活集 + `BuildAndUpload` | 选中的 variants | **仅对这一步生效** |
| Map 写入（`Upsert`） | 上传成功的 variants | 只对选中 + 成功的写 |

换句话说：**"选择"只影响"跑谁"，不影响"藏谁"**。未选的装扮仍然在 tag guard 的覆盖下被摆到 EditorOnly，确保选中装扮的 bundle 里不会带进未选装扮的 GameObject。

这个结构让改动范围变小：
- `BatchPlan` 仍然快照所有 variants。
- 多一个 `selectedVariantIndices: HashSet<int>` 决定循环时跳过谁。
- 菜单 / 受控集合 / tag guard 代码一行不动。

## 4. 用户交互

### 4.1 触发

[AvatarVariantSwitchConfigEditor](../../../Editor/AvatarVariantSwitchConfigEditor.cs#L109-L112) 的按钮：

- 文案：`"批量上传所有装扮"` → `"批量上传..."`（三个点提示弹框）。
- 点击：先调 `Validate(cfg, requireThumbnails: true)`；不过 → 弹原有的错误对话框；过 → 打开 selection window。

### 4.2 Selection window

新增 `Editor/AvatarVariantUploadSelectWindow.cs`。

- 类型：`EditorWindow`，`ShowUtility()` 悬浮窗（Unity editor window 语义不支持真正的 OS 级 modal，但 utility 窗口会显示在主编辑器之上并带独立焦点）。
- 外观：
  - 窗口标题："选择要上传的装扮"。
  - 每个 variant 一行，从上到下：`[✓]` 复选框 + 32×32 缩略图 + 显示名（粗体）+ 小字状态：`已上传 blueprintId: avtr_xxx` 或 `未上传` 或 `仅记录于旧字段`（从 map 查）。
  - 底部两个按钮：`上传选中的 N 个` 和 `取消`。
  - `上传选中的 N 个` 在 N=0 时 disable。
- 默认状态：**全部勾选**。
- 关闭方式：
  - 按 `上传选中的 N 个` → 回调传选中 indices，窗口关闭。
  - 按 `取消` 或 X → 回调传 `null`，窗口关闭。
  - 在窗口开着时如果 cfg 被 destroy / variants 被改动（例如另一个 Inspector 编辑），检测到就自动关闭，按"取消"处理。

### 4.3 调用形态

Selection window 不用阻塞 API（Unity editor 主线程不能阻塞），用**回调 + delegate** 模式：

```csharp
AvatarVariantUploadSelectWindow.Prompt(cfg, onResult: selectedIndices => {
    if (selectedIndices == null) return; // 用户取消
    AvatarVariantSwitchWorkflow.StartBatchUpload(cfg, selectedIndices);
});
```

理由：editor window 是主线程事件循环里的普通窗口，没法"挂起"调用栈。回调是 Unity editor 里这类 modal 的标准写法。

### 4.4 Selection window 取消 vs upload window 取消

两个取消语义不同，不要搞混：

- **Selection window 取消**：还没开始上传，不打扰任何场景状态，直接什么都不做。
- **Upload window 取消**（已有机制）：批处理已经开跑，`progressWindow.CancelRequested` 触发 `cts.Cancel()`，tag guard 负责把场景还原。保持原样。

## 5. 数据流与 API 变更

### 5.1 `AvatarVariantSwitchWorkflow` 签名变更

```csharp
// 旧
public static async void StartBatchUpload(AvatarVariantSwitchConfig cfg);
private static async Task RunBatchUploadAsync(AvatarVariantSwitchConfig cfg);

// 新
public static async void StartBatchUpload(AvatarVariantSwitchConfig cfg, IList<int> selectedIndices);
private static async Task RunBatchUploadAsync(AvatarVariantSwitchConfig cfg, IList<int> selectedIndices);
```

`selectedIndices`：对应 `cfg.variants` 下标的子集。调用方负责保证：

- 非 null、非空（空集在 selection window 就不让"上传"按钮 enable）。
- 元素唯一、都在 `[0, cfg.variants.Count)` 范围内。
- 对应的 `cfg.variants[i]` 不为 null。

`RunBatchUploadAsync` 入口对这几条做 defensive check——非法输入 `throw ArgumentException`，交由 `StartBatchUpload` 外层的 `catch (Exception ex)` 弹错误对话框。不指望 selection window 的"善意"。

### 5.2 `BatchPlan.Snapshot` 扩展

```csharp
private sealed class BatchPlan
{
    // ... 现有字段 ...
    public readonly List<BatchVariantPlan> variants;       // 全部 variants（不变）
    public readonly HashSet<int> selectedIndices;           // 新增：选中子集
}
```

快照时机：`Snapshot(cfg, selectedIndices)` 在 editor 主线程上一次性抓。`variants` 仍然是**全部** variants（不按 selection 过滤），这是 §3 表格的关键前提。

### 5.3 主循环：`pendingIndices` 初始化改变

[AvatarVariantSwitchWorkflow.cs:491](../../../Editor/AvatarVariantSwitchWorkflow.cs#L491)：

```csharp
// 旧
var pendingIndices = Enumerable.Range(0, plan.variants.Count).ToList();

// 新
var pendingIndices = plan.variants
    .Select((v, i) => i)
    .Where(i => plan.selectedIndices.Contains(i))
    .ToList();
```

重试语义不变：失败时 `pendingIndices = passFailures.Select(f => f.OriginalIndex).ToList()`，失败的仍然只在选中子集范围内（`OriginalIndex` 用的是 `cfg.variants` 的全局索引，在选中集合里天然封闭）。

### 5.4 Progress window 显示范围

`AvatarVariantUploadProgressWindow.ShowAndBegin(planItems)` 的 `planItems` 只包含选中的 variants。未选的装扮不出现在进度条里。

构造时：

```csharp
var planItems = new List<AvatarVariantUploadProgressWindow.VariantPlanItem>();
foreach (var i in plan.selectedIndices.OrderBy(i => i))
{
    var v = plan.variants[i];
    planItems.Add(new AvatarVariantUploadProgressWindow.VariantPlanItem(
        v.displayName, v.variantKey, v.thumbnailAsset));
}
```

Progress window 的 `MarkBegin(i)` / `MarkSuccess(i)` / `MarkFailure(i)` 的 `i` 是 progress window 的**内部连续索引**（`_rows[i]`），progress window 这个类本身一行不改。workflow 内部维护一个双向映射：

- `globalIdxToProgressIdx: Dictionary<int, int>`——把 cfg.variants 全局索引映射到 progress window 行号，每次调 Mark* 时转换。
- `progressIdxToGlobalIdx: int[]`——`pendingIndices` / `FailureRecord.OriginalIndex` 继续用全局索引（便于重试循环在 cfg.variants 语义下封闭），需要向 progress window 报状态时用它转回 progress 索引。

这个选择的理由：progress window 的改动成本 > workflow 加一层映射的成本；而且 progress window 的"连续行"语义和它的 UI（滚动列表、行号显示）契合，不值得为选择性上传把它改成稀疏索引。

## 6. 受控集合与 tag guard（不变）

[AvatarVariantSwitchWorkflow.cs:370-397](../../../Editor/AvatarVariantSwitchWorkflow.cs#L370-L397)：

```csharp
var includedRootsUnion = plan.variants    // 注意：全部 variants 的并集
    .SelectMany(variant => variant.includedRoots)
    ...
```

这一块代码**完全不动**。`plan.variants` 本来就是全部，`selectedIndices` 不影响它。

受控集合（tag 翻转范围）= 全部 variants 的 `includedRoots` + 全部 accessory 菜单 GO + `_AccessoriesMenu` 父节点。每轮 `ApplyActive(activeSet)` 时未选 variants 的 roots 自动落到 EditorOnly（因为 `activeSet` 不包含它们）。这是 §3 "装扮隔离仍有效" 的机制。

## 7. 验证（Validate）

**完全不变**。`Validate(cfg, requireThumbnails: true)` 在 selection window **打开前** 跑一次。理由：

1. 未选 variants 仍然参与 tag 翻转、菜单生成。如果它 `includedRoots` 空 / 缺缩略图 / variantKey 重复，tag guard / menu builder 一样会踩空。
2. 保持"开批量上传前，cfg 必须完全合法"的既有契约，不为了部分上传场景开口子。

**推论**：用户如果装扮 X 的缩略图还没准备好，那他这次连"只上传装扮 Y"都做不了——得先把 X 的问题解决。这一条是有意的，不妥协。

## 8. Map 写入

**完全不变**。

- 批处理开头覆盖 `map.parameterName / menuName / defaultValue`——这些是 cfg 的当前值，跟是否全量无关。
- 成功的 variant 在 `TryUploadOneVariantAsync` 里 `Upsert`。
- 未选 variants 的 map 记录 **完全不动**：既不覆盖、也不删。想清理旧 map 记录走 `PruneStaleMapKeys`，跟选择性上传正交。

## 9. Selection window 实现要点

- 窗口状态：私有字段 `_config`、`_selected: HashSet<int>`、`_onResult: Action<List<int>>`、`_variants: List<AvatarVariantEntry>`（快照引用——避免 OnGUI 里重复查 cfg）、`_mapIndex: Dictionary<string, string>`（variantKey → blueprintId，从 map 一次性读出）。
- OnGUI：
  - Header 两行：标题 + 提示文案（"勾选要上传的装扮；未选的不会被上传，但它们的 GameObject 仍会在本次上传中被临时摆成 EditorOnly 以保证装扮隔离"）。
  - 行列表：`EditorGUILayout.BeginScrollView` + 每个 variant 一行。
  - 底部按钮区：`EditorGUILayout.BeginHorizontal` + flexible space + 两个按钮。
- 关闭时机：
  - 点"上传" → `_onResult(_selected.OrderBy(i => i).ToList())`，然后 `Close()`。
  - 点"取消" / X / `OnDisable`（用户拖掉窗口） → `_onResult(null)`（仅在还未返回过时）。
  - 防重入：用一个 `_resultDelivered` flag 保证 `_onResult` 最多调一次。
- 健壮性：`OnGUI` 开头检查 `_config == null || _variants.Count != _config.variants.Count`——如果 cfg 被销毁、或 variants 数量变了，直接 `_onResult(null)` + `Close()`。

## 10. 代码改动清单

| 文件 | 变更 |
|---|---|
| `Editor/AvatarVariantUploadSelectWindow.cs` | 新建 |
| `Editor/AvatarVariantSwitchConfigEditor.cs` | 按钮文案 + 改 onClick：Validate → Prompt → StartBatchUpload |
| `Editor/AvatarVariantSwitchWorkflow.cs` | `StartBatchUpload` / `RunBatchUploadAsync` / `BatchPlan` 加 `selectedIndices` 参数；主循环 `pendingIndices` 初始化用 `selectedIndices`；progress window 的 `planItems` 和 `globalIdx↔progressIdx` 映射 |

进度窗口、tag guard、map、thumbnail capture、menu builder：不变。

## 11. 测试计划

没有自动化测试，手动验证以下场景：

1. **全选路径（回归）**：打开弹框，默认全选，点"上传选中的 N 个"。行为应和老版本"批量上传所有装扮"完全一致。
2. **只选一个**：打开弹框，只勾第 2 个 variant。
   - 预期：progress window 只有 1 行；其他 variants 的场景 tag / activeSelf 在批处理结束后恢复原样；map 里其他 variants 的 blueprintId 保持不变；VRChat 上只有第 2 个 variant 的 bundle 被更新。
3. **断点续传**：把第 1、2 个 variant 全选上传完，重开 Unity，再次打开弹框只勾第 3 个。
   - 预期：第 3 个上传；map 里第 1、2 的 blueprintId 没动。
4. **空选取消**：打开弹框，全部取消勾选。
   - 预期："上传选中的 0 个"按钮 disable。
5. **窗口关闭取消**：打开弹框直接关窗口 / 按取消。
   - 预期：不进入 upload 流程，场景一切如旧。
6. **Validate 门槛**：让某个 variant 缺缩略图，然后点"批量上传..."，不管是不是要选它。
   - 预期：报验证错误对话框，不打开 selection window。
7. **和失败重试交互**：选了 3 个，故意让其中 1 个网络失败（断网/VPN）。
   - 预期：跑完 3 个后弹"重试失败的"对话框，点重试只跑那 1 个；成功的 2 个不重跑。
8. **中途取消**：选了 3 个，开始跑后按 progress window 的取消。
   - 预期：tag guard 恢复所有装扮的 tag / activeSelf / blueprintId；map 只写了已成功的那些。

## 12. 未决与后续

- Selection window 的 "状态" 列目前基于 map 查 blueprintId。如果用户的 `variantKey` 和 map 都对不上（schema v0 遗留），显示什么？——方案：显示"未上传"即可，不特别处理。map 查找用现有的 `FindByKeyOrParam`。
- 如果后续想加"仅选未上传"快捷钮，只要在 selection window 里加一个按钮调 `_selected = variants with empty map blueprintId` 即可，不破坏 spec。
