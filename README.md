# Lanstard Avatar Variant Switcher

Local Unity package for VRChat avatar variant workflows.

## Included

- Int-parameter menu generation through Modular Avatar
- Batch upload flow that toggles variant roots between `Untagged` and `EditorOnly`
- Blueprint ID capture and JSON mapping export
- OSC bridge helpers in `Tools~/AvatarVariantOscBridge`

## Main Scripts

- `Runtime/AvatarVariantSwitchConfig.cs`
- `Editor/AvatarVariantSwitchWorkflow.cs`
- `Editor/AvatarVariantSwitchConfigEditor.cs`

## Default Mapping Output

- `Packages/com.lanstard.avatar-variant-switcher/Generated/avatar-switch-map.json`

## OSC Bridge

PowerShell:

```powershell
.\Packages\com.lanstard.avatar-variant-switcher\Tools~\AvatarVariantOscBridge\RunAvatarVariantOscBridge.bat "D:\path\to\avatar-switch-map.json"
```

C# source:

```powershell
dotnet run --project Packages/com.lanstard.avatar-variant-switcher/Tools~/AvatarVariantOscBridge -- "D:\path\to\avatar-switch-map.json"
```
