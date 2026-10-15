<#
.SYNOPSIS
  框架依赖发布：体积小，目标机器需预装 .NET 8 桌面运行时。

.DESCRIPTION
  宿主（OneBox.exe）发布到 <输出目录> 根目录；每个插件发布到
  <输出目录>\plugins\<插件 id>\，并把 plugin.json 一并复制过去。
  宿主启动时扫描 plugins\ 目录自动发现并加载这些小工具。

  发布参数：win-x64 + 框架依赖（不自带运行时）+ 单文件，产物只含应用
  自身的 exe，体积远小于自包含发布，但要求目标机器已安装
  .NET 8 Desktop Runtime（Microsoft.WindowsDesktop.App 8.x）。

  每个项目（宿主与每个插件）都整合为单个 exe，布局与自包含发布一致：
  宿主一个 exe，每个插件目录一个 exe 加一个 plugin.json。

.PARAMETER OutDir
  输出目录，默认 ./publish/framework-dependent

.PARAMETER Plugins
  只发布指定 id 的插件（默认全部）。例：-Plugins textpin

.PARAMETER NoStop
  跳过"结束正在运行的 OneBox 进程"这一步。默认会先结束进程，
  否则运行中的实例会锁定 exe 导致覆盖失败。

.EXAMPLE
  .\publish-framework-dependent.ps1
  .\publish-framework-dependent.ps1 -OutDir D:\dist\OneBox
  .\publish-framework-dependent.ps1 -Plugins quicknote
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot "publish/framework-dependent"),
    [string[]]$Plugins,
    [switch]$NoStop
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# 目标框架：与各 csproj 的 TargetFramework 保持一致
$targetFramework = "net8.0-windows"
$runtimeMajor = "8"

# 插件自动发现：扫描 src/OneBox.Plugins.*（排除 *.Core 纯逻辑库），
# 插件 id 取自各插件自己的 plugin.json，新增小工具后无需改动本脚本。
function Get-PluginProjects {
    $roots = Get-ChildItem (Join-Path $PSScriptRoot "src") -Directory -Filter "OneBox.Plugins.*" |
        Where-Object { $_.Name -notlike "*.Core" } |
        Sort-Object Name

    $found = [ordered]@{}
    foreach ($root in $roots) {
        $manifestPath = Join-Path $root.FullName "plugin.json"
        if (-not (Test-Path $manifestPath)) { throw "缺少插件清单: $manifestPath" }

        $manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $id = $manifest.id
        if (-not $id) { throw "plugin.json 缺少 id 字段: $manifestPath" }

        $project = Get-ChildItem $root.FullName -Filter *.csproj -File | Select-Object -First 1
        if (-not $project) { throw "未找到插件项目文件: $($root.FullName)" }

        $found[$id] = [pscustomobject]@{ Id = $id; Project = $project.FullName; Manifest = $manifestPath }
    }
    return $found
}

$pluginProjects = Get-PluginProjects
if ($pluginProjects.Count -eq 0) { throw "未发现任何插件（src/OneBox.Plugins.*）" }

if ($Plugins) {
    $unknown = $Plugins | Where-Object { -not $pluginProjects.Contains($_) }
    if ($unknown) { throw "未知插件 id: $($unknown -join ', ')。可用: $($pluginProjects.Keys -join ', ')" }
}

$hostProject = Join-Path $PSScriptRoot "src/OneBox.Host/OneBox.Host.csproj"

# 本机若未装对应桌面运行时，产出来在本机都跑不起来，提前给出明确提示
$desktopRuntime = dotnet --list-runtimes 2>$null |
    Where-Object { $_ -match "^Microsoft\.WindowsDesktop\.App $runtimeMajor\." }
if (-not $desktopRuntime) {
    Write-Warning "本机未检测到 .NET $runtimeMajor 桌面运行时（Microsoft.WindowsDesktop.App $runtimeMajor.x）。框架依赖产物需要目标机器安装它：https://dotnet.microsoft.com/download/dotnet/$runtimeMajor"
}

# 清理旧产物：运行中的实例会锁定 exe，先结束宿主与插件进程。
# 按"进程可执行文件位于本次输出目录下"筛选，而不是硬编码进程名——
# 新增小工具无需再改脚本，也不会误杀正在从源码目录调试的实例。
if (-not $NoStop) {
    $outFull = [System.IO.Path]::GetFullPath($OutDir).TrimEnd('\') + '\'
    Get-Process -ErrorAction SilentlyContinue | Where-Object {
        try {
            $_path = $_.Path
            $_path -and $_path.StartsWith($outFull, [System.StringComparison]::OrdinalIgnoreCase)
        } catch { $false }
    } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# 截图插件的 PaddleOCR 中文模型：缺任一文件，PaddleOcrService.IsAvailable 就为 false，
# 插件会静默回退系统 OCR 引擎（识别质量明显下降）。这里在发布后立刻拦截。
# 发布产物里必须齐全的 4 个文件：det/cls 来自 RapidOcrNet 包，rec/dict 来自源码 models/v5。
$script:RequiredOcrModelFiles = @(
    "models\v5\ch_PP-OCRv5_mobile_det.onnx",
    "models\v5\ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
    "models\v5\ch_PP-OCRv5_rec_mobile.onnx",
    "models\v5\ppocrv5_dict.txt"
)

# 只需由 PrepareModels.ps1 提前放好的 2 个文件（det/cls 由 NuGet 包在构建时自动提供，
# 源工程 models/v5 下本来就没有，不能拿完整清单去卡前置校验）。
$script:SourceOcrModelFiles = @(
    "models\v5\ch_PP-OCRv5_rec_mobile.onnx",
    "models\v5\ppocrv5_dict.txt"
)

# 发布前的前置校验：确认源工程的 PaddleOCR 中文模型齐全。
# 若不做这一步，脚本会先清掉旧产物、再发布失败，结果连原本可用的旧产物也没了。
function Assert-SourcePluginAssets {
    param([string]$PluginId, [string]$ProjectPath)

    if ($PluginId -ne "screenshot") { return }

    $sourceDir = Join-Path (Split-Path $ProjectPath -Parent) "models\v5"
    $missing = @()
    foreach ($relative in $script:SourceOcrModelFiles) {
        $name = Split-Path $relative -Leaf
        if (-not (Test-Path -LiteralPath (Join-Path $sourceDir $name))) {
            $missing += $name
        }
    }

    if ($missing.Count -gt 0) {
        throw "源工程缺少 PaddleOCR 中文模型：$($missing -join ', ')。" +
              "请先在 src/OneBox.Plugins.Screenshot/ 下运行 PrepareModels.ps1 再发布（本次未改动已有产物）。"
    }
}

function Assert-PluginAssets {
    param([string]$PluginId, [string]$PluginDir)

    if ($PluginId -ne "screenshot") { return }

    $missing = @()
    foreach ($relative in $script:RequiredOcrModelFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $PluginDir $relative))) {
            $missing += $relative
        }
    }

    if ($missing.Count -gt 0) {
        throw "插件 $PluginId 发布产物缺少 PaddleOCR 模型：$($missing -join ', ')。" +
              "请先在 src/OneBox.Plugins.Screenshot/ 下运行 PrepareModels.ps1 再重新发布。"
    }

    Write-Output "    模型校验通过：$($script:RequiredOcrModelFiles.Count) 个 OCR 文件齐全"
}

function Publish-Project {
    param([string]$Project, [string]$Destination)

    $arguments = @(
        "publish", $Project,
        "-c", "Release",
        "-o", $Destination,
        "-r", "win-x64",
        "--self-contained", "false",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "--nologo"
    )

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "发布失败: $Project" }
}

Write-Output "==> [框架依赖] 发布宿主 OneBox"
Publish-Project -Project $hostProject -Destination $OutDir

foreach ($id in $pluginProjects.Keys) {
    if ($Plugins -and ($Plugins -notcontains $id)) { continue }

    $plugin = $pluginProjects[$id]
    $pluginDir = Join-Path (Join-Path $OutDir "plugins") $id

    # 前置校验：源模型不齐就直接失败，此时尚未动过旧产物
    Assert-SourcePluginAssets -PluginId $id -ProjectPath $plugin.Project

    # 先清掉目标插件目录：MSBuild 的发布增量缓存（obj\...\PublishOutputs.*.txt）一旦把某文件
    # 记为"已发布"，之后即使它已从输出目录消失也不会补回，会把一次"缺模型/缺文件"的发布
    # 永久固化在产物里。每次从空目录发布可彻底规避这一点。
    if (Test-Path -LiteralPath $pluginDir) {
        Remove-Item -LiteralPath $pluginDir -Recurse -Force
    }

    Write-Output "==> [框架依赖] 发布插件 $id"
    Publish-Project -Project $plugin.Project -Destination $pluginDir

    # 清单必须与入口 exe 同目录，否则宿主无法识别
    Copy-Item $plugin.Manifest $pluginDir -Force

    # 校验：截图插件的 PaddleOCR 中文模型必须齐全，否则插件会静默回退系统 OCR 引擎
    Assert-PluginAssets -PluginId $id -PluginDir $pluginDir
}

Write-Output ""
Write-Output "已发布（框架依赖）到: $OutDir"
Write-Output ""
Write-Output "宿主:"
Get-ChildItem $OutDir -File | Select-Object Name, @{N='大小(MB)';E={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
Write-Output "小工具:"
Get-ChildItem (Join-Path $OutDir "plugins") -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    [pscustomobject]@{
        Id      = $_.Name
        入口    = (Get-ChildItem $_.FullName -Filter *.exe | Select-Object -First 1).Name
        大小MB  = [math]::Round(((Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum)/1MB,1)
    }
} | Format-Table -AutoSize

$totalMB = [math]::Round(((Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum)/1MB,1)
Write-Output "总体积: $totalMB MB（不含运行时，目标机器需预装 .NET $runtimeMajor Desktop Runtime，目标框架 $targetFramework）"
Write-Output "运行 $(Join-Path $OutDir 'OneBox.exe') 即可启动；新插件解压到 plugins\<id>\ 后可在工具箱中启用。"
