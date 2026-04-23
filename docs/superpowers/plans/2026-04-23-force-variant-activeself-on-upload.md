# 批量上传时强制摆正装扮 activeSelf — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 批量上传每件装扮前，把它的 `includedRoots` 顶层 `activeSelf` 置 `true`、其他装扮的顶层置 `false`；批处理结束（正常/取消/异常）把 `activeSelf` 还原到进入批处理前用户在场景里的手改状态。

**Architecture:** 扩展现有的 [AvatarVariantTagGuard](../../../Editor/AvatarVariantTagGuard.cs) 多维护一份 `Dictionary<GameObject, bool>` 的 activeSelf 快照，作用范围只限 `includedRoots` 并集；`ApplyActive` 在翻 tag 之外同步翻 activeSelf；`Restore`/`Dispose` 在还原 tag + blueprintId 之外同步还原 activeSelf。唯一调用点 [AvatarVariantSwitchWorkflow.RunBatchUploadAsync](../../../Editor/AvatarVariantSwitchWorkflow.cs) 改成把 includedRoots 并集单独拎出来传给新签名的 `Capture`。

**Tech Stack:** C# (Unity Editor, .NET Framework/Standard)；Unity 2022.3+ Editor-only 代码；VRC SDK3A + Modular Avatar。无自动测试框架（CLAUDE.md 声明），验证靠 Unity Editor 重编译 + 手动场景验收。

**Spec:** [docs/superpowers/specs/2026-04-23-force-variant-activeself-on-upload-design.md](../specs/2026-04-23-force-variant-activeself-on-upload-design.md)

---

## File Structure

- **Modify**: [Editor/AvatarVariantTagGuard.cs](../../../Editor/AvatarVariantTagGuard.cs) — 增加 `_activeSelfSnapshot` 字段；扩展构造函数、`Capture` 签名、`ApplyActive` 方法、`Restore` 方法。文件职责保持不变（"批处理期间的可还原状态守卫"），只是扩展到多一个状态维度。
- **Modify**: [Editor/AvatarVariantSwitchWorkflow.cs](../../../Editor/AvatarVariantSwitchWorkflow.cs) `RunBatchUploadAsync` 方法内，约第 368–392 行——把现有的 `controlledRoots` 构造拆成"先算 includedRoots 并集 → 再把 accessory 菜单累加进 controlledRoots"，并把 includedRoots 并集作为新参数传给 `Capture`。

**不修改的文件**：Runtime 目录、OSC 桥、`AvatarVariantMenuBuilder`、`AvatarVariantThumbnailCapture`、`AvatarVariantMap`、Inspector、README、CHANGELOG（spec 中声明为 scope 外，且 README 是面向用户的权威文档，这个变更属于静默修复"场景 activeSelf 泄漏进 bundle"的 bug，不需要用户关注新概念）。

---

### Task 1: 扩展 AvatarVariantTagGuard 纳管 includedRoots 的 activeSelf

**Files:**
- Modify: `Editor/AvatarVariantTagGuard.cs` — 整个类（字段、构造、Capture、ApplyActive、Restore）
- Modify: `Editor/AvatarVariantSwitchWorkflow.cs` — `RunBatchUploadAsync` 中 `controlledRoots` 构造段

> 这两个改动要**在同一个 commit 里一起提交**——`Capture` 是 `internal`/`public` 静态方法但只有一个调用点，签名变化会同时失败一处调用。如果分两个 commit，中间状态会编译不过。

- [ ] **Step 1: 复核当前 AvatarVariantTagGuard.cs 实现**

Read: `Editor/AvatarVariantTagGuard.cs`

要确认的当前契约：
- `_tagSnapshot: Dictionary<GameObject, string>`——每个受控 root 的原 tag。
- `Capture(PipelineManager, IEnumerable<GameObject>)`——拍 tag 快照 + 记 `_originalBlueprintId`。
- `ApplyActive(HashSet<GameObject>)`——遍历 `_tagSnapshot.Keys`，按 activeSet 翻 `Untagged`/`EditorOnly`。
- `Restore()`——还 tag + blueprintId，设 `_restored=true` 防重复。
- `Dispose()` → `Restore()`。

- [ ] **Step 2: 替换整个 AvatarVariantTagGuard.cs**

Write to `Editor/AvatarVariantTagGuard.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Core;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    public sealed class AvatarVariantTagGuard : IDisposable
    {
        private readonly Dictionary<GameObject, string> _tagSnapshot;
        private readonly Dictionary<GameObject, bool> _activeSelfSnapshot;
        private readonly PipelineManager _pm;
        private readonly string _originalBlueprintId;
        private bool _restored;

        private AvatarVariantTagGuard(
            PipelineManager pm,
            Dictionary<GameObject, string> tagSnapshot,
            Dictionary<GameObject, bool> activeSelfSnapshot)
        {
            _pm = pm;
            _tagSnapshot = tagSnapshot;
            _activeSelfSnapshot = activeSelfSnapshot;
            _originalBlueprintId = pm.blueprintId ?? string.Empty;
        }

        public static AvatarVariantTagGuard Capture(
            PipelineManager pm,
            IEnumerable<GameObject> controlledRoots,
            IEnumerable<GameObject> activeScopedRoots)
        {
            if (pm == null)
            {
                throw new ArgumentNullException("pm");
            }

            var tagSnapshot = new Dictionary<GameObject, string>();
            foreach (var root in controlledRoots ?? Enumerable.Empty<GameObject>())
            {
                if (root == null || tagSnapshot.ContainsKey(root))
                {
                    continue;
                }

                tagSnapshot.Add(root, root.tag);
            }

            var activeSnapshot = new Dictionary<GameObject, bool>();
            foreach (var root in activeScopedRoots ?? Enumerable.Empty<GameObject>())
            {
                if (root == null || activeSnapshot.ContainsKey(root))
                {
                    continue;
                }

                activeSnapshot.Add(root, root.activeSelf);
            }

            return new AvatarVariantTagGuard(pm, tagSnapshot, activeSnapshot);
        }

        public void ApplyActive(HashSet<GameObject> activeSet)
        {
            activeSet = activeSet ?? new HashSet<GameObject>();

            foreach (var root in _tagSnapshot.Keys)
            {
                if (root == null)
                {
                    continue;
                }

                Undo.RecordObject(root, "Set avatar variant tag");
                root.tag = activeSet.Contains(root) ? "Untagged" : "EditorOnly";
                EditorUtility.SetDirty(root);
            }

            foreach (var root in _activeSelfSnapshot.Keys)
            {
                if (root == null)
                {
                    continue;
                }

                var desired = activeSet.Contains(root);
                if (root.activeSelf == desired)
                {
                    continue;
                }

                Undo.RecordObject(root, "Set avatar variant active");
                root.SetActive(desired);
                EditorUtility.SetDirty(root);
            }
        }

        public void SetBlueprintId(string id)
        {
            Undo.RecordObject(_pm, "Set blueprint id");
            _pm.blueprintId = id ?? string.Empty;
            EditorUtility.SetDirty(_pm);
        }

        public void Restore()
        {
            if (_restored)
            {
                return;
            }

            foreach (var pair in _tagSnapshot)
            {
                var root = pair.Key;
                if (root == null)
                {
                    continue;
                }

                Undo.RecordObject(root, "Restore avatar variant tag");
                root.tag = pair.Value;
                EditorUtility.SetDirty(root);
            }

            Undo.RecordObject(_pm, "Restore blueprint id");
            _pm.blueprintId = _originalBlueprintId;
            EditorUtility.SetDirty(_pm);

            foreach (var pair in _activeSelfSnapshot)
            {
                var root = pair.Key;
                if (root == null)
                {
                    continue;
                }

                if (root.activeSelf == pair.Value)
                {
                    continue;
                }

                Undo.RecordObject(root, "Restore avatar variant active");
                root.SetActive(pair.Value);
                EditorUtility.SetDirty(root);
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

变动点说明：
- 新增字段 `_activeSelfSnapshot`。
- 构造函数新增一个参数接收它。
- `Capture` 新增 `activeScopedRoots` 形参；对它的每个非空元素拍 `activeSelf`。null / 重复项跳过，和 tag snapshot 的写法对齐。
- `ApplyActive` 在原有 tag 翻转块后追加一个 activeSelf 翻转块：按同一个 `activeSet` 判断，用 `SetActive` 调整，带 `Undo.RecordObject` 和 `EditorUtility.SetDirty`。原值和目标值相同时跳过，避免多余的 Mark dirty。
- `Restore` 在原有 tag + blueprintId 还原之后追加 activeSelf 还原块。同样跳过等值情况。
- `SetBlueprintId`、`Dispose`、`_restored` 守卫保持原样。

- [ ] **Step 3: 复核当前 RunBatchUploadAsync 里 controlledRoots 构造段**

Read: `Editor/AvatarVariantSwitchWorkflow.cs` 行 368–392（即 `RunBatchUploadAsync` 里 `var controlledRoots = ...` 开始到 `guard = AvatarVariantTagGuard.Capture(pm, controlledRoots);` 为止）。

确认当前长这样：

```csharp
var controlledRoots = plan.variants
    .SelectMany(variant => variant.includedRoots)
    .Where(root => root != null)
    .Where(root => !IsUnderMenuRoot(root, cfg))
    .Distinct()
    .ToList();

// 把生成的所有配件 toggle GameObject 也纳入受控集：
// 每轮只保留当前装扮对应的 accessory 菜单项，其他装扮的配件菜单项本轮都 EditorOnly。
var accessoryMenuGameObjects = AvatarVariantMenuBuilder
    .EnumerateAllAccessoryMenuGameObjects(cfg)
    .Where(go => go != null)
    .ToList();
foreach (var accGo in accessoryMenuGameObjects)
{
    if (!controlledRoots.Contains(accGo)) controlledRoots.Add(accGo);
}

var accessoriesMenuRoot = AvatarVariantMenuBuilder.FindAccessoriesMenuRoot(cfg);
if (accessoriesMenuRoot != null && !controlledRoots.Contains(accessoriesMenuRoot.gameObject))
{
    controlledRoots.Add(accessoriesMenuRoot.gameObject);
}

guard = AvatarVariantTagGuard.Capture(pm, controlledRoots);
```

- [ ] **Step 4: 改造 controlledRoots 构造段，先算 includedRoots 并集再传给新签名 Capture**

Edit `Editor/AvatarVariantSwitchWorkflow.cs`：

old_string（整个段落，保留原注释和缩进以唯一匹配）:

```csharp
                var controlledRoots = plan.variants
                    .SelectMany(variant => variant.includedRoots)
                    .Where(root => root != null)
                    .Where(root => !IsUnderMenuRoot(root, cfg))
                    .Distinct()
                    .ToList();

                // 把生成的所有配件 toggle GameObject 也纳入受控集：
                // 每轮只保留当前装扮对应的 accessory 菜单项，其他装扮的配件菜单项本轮都 EditorOnly。
                var accessoryMenuGameObjects = AvatarVariantMenuBuilder
                    .EnumerateAllAccessoryMenuGameObjects(cfg)
                    .Where(go => go != null)
                    .ToList();
                foreach (var accGo in accessoryMenuGameObjects)
                {
                    if (!controlledRoots.Contains(accGo)) controlledRoots.Add(accGo);
                }

                var accessoriesMenuRoot = AvatarVariantMenuBuilder.FindAccessoriesMenuRoot(cfg);
                if (accessoriesMenuRoot != null && !controlledRoots.Contains(accessoriesMenuRoot.gameObject))
                {
                    controlledRoots.Add(accessoriesMenuRoot.gameObject);
                }

                guard = AvatarVariantTagGuard.Capture(pm, controlledRoots);
```

new_string:

```csharp
                // includedRoots 并集——tag 翻转和 activeSelf 摆正都要覆盖它们；
                // 但只有 includedRoots 需要 activeSelf 摆正，accessory 菜单 / _AccessoriesMenu
                // 父节点不属于 activeSelf 作用域（它们靠 tag 的 EditorOnly 剥离）。
                var includedRootsUnion = plan.variants
                    .SelectMany(variant => variant.includedRoots)
                    .Where(root => root != null)
                    .Where(root => !IsUnderMenuRoot(root, cfg))
                    .Distinct()
                    .ToList();

                var controlledRoots = new List<GameObject>(includedRootsUnion);

                // 把生成的所有配件 toggle GameObject 也纳入受控集：
                // 每轮只保留当前装扮对应的 accessory 菜单项，其他装扮的配件菜单项本轮都 EditorOnly。
                var accessoryMenuGameObjects = AvatarVariantMenuBuilder
                    .EnumerateAllAccessoryMenuGameObjects(cfg)
                    .Where(go => go != null)
                    .ToList();
                foreach (var accGo in accessoryMenuGameObjects)
                {
                    if (!controlledRoots.Contains(accGo)) controlledRoots.Add(accGo);
                }

                var accessoriesMenuRoot = AvatarVariantMenuBuilder.FindAccessoriesMenuRoot(cfg);
                if (accessoriesMenuRoot != null && !controlledRoots.Contains(accessoriesMenuRoot.gameObject))
                {
                    controlledRoots.Add(accessoriesMenuRoot.gameObject);
                }

                guard = AvatarVariantTagGuard.Capture(pm, controlledRoots, includedRootsUnion);
```

变动点说明：
- 新增一个 local `includedRootsUnion`，内容就是原来 `controlledRoots` 的初始值（纯 includedRoots 并集，去重，排除 menu root 后代）。
- `controlledRoots` 改成基于 `includedRootsUnion` 拷贝一份新 List（因为后续还要往 controlledRoots 里追加 accessory 菜单 GameObject + `_AccessoriesMenu` 父节点，不能让这些追加反噬 `includedRootsUnion`）。
- accessory 菜单累加逻辑原样不动。
- `Capture` 调用传两个参数：`controlledRoots`（tag 全集）+ `includedRootsUnion`（activeSelf 作用域）。

- [ ] **Step 5: 打开 Unity 让 Editor 重编译，检查 Console 无错**

手动操作：
1. 把 Unity Editor 切到前台（工作的 Unity 项目必须已经打开这个包——由用户本地环境保证，plan 执行者不需要替他打开）。
2. Unity 检测到脚本修改会自动进入 compiling 状态（右下角 spinner）。
3. 等 compiling 结束，切到 Console 窗口。
4. 预期：无任何红色 error；允许有已有的 warning 但不应新增 warning。如果发现因本次改动引起的 warning 或 error，回到 Step 2/Step 4 修正。

若 Unity 未打开、用户不在场，退化验证方式：

```bash
dotnet build ./Tools~/AvatarVariantOscBridge/AvatarVariantOscBridge.csproj
```

这**不**验证 Editor asmdef（OSC 桥是独立 csproj），所以这条退化验证只是证明本仓库没引起 OSC 桥连带破坏——Editor 侧的编译结果必须等用户下次打开 Unity 或者明确确认。在这种情况下 Task 1 的 commit 仍可做，但 Task 2 的手动验收步骤不能跳。

- [ ] **Step 6: Commit 两处改动**

```bash
git add Editor/AvatarVariantTagGuard.cs Editor/AvatarVariantSwitchWorkflow.cs
git commit -m "$(cat <<'EOF'
fix: 批量上传时强制摆正装扮 activeSelf

用户在场景里改模时常手动关掉非当前装扮的 includedRoots；之前批量
上传只翻 tag，不动 activeSelf，导致 bundle 里那件衣服是禁用状态，
切过去就是空人。

扩展 AvatarVariantTagGuard：除 tag/blueprintId 外再维护
includedRoots 的 activeSelf 快照。ApplyActive 时按 activeSet
同步摆正；Restore/Dispose 时还原到进入批处理前用户手改的状态。
accessory 菜单 / _AccessoriesMenu 父节点保持只靠 tag 的
EditorOnly 剥离，不纳入 activeSelf 作用域。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: 手动验收

项目没有自动测试套件。按 spec 的测试计划在 Unity 里走一遍。每一项通过就打勾；通不过就回到 Task 1 排查。

**Files:**
- None — 纯手动验证

**前置准备：**
- 一个包含 `AvatarVariantSwitchConfig` 的 avatar 场景，至少配 3 件装扮（A/B/C），每件装扮的 `includedRoots` 至少一个 GameObject。
- 缩略图都已生成（Inspector 里的"渲染缺失缩略图"按钮）。
- 映射 JSON 可写（默认路径或用户配置路径）。

- [ ] **Step 1: 基础场景——场景里故意把 B 关了再跑批量上传**

操作：
1. 在场景 Hierarchy 里选中 B 的 `includedRoots[0]` GameObject，Inspector 顶部勾掉 active checkbox（变灰）。
2. 对 A、C 保持默认（active 打勾）。
3. 点 Inspector 里的"批量上传"按钮。
4. 观察 Progress Window。当某个装扮进入"上传中"状态时，迅速切到 Hierarchy 观察该装扮和其他装扮的 root GameObject 的 active 状态。

预期：
- A 轮到时：A 的 includedRoots active=on（checkbox 勾），B、C 的 includedRoots active=off（checkbox 灰）。
- B 轮到时：B 的 includedRoots active=on，A、C 的 includedRoots active=off。
- C 轮到时：C 的 includedRoots active=on，A、B 的 includedRoots active=off。

批处理成功对话框弹出后：
- A 的 includedRoots 回到 on（和进入前一致）。
- B 的 includedRoots 回到 **off**（保留了用户进入前的手改状态）。
- C 的 includedRoots 回到 on（和进入前一致）。

如果任何一项不符合预期，回 Task 1 Step 2 检查 `_activeSelfSnapshot` 填充 / `ApplyActive` 翻转 / `Restore` 的还原。

- [ ] **Step 2: VRChat 内验证 bundle 正确性**

操作：
1. 把 A、B、C 这三个装扮的 blueprint 都在游戏里的 avatar 收藏夹里加⭐收藏（如果之前没收藏过，先游戏里 Public 的 avatar 搜索框用 blueprint URL 打开 → 收藏 → 回到 home 上传完成生效）。
2. 切到 A：预期看到 A 的衣服和身体正常；**不是空人/骨架**。
3. 切到 B：预期看到 B 的衣服和身体正常；**不是空人/骨架**——这是修复的核心，因为进入批处理前 B 在场景里是关的。
4. 切到 C：预期看到 C 的衣服和身体正常。

这一步是整个修复的端到端验证。如果 B 仍然是空人/骨架，说明 `ApplyActive` 没有把 activeSelf 摆成 true，回 Task 1 Step 2 查 `ApplyActive` 第二个 foreach 块。

- [ ] **Step 3: 取消路径验证**

操作：
1. 和 Step 1 一样让 B 处于 active=off 状态。
2. 点批量上传。
3. 在 Progress Window 上传过程中点 **取消** 按钮。
4. 等待取消对话框/结束态。

预期：
- A、B、C 的 includedRoots active 状态全部回到进入批处理前的值（A=on、B=off、C=on）。
- 没有报错对话框（或只有"用户取消"的提示）。
- Unity Console 没有红色异常。

失败诊断：如果取消后 active 状态没恢复，说明 `Dispose()` 路径没覆盖到 `_activeSelfSnapshot` 的还原；回 Task 1 Step 2 检查 `Restore()` 里的第三个 foreach 块。

- [ ] **Step 4: 异常路径验证（可选，按可操作性来）**

操作（如果能方便模拟网络错误）：
1. 断开网络或关闭代理到 AWS S3 的流量。
2. 让 B 在场景里 active=off。
3. 点批量上传。
4. 预期：有至少一个装扮失败，失败对话框弹出。点 **放弃**（不重试）。

预期：
- Progress Window 进入"部分失败"状态。
- Hierarchy 里所有装扮的 includedRoots active 都回到进入批处理前的值（A=on、B=off、C=on）。
- `_AvatarSwitcherMenu` 仍然存在且结构完整（本次修复不应影响菜单生成）。

这一步如果本地环境不好模拟就跳过，但 Task 2 Step 3 已经覆盖了"guard 在非正常终止下正确 Restore"这条路径的核心语义。

- [ ] **Step 5: 菜单层级不受影响验证**

操作：
1. 和 Step 1 一样跑完批量上传。
2. 批处理结束后展开 `_AvatarSwitcherMenu` 看 Hierarchy。

预期：
- `_AvatarSwitcherMenu` 本身 active=on。
- `_AvatarSwitcherMenu/_AccessoriesMenu`（如果存在）active=on。
- `_AvatarSwitcherMenu/_AccessoriesMenu/Acc_*` 各配件菜单 GameObject active=on。

说明：这些 GameObject 不在 `_activeSelfSnapshot` 作用域，不应被本次修复改到。它们靠 tag 做 EditorOnly 剥离；在 guard Restore 之后 tag 回到原样。

- [ ] **Step 6: Accessory 行为不变验证**

操作：
1. 批量上传后在游戏里切到 A。
2. 通过 Expression Menu 点 A 的某个默认关的 accessory toggle → 预期：accessory 从不可见切换到可见。
3. 再点一次 → 预期：accessory 从可见切换到不可见。
4. 另一个默认开的 accessory 同理反向验证。

预期：MA 生成的 toggle 行为和修复前完全一致——这个修复刻意避开了 accessory 的 activeSelf，不应影响。

失败诊断：如果 accessory 的默认状态反了（默认关变默认开或反之），说明修复意外动到了 accessory target 的 activeSelf；回 Task 1 Step 4 检查 `includedRootsUnion` 的构造，它应该**只**来自 `variant.includedRoots`，不应包含 `variant.accessories[*].target`。

---

## Self-Review

**Spec 覆盖：** spec 里列出的所有改动点都有对应 task——

- Tag guard 扩展 → Task 1 Step 2 ✓
- Workflow 调用点改动 → Task 1 Step 4 ✓
- 测试计划 6 项 → Task 2 Step 1–6 一一对应 ✓
- 非目标（不提供禁用开关、不处理 accessory activeSelf、不做 editor 可视化）→ 计划中不存在对应 task，正确 ✓

**占位符扫描：** 无 TBD/TODO/"add appropriate X"/"similar to Task N"/无代码的代码步骤。所有 new_string/文件内容都是完整可用的 ✓

**类型一致性：** `Capture` 签名 `(PipelineManager, IEnumerable<GameObject>, IEnumerable<GameObject>)` 在 Task 1 Step 2（定义）和 Task 1 Step 4（调用点 `Capture(pm, controlledRoots, includedRootsUnion)`）保持一致 ✓。`ApplyActive(HashSet<GameObject>)` 签名不变，调用点 `RunBatchUploadAsync` 的 `guard.ApplyActive(activeSet)` 无需修改 ✓。`_activeSelfSnapshot: Dictionary<GameObject, bool>` 在字段声明、构造函数参数、Capture 填充、ApplyActive 消费、Restore 消费五处使用一致 ✓。

**签名变更的原子性：** Task 1 里 tag guard 的签名变更和唯一调用点的更新合并到同一 commit（Step 6），保证不会出现"中间状态编译不过"的 commit。

---
