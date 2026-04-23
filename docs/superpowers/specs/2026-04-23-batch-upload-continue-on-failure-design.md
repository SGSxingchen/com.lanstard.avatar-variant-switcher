# 批量上传：单装扮失败不再杀整批 — 设计文档

- **日期**：2026-04-23
- **作者**：Claude + 用户协作
- **背景仓库**：`com.lanstard.avatar-variant-switcher`（VPM 包）

## 1. 背景与目标

[AvatarVariantSwitchWorkflow.RunBatchUploadAsync](../../../Editor/AvatarVariantSwitchWorkflow.cs) 里，主循环对每个装扮串行调 `IVRCSdkAvatarBuilderApi.BuildAndUpload`。**当前行为**：任何一个装扮的上传一抛异常（e.g., `TLS SecureChannelFailure` 之类的瞬时网络问题），外层 catch 直接 `throw`，整批死。

对用户的实际影响：假设有 7 个装扮，跑到第 5 个时 VRChat API 的 TLS 握手瞬时失败，前 4 个已经上传成功的白上（bundle 没白上，map 已持久化；但用户再点"批量上传"时会把这 4 个已成功的 bundle 重新编译、重新上传、CDN 版本号全部 +1，浪费网络 + 时间）。

**目标**：单个装扮失败不中断整批，把失败的装扮收集起来，在循环结束时让用户选择**在同一次会话内**重试失败子集。成功的装扮不重跑。

**非目标**（见 §2）。

## 2. 非目标（YAGNI）

以下明确不做：

- **自动重试**（在同一装扮内部、无需用户点按，带退避地重试 N 次）——上次会话用户明确选择 option 2 而非 option 3。
- **"编辑并重试"**：对话框里不允许修改装扮配置（比如重选缩略图）后再重试。改配置必须关窗口、重开批。
- **并行上传多个装扮**：VRC SDK builder 是 singleton，不适合并行。保持串行。
- **失败装扮的独立"重试"菜单入口**：跨会话的失败不记住，关窗口就完全重来。
- **细分错误类型的差异化处理**（e.g., 网络错误 vs 参数错误）：同等对待，只要抛了就算失败。
- **map 的"最后失败时间"字段**：不往 map schema 里塞错误元数据。

## 3. 当前行为回顾

```
BEGIN
  LockReloadAssemblies
  guard = TagGuard.Capture(...)
  builder = AcquireAsync(...)

  for i in 0..N-1:
    MarkBegin(i)
    try:
      ApplyActive / SetBlueprintId
      BuildAndUpload(...)                  ← 可能抛
      [benign catch: "already uploaded"]   ← 已有
      Upsert map, WriteAtomic
      MarkSuccess(i)
    catch OperationCanceledException: throw
    catch ex:
      MarkFailure(i)
      throw                                ← ★ 杀整批

  EndSessionSuccess
  DisplayDialog("批量上传完成")

FINALLY:
  guard.Dispose()    (还原 tag + blueprintId)
  SaveOpenScenes
  UnlockReloadAssemblies
```

## 4. 新行为

### 4.1 单装扮的 catch 改为"记录失败并 continue"

改动点：`AvatarVariantSwitchWorkflow.RunBatchUploadAsync` 内层 `catch (Exception ex)` 里的 `throw` 删掉，改为把 `(originalIndex, displayName, errorMessage)` 追加到本轮 pass 的 `failures` 列表。`OperationCanceledException` 和 benign "already uploaded" catch 的语义**不变**。

### 4.2 主循环外套一个 `while (true)`

```
pendingIndices = [0..N-1]
lastFailures   = []

while true:
    failures = RunSinglePass(pendingIndices)
    lastFailures = failures
    if failures.Count == 0:
        break
    if !DisplayDialog(list failures, "重试失败的装扮", "放弃"):
        break
    pendingIndices = failures.Select(f => f.originalIndex)
    progressWindow.PrepareRetry(pendingIndices)

if lastFailures.Count == 0:
    EndSessionSuccess(null)
    DisplayDialog("批量上传完成")
else:
    EndSessionFailed("部分失败：Y/N 个装扮未能上传，map 已保留成功项。")
    // 不再弹对话框，窗口底部 HelpBox 已有反馈
```

`RunSinglePass(indices)` 就是当前循环体的行为，只是：
- 遍历的是 `indices`（而非 0..N-1）
- 异常不外抛（除 `OperationCanceledException`），改为 append 到返回的 `failures` 列表

### 4.3 progress window 新增 `PrepareRetry(IList<int> indices)`

[AvatarVariantUploadProgressWindow](../../../Editor/AvatarVariantUploadProgressWindow.cs) 新增一个方法：

```csharp
public void PrepareRetry(IList<int> indicesToRetry)
{
    // 清掉上一轮的"会话结束"状态，让 UI 回到"进行中"模式
    _sessionEnded    = false;
    _cancelRequested = false;
    _finalMessage    = null;
    _finalSeverity   = MessageType.Info;
    _currentIndex    = -1;

    // 把失败行回退到 Pending（绿色/红色 → 灰条）
    foreach (var idx in indicesToRetry ?? Empty):
        if idx in range:
            row.Status        = Pending
            row.BlueprintId   = null
            row.ErrorMessage  = null
            row.StartedAt     = 0
            row.FinishedAt    = 0

    Log("开始重试 N 个失败装扮。")
    RequestRepaint()
}
```

其他方法（`MarkBegin / MarkSuccess / MarkFailure / EndSessionSuccess / EndSessionFailed / EndSessionCancelled`）一行不改。

### 4.4 失败对话框正文

示例：

```
以下 2 个装扮上传失败：

• 校服       — An error occurred while sending the request
• 水手服     — An error occurred while sending the request

是否重试这 2 个？
```

用 `EditorUtility.DisplayDialog(title, message, "重试失败的装扮", "放弃")`，返回 `bool`。

错误消息太长时截断到 ~120 chars 并加 `…`，防止对话框撑爆屏幕（单条错误消息有时会是多段 stack 字符串）。

## 5. 保留的不变量

- **`LockReloadAssemblies` / `UnlockReloadAssemblies` 配对**：最外层 `finally` 里解锁，**覆盖整个 `while` 循环包括所有重试 pass**。重试期间编译锁不释放。
- **`AvatarVariantTagGuard` 生命周期**：在最外层 `using`/`Dispose` 中，跨越整个 `while` 循环。不在重试之间重新 capture——场景里的受控 roots 集合在重试中不会变。每次 `RunSinglePass` 内部循环都会重新 `ApplyActive` + `SetBlueprintId`，所以不会有 tag / blueprint id 的状态泄漏。
- **map 原子写时机**：每个成功装扮结束时一次，失败的不写。多次 pass 之间 map 会被逐步填满，但任何时刻读都是一致的（`WriteAtomic` 保证）。
- **`OperationCanceledException`**：用户按"取消"或 cts 触发时仍立即上抛到最外层 catch，走 `EndSessionCancelled` + `guard.Dispose` + 解锁。重试对话框本身不是可取消对话框，但重试 pass 内部依然响应 cancel。
- **benign "already uploaded" catch**：不动。仍然是"bundle 已更新，缩略图 MD5 撞车，按成功算"。
- **`StartBatchUpload` 外层行为**：仍是 `async void` + `Debug.LogException` 兜 unhandled。正常部分失败**不抛异常**到外层，避免 Unity Console 红色报错（已经在 progress window HelpBox 里反馈了）。

## 6. 边界与错误情况

| 情况 | 行为 |
|------|------|
| 用户在重试 pass 中途按"取消" | `OperationCanceledException` 上抛，走 `EndSessionCancelled`，map 保留本轮之前已成功的项 |
| 重试 pass 里某个装扮又失败了 | append 到这一轮的 `failures`，pass 结束再弹对话框 |
| 重试 pass 里所有失败装扮都成功了 | `failures.Count == 0` → `while` 跳出 → 走成功分支 |
| 用户点"放弃" | `while` 跳出 → 走部分失败分支 → `EndSessionFailed` + 窗口 HelpBox |
| 所有装扮首 pass 就全成功 | `failures.Count == 0` 立刻跳出，行为和今天完全一致 |
| 在重试 pass 之前点"关闭窗口" | progress window 是 `EditorWindow`，关闭只是 hide；`RunBatchUploadAsync` 仍然持着引用继续调。但 **`EndSessionFailed/Success` 之前的 `sessionEnded = true` 还没设，用户不能按"关闭"**（按钮是条件渲染的）。所以这场景不存在。 |
| `guard.Dispose()` 失败 | 已在 finally 里 `Debug.LogException` 兜住；不影响 while 循环（已结束）。 |

## 7. 代码结构变化（提炼）

### `AvatarVariantSwitchWorkflow.cs`

- 保留现有 `StartBatchUpload(cfg)`（`async void` 入口）、`RunBatchUploadAsync(cfg)`、`BatchPlan.Snapshot`、tag guard 管理、builder gate 等。
- `RunBatchUploadAsync` 内重构：
  - 抽出一个私有 `async Task<FailureRecord?> TryUploadOneVariantAsync(int i, BatchPlan plan, AvatarVariantMap map, PipelineManager pm, IVRCSdkAvatarBuilderApi builder, AvatarVariantUploadProgressWindow window, CancellationToken ct)`，返回 `null`（成功）或 `FailureRecord`（失败）。`OperationCanceledException` 仍然上抛。内部包含：ApplyActive / SetBlueprintId / 构造 record / BuildAndUpload（带 benign catch）/ 校验 blueprintId / Upsert map / MarkBegin+MarkSuccess。
  - 外层 `while (true)` 如 §4.2 所述。
  - 新增 helper `string FormatFailureDialogMessage(IList<FailureRecord> failures, int totalN)`。
  - 新增 `private readonly struct FailureRecord { int originalIndex; string displayName; string errorMessage; }`。

### `AvatarVariantUploadProgressWindow.cs`

- 新增 `public void PrepareRetry(IList<int> indicesToRetry)`，约 20 行。
- 其他方法不动。

## 8. 测试计划（手动）

Unity 侧没有测试框架。手动回归清单：

1. **happy path**：7 个装扮全成功 → 对话框"批量上传完成。"。窗口全部绿条。
2. **首次失败 + 重试成功**：最可靠的复现手段是在跑到第 3 个装扮开始上传时临时把网络断开 ~1s 再恢复（防火墙规则 / 临时 disable 网卡 / 简单拔网线都行）。期望：窗口 3 号变红，pass 结束弹对话框，点"重试失败的装扮"，3 号回到 Pending（灰条），然后变黄（Uploading）、变绿（Success），对话框"批量上传完成。"。
3. **首次失败 + 重试又失败 + 放弃**：在步骤 2 的基础上，不恢复网络。期望：第一次 pass 3 号红，对话框；点"重试失败的装扮"，3 号又红，再弹对话框；点"放弃"，窗口底部红色 HelpBox "部分失败：1/7 ..."，map 里 1/2/4/5/6/7 都已写入；3 号在 map 中的 `blueprintId` 字段保持**进入本次批量上传前的原值**——若之前成功上传过就是旧 id，之前没成过就是空（本次失败不会污染 map）。
4. **用户点"取消"**：中途点取消，立即 `EndSessionCancelled`，窗口黄色 HelpBox，guard 正确还原（批处理前后场景 tag / blueprintId 一致）。
5. **首 pass 0 失败**：行为和今天完全一致，不弹任何多余对话框。

## 9. 迁移 / 兼容

- 无持久化 schema 变化。map 结构不动。
- 无公共 API 变化（`AvatarVariantSwitchConfig` / Inspector / 菜单都不动）。
- 现有的"批量上传"菜单项入口不动。
- PowerShell bridge / OSC 桥与本次改动无关。

## 10. 开放问题 / 延后决策

- 当前把 `DisplayDialog` 的错误消息硬截 120 char。这个上限没有严格理由，只是"看着合理"。可后续用户反馈后调。
- 若未来想加自动重试（transient 错误内 N 次 backoff），入口将是 `TryUploadOneVariantAsync` 内部，不影响本次 while 循环结构。
