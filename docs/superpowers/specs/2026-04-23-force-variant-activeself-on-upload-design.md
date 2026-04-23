# 批量上传时强制摆正装扮 activeSelf

## 背景 / 问题

用户在 Unity 场景里改模时会习惯性地把暂时不看的那几件衣服的顶层 GameObject 手动 `SetActive(false)` 来减少场景干扰。现有的批量上传流程只翻 `tag`（让非当前装扮变 `EditorOnly` 被 VRChat 构建剥离），**不碰 `activeSelf`**。结果：

- 用户把衣服 A 关了去改模，然后跑批量上传。
- 当轮到 A 上传时，`includedRoots` 的 tag 被正确设成 `Untagged`（纳入 bundle），但 `activeSelf` 仍是 `false`。
- A 的 bundle 里衣服 GameObject 就是禁用状态，玩家切到 A 看到的是空人/骨架。

对比点：[AvatarVariantThumbnailCapture](../../../Editor/AvatarVariantThumbnailCapture.cs) 渲缩略图时已经在处理 `activeSelf`（按装扮切+finally 还原），**上传流程没跟进**。

## 范围

**做什么：**

- 批量上传中每上传一件衣服前，把它的 `includedRoots` 顶层物体 `activeSelf` 置 `true`，其他所有装扮的 `includedRoots` 顶层物体 `activeSelf` 置 `false`。
- 批处理结束（正常结束 / 异常 / 取消）都把这些顶层物体恢复到进入批处理前的 `activeSelf` 快照值。

**不做什么：**

- 不动 accessory 子物体的 `activeSelf`——Modular Avatar 在运行时会按 bool 参数接管它们的开关，用户在场景里手滑关掉的配件状态不影响上传后表现。
- 不动自动生成的 `_AvatarSwitcherMenu` / `_AccessoriesMenu` 下的 GameObject 的 `activeSelf`——它们在非当前轮次被 `EditorOnly` 剥离，是否 active 不影响 bundle。
- 不动缩略图流程。`AvatarVariantThumbnailCapture` 保持独立，维持它自己那套 snapshot/restore。

## 方案：扩展现有的 AvatarVariantTagGuard

[AvatarVariantTagGuard](../../../Editor/AvatarVariantTagGuard.cs) 目前快照 + 还原两件事：每个受控 GameObject 的 `tag` 和 `PipelineManager.blueprintId`。`ApplyActive(HashSet<GameObject> activeSet)` 这个方法现在只把 tag 翻成 `Untagged` / `EditorOnly`——语义上"Apply**Active**"本来就该指"哪几个是活的"。顺水推舟让它同时管 `activeSelf`。

CLAUDE.md 中已经明说了这个取舍：*"如果你新增了批处理中会被修改的状态，扩展这个 guard（或加一个并列的 guard），不要往 finally 里塞临时清理代码。"* 本设计选"扩展"。

### 数据结构改动

`AvatarVariantTagGuard` 内部新增：

```csharp
private readonly Dictionary<GameObject, bool> _activeSelfSnapshot;
```

- **只记录**需要被"强制摆正"的那批 GameObject——即所有装扮 `includedRoots` 的并集。
- 不包括 accessory 菜单 GameObject、`_AccessoriesMenu` 父节点、accessory 子物体。

### API 改动

`Capture` 签名新增一个参数：

```csharp
public static AvatarVariantTagGuard Capture(
    PipelineManager pm,
    IEnumerable<GameObject> controlledRoots,
    IEnumerable<GameObject> activeScopedRoots)
```

- `controlledRoots` — 现有语义不变（tag 翻转覆盖的完整受控集，含 accessory 菜单 GameObject、`_AccessoriesMenu` 父节点）。
- `activeScopedRoots` — 新增。只记录 `includedRoots` 并集。`Capture` 时为每个元素拍 `activeSelf` 快照，保存到 `_activeSelfSnapshot`。

`ApplyActive(HashSet<GameObject> activeSet)` 行为扩展：

- 保留现有的 tag 翻转（遍历 `_tagSnapshot.Keys`，按 `activeSet` 成员资格设 `Untagged` / `EditorOnly`）。
- **新增**：遍历 `_activeSelfSnapshot.Keys`，按 `activeSet` 成员资格调 `SetActive(activeSet.Contains(root))`。用 `Undo.RecordObject` 记录。

`Restore` / `Dispose` 行为扩展：

- 保留现有的 tag 还原 + blueprintId 还原。
- **新增**：遍历 `_activeSelfSnapshot`，把每个 GameObject 的 `activeSelf` 还原到快照值（用 `SetActive(snapshotValue)`，同样 `Undo.RecordObject`）。

顺序：Restore 时，tag 和 blueprintId 先还，activeSelf 后还（顺序不重要，但保持 tag→active 的一致性降低 Unity 内部 mark dirty 次数）。

### 调用点改动

[AvatarVariantSwitchWorkflow.RunBatchUploadAsync](../../../Editor/AvatarVariantSwitchWorkflow.cs) 约第 368–392 行。当前：

```csharp
var controlledRoots = plan.variants
    .SelectMany(variant => variant.includedRoots)
    .Where(root => root != null)
    .Where(root => !IsUnderMenuRoot(root, cfg))
    .Distinct()
    .ToList();

// 累加 accessory 菜单 GameObject 和 _AccessoriesMenu 父节点
...

guard = AvatarVariantTagGuard.Capture(pm, controlledRoots);
```

改成在累加 accessory 菜单之前，先把"仅 includedRoots 的并集"切出来单独传给 `Capture`：

```csharp
var includedRootsUnion = plan.variants
    .SelectMany(variant => variant.includedRoots)
    .Where(root => root != null)
    .Where(root => !IsUnderMenuRoot(root, cfg))
    .Distinct()
    .ToList();

var controlledRoots = new List<GameObject>(includedRootsUnion);

// （保持现有逻辑）把生成的配件 toggle GameObject 加入 controlledRoots
// （保持现有逻辑）把 _AccessoriesMenu 父节点加入 controlledRoots

guard = AvatarVariantTagGuard.Capture(pm, controlledRoots, includedRootsUnion);
```

`TryUploadOneVariantAsync` 里的 `guard.ApplyActive(activeSet)` 调用点**不变**——`activeSet` 已经包含了当前装扮的 `includedRoots`（line 405），所以扩展后的 `ApplyActive` 会正确地把当前装扮的 root 置 true、其他装扮的 root 置 false。

## 边界 & 不变式

- 批处理前后场景中所有装扮顶层 `includedRoots` 的 `activeSelf` 必须回到进入前的值。这个不变式已经和 tag guard 现有的"批处理前后场景状态必须一致"对齐。
- 异常路径覆盖：`guard.Dispose()` 在 `RunBatchUploadAsync` 的 `finally` 里调用，覆盖正常结束、`OperationCanceledException`、任何其它异常。
- 和缩略图流程正交：`AvatarVariantThumbnailCapture.CaptureCore` 有自己独立的 `activeSnapshot`，两个流程互不调用、互不依赖。即使在缩略图流程抛异常导致不完整还原的极端情况下，下一次批量上传进入时 `Capture` 还是会重新拍一次 snapshot，所以不会把"错误的 active 状态"当作基准。

## 受影响的组件 / 文件

1. [Editor/AvatarVariantTagGuard.cs](../../../Editor/AvatarVariantTagGuard.cs) — 加 `_activeSelfSnapshot` 字段，扩展 `Capture` 签名，扩展 `ApplyActive` 和 `Restore` 行为。
2. [Editor/AvatarVariantSwitchWorkflow.cs](../../../Editor/AvatarVariantSwitchWorkflow.cs) `RunBatchUploadAsync` — 构造 `includedRootsUnion`，传给新签名的 `Capture`。
3. `docs/superpowers/specs/2026-04-23-force-variant-activeself-on-upload-design.md` — 本文件。

**无需改动**：
- `Runtime/*`（运行时边界 / VRChat SDK EditorOnly 约束不动）
- OSC 桥（协议不变）
- `AvatarVariantThumbnailCapture`（各自独立）
- `AvatarVariantMenuBuilder`（菜单生成规则不变）
- `AvatarVariantMap` / 映射文件 schema（不变）

## 测试计划

项目没有自动测试套件（见 CLAUDE.md）。手动验收清单：

1. **基础场景**：三件装扮 A/B/C，场景里故意把 B 的 `includedRoots` 关了（`activeSelf=false`）。跑批量上传。
   - 观察 Inspector：A 轮到时 A=on、B=off、C=off；B 轮到时 A=off、B=on、C=off；C 轮到时类推。
   - 批处理正常结束后：A 回到进入前状态、B **仍然是关的**（用户手改状态保留）、C 回到进入前状态。
2. **VRChat 内验证**：A/B/C 三个 blueprint 都能正常显示衣服（不是空人/骨架）。特别是 B（场景里进入前是关的）。
3. **取消路径**：批处理中途点 Progress Window 的 Cancel。
   - 取消生效，场景 activeSelf 全部回到进入前状态（包括 B 仍然是关的）。
4. **异常路径**：故意断网模拟 S3 TLS 失败。失败对话框弹出后点「放弃」。
   - 场景 activeSelf 全部回到进入前状态。
5. **菜单生成不受影响**：`_AvatarSwitcherMenu` / `_AccessoriesMenu` 层级在批处理期间和结束后都保持 `activeSelf=true`（因为它们不在新增的 `_activeSelfSnapshot` 作用域内，也不在每轮的 `activeSet` 处理范围）。
6. **配件行为不变**：任选一个 variant 的 accessory bool toggle 进游戏测试，默认开/默认关行为和之前完全一致。

## 非目标 / YAGNI

- 不提供 UI 开关让用户「禁用这个强制摆正行为」。场景里手改 activeSelf 是临时辅助，上传时理所当然应该得到正确结果；没有使用场景需要保留"上传时 activeSelf 忠实于场景现状"这个语义。
- 不处理 accessory target 子物体的 activeSelf。Modular Avatar 接管，不碰。
- 不做 editor 可视化（高亮即将被强制关闭的物体等）。行为在 Progress Window 里已经足够透明。
