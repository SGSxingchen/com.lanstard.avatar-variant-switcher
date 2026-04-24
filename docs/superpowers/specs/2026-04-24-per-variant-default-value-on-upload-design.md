# 批量上传时给每件装扮烤进各自的 AvatarVariant 默认值

## 背景 / 问题

所有装扮当前共用同一份 [AvatarVariantMenuBuilder.BuildAllParameters](../../../Editor/AvatarVariantMenuBuilder.cs) 产出的 `ModularAvatarParameters`，该组件里 `AvatarVariant` 的 `saved=true`、`defaultValue=cfg.defaultValue`（通常 0）。批量上传时每件 variant 都把这份组件烤进各自 blueprint 的 `VRCExpressionParameters`。结果：**每件 blueprint 的 AvatarVariant 都默认为 0，而且会 saved 持久化**。

### 用户层可观测的症状

1. **手动切装扮后立刻弹回**：用户在 VRChat 里（不管是走我们自己的菜单还是 VRChat 自己的收藏夹）切到变体 N → VRChat 加载变体 N 的 blueprint → VRChat 把 `AvatarVariant` 初始化为 defaultValue=0 → OSC 桥收到 `value=0` → 查表映射到变体 0 的 blueprintId → 和 `_currentAvatarId = avtr_变体N` 不一致 → 桥发 `/avatar/change avtr_变体0` → 回到第一件装扮。弹回现象。

2. **saved 值跨装扮污染**：用户在变体 N 上点"断罪旗袍"（value=2），VRChat 在变体 N 的 saved state 里把 `AvatarVariant=2` 存一下；桥发起切换到变体 2；完成。用户后续再次加载变体 N（通过菜单或收藏夹）→ VRChat 恢复 saved 值 `AvatarVariant=2` → 桥看到 value=2 对应变体 2 的 blueprintId，和当前 `avtr_变体N` 不一致 → 发切换到变体 2。原本想加载变体 N 的用户被弹到变体 2。

### 为什么之前没人发现

早期版本菜单按钮是 `ControlType.Button`，按下瞬时 set value、松开立刻回 0，根本没机会用 OSC 把切换真正发出去——发出去也被接下来的 value=0 echo 覆盖掉。现在菜单改成 Toggle（commit `eb10b63`）后，OSC 链路要真正生效就必须解决这个参数默认值问题，否则会有 saved 污染和加载后弹回两个症状。

## 范围

**做什么：**

1. 批量上传每件装扮前，把 avatar root 下 `_AvatarSwitcherMenu` 上挂的 `ModularAvatarParameters` 组件里 `AvatarVariant` 那一项的 `defaultValue` 临时改成当前变体的 `paramValue`。批处理结束（正常/取消/异常）还原。
2. 把主 `AvatarVariant` 参数的 `saved=true` 改成 `saved=false`（在 [AvatarVariantMenuBuilder.BuildAllParameters](../../../Editor/AvatarVariantMenuBuilder.cs) 里）。配件 bool 参数保持 `saved=true` 不动。

**不做什么：**

- 不改 [AvatarVariantMap](../../../Editor/AvatarVariantMap.cs) schema 和映射文件格式。`defaultValue` 字段仍然是"整个配置的逻辑默认值"（用于 OSC 桥参考、下次菜单生成）。每件 blueprint 烤进去的 defaultValue 是上传时临时覆盖，不进映射文件。
- 不改 OSC 桥 / 桥协议 / `/avatar/change` 行为。
- 不改 `AvatarVariantTagGuard`。它现在管三件事（tag / blueprintId / activeSelf），再塞第四件会把单一职责的边界推得更远；并列加一个 `AvatarVariantParamDefaultGuard` 更干净，CLAUDE.md 里也写了"扩展这个 guard（或加一个并列的 guard）"都接受。
- 不改 accessory bool 参数的 `saved`——它们的参数名每件 variant 都不一样（`Acc_<variantKey>_<idx>`），跨 variant 不会污染，且用户明确希望"配件开关被记住"。
- 不在 Inspector 暴露新字段。整个行为用户无感——映射文件里的 `defaultValue` 语义不变。
- 不提供"禁用 per-variant 覆盖"的选项。没有正当需求。

## 方案

### A. `saved = false` on AvatarVariant

改 [AvatarVariantMenuBuilder.BuildAllParameters](../../../Editor/AvatarVariantMenuBuilder.cs) 第一个 `ParameterConfig`（`nameOrPrefix = cfg.parameterName.Trim()` 那个）的 `saved = true` 改成 `saved = false`。其余字段 + 后面循环里 accessory 的 `ParameterConfig` 保持原样。

这样每次变体加载，VRChat 都用该变体的 ExpressionParameters 里烤进去的 `defaultValue` 初始化 `AvatarVariant`，不受历史 saved 值污染。

### B. 新增 AvatarVariantParamDefaultGuard

并列于 [AvatarVariantTagGuard](../../../Editor/AvatarVariantTagGuard.cs)，做同样的"快照-改-还原"动作，只不过作用对象是 MA 组件里的 `defaultValue` 字段。

```csharp
public sealed class AvatarVariantParamDefaultGuard : IDisposable
{
    public static AvatarVariantParamDefaultGuard Capture(AvatarVariantSwitchConfig cfg);
    public void SetDefault(int value);
    public void Restore();
    public void Dispose();   // => Restore()
}
```

**`Capture`**：通过 `cfg.AvatarRoot.transform.Find("_AvatarSwitcherMenu")` 找到菜单根，取上面挂的 `ModularAvatarParameters` 组件。在它的 `parameters` 列表里按 `nameOrPrefix == cfg.parameterName.Trim()` 找到 `AvatarVariant` 那一项。快照它当前的 `defaultValue` 到字段 `_originalDefault`，同时保存组件引用和该项在列表中的索引。

如果找不到菜单根、组件、或参数项——抛 `InvalidOperationException`。调用点 `RunBatchUploadAsync` 在 `Capture` 之前刚调过 `AvatarVariantMenuBuilder.Generate(cfg)`，菜单组件必然存在；抛异常是防御性的——出现说明 caller 违反了契约，`finally` 会走到 `guard.Dispose()` 也没事（`_restored` 保护重复 restore）。

**`SetDefault(int value)`**：`parameters[index].defaultValue = (float)value`。因为 `ParameterConfig` 是 struct，要先取出副本修改再写回列表。用 `Undo.RecordObject(_component, "...")` 记录；`EditorUtility.SetDirty(_component)` 标脏。

**`Restore`**：把 `parameters[index].defaultValue` 还原为 `_originalDefault`。同样的 Undo + SetDirty。设 `_restored = true`。

**`Dispose`**：调 `Restore`。幂等。

### C. 接入 RunBatchUploadAsync

[AvatarVariantSwitchWorkflow.RunBatchUploadAsync](../../../Editor/AvatarVariantSwitchWorkflow.cs) 里在 `guard = AvatarVariantTagGuard.Capture(...)` 这一行之后立刻 `paramDefaultGuard = AvatarVariantParamDefaultGuard.Capture(cfg)`。

`TryUploadOneVariantAsync` 里，在 `guard.ApplyActive(activeSet)` 之后 `guard.SetBlueprintId(...)` 之前，插入 `paramDefaultGuard.SetDefault(variant.paramValue)`。

`finally` 块里 `guard.Dispose()` 之后再 `paramDefaultGuard.Dispose()`（顺序不重要，都有 `_restored` 幂等守卫；但为了可读性和对称性放一起）。

### D. 上传流程时序

```
RunBatchUploadAsync
├── Generate(cfg)                          // 建菜单，MA 组件出现
├── tagGuard.Capture(...)                  // 快照 tag/blueprintId/activeSelf
├── paramDefaultGuard.Capture(cfg)         // 快照当前 AvatarVariant defaultValue
│
├── for each variant N:
│   ├── tagGuard.ApplyActive(activeSet)    // tag 翻转 + activeSelf 摆正
│   ├── paramDefaultGuard.SetDefault(N)    // 把 MA 里 AvatarVariant.defaultValue 改成 N
│   ├── tagGuard.SetBlueprintId(...)       // 写 pipeline blueprintId
│   ├── SaveOpenScenes                     // 把当前场景状态（含被改的 defaultValue）落盘
│   └── BuildAndUpload                     // MA 在构建时读组件，把 defaultValue=N 烤进
│                                          //   该 blueprint 的 VRCExpressionParameters
│
└── finally:
    ├── tagGuard.Dispose()                 // 还原 tag/blueprintId/activeSelf
    ├── paramDefaultGuard.Dispose()        // 还原 AvatarVariant.defaultValue
    └── SaveOpenScenes                     // 落盘还原后的状态
```

批处理前 / 批处理后，`_AvatarSwitcherMenu/ModularAvatarParameters` 里 `AvatarVariant.defaultValue` 保持和进入前一致（= `cfg.defaultValue`）。

## 边界 & 不变式

- **批处理前后 scene 状态一致**：这个不变式已经是 tagGuard 的基本契约，新 guard 遵守同样规则。
- **取消路径**：`RunBatchUploadAsync` 的 `finally` 在 `catch OperationCanceledException` 之后照样执行。两个 guard 都 Dispose。场景还原。
- **异常路径**：同上。
- **嵌套调用**：`StartBatchUpload` 有 `_busy` 守卫，不可能嵌套。
- **paramGuard 独立于 tagGuard 的失败**：如果 `paramDefaultGuard.Capture` 抛异常（菜单不完整），`tagGuard` 已经被 Capture 了 → `finally` 先 `tagGuard.Dispose()` 还原 → 再尝试 `paramDefaultGuard?.Dispose()` 但它从未成功构造，`?.` null 保护。
- **MA 组件被用户手动删/改**：极端场景。`_component` 引用的组件被删后，`EditorUtility.SetDirty` 和 `Undo.RecordObject` 会抛 `ArgumentNullException`。我们的 `SetDefault` 和 `Restore` 不做额外兜底——用户动了生成的菜单根结构是越权行为，应该重新跑 Generate。catch 走到外层 `finally` 里报错，日志打出来，用户重试。

## 受影响的组件 / 文件

1. [Editor/AvatarVariantMenuBuilder.cs](../../../Editor/AvatarVariantMenuBuilder.cs) — `BuildAllParameters` 里第一个 ParameterConfig 的 `saved=true` → `saved=false`。
2. **新建** [Editor/AvatarVariantParamDefaultGuard.cs](../../../Editor/AvatarVariantParamDefaultGuard.cs) — 新的 guard 类。
3. [Editor/AvatarVariantSwitchWorkflow.cs](../../../Editor/AvatarVariantSwitchWorkflow.cs) — 三处改动：
   - `RunBatchUploadAsync` 里，`tagGuard` 捕获后紧接着 `paramDefaultGuard = Capture(cfg)`；
   - `TryUploadOneVariantAsync` 里，`ApplyActive` 之后 `SetBlueprintId` 之前插入 `paramDefaultGuard.SetDefault(variant.paramValue)`；
   - `finally` 里 `tagGuard.Dispose()` 之后加 `paramDefaultGuard?.Dispose()`。

**无需改动：**

- `AvatarVariantTagGuard` — 不动，它管的三个状态是正交的。
- `AvatarVariantMap` / 映射文件 schema — 不动。
- OSC 桥 / 协议 — 不动。
- `AvatarVariantSwitchConfig` / Inspector — 不动。
- README — 行为是静默修复"菜单/手动切装扮后被弹回第一件"的 bug，用户侧没有新概念要理解。

## 测试计划

项目没有自动测试套件（见 CLAUDE.md）。手动验收清单：

### 前置

- 一个已经用过批量上传、在 VRChat 上能看见多件装扮的 avatar 工程（用户当前 avatar 符合）。
- 把当前分支改动拉到本地，在 Unity 里重编译通过（Console 无红色 error）。
- OSC 桥用 `--debug` 起着，好观察切换消息。

### 基础场景：单件装扮加载不弹回

1. 在 Unity 里点 Inspector 的"批量上传" → 全部 7 件重传。
2. 在游戏里，打开收藏夹点任意一件非 0 值的装扮（例如变体 2 "断罪旗袍"）。
3. **预期**：
   - 头像变成 2 号装扮并稳定；不在 1 秒内弹回 0 号。
   - 桥日志：
     - 收到 `/avatar/change avtr_断罪旗袍`
     - 收到 `value=2 (断罪旗袍)`，然后日志打 `already on avtr_断罪旗袍` 或不再打切换行（抑制成功）。
4. 失败诊断：
   - 桥日志显示 `value=0 (黑曜石)` 被当作切换目标 → MA 组件的 `defaultValue` 没被改到，检查 `paramDefaultGuard.SetDefault` 是否真走了。打开 Unity 里的 `_AvatarSwitcherMenu` 的 `ModularAvatarParameters` 组件，重传过程中用 Inspector 看 `AvatarVariant` 的 defaultValue 是否随 variant 切换。

### 菜单切换：Toggle 停留

1. 在 VRChat 内当前装扮打开菜单 → 切换装扮子菜单 → 点"睡衣"（value=4）。
2. **预期**：
   - 菜单里"睡衣"高亮，其他灭掉。状态稳定不闪烁。
   - 桥日志：`value=4 (睡衣)` → `/avatar/change avtr_睡衣` → 收到 echo → `already on ...` 抑制后续。
   - 加载完后菜单继续高亮"睡衣"。

### 跨会话持久（saved=false 行为验证）

1. 停 VRChat（关游戏不只是 leave instance）。
2. 重新进 VRChat，加载变体 2。
3. **预期**：
   - 不走我们菜单，也不 OSC → 纯 VRChat 加载。
   - 头像稳定在变体 2；菜单里高亮"断罪旗袍"（value=2）。
4. 说明：变体 2 的 blueprint 烤的 defaultValue=2，saved=false，每次新会话都落到默认 2，菜单和头像自洽。

### 批处理前后场景状态一致

1. 在 Unity 里打开 avatar 场景。先确认 `_AvatarSwitcherMenu/ModularAvatarParameters` 组件里 `AvatarVariant` 那一项的 `Default Value` = `cfg.defaultValue`（假设 0）。
2. 跑批量上传，等全部成功。
3. 打完看 `AvatarVariant` 的 `Default Value`：**必须仍然是 0**（cfg.defaultValue），没被残留成最后一件 variant 的值。
4. 失败诊断：如果 defaultValue 被残成 6（或任何非 0），说明 `paramDefaultGuard.Dispose`/`Restore` 没跑到——检查 `finally` 块；或者 `_restored` 守卫意外阻止了还原。

### 取消路径

1. 跑批量上传，上传到第 3 件时在 Progress Window 点"取消"。
2. 取消对话框出现后确认。
3. 检查 scene：
   - `_AvatarSwitcherMenu/ModularAvatarParameters` 里 `AvatarVariant.defaultValue = 0`（cfg.defaultValue）。
   - tag / activeSelf / blueprintId 都回到进入前状态（tagGuard 已有验收，只是确认没被新 guard 干扰）。

### 异常路径

1. 故意断网跑批量上传。
2. 失败对话框弹出后点"放弃"。
3. 检查 scene：同取消路径的预期。

### 配件行为不变

1. 跑完批量上传后进 VRChat，切到任一带 accessory 的 variant。
2. 切 accessory toggle：on/off 状态正常，加载该 variant 时 accessory 开关状态被保留（因为 accessory 参数 saved=true 不变）。

## 非目标 / YAGNI

- 不让用户在 Inspector 里手动指定某件 variant 的 per-variant defaultValue。自动用 `paramValue` 就是对的。
- 不把逻辑 defaultValue（cfg.defaultValue）废弃——它仍然用来标"当哪件都没加载时菜单应该高亮谁"，这个语义是 `GenerateMenu`（非批处理路径）仍然需要的。
- 不给 `AvatarVariantParamDefaultGuard` 加"容忍菜单被手动删掉"的兜底。用户动了生成菜单是越权行为，应该先重新 Generate。
- 不在桥端做"加载后第一个 param change 是 defaultValue 就忽略"这类防御性去重。让上传端把正确值烤进去就够了，桥端保持简单。
