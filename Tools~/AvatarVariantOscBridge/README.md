# Avatar Variant OSC Bridge

独立 .NET 8 控制台程序。读取 Unity 侧生成的 `avatar-switch-map.json`，把 VRChat 发出的参数变化转成 `/avatar/change` 指令回传。

## 硬前提

**VRChat 的 `/avatar/change` OSC 命令只对"收藏夹里"的 avatar 生效。**
首次上传完所有变体后，请在 VRChat 游戏内把每个变体都 ⭐ 加入收藏夹，否则本工具发出的切换指令会被客户端静默丢弃。

## 运行

命令行：

```powershell
dotnet run --project Packages/com.lanstard.avatar-variant-switcher/Tools~/AvatarVariantOscBridge -- ^
  --map "D:/path/to/avatar-switch-map.json"
```

或用位置参数：

```powershell
dotnet run --project Packages/com.lanstard.avatar-variant-switcher/Tools~/AvatarVariantOscBridge -- ^
  "D:/path/to/avatar-switch-map.json"
```

批处理入口：

```bat
Packages\com.lanstard.avatar-variant-switcher\Tools~\AvatarVariantOscBridge\RunAvatarVariantOscBridge.bat "D:\path\to\avatar-switch-map.json"
```

## 参数

| 参数 | 默认值 | 说明 |
|---|---|---|
| 位置参数 / `--map` / `--mapping` | *（必填）* | 映射 JSON 文件绝对路径 |
| `--host` | `127.0.0.1` | VRChat 客户端地址 |
| `--listen` / `--listen-port` | `9001` | 本地监听端口（VRChat 向此发参数变化） |
| `--send` / `--send-port` | `9000` | VRChat 监听端口（本工具发切换指令） |

## 行为

- 启动时加载映射文件，列出所有变体及其 blueprint id。
- 监听 `/avatar/parameters/<parameterName>`；当收到的 int 值发生变化时，查找对应变体并发送 `/avatar/change s <blueprintId>` 到 9000。
- 监听 `/avatar/change` 反向消息，记录当前 avatar id，避免重复发送相同的切换命令。
- 映射文件用 `FileSystemWatcher` 热重载（500ms 去抖）。解析失败时保留上一份有效映射、打印警告到 stderr；Unity 侧用 `WriteAtomic`（temp + rename）尽量避免读到半写入文件。
- Ctrl+C 干净退出。

## 构建

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

需要在机器上有 .NET 8 SDK 或运行时。
