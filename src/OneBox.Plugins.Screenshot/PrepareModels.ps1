<#
.SYNOPSIS
  下载 PaddleOCR 中文识别模型与字典到截图插件目录（PaddleOCR 双引擎）。

.DESCRIPTION
  RapidOcrNet NuGet 包自带的 models/v5 只含 latin 识别模型，中文识别需要：
    - ch_PP-OCRv5_rec_mobile.onnx  (~15.9 MB，识别模型)
    - ppocrv5_dict.txt             (~18400 行，字符字典)
  det/cls 两个模型由 RapidOcrNet 包的 build targets 在构建时自动拷贝到输出目录。
  本脚本把上面两个文件下载到 src/OneBox.Plugins.Screenshot/models/v5/，
  csproj 会把它们一并复制到发布输出。

  模型来源：ModelScope RapidAI/RapidOCR 官方仓库（Apache-2.0）。
  下载后模型齐全，截图插件自动切换到 PaddleOCR 引擎；缺失时静默回退系统引擎。

.EXAMPLE
  .\PrepareModels.ps1
#>
$ErrorActionPreference = "Stop"

$destDir = Join-Path $PSScriptRoot "models/v5"
New-Item -ItemType Directory -Force -Path $destDir | Out-Null

$files = @(
    @{
        Name = "ch_PP-OCRv5_rec_mobile.onnx"
        Url  = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx"
        Sha256 = "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5"
    },
    @{
        Name = "ppocrv5_dict.txt"
        Url  = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt"
        Sha256 = $null
    }
)

foreach ($f in $files) {
    $dest = Join-Path $destDir $f.Name
    if ((Test-Path $dest) -and ($null -eq $f.Sha256 -or (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower() -eq $f.Sha256)) {
        Write-Host "已存在，跳过: $($f.Name)"
        continue
    }

    Write-Host "下载 $($f.Name) ..."
    Invoke-WebRequest -Uri $f.Url -OutFile $dest -MaximumRedirection 5

    if ($f.Sha256) {
        $actual = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $f.Sha256) {
            Remove-Item $dest -Force
            throw "SHA256 校验失败: $($f.Name)"
        }
        Write-Host "SHA256 校验通过: $($f.Name)"
    }
}

Write-Host ""
Write-Host "PaddleOCR 中文模型已就绪: $destDir"
Get-ChildItem $destDir | Format-Table Name, @{L='MB';E={[math]::Round($_.Length/1MB,2)}} -AutoSize
