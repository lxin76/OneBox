# 发布为 win-x64 自包含单文件 exe
# 用法：.\publish.ps1 [-OutDir <输出目录>]   （默认输出到 ./publish）
param(
    [string]$OutDir = (Join-Path $PSScriptRoot "publish")
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# 清理旧产物（若旧 exe 被运行中实例锁定会失败，先尝试结束占用进程）
Get-Process -Name ClipboardFloat -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

dotnet publish src/ClipboardFloat.App/ClipboardFloat.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o $OutDir

Write-Output "已发布到: $OutDir"
Get-ChildItem $OutDir | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
