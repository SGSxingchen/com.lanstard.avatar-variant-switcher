# 批量上传时给每件装扮烤进各自的 AvatarVariant 默认值 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 批量上传每件装扮前，临时把 `_AvatarSwitcherMenu/ModularAvatarParameters` 里 `AvatarVariant` 的 `defaultValue` 改成当前 variant 的 `paramValue`；批处理结束（正常/取消/异常）还原。同时把 `AvatarVariant` 参数的 `saved=true` 改成 `saved=false`，以避免跨 avatar 的持久值污染。

**Architecture:** 新增一个并列于 [AvatarVariantTagGuard](../../../Editor/AvatarVariantTagGuard.cs) 的 `AvatarVariantParamDefaultGuard`，同样走"Capture → SetDefault → Restore → Dispose"模式。`RunBatchUploadAsync` 在既有 tagGuard 旁边多 Capture 一个新 guard，每轮 upload 前 `SetDefault(variant.paramValue)`，`finally` 里 Dispose。[AvatarVariantMenuBuilder.BuildAllParameters](../../../Editor/AvatarVariantMenuBuilder.cs) 里主参数的 `saved=true` 改 `saved=false`，配件 bool 参数不动。

**Tech Stack:** C# (Unity Editor, .NET Framework/Standard)；Unity 2022.3+ Editor-only；VRC SDK3A + Modular Avatar core。无自动测试框架（CLAUDE.md 声明），Editor 代码验证靠 Unity 重编译 + 手动场景验收。

**Spec:** [docs/superpowers/specs/2026-04-24-per-variant-default-value-on-upload-design.md](../specs/2026-04-24-per-variant-default-value-on-upload-design.md)

---

## File Structure

- **Modify**: [Editor/AvatarVariantMenuBuilder.cs](../../../Editor/AvatarVariantMenuBuilder.cs) — `BuildAllParameters` 里第一个 `ParameterConfig`（主 int 参数）的 `saved` 字段从 `true` 改成 `false`。一行修改。其他循环里的 accessory bool 参数保持 `saved = true` 不变。
- **Create**: [Editor/AvatarVariantParamDefaultGuard.cs](../../../Editor/AvatarVariantParamDefaultGuard.cs) — 新建 guard 类。单一职责：捕获批处理前 MA 组件里 `AvatarVariant.defaultValue` 的值、允许运行中覆盖、`Dispose`/`Restore` 还原。
- **Modify**: [Editor/AvatarVariantSwitchWorkflow.cs](../../../Editor/AvatarVariantSwitchWorkflow.cs) `RunBatchUploadAsync` 方法内三处插入：`tagGuard` Capture 之后 + `TryUploadOneVariantAsync` 内 `ApplyActive` 之后 + `finally` 块 tagGuard Dispose 之后。

**不修改的文件：** Runtime 目录、OSC 桥、`AvatarVariantTagGuard`、`AvatarVariantThumbnailCapture`、`AvatarVariantMap`、Inspector (`AvatarVariantSwitchConfigEditor`)、README、CHANGELOG——见 spec 的"受影响的组件 / 文件"和"非目标"章节。

---

### Task 1: 主 AvatarVariant 参数 saved=false

**Files:**
- Modify: `Editor/AvatarVariantMenuBuilder.cs` — `BuildAllParameters` 方法第一个 `ParameterConfig`

> 本 Task 单独成一个 commit：它是独立、可观察的修复（"saved 值跨装扮污染"这条路径不再存在）；即使 Task 2/3 还没做完也能单独合入。Task 2/3 必须一起提交（签名与唯一 use site 配对）。

- [ ] **Step 1: 复核当前 BuildAllParameters 第一个 ParameterConfig**

Read: `Editor/AvatarVariantMenuBuilder.cs` 第 141–176 行（整个 `BuildAllParameters` 方法）。

确认第一个 `ParameterConfig` 当前长这样：

```csharp
new ParameterConfig
{
    nameOrPrefix = cfg.parameterName.Trim(),
    syncType = ParameterSyncType.Int,
    localOnly = false,
    saved = true,
    hasExplicitDefaultValue = true,
    defaultValue = cfg.defaultValue
}
```

- [ ] **Step 2: 改 saved=true → saved=false**

Edit `Editor/AvatarVariantMenuBuilder.cs`:

old_string（整个 `ParameterConfig` 块，连上下文确保唯一）:

```csharp
            var list = new List<ParameterConfig>
            {
                new ParameterConfig
                {
                    nameOrPrefix = cfg.parameterName.Trim(),
                    syncType = ParameterSyncType.Int,
                    localOnly = false,
                    saved = true,
                    hasExplicitDefaultValue = true,
                    defaultValue = cfg.defaultValue
                }
            };
```

new_string:

```csharp
            var list = new List<ParameterConfig>
            {
                new ParameterConfig
                {
                    nameOrPrefix = cfg.parameterName.Trim(),
                    syncType = ParameterSyncType.Int,
                    localOnly = false,
                    // 主 AvatarVariant 参数不走 saved——每件 blueprint 烤进各自的 defaultValue，
                    // 加载即落到该 variant 的值，和 blueprintId 自洽；saved=true 会让 VRChat
                    // 把菜单中间态值跨 variant 持久化，触发加载后被弹走（详见 spec）。
                    saved = false,
                    hasExplicitDefaultValue = true,
                    defaultValue = cfg.defaultValue
                }
            };
```

变动点说明：
- 仅改第一个 ParameterConfig 的 `saved` 字段为 `false`。
- 加一段两行的中文注释说明为什么——主参数和配件 bool 参数的取舍不一样，后人看这段会想知道原因。
- 下面循环里 accessory bool 参数的 `ParameterConfig` **不动**，仍然 `saved = true`（accessory 参数名每件 variant 唯一，不会跨 variant 污染）。

- [ ] **Step 3: 打开 Unity 让 Editor 重编译，检查 Console 无错**

手动操作：
1. 切到 Unity Editor，触发脚本重编译（右下角 spinner 出现）。
2. 等 compiling 结束，切到 Console 窗口。
3. 预期：无红色 error；允许已有 warning，不应新增 warning。

若 Unity 未打开，退化验证：

```bash
dotnet build ./Tools~/AvatarVariantOscBridge/AvatarVariantOscBridge.csproj
```

这**不**验证 Editor asmdef（OSC 桥是独立 csproj），仅证明本次改动没波及 OSC 桥。Editor 编译结果必须等用户在 Unity 里确认。

- [ ] **Step 4: Commit**

```bash
git add Editor/AvatarVariantMenuBuilder.cs
git commit -m "$(cat <<'EOF'
fix: 主 AvatarVariant 参数 saved=false 避免跨装扮持久值污染

之前主 AvatarVariant 参数 saved=true，VRChat 会在每件 avatar
的 saved state 里记录它的最后值。用户在变体 N 上按"变体 M"的
菜单 Toggle 时，VRChat 瞬时把 AvatarVariant 设成 M 并在变体 N
的 saved state 里存下来；桥完成切换到变体 M 后，用户下次加载
变体 N 会读出 saved 值 M，桥看到和当前 blueprintId 不符，立刻
又发起一次切换——循环弹走。

主参数 saved=false + per-variant 烤 defaultValue（下一个 commit）
让每次加载都用该 variant 自己的 defaultValue 初始化。配件参数
的 saved 不动：参数名每件 variant 唯一（Acc_<variantKey>_<idx>），
不会跨 variant 污染，且用户明确希望配件开关被记住。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: 新增 AvatarVariantParamDefaultGuard + 接入 workflow

**Files:**
- Create: `Editor/AvatarVariantParamDefaultGuard.cs`
- Modify: `Editor/AvatarVariantSwitchWorkflow.cs` — `RunBatchUploadAsync` 三处插入

> 这两处改动要**在同一个 commit 里提交**——新 guard 的 `Capture`/`SetDefault`/`Dispose` 是 public API 但唯一调用点在 workflow；如果分成两个 commit，第一个 commit 是无调用的死代码，第二个 commit 才把它用上。合成一次 commit 既保证中间状态可编译可用，也让 diff 可读性更好（看得到 guard 和 use site 如何配对）。

- [ ] **Step 1: 复核当前 AvatarVariantTagGuard.cs（参考设计风格）**

Read: `Editor/AvatarVariantTagGuard.cs` 整个文件。

关注几个模式：
- `private sealed class` + `IDisposable`
- 构造函数 `private`，静态 `Capture` 方法作为工厂
- `bool _restored` 防止重复 Restore
- `Dispose()` 调 `Restore()`
- 每个场景修改都用 `Undo.RecordObject(..., "...")` + `EditorUtility.SetDirty(...)`

新 guard 照搬这些风格。

- [ ] **Step 2: 新建 AvatarVariantParamDefaultGuard.cs**

Write to `Editor/AvatarVariantParamDefaultGuard.cs`:

```csharp
using System;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    /// <summary>
    /// 批量上传期间临时把 _AvatarSwitcherMenu/ModularAvatarParameters 里
    /// AvatarVariant 那一项的 defaultValue 改成当前 variant 的 paramValue，
    /// 结束时（正常 / 取消 / 异常）还原到进入批处理前的值。
    ///
    /// 并列于 AvatarVariantTagGuard——各自管自己一类状态，互不依赖。
    /// </summary>
    public sealed class AvatarVariantParamDefaultGuard : IDisposable
    {
        private readonly ModularAvatarParameters _component;
        private readonly int _paramIndex;
        private readonly float _originalDefault;
        private bool _restored;

        private AvatarVariantParamDefaultGuard(
            ModularAvatarParameters component,
            int paramIndex,
            float originalDefault)
        {
            _component = component;
            _paramIndex = paramIndex;
            _originalDefault = originalDefault;
        }

        public static AvatarVariantParamDefaultGuard Capture(AvatarVariantSwitchConfig cfg)
        {
            if (cfg == null)
            {
                throw new ArgumentNullException("cfg");
            }
            if (cfg.AvatarRoot == null)
            {
                throw new InvalidOperationException("无法解析 Avatar Root。");
            }

            var menuRoot = cfg.AvatarRoot.transform.Find(AvatarVariantMenuBuilder.GeneratedMenuRootName);
            if (menuRoot == null)
            {
                throw new InvalidOperationException("找不到 _AvatarSwitcherMenu——批处理前 Generate 是否执行过？");
            }

            var component = menuRoot.GetComponent<ModularAvatarParameters>();
            if (component == null)
            {
                throw new InvalidOperationException("_AvatarSwitcherMenu 上缺少 ModularAvatarParameters 组件。");
            }

            var paramName = (cfg.parameterName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(paramName))
            {
                throw new InvalidOperationException("cfg.parameterName 为空。");
            }

            var list = component.parameters;
            if (list == null)
            {
                throw new InvalidOperationException("ModularAvatarParameters.parameters 为空。");
            }

            var index = -1;
            for (var i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (string.IsNullOrWhiteSpace(entry.nameOrPrefix))
                {
                    continue;
                }

                if (string.Equals(entry.nameOrPrefix.Trim(), paramName, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                throw new InvalidOperationException(
                    string.Format("在 ModularAvatarParameters.parameters 里找不到名为 \"{0}\" 的条目。", paramName));
            }

            return new AvatarVariantParamDefaultGuard(component, index, list[index].defaultValue);
        }

        public void SetDefault(int value)
        {
            if (_component == null)
            {
                return;
            }

            var list = _component.parameters;
            if (list == null || _paramIndex < 0 || _paramIndex >= list.Count)
            {
                return;
            }

            var entry = list[_paramIndex];
            var newDefault = (float)value;
            if (entry.defaultValue == newDefault)
            {
                return;
            }

            Undo.RecordObject(_component, "Set AvatarVariant default value");
            entry.defaultValue = newDefault;
            list[_paramIndex] = entry;
            EditorUtility.SetDirty(_component);
        }

        public void Restore()
        {
            if (_restored)
            {
                return;
            }

            if (_component == null)
            {
                _restored = true;
                return;
            }

            var list = _component.parameters;
            if (list != null && _paramIndex >= 0 && _paramIndex < list.Count)
            {
                var entry = list[_paramIndex];
                if (entry.defaultValue != _originalDefault)
                {
                    Undo.RecordObject(_component, "Restore AvatarVariant default value");
                    entry.defaultValue = _originalDefault;
                    list[_paramIndex] = entry;
                    EditorUtility.SetDirty(_component);
                }
            }

            _restored = true;
        }

        public void Dispose()
        {
            Restore();
        }
    }
}
```

实现要点说明：
- `Capture(cfg)`：五道防御性校验——cfg 非空、AvatarRoot 非空、menuRoot 存在、ModularAvatarParameters 组件存在、parameters 列表存在、能找到 paramName 对应的条目。任何一步失败抛 `InvalidOperationException`；调用点在 `RunBatchUploadAsync` 里 `AvatarVariantMenuBuilder.Generate(cfg)` 刚跑过之后立即 Capture，这些条件不应该失败，抛异常=违反 caller 契约。
- `SetDefault(int)`：`ParameterConfig` 是 struct，`list[i]` 返回 copy；要改必须取 copy → 改 → 写回列表。`(float)value` 显式转型因为 `defaultValue` 是 `float`。`entry.defaultValue == newDefault` 跳过无变化以免多余 mark dirty。
- `Restore()`：`_restored` 幂等保护。`_component` 在上传过程中理论上不会被销毁，但防御性 null 检查不花钱。
- `Dispose`：调 Restore。

- [ ] **Step 3: 复核当前 RunBatchUploadAsync 要插入的三处位置**

Read: `Editor/AvatarVariantSwitchWorkflow.cs`。

确认三处位置：

**位置 A**：方法顶部 `AvatarVariantTagGuard guard = null;` 之后。
**位置 B**：`guard = AvatarVariantTagGuard.Capture(pm, controlledRoots, includedRootsUnion);` 这一行之后。
**位置 C**：`TryUploadOneVariantAsync` 内 `guard.ApplyActive(activeSet);` 之后、`guard.SetBlueprintId(...)` 之前。
**位置 D**：`finally` 里 `guard.Dispose();` 所在 try/catch 块之后、新开一个 try/catch 调 `paramDefaultGuard?.Dispose()`。

- [ ] **Step 4: 在 RunBatchUploadAsync 顶部声明 paramDefaultGuard 局部变量（位置 A）**

Edit `Editor/AvatarVariantSwitchWorkflow.cs`:

old_string:

```csharp
            _busy = true;
            using var cts = new CancellationTokenSource();
            AvatarVariantTagGuard guard = null;
            AvatarVariantUploadProgressWindow progressWindow = null;
            EditorApplication.LockReloadAssemblies();
```

new_string:

```csharp
            _busy = true;
            using var cts = new CancellationTokenSource();
            AvatarVariantTagGuard guard = null;
            AvatarVariantParamDefaultGuard paramDefaultGuard = null;
            AvatarVariantUploadProgressWindow progressWindow = null;
            EditorApplication.LockReloadAssemblies();
```

- [ ] **Step 5: 在 tagGuard Capture 后立即 Capture paramDefaultGuard（位置 B）**

Edit `Editor/AvatarVariantSwitchWorkflow.cs`:

old_string:

```csharp
                guard = AvatarVariantTagGuard.Capture(pm, controlledRoots, includedRootsUnion);

                var builder = await AvatarVariantBuilderGate.AcquireAsync(cts.Token);
```

new_string:

```csharp
                guard = AvatarVariantTagGuard.Capture(pm, controlledRoots, includedRootsUnion);
                paramDefaultGuard = AvatarVariantParamDefaultGuard.Capture(cfg);

                var builder = await AvatarVariantBuilderGate.AcquireAsync(cts.Token);
```

- [ ] **Step 6: 在 TryUploadOneVariantAsync 内 ApplyActive 之后插入 SetDefault（位置 C）**

Edit `Editor/AvatarVariantSwitchWorkflow.cs`:

old_string:

```csharp
                    guard.ApplyActive(activeSet);

                    guard.SetBlueprintId(ResolveExistingBlueprintId(map, variant.variantKey, variant.paramValue, variant.legacyUploadedBlueprintId));
```

new_string:

```csharp
                    guard.ApplyActive(activeSet);
                    paramDefaultGuard.SetDefault(variant.paramValue);

                    guard.SetBlueprintId(ResolveExistingBlueprintId(map, variant.variantKey, variant.paramValue, variant.legacyUploadedBlueprintId));
```

- [ ] **Step 7: 在 finally 里 tagGuard Dispose 之后加 paramDefaultGuard Dispose（位置 D）**

Edit `Editor/AvatarVariantSwitchWorkflow.cs`:

old_string:

```csharp
            finally
            {
                try
                {
                    if (guard != null)
                    {
                        guard.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }

                try
                {
                    EditorSceneManager.SaveOpenScenes();
                }
```

new_string:

```csharp
            finally
            {
                try
                {
                    if (guard != null)
                    {
                        guard.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }

                try
                {
                    if (paramDefaultGuard != null)
                    {
                        paramDefaultGuard.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }

                try
                {
                    EditorSceneManager.SaveOpenScenes();
                }
```

变动点说明：
- 独立的 try/catch 块，和 tagGuard 的保持同样风格——一个 guard Dispose 抛异常不影响下一个。
- 新块在 tagGuard Dispose 之后、SaveOpenScenes 之前：顺序不重要（两个 guard 的状态正交），但放在 SaveOpenScenes 之前，是确保 Dispose 之后 SaveOpenScenes 一次性把两个 guard 都还原的结果落盘。

- [ ] **Step 8: Unity 里重编译，Console 无错**

同 Task 1 Step 3。

若 Unity 未打开，退化验证：

```bash
dotnet build ./Tools~/AvatarVariantOscBridge/AvatarVariantOscBridge.csproj
```

- [ ] **Step 9: Commit**

```bash
git add Editor/AvatarVariantParamDefaultGuard.cs Editor/AvatarVariantSwitchWorkflow.cs
git commit -m "$(cat <<'EOF'
fix: 批量上传每件烤进各自的 AvatarVariant defaultValue

并列于 AvatarVariantTagGuard 新增 AvatarVariantParamDefaultGuard，
在批处理前 Capture _AvatarSwitcherMenu 上的
ModularAvatarParameters 组件里 AvatarVariant 条目的 defaultValue；
TryUploadOneVariantAsync 里每件上传前 SetDefault(paramValue)，
MA 在 BuildAndUpload 时把这个 defaultValue 烤进该 blueprint 的
VRCExpressionParameters；finally 里 Dispose 还原。

配合上一个 commit（主参数 saved=false），每件 blueprint 加载时
AvatarVariant 落到自己的 defaultValue=N，桥看 value=N 匹配
_currentAvatarId=avtr_变体N，抑制重发——手动切装扮 / 菜单切装扮
都不会被弹回第一件。

批处理前后 scene 里 ModularAvatarParameters 的 AvatarVariant
defaultValue 不变（保持 cfg.defaultValue）——guard 的 Restore 覆盖
了正常 / 取消 / 异常三条路径。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: 手动验收（按 spec "测试计划"走）

**Files:**
- None — 纯手动 Unity + VRChat 内验证

**前置准备**：
- Unity 里当前分支已编译通过（Task 1 Step 3 / Task 2 Step 8 已做）。
- 当前 avatar 已经走过一次批量上传（现网版本），映射文件里每件 variant 都有 blueprintId。
- OSC 桥用 `--debug` 起着，看得到 `/avatar/change` 和 `value=N` 日志。
- VRChat 里这些 variant 的 blueprint 已经加⭐收藏。

> 验收与修复 OSC mDNS 发现问题（还在另一条支线上等用户跑 --debug 日志）**相对正交**——即使 OSC 桥 mDNS 发现还没调通，本 Task 的"批处理前后 scene 状态一致"验收和"重传"本身仍然可以做。桥发现失败时，游戏内表现是切换菜单点了没反应，这个 Task 不能完整端到端验收，但能单独 cover 到"批量上传正确写入每件 blueprint 的 defaultValue"。

- [ ] **Step 1: 重新批量上传 7 件装扮**

操作：
1. 打开 Unity 场景。
2. Inspector 里确认 `_AvatarSwitcherMenu/ModularAvatarParameters` 组件里 `AvatarVariant` 那一项的 Default Value = 0（或 cfg.defaultValue 的值）。
3. 点"批量上传"。
4. 等 7 件全部成功。

预期：
- Progress Window 显示 7/7 成功。
- 批处理结束对话框"批量上传完成"弹出。

过程中可观察（可选）：在上传某件 variant 时切到 Hierarchy，选中 `_AvatarSwitcherMenu`，在 Inspector 里看 `ModularAvatarParameters` 组件 `AvatarVariant` 条目的 Default Value——它应该随 variant 切换变动（变体 0 时是 0、变体 1 时是 1、……）。

- [ ] **Step 2: 确认批处理后 scene 状态还原**

操作：批处理结束后，在 Hierarchy 里选中 `_AvatarSwitcherMenu`。Inspector 里看 `ModularAvatarParameters` 组件：

预期：`AvatarVariant` 条目的 Default Value **必须是 0**（cfg.defaultValue），不是最后一件 variant 的值（比如 6）。

失败诊断：如果 Default Value 变成了 6 或其它非 cfg.defaultValue 的值：
- guard 的 Restore 没跑到：检查 `finally` 块里 `paramDefaultGuard?.Dispose()` 是否被执行（通过加日志 Debug.Log("paramDefaultGuard dispose called") 临时 verify 一次）。
- `_restored` 守卫意外阻止：检查没有多处 Dispose 的 race（目前只有 `finally` 一处，理论上不会出）。

- [ ] **Step 3: 手动加载任一非 0 变体不弹回**

操作：
1. 进 VRChat。
2. 从收藏夹加载变体 2（"断罪旗袍"）——不通过我们自己的菜单，直接走 VRChat 自己的 avatar 选择。
3. 等加载完成。

预期：
- 头像变成变体 2，稳定不弹走。
- 桥日志（`--debug` 模式）：
  ```
  ... current avatar -> avtr_<变体2>
  ... rx from ...: resp ... A=[A /avatar/parameters/AvatarVariant -> 2]  // 或类似
  ... value=2 (断罪旗袍): already on avtr_<变体2>.   // 抑制成功
  ```

失败诊断：如果日志里出现 `value=0 (黑曜石)` 被当作切换目标：
- 变体 2 的 blueprint 烤进去的 defaultValue 仍然是 0 → guard.SetDefault 没真的影响到 BuildAndUpload 读到的组件状态。
- 可能是 EditorSceneManager.SaveOpenScenes 在 SetDefault 之后没生效：检查 workflow 里 SaveOpenScenes 是否在 SetDefault 之后执行（应该是：SetDefault → ... → MarkSceneDirty → SaveOpenScenes → BuildAndUpload）。
- 或者 MA 的构建路径不读 ModularAvatarParameters 组件：这个不大可能，但可以在 MA source 里 grep `defaultValue` 确认。

- [ ] **Step 4: 菜单切换装扮 Toggle 停留**

操作：在游戏里当前装扮打开菜单 → 切换装扮子菜单 → 点"睡衣"（value=4）。

预期：
- 菜单里"睡衣"高亮稳定（不闪烁回第一件）。
- 桥日志：
  ```
  ... value=4 (睡衣) -> avtr_<睡衣>
  ... current avatar -> avtr_<睡衣>
  ```
  加载完后不应再有 `value=0 (黑曜石): ...` 的切换行。

失败诊断：如果加载完后又出现 `value=0` 被切走——同 Step 3 诊断路径。

- [ ] **Step 5: 跨会话持久 saved=false 验证**

操作：
1. 在某件非 0 变体上退出 VRChat（完整关游戏，不是 leave instance）。
2. 重开 VRChat，从收藏夹加载变体 5（"白裙"）或其它非 0 变体。

预期：
- 头像稳定在变体 5，菜单高亮"白裙"。

失败诊断：如果菜单高亮的是其它 variant、或头像被切走：
- 主参数 saved 没改成 false（Task 1 漏改）：Inspector 里看 MA 组件的 `AvatarVariant` 条目的 Saved 字段——应是 false。
- 如果 Saved 确实是 false 但加载仍不对：可能是 VRChat 客户端缓存了旧 expressionParameters——重新批量上传一次，再试。

- [ ] **Step 6: 取消路径**

操作：
1. 点"批量上传"。
2. 上传到第 3 件时点 Progress Window 里的"取消"。
3. 确认取消对话框。

预期：
- scene 里 `_AvatarSwitcherMenu/ModularAvatarParameters` 的 `AvatarVariant.Default Value` 回到 cfg.defaultValue。
- tag / activeSelf / blueprintId 都回到进入前（tagGuard 已有验收，本 Task 只验证 paramDefaultGuard 没干扰这三个）。

- [ ] **Step 7: 异常路径**

操作：
1. 断网状态下点"批量上传"。
2. 失败对话框弹出后点"放弃"。

预期：同 Step 6。

- [ ] **Step 8: 配件行为不变**

操作：
1. 进 VRChat，切到任一带 accessory 的 variant（用本 variant 的菜单按钮，Toggle 类型）。
2. 切 accessory toggle 开 / 关若干次。
3. 切到别的 variant 再切回来。

预期：
- Accessory toggle 正常切换显示。
- 切回同一 variant 时 accessory 的开关状态被保留（accessory 参数 saved=true，能跨会话持久）。

失败诊断：如果 accessory 行为异常——本 Task 改动没动 accessory 参数，不应有影响；真出问题说明 Task 1 的 saved=true → false 改错了位置（应该只改第一个 ParameterConfig，不动循环里的 accessory）。

---

## Self-Review

**Spec 覆盖：**

- 方案 A: `saved=false` on AvatarVariant → Task 1 ✓
- 方案 B: 新增 AvatarVariantParamDefaultGuard → Task 2 Step 2 ✓
- 方案 C: 接入 RunBatchUploadAsync（3 处插入） → Task 2 Step 4/5/6/7 ✓
- 方案 D: 时序文档 → 已在 spec 体现，plan 不需要额外 task
- 测试计划 8 项 → Task 3 Step 1–8 一一对应 ✓
- 非目标 → 计划里没引入对应 task，正确 ✓

**占位符扫描：** 无 TBD / TODO / "add appropriate X" / "similar to Task N" / 无代码的代码步骤。所有 new_string 和新建文件内容都是完整可用的 C# ✓

**类型一致性：**
- `AvatarVariantParamDefaultGuard.Capture(AvatarVariantSwitchConfig)` 在 Task 2 Step 2 定义，Task 2 Step 5 调用点签名一致 ✓
- `SetDefault(int)` 在 Task 2 Step 2 定义，Task 2 Step 6 调用点 `SetDefault(variant.paramValue)`，`variant.paramValue` 是 `int` ✓
- `Dispose()` 在 Task 2 Step 2 定义，Task 2 Step 7 调用点 `paramDefaultGuard?.Dispose()` 签名一致 ✓
- `AvatarVariantMenuBuilder.GeneratedMenuRootName` 在 Task 2 Step 2 Capture 方法里用到，该常量在 [AvatarVariantMenuBuilder.cs:14](../../../Editor/AvatarVariantMenuBuilder.cs#L14) 已定义为 `public const string`，可被 Editor 命名空间内其它类直接引用 ✓
- `ModularAvatarParameters.parameters` 是 `List<ParameterConfig>`（from nadena.dev.modular_avatar.core），`ParameterConfig` 是 struct（已在既有代码中按 struct 使用，例如 [AvatarVariantSwitchWorkflow.cs:849–862](../../../Editor/AvatarVariantSwitchWorkflow.cs#L849-L862) 的 `parameter.nameOrPrefix` 读取）——`list[i]` 返回 copy 的 struct 语义保证 Step 2 里"取 copy → 改 → 写回"写法正确 ✓

**签名变更的原子性：** Task 2 的 guard 新建和 workflow use site 合并到同一 commit，不会出现"中间状态编译不过"的 commit。Task 1 的 `saved=true→false` 是单点字段改动，独立成 commit 无 breakage 风险。

---
