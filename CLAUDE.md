# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 这个包是什么

一个 Unity/VPM 包 (`com.lanstard.avatar-variant-switcher`)：把**同一个** VRChat avatar root 作为多个独立 blueprint 上传（每套装扮一个），再配一个独立 .NET 8 OSC 桥，在运行时根据同步的 int 参数切换对应的 avatar。

[README.md](README.md) 是面向用户的权威文档（中文）。本文件记录编辑代码时需要知道的非显而易见的架构信息。

## 仓库结构（VPM 包）

- [Runtime/](Runtime/) — MonoBehaviour + serializable 数据类。Assembly：`Lanstard.AvatarVariantSwitcher`。
- [Editor/](Editor/) — 所有工作流逻辑。Assembly：`Lanstard.AvatarVariantSwitcher.Editor`，editor-only，引用 VRC SDK3A + Modular Avatar core。
- [Tools~/AvatarVariantOscBridge/](Tools~/AvatarVariantOscBridge/) — 独立的 .NET 8 控制台程序。`~` 后缀是 Unity 约定：**Unity 不会导入这个文件夹**，所以它不会被编进 editor asmdef。当作一个独立项目看。

没有测试套件，Unity 侧也没有构建脚本——打开项目时 Unity 自动编译 asmdef。唯一有真实构建命令的是 OSC 桥。

## 常用命令

OSC 桥（需要 .NET 8 SDK）：

```bash
# 开发运行：
dotnet run --project Packages/com.lanstard.avatar-variant-switcher/Tools~/AvatarVariantOscBridge -- --map "<绝对路径>/avatar-switch-map.json"

# 构建发布版（依赖运行时）：
dotnet publish -c Release -r win-x64 --self-contained false Packages/com.lanstard.avatar-variant-switcher/Tools~/AvatarVariantOscBridge
```

没装 .NET 8 SDK 的机器有 PowerShell 后备实现（`AvatarVariantOscBridge.ps1` + `RunAvatarVariantOscBridge.bat`），协议完全相同。

## 依赖（VPM 层面）

- Unity 2022.3+
- VRChat SDK - Avatars ≥ 3.9.0
- Modular Avatar ≥ 1.10.0
- OSC 桥：Windows + .NET 8 运行时

## 核心思路

同一个 avatar 上传 N 次，每次只激活不同的子物体子集，两次上传之间换 `PipelineManager.blueprintId`。映射文件是 `variantKey → blueprintId` 的唯一真相源。

### 每个装扮的单轮上传

对每个 [AvatarVariantEntry](Runtime/AvatarVariantEntry.cs)：

1. 把该装扮的 `includedRoots`（+ 其配件菜单 GameObject）tag 设为 `Untagged`，其他装扮受控 roots 设为 `EditorOnly`。`EditorOnly` 的物体会在 VRChat 构建时被剥离——这就是"不删场景物体也能隔离装扮"的实现。
2. 从映射里查该装扮已知的 `blueprintId`（先按 `variantKey`，再按 `paramValue` 回落）。查到 → 写进 `PipelineManager.blueprintId`（更新已有 avatar）；查不到 → 留空，让 SDK 新建一个 blueprint。
3. 调 `IVRCSdkAvatarBuilderApi.BuildAndUpload`。
4. 读回新 `blueprintId`，`Upsert` 到映射，原子写。

整个流程由 [AvatarVariantSwitchWorkflow.RunBatchUploadAsync](Editor/AvatarVariantSwitchWorkflow.cs) 编排，外层套一对 `LockReloadAssemblies` / `UnlockReloadAssemblies`，防止批处理中途 editor 触发程序集重载。

### 兜底：tag + blueprintId 还原

[AvatarVariantTagGuard](Editor/AvatarVariantTagGuard.cs) 是回滚机制。`Capture` 在批处理开始前把每个受控 root 的 tag 和当前 `blueprintId` 快照下来；`Dispose()`/`Restore()` 无论批处理是正常结束、抛异常还是被取消，都把它们还原回去。**批处理前后场景状态必须一致**——如果你新增了批处理中会被修改的状态，扩展这个 guard（或加一个并列的 guard），不要往 finally 里塞临时清理代码。

### 受控 roots 集合

批处理会翻 tag 的 GameObject 集合 = 所有装扮的并集：

- `variant.includedRoots`
- `_AvatarSwitcherMenu/_AccessoriesMenu/` 下每个配件菜单 GameObject（[AvatarVariantMenuBuilder.EnumerateAllAccessoryMenuGameObjects](Editor/AvatarVariantMenuBuilder.cs)）
- `_AccessoriesMenu` 父节点本身（这样没配件的装扮轮到时不会还带着个空菜单节点）

Inspector 的校验会拒绝任何 `_AvatarSwitcherMenu/` 下的 `includedRoots`（那是 builder 独占的）。

## 映射文件 (`outputMapPath`)

- 默认：`Assets/AvatarVariantSwitcher/Generated/avatar-switch-map.json`（第一次写入时创建）。
- 通过 `tmp + File.Replace` 原子写，见 [AvatarVariantMap.WriteAtomic](Editor/AvatarVariantMap.cs)。这一点是**关键**：OSC 桥用 `FileSystemWatcher` + 500ms 去抖监听它，读失败时回退到上一份有效映射（避免读到半写入文件时崩掉）。
- Schema v1 查找顺序：`variantKey` → `paramValue`。`variantKey` 是稳定 GUID（Inspector 自动生成，**不要手动改**）；重命名 / 重排 / 改 `paramValue` 都不影响映射。
- 有一个旧 schema（带 `entries` + `version`），由 `ConvertLegacyMap` 处理——不要在没做迁移的情况下删这条路径，可能还有旧用户的映射文件。
- 如果 `outputMapPath` 指向非 embedded 的 `Packages/` 目录，Inspector 会给出警告（那种情况下该目录只读）。

## 配置组件与运行时边界

[AvatarVariantSwitchConfig](Runtime/AvatarVariantSwitchConfig.cs) 实现 `VRC.SDKBase.IEditorOnly`——VRChat SDK 会在构建时剥离带 `IEditorOnly` 的组件，所以这个配置永远不会进任何上传的 blueprint。保持 Runtime asmdef 不依赖 Editor-only 代码；所有工作流逻辑留在 Editor asmdef 里。

若干字段带 `legacy*` 前缀 + `[FormerlySerializedAs]`（`legacyThumbnail`、`legacyInstallTargetMenu`、`legacyUploadedBlueprintId`、`legacyUploadedAvatarDescription`），是给 schema v1 之前的旧场景的迁移钩子——没做专门的迁移动作就不要删。

## 菜单生成（Modular Avatar）

[AvatarVariantMenuBuilder.Generate](Editor/AvatarVariantMenuBuilder.cs) 在 avatar root 下生成 `_AvatarSwitcherMenu`，挂：

- 一个 `ModularAvatarMenuInstaller` → 装到 `cfg.InstallTargetMenu`。该 getter 在用户没指定子菜单（或显式指向了根 expressions menu）时返回 null，**MA 约定 `installTargetMenu == null` 才表示装到根菜单**——绝对不能把它显式赋成 avatar 的根 expressions menu，否则 MA 的 `VirtualMenu` 不会把根菜单加进 `_visitedMenus`，inspector 会报"选择的菜单不属于此 Avatar"。
- 一个 `ModularAvatarParameters` 声明**所有**同步参数（主 int + 所有装扮所有配件的 bool）。
- 一个 "Switch Variant" SubMenu，每个装扮一个 Button。
- 可选的 `_AccessoriesMenu` SubMenu，把所有装扮的配件 toggle 平铺放一起。**按装扮隔离是靠 tag 做的，不是靠父子关系**——所有配件 GameObject 都是 `_AccessoriesMenu` 的平级子物体，命名用 `Acc_<variantKeyPrefix>_<idx>_<label>` 约定（[BuildAccessoryGameObjectName](Editor/AvatarVariantMenuBuilder.cs)）。这种命名让批处理工人可以不动层级就按装扮过滤配件菜单项。

`Generate` 每次都是**完全重建**（`ClearChildrenAndNonTransformComponents` + 重新创建），不要尝试做增量补丁。

## 参数预算

[ValidateParameterBudget](Editor/AvatarVariantSwitchWorkflow.cs) 把已有 Expression Parameters + avatar root 下所有其他 `ModularAvatarParameters` 组件 + 本包生成的参数合并，把 MA 的 `ParameterConfig` 转成 VRC 的 `VRCExpressionParameters.Parameter`（忽略 internal / 未同步的），再和 `VRCExpressionParameters.MAX_PARAMETER_COST` 比较。**新增同步参数时要接入 `BuildAllParameters`**，不然这个预算检查看不到。

## 缩略图

[AvatarVariantThumbnailCapture](Editor/AvatarVariantThumbnailCapture.cs) 渲染 1024² PNG 到 `Assets/AvatarVariantSwitcher/Generated/Thumbnails/`。它临时新建一个 `Camera`（+ 如果场景里没有 Directional Light，新建一个），按装扮切换受控集合的 `activeSelf`，渲染，然后**在 finally 里把每个被改过的 GameObject 的 `activeSelf` 还原**。和 tag guard 一样，结束时必须把场景恢复干净。临时物体用 `HideFlags.HideAndDontSave`。

## OSC 桥协议

桥有两种运行模式：**OSCQuery**（默认）和 **legacy**（`--legacy` 启用）。默认模式通过 mDNS 和 VRChat 协商动态端口，跟其它 OSC 工具（VRCOSC、OSCLeash、bHapticsOSC 等）天然共存；legacy 模式保留硬编码 9000/9001 行为以便向后兼容或跟老 OSC 路由器互操作。

**OSCQuery 模式（默认）**：
- 启动时选一个随机 TCP（HTTP schema 服务）和随机 UDP（OSC 接收）端口。
- 通过 mDNS 在 `224.0.0.251:5353` 广播两条服务：
  - `AvatarVariantSwitcher-<pid>._osc._udp.local.` — OSC 接收端
  - `AvatarVariantSwitcher-<pid>._oscjson._tcp.local.` — OSCQuery schema
- 浏览 mDNS 响应，匹配服务名前缀 `VRChat-Client-` 的 `_osc._udp.local.` service，拿到 VRChat 的 UDP endpoint，作为 `/avatar/change` 的发送目标。
- HTTP schema 响应 `GET /`、`GET /?HOST_INFO`、`GET /avatar/parameters/<paramName>`——告诉 VRChat 我们接收 `/avatar/parameters/<paramName>` (int) 和 `/avatar/change` (string)。
- mDNS socket 用 `SO_REUSEADDR` 绑 5353，和系统 mDNS / Bonjour 或其它 OSCQuery 工具共享端口。

**Legacy 模式（`--legacy`）**：
- 在 `127.0.0.1:9001` 监听 `/avatar/parameters/<parameterName>`，发到 `127.0.0.1:9000`。
- 非 legacy 模式下 `--host` / `--listen` / `--send` 打警告并忽略；要用这些 flag 必须同时传 `--legacy`。

**两种模式都**：
- 监听 `/avatar/change` 回声记录当前 avatar id，避免重复发同一条切换命令。
- **硬前提**：`/avatar/change` 只对游戏内 ⭐ 收藏夹里的 avatar 生效。桥启动时的红色 banner 就是喊这件事的，**保留它**。

实现文件（均在 [Tools~/AvatarVariantOscBridge/](Tools~/AvatarVariantOscBridge/)）：

- CLI 解析：[BridgeOptions.Parse](Tools~/AvatarVariantOscBridge/BridgeOptions.cs)
- 主循环：[VariantBridge.RunAsync](Tools~/AvatarVariantOscBridge/VariantBridge.cs)
- OSC 包编解码：`OscCodec.cs`
- OSCQuery orchestrator：[OscQueryService](Tools~/AvatarVariantOscBridge/OscQueryService.cs)（组合 mDNS + HTTP + 端点注册）
- DNS 报文编解码：[DnsCodec](Tools~/AvatarVariantOscBridge/DnsCodec.cs)（RFC 1035/6762 子集，只覆盖 PTR/SRV/TXT/A + label 压缩）
- mDNS 广播 + 浏览：[MDnsResponder](Tools~/AvatarVariantOscBridge/MDnsResponder.cs)
- OSCQuery HTTP schema：[OscQueryHttpHost](Tools~/AvatarVariantOscBridge/OscQueryHttpHost.cs)
- JSON schema 模型：[OscQueryJson.cs](Tools~/AvatarVariantOscBridge/OscQueryJson.cs)

**AOT 约束**：csproj 开了 `<PublishAot>true</PublishAot>` + `<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>`。所有 JSON 序列化走 [BridgeJsonContext](Tools~/AvatarVariantOscBridge/BridgeJsonContext.cs) source generator，不用反射。新增参与 JSON 序列化的类型要在 BridgeJsonContext 加 `[JsonSerializable(typeof(X))]`。不要往桥里引 `Newtonsoft.Json`、`EmbedIO`、`MeaMod.DNS` 之类反射重的库——DnsCodec / MDnsResponder / OscQueryHttpHost 都是自写的正是出于这个考虑。

**PowerShell 后备**：[AvatarVariantOscBridge.ps1](Tools~/AvatarVariantOscBridge/AvatarVariantOscBridge.ps1) 仍是硬编码 9000/9001 的老派实现，相当于 C# 版的 `--legacy`。不实现 OSCQuery（写 mDNS + HttpListener 的 PS 版本成本太高）。

## Editor UX 约定

[AvatarVariantSwitchConfigEditor](Editor/AvatarVariantSwitchConfigEditor.cs) 是唯一的自定义 Inspector。所有 label、tooltip 都是**中文**——加新字段时保持风格（在 `ConfigFields` / `EntryFields` 数组里同时填中文 label 和中文 tooltip）。Inspector 行为：

- 每次 repaint 都跑一次完整的 `BuildValidationReport`，实时呈现错误 / 警告。
- 有一个拖放区，把 avatar root 下的子物体拖进去会自动创建新装扮条目。
- 每次 repaint，`AutoScanNewRoots` 会扫一遍每个装扮 `includedRoots` 里**新加的、还没扫过的**条目，把它的直接子物体追加到 `accessories`——扫过的记录在 `AvatarVariantEntry.autoScannedRoots` 里。把一个 root 从 `includedRoots` 删掉会同时从 `autoScannedRoots` 里剔除，再加回来会重新扫一次。**用户手动删掉的 accessory 永远不会被自动加回来。** 改扫描逻辑时请保留这个"每次新增扫一次"的语义。
- Armature 之类的骨架根节点会在自动扫描时被跳过（见 commit `b58e80e`）。

## 校验器强制的约束（别随手解除）

- 最多 7 个装扮。Expression Menu 单层 8 槽减去 SubMenu 入口本身。加分页在 README 里列为"下一版再说"，当前版没有。
- `defaultValue` 必须等于某个装扮的 `paramValue`。
- `variant.includedRoots` 必须是 avatar root 的后代，装扮间不能父子重叠，不能位于 `_AvatarSwitcherMenu` 下。
- `paramValue` 非负且装扮间唯一。
- Accessory target 必须位于所属装扮的 `includedRoots` 子树下。

要改这些的话，同时去修 `BuildValidationReport` 里对应的检查，并确认批处理工人那条路径也没被破坏。

## async / editor 时序陷阱

- `StartBatchUpload` 用 `async void` 是故意的——它是 editor 菜单回调。要改的是内层的 `RunBatchUploadAsync`。
- `LockReloadAssemblies` 必须在 `finally` 里配对 `Unlock`；忘了解锁会导致脚本不再重新编译。当前代码是配对的，**保持配对**。
- `AvatarVariantBuilderGate.AcquireAsync` 触发 SDK 面板打开后，用 `VRCSdkControlPanel.TryGetBuilder` 最多轮询 100 × 100ms。真走到超时返回时要报告原因，不要默默重试到天荒地老。
