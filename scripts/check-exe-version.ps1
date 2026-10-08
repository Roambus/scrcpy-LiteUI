<#
校验打包产物里真正带的版本号，免得「改了代码忘了升版本号」就把包发出去。

为什么读产物元数据、而不是在 exe 里搜字符串：单文件自包含发布把托管程序集压进了 exe，
二进制里搜到的 "x.y.z" 很可能来自运行时自带的某个库（旧版就踩过这个坑：搜出来的其实是
zlib 的版本号），会骗过检查。PE 头里本来就有编译期写好的 FileVersion / ProductVersion，
直接读它最可靠。

用法：

    pwsh scripts\check-exe-version.ps1                      # 校验默认产物 dist\快投.exe
    pwsh scripts\check-exe-version.ps1 -Exe dist\快投.exe
    pwsh scripts\check-exe-version.ps1 -Expect 9.9.9        # 走不一致分支，用来确认 CI 会变红

退出码：0=一致，1=不一致，2=打不开产物或读不到期望版本号。
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$Expect
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repoRoot "dist\快投.exe" }

function Get-SourceVersion {
    # 版本号唯一来源：仓库根的 Directory.Build.props（发版脚本与 CI 都读它）
    $props = Join-Path $repoRoot "Directory.Build.props"
    if (-not (Test-Path -LiteralPath $props -PathType Leaf)) {
        Write-Host "找不到 Directory.Build.props：$props" -ForegroundColor Red
        exit 2
    }
    $text = Get-Content -LiteralPath $props -Raw -Encoding UTF8
    $matched = [regex]::Match($text, '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $matched.Success) {
        Write-Host "Directory.Build.props 里没读到 <Version> 节点：$props" -ForegroundColor Red
        exit 2
    }
    return $matched.Groups[1].Value
}

if (-not $Expect) { $Expect = Get-SourceVersion }

if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) {
    Write-Host "打不开打包产物：$Exe（先跑一遍 dotnet publish）" -ForegroundColor Red
    exit 2
}

$full = (Resolve-Path -LiteralPath $Exe).Path
$info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($full)

# ProductVersion 是 InformationalVersion：仓库里跑构建时 .NET SDK 会在后面追加 "+<提交号>"，
# 比对前把这段尾巴切掉（是不是 git 仓库、有没有提交号都不该影响校验结果）。
$product = $info.ProductVersion
if ($product) { $product = $product.Split('+')[0].Trim() }
$file = $info.FileVersion

Write-Host "产物: $Exe"
Write-Host "期望版本: $Expect"
Write-Host "产物里的版本号: ProductVersion=$product  FileVersion=$file"

if ($product -ne $Expect) {
    $message = "版本号对不上：源码里是 $Expect，产物里是 $product。"
    $hint = "请把 Directory.Build.props 的 <Version> 改对，再重新打包。"
    Write-Host $message -ForegroundColor Red
    Write-Host $hint -ForegroundColor Red
    exit 1
}

Write-Host "版本号一致"
exit 0
