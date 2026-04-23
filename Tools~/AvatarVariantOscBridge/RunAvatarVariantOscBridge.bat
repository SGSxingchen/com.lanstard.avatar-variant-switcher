@echo off
setlocal
powershell -ExecutionPolicy Bypass -File "%~dp0AvatarVariantOscBridge.ps1" %*
