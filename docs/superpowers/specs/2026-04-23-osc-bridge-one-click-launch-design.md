# OSC 桥一键启动 — 设计文档

- **日期**：2026-04-23
- **作者**：Claude + 用户协作
- **背景仓库**：`com.lanstard.avatar-variant-switcher`（VPM 包）

## 1. 背景与目标

现在 `Tools~/AvatarVariantOscBridge/` 是一个 .NET 8 console 程序，用户要启动它必须：

- 装 .NET 8 SDK，然后 `dotnet run --project ... -- --map "<绝对路径>"`，或
- 用 PowerShell 后备（`AvatarVariantOscBridge.ps1` + `.bat`），自己去改脚本里的路径

这两条路径都要求用户打开终端、复制粘贴绝对路径、理解 CLI 参数。**对非开发者极度不友好**，README 里这段是用户反馈的主要痛点之一。

**目标**：用户在 Unity Inspector 上点一个按钮，剩下的事自动化——下载桥、启动桥、喂映射路径。桥本身也能独立于 Unity 被双击使用。

## 2. 非目标（YAGNI）

以下明确不做，避免方案漂移：

- Linux / macOS 构建
- Inspector 内追踪桥的运行状态（PID / mutex / 状态指示）
- Inspector 内回显桥的 stdout
- Inspector 内的"停止"按钮
- 自动重启 / 守护进程
- 桥与 Inspector 之间的版本协议兼容性检查
- 桥的托盘化 / GUI 化（保持 console）

## 3. 整体数据流

```
┌─────────────────────────┐    Process.Start --map <abs>    ┌──────────────────────────────┐
│ Unity Editor (Inspector)│ ──────────────────────────────► │ bridge.exe (console window)  │
│  - "启动 OSC 桥" 按钮   │                                 │  - 9001 监听 / 9000 发送     │
│  - 检查更新 + 下载      │                                 │  - FileSystemWatcher 热重载  │
│  - Process.Start        │                                 │  - 首次弹文件选择框          │
└─────────────────────────┘                                 │  - %LOCALAPPDATA% 存上次路径 │
            ▲                                               └──────────────────────────────┘
            │                                                             ▲
            │ HTTPS (GitHub API + Release assets)                         │
            │                                                             │ 启动 / 关闭
            ▼                                                             │ 由用户掌握
┌────────────────────────────────────────────────┐                        │
│ GitHub Actions (on push tag v*)                │  ──── Release asset ───┘
│  - dotnet publish AOT win-x64 single-file      │  (AvatarVariantOscBridge-<tag>-win-x64.exe)
│  - 产出单个 exe 当 release asset               │
└────────────────────────────────────────────────┘
```

关键边界：

- **桥不知道 Unity 存在**。接受 `--map` 或读"上次路径"。非 Unity 用户也能直接用。
- **Inspector 不知道桥在干啥**。拉起来就撒手，关 console 窗口 = 关桥。
- **GitHub Actions 不知道 Inspector 存在**。只产 release asset，纯构建流水线。

## 4. 桥侧变更

### 4.1 启动参数行为

当前 [BridgeOptions.Parse](../../../Tools~/AvatarVariantOscBridge/BridgeOptions.cs) 遇到无参就打 usage 退出。改为：

- **无 `--map` / 无位置参数**：
  1. 读 `%LOCALAPPDATA%/LanstardAvatarVariantBridge/settings.json`
  2. 若有 `lastMappingPath` 且文件存在 → 用它
  3. 否则 → 调 Win32 `OPENFILENAME` (`GetOpenFileName`) 弹原生文件选择框，用户选完把路径写回 settings
  4. 用户取消 → 打印提示退出
- **有 `--map` 或位置参数**：照常解析；同时更新 `settings.json` 的 `lastMappingPath`（这样 Inspector 启过一次后，用户以后直接双击 exe 也有记忆）。

### 4.2 Settings 文件

路径：`%LOCALAPPDATA%/LanstardAvatarVariantBridge/settings.json`

Schema：

```json
{ "lastMappingPath": "D:\\...\\avatar-switch-map.json" }
```

放 `%LOCALAPPDATA%` 而不是 exe 同目录 —— 因为 Inspector 自动覆盖更新 exe 时不会冲掉用户配置。

### 4.3 AOT 兼容化

桥现在用 `System.Text.Json` 反射序列化，AOT 下会 warning / fail。改动：

- `.csproj` 加：
  ```xml
  <PropertyGroup>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
  </PropertyGroup>
  ```
- 给 `AvatarVariantMap` 和新的 `BridgeSettings` 类各加一个 `JsonSerializerContext` + `[JsonSerializable]` 注解
- 序列化/反序列化调用改用生成的 `Default` context，不走反射

Win32 文件对话框通过 P/Invoke 调用，不走 reflection，AOT 直接能过。

**体积预期**：AOT single-file win-x64 约 8–12 MB。**如果 AOT 有无法绕开的坑**，降级到 `PublishSingleFile=true` + `PublishTrimmed=true` + `InvariantGlobalization=true`，产物约 30 MB，仍可接受。

### 4.4 兼容性

保留现有所有 CLI 参数（`--host` `--listen` `--send`）。现有的 `.bat` / `.ps1` 用户也能继续用。新行为是"无参时找得到就启动"，是叠加能力，不破旧接口。

## 5. Inspector 侧变更

### 5.1 UI

在 [AvatarVariantSwitchConfigEditor](../../../Editor/AvatarVariantSwitchConfigEditor.cs) 的 Inspector 底部加一个分区：

```
┌─ OSC 桥 ──────────────────────────────────┐
│  [ 启动 OSC 桥 ]                           │
│  （tooltip：说明点击后会做什么）           │
└────────────────────────────────────────────┘
```

### 5.2 点击流程

全程用 `EditorUtility.DisplayProgressBar` 提示状态，总超时 60 秒：

1. **校验映射**：`cfg.outputMapPath` 必须存在且非空，否则 `EditorUtility.DisplayDialog` 提示"请先至少上传一次装扮以生成映射文件"，返回。
2. **查最新 tag**：HTTP GET `https://api.github.com/repos/{REPO_SLUG}/releases/latest`（UA: `LanstardAvatarVariantSwitcher/<package-version>`）。读 `tag_name` 和匹配 `AvatarVariantOscBridge-*-win-x64.exe` 的 asset 的 `browser_download_url` + 对应 `.sha256` asset。超时 5 秒。

   `REPO_SLUG` 的来源（按优先级）：
   1. 读 `package.json` 的 `repository.url` 字段解析（VPM/npm 标准格式）
   2. 读不到就从 Editor 常量 `AvatarVariantBridgeLauncher.DefaultRepoSlug` 取

   **前置**：本仓库当前没有 git remote，作者把包推到 GitHub 后需要同步更新 `package.json` 的 `repository` 字段（标准 VPM 实践应当填）；Inspector 代码里的常量作为 fallback。
3. **对比本地版本**：读 `%LOCALAPPDATA%/LanstardAvatarVariantBridge/version.txt`。若不一致或缺文件 / exe → 下载。
4. **下载**：流式写 `bridge.exe.tmp`，下载完成后比对 SHA256，通过则 `File.Replace` 覆盖 `bridge.exe`，写入新 `version.txt`。SHA256 不通过 → 删 tmp，报错退出。
5. **启动**：`Process.Start(new ProcessStartInfo { FileName = bridgeExe, Arguments = $"--map \"{absPath}\"", UseShellExecute = true })`。`UseShellExecute = true` 让桥在新 console 窗口起。
6. **撒手**：不保留 `Process` 引用，不订阅 `Exited`，清 progress bar 返回。关 Unity 不影响桥。

### 5.3 降级

每一步失败都要有出路：

| 情况 | 行为 |
|---|---|
| GitHub API 超时/失败，本地已有 exe | 用本地 exe 启动，UI 显示提示"未能检查更新，使用本地版本" |
| GitHub API 失败，本地无 exe | `EditorUtility.DisplayDialog`：`[打开 Release 页面] [重试] [取消]` |
| 下载失败 | 清 tmp，对话框 `[重试] [打开 Release 页面] [取消]` |
| SHA256 不匹配 | 清 tmp，保留旧 exe，报错"下载文件校验失败，请重试" |
| 启动 Process 失败 | 对话框提示 exe 路径 + 错误信息 |

### 5.4 辅助菜单项

Unity 菜单加 `Tools/Lanstard Avatar Variant Switcher/清除桥缓存`：

- 删 `%LOCALAPPDATA%/LanstardAvatarVariantBridge/bridge.exe`
- 删 `version.txt`
- 保留 `settings.json`（用户自己的上次路径别冲掉）
- 完成后弹对话框告知

### 5.5 不做的事

- 不跟 Unity 进程生命周期绑定
- 不跟踪桥的运行状态
- 不捕获 stdout
- 不做"停止"按钮
- 不做自动重启

## 6. GitHub Actions

### 6.1 文件

`.github/workflows/release-bridge.yml`（仓库根，不是包内，因为 VPM 包的 git 根决定了 Actions 触发）。

### 6.2 触发

- `push` with tag matching `v*`（如 `v1.0.0`、`v1.2.3-rc1`）
- `workflow_dispatch`（手动触发，方便测试）

### 6.3 Job

单个 `windows-latest` runner：

1. `actions/checkout@v4`
2. `actions/setup-dotnet@v4` with `dotnet-version: 8.0.x`
3. `dotnet publish Packages/com.lanstard.avatar-variant-switcher/Tools~/AvatarVariantOscBridge -c Release -r win-x64 /p:PublishAot=true /p:PublishSingleFile=true /p:InvariantGlobalization=true`
4. 重命名 `AvatarVariantOscBridge.exe` → `AvatarVariantOscBridge-${{ github.ref_name }}-win-x64.exe`
5. 计算 SHA256 → `AvatarVariantOscBridge-${{ github.ref_name }}-win-x64.exe.sha256`
6. `softprops/action-gh-release@v2` 创建 release，附上 exe 和 sha256

### 6.4 Tag 命名

语义化版本 `vMAJOR.MINOR.PATCH[-prerelease]`。Inspector 默认只认 `releases/latest`（GitHub 会自动过滤掉 prerelease）。

### 6.5 预留

在 `BridgeSettings.cs` 里写一个常量 `BRIDGE_PROTOCOL_VERSION = 1`，**当前不使用**。将来真要做版本门卫时用。

## 7. 手测清单（没测试套件，手测要走完）

### 7.1 桥独立

- [ ] 双击 `bridge.exe` 首次启动 → 弹文件选择框 → 选真实 `avatar-switch-map.json` → 正常跑
- [ ] 再次双击 → 直接用上次路径启动（秒开）
- [ ] `bridge.exe --map <path>` → 用指定路径 + 更新 settings
- [ ] `bridge.exe --map <不存在>` → 报错退出
- [ ] 跑起来后改 map.json 保存 → 看到 reload 日志
- [ ] 模拟半写（写一半再 rename）→ 桥不崩，回退到上版本

### 7.2 Inspector

- [ ] `cfg.outputMapPath` 空 → 按钮点击提示先上传
- [ ] `%LOCALAPPDATA%` 空白 + 联网 → 正常下载 → console 窗口弹出
- [ ] `%LOCALAPPDATA%` 已有 exe、tag 一致 → 不重下，秒启动
- [ ] `%LOCALAPPDATA%` 已有 exe、tag 变了 → 下新版覆盖
- [ ] 断网 + 有本地 exe → 用本地启动 + 提示
- [ ] 断网 + 无本地 exe → 对话框提供"打开 Release 页/重试"
- [ ] 故意改坏 `.sha256` → 校验失败 + 不覆盖旧 exe
- [ ] `Tools/.../清除桥缓存` → 清干净 exe 和 version，settings 保留

### 7.3 CI

- [ ] 推 `v0.0.1-test` → Action 跑完 → Release 页有两个 asset
- [ ] `dotnet publish` log 里没有 AOT/trim warning（有则按提示加注解）

## 8. 文档更新

- **README.md**：加"首次使用 OSC 桥"小节，三行话：在 Inspector 点"启动 OSC 桥"按钮 → 等 console 窗口弹出 → 开 VRChat。现有的 `dotnet run` / `dotnet publish` 命令降级到"开发者自己跑源码"那一节，或直接删掉。
- **CLAUDE.md**：加一条约定 —— 改桥侧序列化时同步维护 `[JsonSerializable]`，否则 AOT 构建会挂。

## 9. 风险

| 风险 | 缓解 |
|---|---|
| `PublishAot` 遇上 `System.Text.Json` 或 `FileSystemWatcher` 的反射路径 | 降级到 `PublishSingleFile=true` + `PublishTrimmed=true`，产物 30 MB 仍可接受 |
| GitHub API 限流（未认证 60 req/h per IP） | 加 `User-Agent` 头（GitHub 要求）；本地 exe 优先，API 失败不阻塞 |
| 用户环境没有 `%LOCALAPPDATA%` 写权限（企业锁机） | 降级对话框提示手动下载 |
| Win32 文件对话框在不同 Windows 版本 API 小差异 | 用 `comdlg32.dll!GetOpenFileNameW`（所有 Win 版本都稳定） |
| Tag 打错、Release 里没有期望的 asset 文件名 | Inspector 解析 asset 列表时找不到目标文件名 → 报错引导用户提 issue |

## 10. 实施范围

落地时分这些文件变更：

**新增**：
- `.github/workflows/release-bridge.yml`
- `Tools~/AvatarVariantOscBridge/BridgeSettings.cs`（含 `JsonSerializerContext` + protocol version 常量）
- `Tools~/AvatarVariantOscBridge/FileDialog.cs`（Win32 P/Invoke 封装）
- `Editor/AvatarVariantBridgeLauncher.cs`（下载 + 启动逻辑）
- `Editor/AvatarVariantBridgeCacheMenu.cs`（清除缓存菜单项）

**修改**：
- `Tools~/AvatarVariantOscBridge/AvatarVariantOscBridge.csproj`（加 AOT props）
- `Tools~/AvatarVariantOscBridge/BridgeOptions.cs`（无参时读 settings / 弹对话框）
- `Tools~/AvatarVariantOscBridge/AvatarVariantMap.cs`（加 `JsonSerializable` 注解）
- `Editor/AvatarVariantSwitchConfigEditor.cs`（加"OSC 桥"分区 + 按钮）
- `package.json`（加 `repository` 字段，作者推仓库到 GitHub 后填）
- `README.md`（简化 OSC 桥那节）
- `CLAUDE.md`（加 AOT 注意事项）
