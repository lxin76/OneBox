<#
.SYNOPSIS
  发布 OneBox 宿主与全部小工具插件。

.DESCRIPTION
  宿主（OneBox.exe）发布到 <输出目录> 根目录；每个插件发布到
  <输出目录>\plugins\<插件 id>\，并把 plugin.json 一并复制过去。
  宿主启动时扫描 plugins\ 目录自动发现并加载这些小工具。

.PARAMETER OutDir
  输出目录，默认 ./publish

.PARAMETER Plugins
  只发布指定 id 的插件（默认全部）。例：-Plugins clipboard

.PARAMETER FrameworkDependent
  插件改为依赖框架发布（体积小，需要目标机器已安装 .NET 8 桌面运行时）。

.EXAMPLE
  .\publish.ps1
  .\publish.ps1 -OutDir D:\dist\OneBox
  .\publish.ps1 -Plugins quicknote -FrameworkDependent
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot "publish"),
    [string[]]$Plugins,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# 插件清单：id => 项目路径。新增小工具时在这里加一行即可。
$pluginProjects = [ordered]@{
    "clipboard"  = "src/OneBox.Plugins.Clipboard/OneBox.Plugins.Clipboard.csproj"
    "quicknote"  = "src/OneBox.Plugins.QuickNote/OneBox.Plugins.QuickNote.csproj"
    "screenshot" = "src/OneBox.Plugins.Screenshot/OneBox.Plugins.Screenshot.csproj"
}

if ($Plugins) {
    $unknown = $Plugins | Where-Object { -not $pluginProjects.Contains($_) }
    if ($unknown) { throw "未知插件 id: $($unknown -join ', ')。可用: $($pluginProjects.Keys -join ', ')" }
}

$hostProject = "src/OneBox.Host/OneBox.Host.csproj"

# 清理旧产物：运行中的实例会锁定 exe，先结束宿主与插件进程
Get-Process -Name OneBox, OneBox.Plugins.Clipboard, OneBox.Plugins.QuickNote, OneBox.Plugins.Screenshot -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

function Publish-Project {
    param([string]$Project, [string]$Destination, [switch]$FrameworkDependent)

    $arguments = @(
        "publish", $Project,
        "-c", "Release",
        "-o", $Destination,
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "--nologo"
    )

    if ($FrameworkDependent) {
        # 依赖框架：需要目标机器安装 .NET 8 桌面运行时
        $arguments += @("-r", "win-x64", "--self-contained", "false")
    }
    else {
        # 自包含单文件：拷贝即用，无需预装运行时
        $arguments += @(
            "-r", "win-x64", "--self-contained", "true",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true"
        )
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "发布失败: $Project" }
}

Write-Output "==> 发布宿主 OneBox"
Publish-Project -Project $hostProject -Destination $OutDir

foreach ($id in $pluginProjects.Keys) {
    if ($Plugins -and ($Plugins -notcontains $id)) { continue }

    $project = $pluginProjects[$id]
    $pluginDir = Join-Path (Join-Path $OutDir "plugins") $id

    Write-Output "==> 发布插件 $id"
    Publish-Project -Project $project -Destination $pluginDir -FrameworkDependent:$FrameworkDependent

    # 清单必须与入口 exe 同目录，否则宿主无法识别
    Copy-Item (Join-Path (Split-Path $project -Parent) "plugin.json") $pluginDir -Force
}

Write-Output ""
Write-Output "已发布到: $OutDir"
Write-Output ""
Write-Output "宿主:"
Get-ChildItem $OutDir -File | Select-Object Name, @{N='大小(MB)';E={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
Write-Output "小工具:"
Get-ChildItem (Join-Path $OutDir "plugins") -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    [pscustomobject]@{
        Id      = $_.Name
        入口     = (Get-ChildItem $_.FullName -Filter *.exe | Select-Object -First 1).Name
        大小MB   = [math]::Round(((Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum)/1MB,1)
    }
} | Format-Table -AutoSize

Write-Output "运行 $(Join-Path $OutDir 'OneBox.exe') 即可启动；新插件解压到 plugins\<id>\ 后可在工具箱中启用。"
