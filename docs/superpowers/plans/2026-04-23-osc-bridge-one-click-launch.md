# OSC 桥一键启动 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让用户在 Unity Inspector 点一下按钮就能把 OSC 桥跑起来——Inspector 负责从 GitHub Release 下载/缓存 exe 并启动，桥本身能独立双击使用（记住上次映射路径，第一次弹文件选择框）。

**Architecture:** 三块分离的子系统通过明确边界通信——桥（console 程序，读 `%LOCALAPPDATA%/LanstardAvatarVariantBridge/settings.json`，弹 Win32 文件对话框，走 AOT 编译）、Inspector（GitHub API 查最新 tag、SHA256 校验下载、`Process.Start` 拉起桥）、GitHub Actions（push tag 触发 AOT publish 并发布 release asset）。没有测试套件，验收靠手测（依设计文档 [2026-04-23-osc-bridge-one-click-launch-design.md](../specs/2026-04-23-osc-bridge-one-click-launch-design.md) 第 7 节清单）。

**Tech Stack:** .NET 8 + NativeAOT、`System.Text.Json` source generator、Win32 P/Invoke (`comdlg32!GetOpenFileNameW`)、Unity Editor API、`System.Net.Http.HttpClient`、GitHub Actions (`actions/setup-dotnet@v4` + `softprops/action-gh-release@v2`)。

---

## File structure

**新增文件**：
- `.github/workflows/release-bridge.yml` — CI 流水线
- `Tools~/AvatarVariantOscBridge/BridgeJsonContext.cs` — `JsonSerializerContext` source generator（AOT 序列化）
- `Tools~/AvatarVariantOscBridge/BridgeSettings.cs` — 桥端用户设置（`lastMappingPath`）
- `Tools~/AvatarVariantOscBridge/FileDialog.cs` — Win32 `GetOpenFileNameW` P/Invoke 封装
- `Editor/AvatarVariantBridgeLauncher.cs` — 下载 + SHA256 校验 + 启动桥
- `Editor/AvatarVariantBridgeCacheMenu.cs` — 清除缓存菜单项

**修改文件**：
- `Tools~/AvatarVariantOscBridge/AvatarVariantOscBridge.csproj` — 加 AOT 属性
- `Tools~/AvatarVariantOscBridge/AvatarVariantMap.cs` — 加 `[JsonPropertyName]`，改用 `BridgeJsonContext.Default`
- `Tools~/AvatarVariantOscBridge/BridgeOptions.cs` — 无参时读 settings / 弹文件对话框
- `Editor/AvatarVariantSwitchConfigEditor.cs` — 加"OSC 桥"分区
- `package.json` — 加 `repository` 字段
- `README.md` — 简化 OSC 桥使用章节
- `CLAUDE.md` — 加 AOT 序列化约束提示

---

## Task 1: 桥侧 AOT 基础（csproj + JSON source generator）

**为什么第一个做**：后续所有桥侧改动都依赖 AOT 编译能过。先把 JSON 序列化从反射切到 source generator，再接入其他变更。

**Files:**
- Modify: `Tools~/AvatarVariantOscBridge/AvatarVariantOscBridge.csproj`
- Create: `Tools~/AvatarVariantOscBridge/BridgeJsonContext.cs`
- Modify: `Tools~/AvatarVariantOscBridge/AvatarVariantMap.cs`

- [ ] **Step 1: 更新 csproj 加 AOT 属性**

把 `Tools~/AvatarVariantOscBridge/AvatarVariantOscBridge.csproj` 整个覆盖成：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>

    <!-- AOT + 单文件 + 去本地化 → exe 约 8–12 MB -->
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: 创建 BridgeJsonContext.cs**

写 `Tools~/AvatarVariantOscBridge/BridgeJsonContext.cs`，内容：

```csharp
using System.Text.Json.Serialization;

namespace AvatarVariantOscBridge;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AvatarVariantMap))]
[JsonSerializable(typeof(AvatarVariantMapEntry))]
internal partial class BridgeJsonContext : JsonSerializerContext
{
}
```

> 注：这里暂时只注册 `AvatarVariantMap` 类型。`BridgeSettings` 在 Task 2 加进来。

- [ ] **Step 3: 改造 AvatarVariantMap.cs 为 source-gen 友好**

把 `Tools~/AvatarVariantOscBridge/AvatarVariantMap.cs` 整个覆盖成：

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvatarVariantOscBridge;

internal sealed class AvatarVariantMap
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("generatedAtUtc")]
    public string GeneratedAtUtc { get; set; } = string.Empty;

    [JsonPropertyName("parameterName")]
    public string ParameterName { get; set; } = string.Empty;

    [JsonPropertyName("menuName")]
    public string MenuName { get; set; } = string.Empty;

    [JsonPropertyName("defaultValue")]
    public int DefaultValue { get; set; }

    [JsonPropertyName("variants")]
    public List<AvatarVariantMapEntry> Variants { get; set; } = new();

    public static AvatarVariantMap Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Mapping file not found.", path);

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Mapping file is empty.");

        var map = JsonSerializer.Deserialize(json, BridgeJsonContext.Default.AvatarVariantMap);
        if (map == null)
            throw new InvalidDataException("Failed to deserialize mapping file.");

        map.Variants ??= new List<AvatarVariantMapEntry>();
        if (string.IsNullOrWhiteSpace(map.ParameterName))
            throw new InvalidDataException("Mapping file is missing parameterName.");

        return map;
    }
}

internal sealed class AvatarVariantMapEntry
{
    [JsonPropertyName("variantKey")]
    public string VariantKey { get; set; } = string.Empty;

    [JsonPropertyName("paramValue")]
    public int ParamValue { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("blueprintId")]
    public string BlueprintId { get; set; } = string.Empty;
}
```

主要变化：
- 每个属性加 `[JsonPropertyName]` 显式映射 camelCase（旧实现靠 `PropertyNameCaseInsensitive`，source-gen 里不支持）。
- 反序列化改用 `BridgeJsonContext.Default.AvatarVariantMap`（source-gen 提供的 `JsonTypeInfo<T>`），避免反射。
- 去掉 `private static readonly JsonSerializerOptions Options` 字段。

- [ ] **Step 4: 本地验证 AOT build（如果装了 .NET 8 SDK）**

在仓库根执行：

```bash
dotnet publish Tools~/AvatarVariantOscBridge -c Release -r win-x64 /p:PublishSingleFile=true
```

Expected：
- 构建成功，无 `IL2026`/`IL3050`/`AOT` warning
- 产物在 `Tools~/AvatarVariantOscBridge/bin/Release/net8.0/win-x64/publish/AvatarVariantOscBridge.exe`，体积 8–15 MB
- 跑 `./AvatarVariantOscBridge.exe --map <真实 map.json>` → 正常启动并读到映射

如果没装 .NET 8 SDK：跳过此步，靠 Task 5 的 GitHub Actions 验证。

- [ ] **Step 5: Commit**

```bash
git add Tools~/AvatarVariantOscBridge/AvatarVariantOscBridge.csproj \
        Tools~/AvatarVariantOscBridge/BridgeJsonContext.cs \
        Tools~/AvatarVariantOscBridge/AvatarVariantMap.cs
git commit -m "bridge: switch JSON to source-gen + enable AOT publish"
```

---

## Task 2: BridgeSettings（持久化桥侧用户配置）

**Files:**
- Create: `Tools~/AvatarVariantOscBridge/BridgeSettings.cs`
- Modify: `Tools~/AvatarVariantOscBridge/BridgeJsonContext.cs`

- [ ] **Step 1: 创建 BridgeSettings.cs**

写 `Tools~/AvatarVariantOscBridge/BridgeSettings.cs`：

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvatarVariantOscBridge;

internal sealed class BridgeSettings
{
    /// <summary>
    /// 桥 ↔ Inspector / 映射文件的协议版本。当前未使用，
    /// 未来映射文件 schema 或 OSC 协议真出现不兼容时用作门卫。
    /// </summary>
    public const int BridgeProtocolVersion = 1;

    [JsonPropertyName("lastMappingPath")]
    public string? LastMappingPath { get; set; }

    private static string SettingsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanstardAvatarVariantBridge");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static BridgeSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new BridgeSettings();
            var json = File.ReadAllText(SettingsPath);
            if (string.IsNullOrWhiteSpace(json)) return new BridgeSettings();
            return JsonSerializer.Deserialize(json, BridgeJsonContext.Default.BridgeSettings)
                ?? new BridgeSettings();
        }
        catch
        {
            // Corrupt / unreadable settings → start fresh, never crash on launch.
            return new BridgeSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(this, BridgeJsonContext.Default.BridgeSettings);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARN: failed to save settings: {ex.Message}");
        }
    }
}
```

- [ ] **Step 2: 把 BridgeSettings 加到 JsonContext**

修改 `Tools~/AvatarVariantOscBridge/BridgeJsonContext.cs`，在两个 `AvatarVariantMap*` 之下加一行：

```csharp
[JsonSerializable(typeof(BridgeSettings))]
```

完整文件：

```csharp
using System.Text.Json.Serialization;

namespace AvatarVariantOscBridge;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AvatarVariantMap))]
[JsonSerializable(typeof(AvatarVariantMapEntry))]
[JsonSerializable(typeof(BridgeSettings))]
internal partial class BridgeJsonContext : JsonSerializerContext
{
}
```

- [ ] **Step 3: 编译验证**

```bash
dotnet build Tools~/AvatarVariantOscBridge -c Release
```

Expected：无错误无 warning。

- [ ] **Step 4: Commit**

```bash
git add Tools~/AvatarVariantOscBridge/BridgeSettings.cs \
        Tools~/AvatarVariantOscBridge/BridgeJsonContext.cs
git commit -m "bridge: add BridgeSettings for %LOCALAPPDATA% persistence"
```

---

## Task 3: FileDialog（Win32 文件选择框）

**Files:**
- Create: `Tools~/AvatarVariantOscBridge/FileDialog.cs`

- [ ] **Step 1: 创建 FileDialog.cs**

写 `Tools~/AvatarVariantOscBridge/FileDialog.cs`：

```csharp
using System.Runtime.InteropServices;
using System.Text;

namespace AvatarVariantOscBridge;

/// <summary>
/// Win32 GetOpenFileNameW 的最小封装。原生 API 不走反射，AOT 直接可用。
/// </summary>
internal static class FileDialog
{
    public static string? PickMappingFile(string title, string? initialPath)
    {
        var fileBuffer = new StringBuilder(260);
        if (!string.IsNullOrWhiteSpace(initialPath) && File.Exists(initialPath))
        {
            fileBuffer.Append(initialPath);
        }

        // 过滤器格式："显示文本\0通配\0显示文本\0通配\0\0"
        var filter = "Avatar Variant Map (*.json)\0*.json\0All files (*.*)\0*.*\0\0";

        var ofn = new OpenFileName
        {
            lStructSize = Marshal.SizeOf<OpenFileName>(),
            hwndOwner = IntPtr.Zero,
            lpstrFilter = filter,
            lpstrFile = fileBuffer,
            nMaxFile = fileBuffer.Capacity,
            lpstrTitle = title,
            Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER | OFN_NOCHANGEDIR
        };

        if (!GetOpenFileNameW(ref ofn))
        {
            return null; // 用户取消或对话框打开失败
        }

        var result = fileBuffer.ToString();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_EXPLORER      = 0x00080000;
    private const int OFN_NOCHANGEDIR   = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int    lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrFilter;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrCustomFilter;
        public int    nMaxCustFilter;
        public int    nFilterIndex;
        public StringBuilder lpstrFile;
        public int    nMaxFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrFileTitle;
        public int    nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string  lpstrTitle;
        public int    Flags;
        public short  nFileOffset;
        public short  nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTemplateName;
        public IntPtr pvReserved;
        public int    dwReserved;
        public int    FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);
}
```

- [ ] **Step 2: 编译验证**

```bash
dotnet build Tools~/AvatarVariantOscBridge -c Release
```

Expected：无错误无 warning。

- [ ] **Step 3: Commit**

```bash
git add Tools~/AvatarVariantOscBridge/FileDialog.cs
git commit -m "bridge: add Win32 file picker for first-run map selection"
```

---

## Task 4: 改造 BridgeOptions — 无参时从 settings 或文件对话框补齐

**Files:**
- Modify: `Tools~/AvatarVariantOscBridge/BridgeOptions.cs`

- [ ] **Step 1: 重写 BridgeOptions.cs**

把 `Tools~/AvatarVariantOscBridge/BridgeOptions.cs` 整个覆盖成：

```csharp
namespace AvatarVariantOscBridge;

internal sealed class BridgeOptions
{
    public string MappingPath { get; private set; } = string.Empty;
    public string Host { get; private set; } = "127.0.0.1";
    public int ListenPort { get; private set; } = 9001;
    public int SendPort { get; private set; } = 9000;

    public static BridgeOptions? Parse(string[] args)
    {
        var opts = new BridgeOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--map":
                case "--mapping":
                    opts.MappingPath = Path.GetFullPath(RequireValue(args, ref i, arg));
                    break;
                case "--host":
                    opts.Host = RequireValue(args, ref i, arg);
                    break;
                case "--listen":
                case "--listen-port":
                    opts.ListenPort = int.Parse(RequireValue(args, ref i, arg));
                    break;
                case "--send":
                case "--send-port":
                    opts.SendPort = int.Parse(RequireValue(args, ref i, arg));
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return null;
                default:
                    if (string.IsNullOrWhiteSpace(opts.MappingPath) && !arg.StartsWith('-'))
                    {
                        opts.MappingPath = Path.GetFullPath(arg);
                    }
                    else
                    {
                        Console.Error.WriteLine($"Unknown argument: {arg}");
                        PrintUsage();
                        return null;
                    }
                    break;
            }
        }

        // 没显式给映射路径 → 走 settings / 文件选择框补齐。
        if (string.IsNullOrWhiteSpace(opts.MappingPath))
        {
            if (!TryResolveMappingPathFromSettings(out var resolved))
                return null;
            opts.MappingPath = resolved;
        }

        // 任何情况下都记住本次用的映射路径，方便下次双击直接跑。
        PersistLastMappingPath(opts.MappingPath);

        return opts;
    }

    private static bool TryResolveMappingPathFromSettings(out string path)
    {
        path = string.Empty;

        var settings = BridgeSettings.Load();
        if (!string.IsNullOrWhiteSpace(settings.LastMappingPath) && File.Exists(settings.LastMappingPath))
        {
            path = settings.LastMappingPath!;
            Console.WriteLine($"Using last mapping file: {path}");
            return true;
        }

        Console.WriteLine("No mapping path given. Opening file picker...");
        var picked = FileDialog.PickMappingFile(
            title: "Select avatar-switch-map.json",
            initialPath: settings.LastMappingPath);

        if (string.IsNullOrWhiteSpace(picked))
        {
            Console.Error.WriteLine("No mapping file selected. Exiting.");
            return false;
        }

        path = Path.GetFullPath(picked);
        return true;
    }

    private static void PersistLastMappingPath(string path)
    {
        var settings = BridgeSettings.Load();
        if (string.Equals(settings.LastMappingPath, path, StringComparison.OrdinalIgnoreCase))
            return;
        settings.LastMappingPath = path;
        settings.Save();
    }

    private static string RequireValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {optionName}");
        index++;
        return args[index];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  AvatarVariantOscBridge                          (uses last-used mapping, or shows file picker)");
        Console.WriteLine("  AvatarVariantOscBridge <mapping.json>            [--host 127.0.0.1] [--listen 9001] [--send 9000]");
        Console.WriteLine("  AvatarVariantOscBridge --map <mapping.json>      [--host ...] [--listen ...] [--send ...]");
    }
}
```

关键行为：
- 有参：照常解析；解析完后把 `MappingPath` 写进 settings
- 无参：先查 settings → 有效路径直接用；无效/缺失 → 弹文件选择框 → 用户选了写回 settings，取消则退出

- [ ] **Step 2: 编译验证**

```bash
dotnet build Tools~/AvatarVariantOscBridge -c Release
```

Expected：无错误无 warning。

- [ ] **Step 3: 手测（依设计文档 7.1 节）**

准备一份真实 `avatar-switch-map.json`（从 Unity 项目的 `Assets/AvatarVariantSwitcher/Generated/` 复制一份到桌面）。

```bash
cd Tools~/AvatarVariantOscBridge/bin/Release/net8.0/<runtime-id>/publish
```

（如果没做 publish，走 `dotnet run --project Tools~/AvatarVariantOscBridge --` 也行）

依次验证：
- [ ] 删掉 `%LOCALAPPDATA%\LanstardAvatarVariantBridge\settings.json`（如果有）→ 启动桥 → 弹文件选择框 → 选 map.json → 桥正常跑
- [ ] 再次启动（无参）→ 不弹框，直接用上次路径
- [ ] 启动桥加参 `--map <别的路径>` → 用新路径；退出后再无参启动 → 用的是新路径
- [ ] 启动桥加参 `--map <不存在的路径>` → `FileNotFoundException` 报错退出，不改写 settings（因为 `File.Exists` 检查发生在 `AvatarVariantMap.Load`，`BridgeOptions.Parse` 仍然写了 settings——这是预期行为，下次无参启动会再弹框因为路径失效）

- [ ] **Step 4: Commit**

```bash
git add Tools~/AvatarVariantOscBridge/BridgeOptions.cs
git commit -m "bridge: auto-resolve mapping path from settings or file picker"
```

---

## Task 5: GitHub Actions release workflow

**Files:**
- Create: `.github/workflows/release-bridge.yml`

- [ ] **Step 1: 创建 workflow**

写 `.github/workflows/release-bridge.yml`：

```yaml
name: Release OSC Bridge

on:
  push:
    tags:
      - 'v*'
  workflow_dispatch:

permissions:
  contents: write

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 8
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Publish bridge (AOT, win-x64)
        shell: pwsh
        run: |
          dotnet publish Tools~/AvatarVariantOscBridge `
            -c Release `
            -r win-x64 `
            /p:PublishSingleFile=true `
            -o publish

      - name: Rename artifact and compute SHA256
        id: artifact
        shell: pwsh
        run: |
          $tag = "${{ github.ref_name }}"
          if ([string]::IsNullOrWhiteSpace($tag) -or -not $tag.StartsWith('v')) {
            $tag = "dev-$(Get-Date -Format 'yyyyMMddHHmmss')"
          }
          $exeSrc = "publish/AvatarVariantOscBridge.exe"
          $exeDst = "publish/AvatarVariantOscBridge-$tag-win-x64.exe"
          $hashDst = "$exeDst.sha256"

          if (-not (Test-Path $exeSrc)) {
            throw "Expected $exeSrc not found. Publish must have failed."
          }

          Move-Item $exeSrc $exeDst
          $hash = (Get-FileHash -Algorithm SHA256 -Path $exeDst).Hash.ToLowerInvariant()
          "$hash  $(Split-Path $exeDst -Leaf)" | Out-File -FilePath $hashDst -Encoding ASCII -NoNewline

          "exe=$exeDst" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
          "sha=$hashDst" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
          "tag=$tag" | Out-File -FilePath $env:GITHUB_OUTPUT -Append

      - name: Verify no AOT warnings
        shell: pwsh
        run: |
          # publish 阶段有 AOT warning 时 exit code 非 0 就 fail；
          # 这一步只是给一个 sanity log，主要依赖 publish 自身报错。
          Write-Host "Published asset: ${{ steps.artifact.outputs.exe }}"
          Write-Host "SHA256 file:     ${{ steps.artifact.outputs.sha }}"

      - name: Create release
        if: startsWith(github.ref, 'refs/tags/v')
        uses: softprops/action-gh-release@v2
        with:
          tag_name: ${{ github.ref_name }}
          files: |
            ${{ steps.artifact.outputs.exe }}
            ${{ steps.artifact.outputs.sha }}
          generate_release_notes: true

      - name: Upload as artifact (for workflow_dispatch)
        if: ${{ !startsWith(github.ref, 'refs/tags/v') }}
        uses: actions/upload-artifact@v4
        with:
          name: bridge-${{ steps.artifact.outputs.tag }}-win-x64
          path: |
            publish/AvatarVariantOscBridge-*-win-x64.exe
            publish/AvatarVariantOscBridge-*-win-x64.exe.sha256
```

说明：
- tag 触发时自动建 release 并附 exe + sha256
- 手动 dispatch 时产物以 artifact 形式留存，方便测 CI 而不创建 release
- `/p:PublishAot=true` 和 `/p:PublishSingleFile=true` 在 csproj 里已经打开，workflow 只再强一道 SingleFile 保险

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/release-bridge.yml
git commit -m "ci: add AOT release workflow for OSC bridge"
```

---

## Task 6: 测发一次 pre-release 验证 CI

**此任务需要仓库已推送到 GitHub。如果仓库还没推远程，先把 master 推到 GitHub 新建的仓库。**

- [ ] **Step 1: 推 master 到 GitHub（如果还没）**

```bash
# 用户在 GitHub 上新建 repo lanstard/com.lanstard.avatar-variant-switcher（或类似名字）
git remote add origin https://github.com/<owner>/<repo>.git
git push -u origin master
```

- [ ] **Step 2: 推测试 tag**

```bash
git tag v0.0.1-test
git push origin v0.0.1-test
```

- [ ] **Step 3: 验证 Actions 结果**

打开 `https://github.com/<owner>/<repo>/actions`：
- [ ] workflow 跑成功（绿勾）
- [ ] 没有 `IL2026` / `IL3050` / `AOT` warning（在 `Publish bridge (AOT, win-x64)` 步骤的 log 里翻）

打开 `https://github.com/<owner>/<repo>/releases`：
- [ ] 有 `v0.0.1-test` 这个 release
- [ ] 附件有 `AvatarVariantOscBridge-v0.0.1-test-win-x64.exe` 和 `AvatarVariantOscBridge-v0.0.1-test-win-x64.exe.sha256`
- [ ] 下载 exe，本地双击 → 弹文件选择框（因为本机 settings 还没 `lastMappingPath` 或者是从没用过的机器）→ 选一个真实 map.json → 桥正常启动

- [ ] **Step 4: 删掉测试 tag**

```bash
# 本地
git tag -d v0.0.1-test
# 远程
git push origin :refs/tags/v0.0.1-test
```

GitHub UI 里把 `v0.0.1-test` release 改成 "draft" 或直接删掉，免得被 Inspector 拉到当 "latest"。

---

## Task 7: package.json 加 repository 字段

**Files:**
- Modify: `package.json`

- [ ] **Step 1: 编辑 package.json**

把 `package.json` 内容改为（在 `"vpmDependencies"` 之前插入 `"repository"` 字段）：

```json
{
  "name": "com.lanstard.avatar-variant-switcher",
  "displayName": "Avatar 装扮切换器",
  "version": "0.1.0",
  "unity": "2022.3",
  "description": "把同一个 VRChat avatar 的多套衣服/配件组合一键批量上传成独立 blueprint，运行时通过 OSC 自动切换对应模型。",
  "author": {
    "name": "lanstard"
  },
  "repository": {
    "type": "git",
    "url": "https://github.com/<owner>/<repo>.git"
  },
  "vpmDependencies": {
    "com.vrchat.avatars": ">=3.9.0",
    "nadena.dev.modular-avatar": ">=1.10.0"
  }
}
```

把 `<owner>/<repo>` 换成 Task 6 里实际建的 GitHub 仓库 slug（例如 `lanstard/avatar-variant-switcher`）。

- [ ] **Step 2: Commit**

```bash
git add package.json
git commit -m "package: declare repository URL for release lookup"
```

---

## Task 8: Editor 侧 — AvatarVariantBridgeLauncher

**Files:**
- Create: `Editor/AvatarVariantBridgeLauncher.cs`

- [ ] **Step 1: 创建 AvatarVariantBridgeLauncher.cs**

写 `Editor/AvatarVariantBridgeLauncher.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    /// <summary>
    /// 负责把 OSC 桥从 GitHub Release 拉下来、校验、启动。
    /// 单独一个类，和 Inspector 解耦，方便以后也从菜单项或其他入口调用。
    /// </summary>
    internal static class AvatarVariantBridgeLauncher
    {
        // Inspector 代码兜底常量；优先走 package.json 的 repository.url。
        private const string DefaultRepoSlug = "lanstard/avatar-variant-switcher";

        private const string AssetNamePattern = "AvatarVariantOscBridge-{0}-win-x64.exe";
        private const string PackageFolder = "com.lanstard.avatar-variant-switcher";

        private static string CacheDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LanstardAvatarVariantBridge");

        private static string BridgeExePath => Path.Combine(CacheDirectory, "bridge.exe");
        private static string VersionPath => Path.Combine(CacheDirectory, "version.txt");

        /// <summary>
        /// 按钮入口：全链路跑完。失败时用 EditorUtility.DisplayDialog 报告。
        /// </summary>
        public static void DownloadAndLaunch(string absoluteMappingPath)
        {
            if (string.IsNullOrWhiteSpace(absoluteMappingPath) || !File.Exists(absoluteMappingPath))
            {
                EditorUtility.DisplayDialog(
                    "启动 OSC 桥",
                    "映射文件还不存在。请先至少执行一次 [批量上传所有装扮] 或 [写入映射文件]，生成 avatar-switch-map.json。",
                    "确定");
                return;
            }

            string exePath = null;
            try
            {
                EditorUtility.DisplayProgressBar("OSC 桥", "检查更新...", 0.1f);
                exePath = EnsureLatestExe();
            }
            catch (Exception ex)
            {
                if (File.Exists(BridgeExePath))
                {
                    Debug.LogWarning($"[AvatarVariantSwitcher] 未能检查更新，使用本地已有版本。原因：{ex.Message}");
                    exePath = BridgeExePath;
                }
                else
                {
                    EditorUtility.ClearProgressBar();
                    var repoUrl = $"https://github.com/{ResolveRepoSlug()}/releases/latest";
                    var click = EditorUtility.DisplayDialogComplex(
                        "启动 OSC 桥",
                        $"下载桥 exe 失败：{ex.Message}\n\n请检查网络，或手动打开 Release 页面下载。",
                        "打开 Release 页面",
                        "取消",
                        "重试");
                    switch (click)
                    {
                        case 0: Application.OpenURL(repoUrl); return;
                        case 2: DownloadAndLaunch(absoluteMappingPath); return;
                        default: return;
                    }
                }
            }

            try
            {
                EditorUtility.DisplayProgressBar("OSC 桥", "启动...", 0.9f);
                LaunchBridge(exePath, absoluteMappingPath);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    "启动 OSC 桥",
                    $"启动进程失败：{ex.Message}\n\nexe 路径：{exePath}",
                    "确定");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// 保证 CacheDirectory 里的 bridge.exe 是 latest release 的版本。
        /// 返回 exe 绝对路径。失败时抛异常，由调用方决定回退策略。
        /// </summary>
        private static string EnsureLatestExe()
        {
            Directory.CreateDirectory(CacheDirectory);

            var latest = QueryLatestRelease();
            var localVersion = File.Exists(VersionPath) ? File.ReadAllText(VersionPath).Trim() : null;

            if (File.Exists(BridgeExePath) && string.Equals(localVersion, latest.TagName, StringComparison.Ordinal))
            {
                return BridgeExePath; // 已是最新
            }

            EditorUtility.DisplayProgressBar("OSC 桥", $"下载 {latest.TagName}...", 0.4f);
            var tmp = BridgeExePath + ".tmp";
            DownloadFile(latest.ExeUrl, tmp);

            EditorUtility.DisplayProgressBar("OSC 桥", "校验 SHA256...", 0.8f);
            var shaResponse = DownloadString(latest.ShaUrl);
            var expectedSha = shaResponse
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0]
                .Trim().ToLowerInvariant();
            var actualSha = ComputeSha256(tmp);
            if (!string.Equals(expectedSha, actualSha, StringComparison.Ordinal))
            {
                File.Delete(tmp);
                throw new InvalidDataException($"SHA256 mismatch. expected={expectedSha} actual={actualSha}");
            }

            if (File.Exists(BridgeExePath))
                File.Replace(tmp, BridgeExePath, null);
            else
                File.Move(tmp, BridgeExePath);

            File.WriteAllText(VersionPath, latest.TagName);
            return BridgeExePath;
        }

        private static ReleaseInfo QueryLatestRelease()
        {
            var slug = ResolveRepoSlug();
            var url = $"https://api.github.com/repos/{slug}/releases/latest";
            var json = DownloadString(url, timeoutSeconds: 5);

            var response = JsonUtility.FromJson<GithubReleaseResponse>(json);
            if (response == null || string.IsNullOrWhiteSpace(response.tag_name))
                throw new InvalidDataException("GitHub release 响应缺少 tag_name");

            var tag = response.tag_name;
            var exeName = string.Format(AssetNamePattern, tag);
            string exeUrl = null, shaUrl = null;
            if (response.assets != null)
            {
                foreach (var asset in response.assets)
                {
                    if (asset == null) continue;
                    if (string.Equals(asset.name, exeName, StringComparison.Ordinal))
                        exeUrl = asset.browser_download_url;
                    else if (string.Equals(asset.name, exeName + ".sha256", StringComparison.Ordinal))
                        shaUrl = asset.browser_download_url;
                }
            }

            if (exeUrl == null || shaUrl == null)
                throw new InvalidDataException($"Release {tag} 不含期望的 asset（{exeName} 或 .sha256）");

            return new ReleaseInfo(tag, exeUrl, shaUrl);
        }

        private static void LaunchBridge(string exePath, string mappingPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--map \"{mappingPath}\"",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            };
            using var proc = Process.Start(psi);
            // 不保留引用，不 Wait，关 Unity 不影响桥。
        }

        private static string ResolveRepoSlug()
        {
            try
            {
                var packagePath = PackageJsonPath();
                if (packagePath != null && File.Exists(packagePath))
                {
                    var json = File.ReadAllText(packagePath);
                    var pkg = JsonUtility.FromJson<PackageJsonFile>(json);
                    if (pkg?.repository != null)
                    {
                        var slug = ParseSlug(pkg.repository.url);
                        if (!string.IsNullOrWhiteSpace(slug) && !slug.Contains("<owner>"))
                            return slug;
                    }
                }
            }
            catch { /* fall through */ }

            return DefaultRepoSlug;
        }

        private static string PackageJsonPath()
        {
            var candidates = new List<string>
            {
                Path.Combine("Packages", PackageFolder, "package.json"),
                Path.Combine("Packages", "package.json"),
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return null;
        }

        private static string ParseSlug(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var trimmed = url.Trim();
            var sshPrefix = "git@github.com:";
            var httpsPrefix = "https://github.com/";
            string body;
            if (trimmed.StartsWith(sshPrefix, StringComparison.OrdinalIgnoreCase))
                body = trimmed.Substring(sshPrefix.Length);
            else if (trimmed.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase))
                body = trimmed.Substring(httpsPrefix.Length);
            else
                return null;
            if (body.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                body = body.Substring(0, body.Length - 4);
            var parts = body.Split('/');
            return parts.Length == 2 ? $"{parts[0]}/{parts[1]}" : null;
        }

        private static string ComputeSha256(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string DownloadString(string url, int timeoutSeconds = 30)
        {
            using var http = CreateHttpClient(timeoutSeconds);
            return http.GetStringAsync(url).GetAwaiter().GetResult();
        }

        private static void DownloadFile(string url, string destPath)
        {
            using var http = CreateHttpClient(timeoutSeconds: 120);
            using var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var src = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var dst = File.Create(destPath);
            src.CopyTo(dst);
        }

        private static HttpClient CreateHttpClient(int timeoutSeconds)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            // GitHub API 强制要求 User-Agent。
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LanstardAvatarVariantSwitcher", "0.1"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return http;
        }

        internal static void ClearCache()
        {
            try
            {
                if (File.Exists(BridgeExePath)) File.Delete(BridgeExePath);
                if (File.Exists(VersionPath)) File.Delete(VersionPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AvatarVariantSwitcher] 清除缓存失败：{ex.Message}");
            }
        }

        internal static string GetCacheDirectoryForDisplay() => CacheDirectory;

        private readonly struct ReleaseInfo
        {
            public readonly string TagName;
            public readonly string ExeUrl;
            public readonly string ShaUrl;
            public ReleaseInfo(string tag, string exe, string sha) { TagName = tag; ExeUrl = exe; ShaUrl = sha; }
        }

        // Unity JsonUtility 只处理 [Serializable] 的 public 字段（不是属性）。
        // GitHub API 返回的字段名必须精确匹配。
        [Serializable]
        private class GithubReleaseResponse
        {
            public string tag_name;
            public GithubReleaseAsset[] assets;
        }

        [Serializable]
        private class GithubReleaseAsset
        {
            public string name;
            public string browser_download_url;
        }

        [Serializable]
        private class PackageJsonFile
        {
            public PackageJsonRepository repository;
        }

        [Serializable]
        private class PackageJsonRepository
        {
            public string url;
        }
    }
}
```

> 说明：用 Unity 原生 `JsonUtility` 而不是 `System.Text.Json`——后者在 Unity 2022.3 Editor 里不是默认可用的程序集。`JsonUtility` 只吃 `[Serializable]` 的 public 字段（不吃属性），所以 `GithubReleaseResponse` / `PackageJsonFile` 等 POCO 用 public 字段。

- [ ] **Step 2: 编译验证**

让 Unity 重新编译（保存文件 → 切回 Unity）。Console 不应有 error。

- [ ] **Step 3: Commit**

```bash
git add Editor/AvatarVariantBridgeLauncher.cs
git commit -m "editor: add bridge auto-download + launch logic"
```

---

## Task 9: Editor 侧 — 清除缓存菜单项

**Files:**
- Create: `Editor/AvatarVariantBridgeCacheMenu.cs`

- [ ] **Step 1: 创建 AvatarVariantBridgeCacheMenu.cs**

写 `Editor/AvatarVariantBridgeCacheMenu.cs`：

```csharp
using System.IO;
using UnityEditor;

namespace Lanstard.AvatarVariantSwitcher.Editor
{
    internal static class AvatarVariantBridgeCacheMenu
    {
        [MenuItem("Tools/Lanstard Avatar Variant Switcher/清除桥缓存", priority = 1000)]
        private static void ClearBridgeCache()
        {
            var dir = AvatarVariantBridgeLauncher.GetCacheDirectoryForDisplay();
            if (!Directory.Exists(dir))
            {
                EditorUtility.DisplayDialog(
                    "清除桥缓存",
                    $"缓存目录不存在，无需清理：\n{dir}",
                    "确定");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "清除桥缓存",
                    $"将清除下列文件（保留 settings.json 不动）：\n\n{dir}\\bridge.exe\n{dir}\\version.txt\n\n继续？",
                    "清除",
                    "取消"))
            {
                return;
            }

            AvatarVariantBridgeLauncher.ClearCache();
            EditorUtility.DisplayDialog("清除桥缓存", "完成。下次启动桥时会重新下载。", "确定");
        }
    }
}
```

- [ ] **Step 2: 编译验证**

Unity Console 无错，菜单 `Tools → Lanstard Avatar Variant Switcher → 清除桥缓存` 可见。

- [ ] **Step 3: Commit**

```bash
git add Editor/AvatarVariantBridgeCacheMenu.cs
git commit -m "editor: add menu item to clear bridge cache"
```

---

## Task 10: Inspector UI 集成 — "启动 OSC 桥"按钮

**Files:**
- Modify: `Editor/AvatarVariantSwitchConfigEditor.cs`

- [ ] **Step 1: 在 Inspector 底部加 OSC 桥分区**

打开 `Editor/AvatarVariantSwitchConfigEditor.cs`，找到 `OnInspectorGUI` 方法末尾的 `"写入映射文件"` 按钮（[AvatarVariantSwitchConfigEditor.cs:114-117](Editor/AvatarVariantSwitchConfigEditor.cs#L114-L117)）：

```csharp
                if (GUILayout.Button("写入映射文件"))
                {
                    AvatarVariantSwitchWorkflow.WriteMap(config);
                }
            }
        }
```

在 `"写入映射文件"` 按钮**后**、外层闭花括号**前**，插入：

```csharp
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("OSC 桥", EditorStyles.boldLabel);
                var launchTooltip =
                    "点击后：\n" +
                    "1. 从 GitHub Release 检查并下载最新桥 exe 到 %LOCALAPPDATA%\\LanstardAvatarVariantBridge\\\n" +
                    "2. 以当前映射文件路径为参数启动桥（一个 console 窗口）\n" +
                    "断网时会用本地已缓存的版本。关闭 console 窗口 = 停止桥。";
                if (GUILayout.Button(new GUIContent("启动 OSC 桥", launchTooltip)))
                {
                    AvatarVariantBridgeLauncher.DownloadAndLaunch(
                        System.IO.Path.GetFullPath(config.outputMapPath));
                }
```

> 注意 `config.outputMapPath` 可能是相对路径（项目 root 相对），`Path.GetFullPath` 会相对 Editor 当前工作目录（项目 root）解析，得到绝对路径。

- [ ] **Step 2: 编译验证**

Unity Console 无错。

- [ ] **Step 3: 手测（依设计文档 7.2 节）**

前置：Task 7 已经把 `package.json` 的 `repository.url` 填成真实 slug；Task 6 里已经发过至少一个非 `-test` 的 release（或者至少走 `workflow_dispatch` 拿到过一次 artifact 验证流水线通顺，但"latest release"要真的存在）。

场景：

- [ ] `cfg.outputMapPath` 为空或指向不存在文件 → 按钮点击弹 "请先至少执行一次 [批量上传所有装扮]"
- [ ] 清干净 `%LOCALAPPDATA%\LanstardAvatarVariantBridge\bridge.exe` + `version.txt` + 联网 → 点按钮 → 进度条走一圈 → console 窗口弹出 → 桥启动成功
- [ ] 缓存已有 exe，tag 一致 → 点按钮 → 不重下，秒起 console 窗口
- [ ] 手动改 `version.txt` 成别的字符串（模拟 tag 更新）→ 点按钮 → 重新下载覆盖
- [ ] 拔网线 + 本地有 exe → 点按钮 → 提示"未能检查更新，使用本地已有版本"（Unity Console log），照样起
- [ ] 拔网线 + `Tools/.../清除桥缓存` 先清干净 → 点按钮 → 对话框 `[打开 Release 页面] [重试] [取消]`
- [ ] 故意把远端 `.sha256` 改坏（本地测：Fiddler 拦截 / 或手动改缓存 exe 然后 version.txt 删掉触发重下载）→ `SHA256 mismatch` 报错，旧 exe 保留
- [ ] `Tools/.../清除桥缓存` → 提示确认 → 清干净 exe + version，`settings.json` 保留（手动打开 %LOCALAPPDATA%\LanstardAvatarVariantBridge 确认）

- [ ] **Step 4: Commit**

```bash
git add Editor/AvatarVariantSwitchConfigEditor.cs
git commit -m "editor: add launch OSC bridge button to inspector"
```

---

## Task 11: 文档更新

**Files:**
- Modify: `README.md`
- Modify: `CLAUDE.md`

- [ ] **Step 1: 读当前 README 对应章节**

用 Read 工具查看 `README.md`，定位描述 OSC 桥启动方法的章节（通常含 `dotnet run` 或 `Tools~` 字样）。记录当前写法的范围（起始/结束行号）。

- [ ] **Step 2: 替换 README 中的 OSC 桥启动章节**

把原章节替换为（沿用仓库已有中文风格）：

```markdown
## 启动 OSC 桥（运行时切换 Avatar）

**推荐方式**：在 Unity 里选中带 `AvatarVariantSwitchConfig` 的物体，Inspector 底部点 **[启动 OSC 桥]** 按钮。

- 第一次点会从 GitHub Release 下载桥 exe 到 `%LOCALAPPDATA%\LanstardAvatarVariantBridge\`（自动）
- 下次点直接秒起（走本地缓存，除非有新版本）
- 弹出的黑色 console 窗口就是桥。**关窗口 = 停桥**。
- 断网时会用本地已有的版本继续工作

桥工作期间会实时监听映射文件变化——你重新上传装扮后映射自动热重载，不需要重启桥。

### 手动启动（非 Unity 场景 / 开发者）

从 [Releases](https://github.com/<owner>/<repo>/releases) 下载 `AvatarVariantOscBridge-*-win-x64.exe`，双击即可：

- 首次启动：弹文件选择框，选 `avatar-switch-map.json`
- 之后启动：自动用上次选过的路径

也可以通过命令行覆盖参数：

```bash
AvatarVariantOscBridge.exe --map "D:\path\to\avatar-switch-map.json" [--host 127.0.0.1] [--listen 9001] [--send 9000]
```

### 从源码运行（需要 .NET 8 SDK）

```bash
dotnet run --project Packages/com.lanstard.avatar-variant-switcher/Tools~/AvatarVariantOscBridge -- --map "<绝对路径>/avatar-switch-map.json"
```

> ⚠️ VRChat 的 `/avatar/change` OSC 指令**只对游戏内收藏夹里的 avatar 生效**。第一次上传完所有装扮后记得在游戏里逐个 ⭐ 收藏。
```

把 `<owner>/<repo>` 换成实际值（和 `package.json` 里一致）。

PowerShell 后备章节（`AvatarVariantOscBridge.ps1`）如果 README 里有，保留——它依然是可用的独立后备。

- [ ] **Step 3: 更新 CLAUDE.md 加 AOT 约束**

打开 `CLAUDE.md`，找到桥相关的 `## 常用命令` 段（提到 `dotnet run` / `dotnet publish` 那段）。在该段**之前**（或之后合适位置）加一个新段：

```markdown
## 桥侧 AOT 序列化约束

桥以 NativeAOT 单文件发布（见 [AvatarVariantOscBridge.csproj](Tools~/AvatarVariantOscBridge/AvatarVariantOscBridge.csproj) 的 `<PublishAot>true</PublishAot>`）。这带来几个**必须遵守的约束**，否则 GitHub Actions 构建会挂：

- 所有参与 JSON 序列化的类型必须注册在 [BridgeJsonContext](Tools~/AvatarVariantOscBridge/BridgeJsonContext.cs) 里（加 `[JsonSerializable(typeof(X))]`），且类型的每个字段都有 `[JsonPropertyName]` 显式映射 JSON 名（我们不用 `PropertyNameCaseInsensitive`，source-gen 不支持）。
- 序列化/反序列化调用走 `BridgeJsonContext.Default.<Type>` 作为 `JsonTypeInfo`，不要走反射 API。
- 不引入依赖反射的库（`Newtonsoft.Json` / `System.Reflection.Emit` 等）。
- 新加 Win32 / P/Invoke 代码 OK——那是原生，不走反射，AOT 天然友好（见 [FileDialog.cs](Tools~/AvatarVariantOscBridge/FileDialog.cs)）。

改桥侧代码时，本地跑一次 `dotnet publish Tools~/AvatarVariantOscBridge -c Release -r win-x64` 看有没有 `IL2026` / `IL3050` warning，有就得修。
```

同时，把原来描述`dotnet publish`命令的地方改为明确提到 AOT：

在原文件里找：

```
# 构建发布版（依赖运行时）：
dotnet publish -c Release -r win-x64 --self-contained false Packages/com.lanstard.avatar-variant-switcher/Tools~/AvatarVariantOscBridge
```

替换为：

```
# 构建发布版（AOT 单文件，无运行时依赖）：
dotnet publish Packages/com.lanstard.avatar-variant-switcher/Tools~/AvatarVariantOscBridge -c Release -r win-x64 /p:PublishSingleFile=true
```

- [ ] **Step 4: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: document one-click bridge launch + AOT constraints"
```

---

## Task 12: 正式发布第一个版本

**Files:** 无代码改动，只做 git 操作

- [ ] **Step 1: 在仓库根推正式 tag**

```bash
# 确认 package.json 的 version 是期望的 release 版本号，比如 0.1.0
# 然后推 tag
git tag v0.1.0
git push origin v0.1.0
```

- [ ] **Step 2: 验证 Release**

- [ ] `https://github.com/<owner>/<repo>/actions` — 最新 workflow run 绿勾，无 AOT warning
- [ ] `https://github.com/<owner>/<repo>/releases/latest` — 是 `v0.1.0`，附件包含 `AvatarVariantOscBridge-v0.1.0-win-x64.exe` + `.sha256`
- [ ] 从干净的电脑（或清缓存）用 Inspector 按钮走一次全流程，确认端到端通顺

---

## Self-Review 检查项（写完计划后作者对照）

- [ ] Task 1–11 覆盖设计文档第 10 节列出的所有新增 / 修改文件
- [ ] 每个代码 Step 都有完整的代码块，没有 "similar to above" 或 "fill in details"
- [ ] 每个任务结尾有明确的 commit 命令
- [ ] Task 6（pre-release 测试）和 Task 12（正式发布）明确区分
- [ ] Task 8 里引用的方法签名（`DownloadAndLaunch` / `ClearCache` / `GetCacheDirectoryForDisplay`）和 Task 9 / 10 调用的方法一致
- [ ] 没有引用不存在的类型或方法
- [ ] 手测步骤能对应到 spec 第 7 节的原始检查清单
