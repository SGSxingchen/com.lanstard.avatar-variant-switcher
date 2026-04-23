# Lanstard Avatar Variant Switcher

VRChat 同一 avatar 多套衣服/配件变体的批量上传 + OSC 切换工具集。

## 适用场景

你有一个主体 avatar，想维护多套装扮（比如日常服、战斗服、校服、泳装…），每套都希望：

- 作为独立 VRChat avatar 上传（有独立 blueprint id、独立预览图）；
- 所有装扮共享同一个基础身体、同一套表情动画；
- 在游戏内通过同一个 Expression Menu 条目切换（int 参数）；
- 由运行时 OSC 工具自动切换 avatar（不用每次手动从菜单选）。

本包把这一套流程做成一键化。

## 架构

- 场景里只有 **1 个主 Avatar Root**，挂 1 个 `VRCAvatarDescriptor` + 1 个 `PipelineManager`。
- 每个"变体" = 一个 `AvatarVariantEntry` 配置项，包含：
  - `displayName`、`variantKey`（稳定 GUID）、`paramValue`、`thumbnail`、`menuIcon`、`uploadedName`、`uploadedDescription`
  - `includedRoots: List<GameObject>` —— 该变体要保留的装扮根节点
- 批量上传时：当前变体的 `includedRoots` 打 `Untagged`，其他所有变体的受控 roots 打 `EditorOnly`，对同一个 avatar root 反复上传。每次得到一个新的 blueprint id，记录到 `variantKey → blueprintId` 映射文件。
- 批量结束后，tag 和 `PipelineManager.blueprintId` **恢复为开始前的原值**；映射文件是 blueprint id 的唯一真实来源。

## 依赖

- Unity 2022.3+
- VRChat SDK - Avatars ≥ 3.9.0
- Modular Avatar ≥ 1.10.0
- OSC Bridge 端：.NET 8 运行时（Windows，或 PowerShell 后备版本）

## 主要脚本

| 文件 | 作用 |
|---|---|
| `Runtime/AvatarVariantSwitchConfig.cs` | MonoBehaviour 配置组件（挂在主 Avatar Root） |
| `Runtime/AvatarVariantEntry.cs` | `[Serializable]` 变体条目 |
| `Editor/AvatarVariantSwitchConfigEditor.cs` | CustomEditor：ReorderableList + 校验 + 按钮 |
| `Editor/AvatarVariantSwitchWorkflow.cs` | 批量上传编排（async Task + LockReloadAssemblies + BatchPlan 快照 + Restore） |
| `Editor/AvatarVariantMenuBuilder.cs` | 生成 Modular Avatar 切换菜单 |
| `Editor/AvatarVariantTagGuard.cs` | 受控 roots tag + blueprintId 快照 / 应用 / 还原 |
| `Editor/AvatarVariantMap.cs` | 映射 JSON 读写（atomic write + prune） |
| `Editor/AvatarVariantBuilderGate.cs` | VRChat SDK Builder 打开 + 登录态保障 |
| `Tools~/AvatarVariantOscBridge/` | 独立 .NET 8 OSC 桥（监听 9001 / 发 9000） |

## 使用流程

1. 把 `Avatar Variant Switch Config` 组件挂到主 avatar root（Add Component → Lanstard → Avatar Variant Switch Config）；`avatarDescriptor` 会自动填。
2. 配置 `parameterName`（默认 `AvatarVariant`）、`menuName`、`defaultValue`、`releaseStatus`、`outputMapPath`。
3. 在 `variants` 列表里添加每套装扮：
   - `displayName`、`paramValue`（唯一、非负、`defaultValue` 必须命中其中一个）
   - `thumbnail` 图片（首次上传必填）
   - `includedRoots` 放该装扮要开的子物体根节点（不能包含 `_AvatarSwitcherMenu`）
4. 点 **Generate / Refresh Menu**：主 avatar root 下生成 `_AvatarSwitcherMenu` 子物体，挂好 MA 菜单和参数。
5. 打开 VRChat SDK 控制面板并登录。
6. 点 **Batch Upload All Variants**：
   - 校验、快照 tag/blueprintId、锁 assembly reload、
   - 串行对每个变体：设 tag、从 map 回填 blueprintId（若存在 → 更新；若不存在 → 清空 → 新建）、上传、记录新 id、原子写 map。
   - 完成或中途取消后：Restore tag + Restore blueprintId + Unlock reload。
7. 首次上传完后，打开 VRChat，**把每个变体都 ⭐ 收藏**（下文的 OSC 切换硬前提）。
8. 启动 OSC Bridge：

   ```powershell
   dotnet run --project Packages/com.lanstard.avatar-variant-switcher/Tools~/AvatarVariantOscBridge -- --map "<绝对路径>/avatar-switch-map.json"
   ```

   或 PowerShell 后备版本：

   ```bat
   Packages\com.lanstard.avatar-variant-switcher\Tools~\AvatarVariantOscBridge\RunAvatarVariantOscBridge.bat "D:\...\avatar-switch-map.json"
   ```

9. 游戏内选 Expression Menu → Switch Variant → 某个变体 → OSC 工具收到参数变化 → 发 `/avatar/change` → VRChat 切 avatar。

## 映射文件 schema

默认位置：`Assets/AvatarVariantSwitcher/Generated/avatar-switch-map.json`（可改）

```json
{
  "schemaVersion": 1,
  "generatedAtUtc": "2026-04-23T12:34:56.789Z",
  "parameterName": "AvatarVariant",
  "menuName": "Switch Variant",
  "defaultValue": 0,
  "variants": [
    {
      "variantKey": "a1b2c3d4...",
      "paramValue": 0,
      "displayName": "Default Outfit",
      "blueprintId": "avtr_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
    }
  ]
}
```

`variantKey` 是稳定 GUID，重命名 / 重排 / 改 paramValue 都不影响身份。Editor 侧用原子写 (`tmp + File.Move`) 避免桥端读到半写入文件。

## OSC 切换的硬前提

> **VRChat 的 `/avatar/change` OSC 命令只对"收藏夹里的 avatar"生效。**
>
> 首次上传完所有变体后，请在 VRChat 游戏内把每个变体都 ⭐ 加入收藏夹，否则本工具发出的切换指令会被客户端静默丢弃。

## 已知限制

- 初版最多支持 **7 个变体**（单层 Expression Menu 控件上限 8，SubMenu 入口占 1）。超过时 Validate 报错，分页能力下一版再加。
- 映射文件路径默认落在 `Assets/` 下。若改到 `Packages/` 且该包非 embedded（VPM 只读安装），Inspector 会给出警告。
- Windows 环境测试。Linux / macOS 的 .NET 8 + dotnet 可跑 OSC 桥，但 VRChat 客户端本身仅 Windows / 支持的主机上可用。
