<#
一键发版：校验版本号 → 打包 → 校验产物 → 推分支 → 打标签 → 建 GitHub Release → 上传 exe。

用法：

    pwsh scripts\release.ps1 v2.0.1                  # 正式发一版
    pwsh scripts\release.ps1 v2.0.1 -DryRun          # 干跑，只看会发什么
    pwsh scripts\release.ps1 v2.0.1 -NoBuild         # 用已有产物发
    pwsh scripts\release.ps1 -UpdateRepoMeta         # 只修仓库描述与 topics

发布说明放在 release_notes\<tag>.md（入库，跟代码一起 review），也可以用 -Notes 指别处。

凭据：优先环境变量 GITHUB_TOKEN / GH_TOKEN，否则走本机已存的 git 凭据（git credential fill）。
网络全用 .NET 自带的 System.Net.Http，不引第三方依赖。

发版是不可逆动作（要推标签、公开发布页），所以先用 -DryRun 空跑一遍。

这份脚本是从旧版的 legacy\scripts\release.py 逐条对照移植的：凭据回退、干跑、分步报错、
「标签推了但分支没推」那个坑都保留着，没有做简化。
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Tag,
    [string]$Notes,
    [string]$Exe,
    [string]$Name,
    [switch]$Draft,
    [switch]$Prerelease,
    [switch]$NoBuild,
    [switch]$DryRun,
    [switch]$Force,
    [switch]$UpdateRepoMeta
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$defaultExe = "dist\快投.exe"
$notesDir = Join-Path $repoRoot "release_notes"
$targetBranch = "main"

# 仓库门面（GitHub 描述限 350 字符，topics 限 20 个、小写、连字符）
$repoDescription = "Windows 下 scrcpy 的图形化启动器：插上数据线或连上 Wi-Fi，点一下就能投屏。侧边栏设备入口、扫码配对、USB 一键转无线、单文件便携 EXE 无需安装。"
$repoTopics = @("scrcpy", "screen-mirroring", "android", "adb", "windows", "dotnet", "wpf", "csharp", "gui", "launcher")

# PowerShell 5.1 默认可能停在 TLS 1.0，调 GitHub API 会被拒
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
try { Add-Type -AssemblyName System.Net.Http | Out-Null } catch { }

# ---------- 小工具 ----------

function Write-Step([string]$Text) { Write-Host "=== $Text ===" }

function Fail([string]$Message) {
    # 走 stderr 直接写一行：发版脚本遇到的都是「一句话能说清」的失败，不甩堆栈
    [Console]::Error.WriteLine("发版中断：$Message")
    exit 1
}

function Get-DotNet {
    # 本机可能只装了 SDK、没把 dotnet 放进 PATH，退回到默认安装位置
    $found = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    $fallback = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $fallback) { return $fallback }
    Fail "找不到 dotnet：请装好 .NET 10 SDK 并放进 PATH（或确认 $fallback 存在）"
}

function Get-PowerShellExe {
    $found = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    $fallback = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    if (Test-Path -LiteralPath $fallback) { return $fallback }
    Fail "找不到 PowerShell，无法调用 scripts\check-exe-version.ps1"
}

function Invoke-Native {
    # 跑一条命令，实时把输出透给用户（打包要好几分钟，闷着不吭声太难受）
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory = $repoRoot
    )
    $previous = $ErrorActionPreference
    # 原生命令往 stderr 写东西（git 的进度、csc 的告警）不该被当成终止性错误
    $ErrorActionPreference = "Continue"
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
        $ErrorActionPreference = $previous
    }
    if ($exitCode -ne 0) {
        Fail "命令失败（退出码 $exitCode）：$FilePath $($Arguments -join ' ')"
    }
}

function Invoke-GitCapture {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $raw = & git @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    return [pscustomobject]@{
        Code   = $exitCode
        Output = (($raw | ForEach-Object { [string]$_ }) -join "`n")
    }
}

function Get-GitText {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $result = Invoke-GitCapture -Arguments $Arguments
    if ($result.Code -ne 0) {
        Fail "git $($Arguments -join ' ') 失败：$($result.Output.Trim())"
    }
    return $result.Output.Trim()
}

function Read-SourceVersion {
    $props = Join-Path $repoRoot "Directory.Build.props"
    if (-not (Test-Path -LiteralPath $props -PathType Leaf)) { Fail "找不到 Directory.Build.props：$props" }
    $text = Get-Content -LiteralPath $props -Raw -Encoding UTF8
    $matched = [regex]::Match($text, '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $matched.Success) { Fail "Directory.Build.props 里没读到 <Version> 节点：$props" }
    return $matched.Groups[1].Value
}

function Get-RemoteRepo {
    # 从 origin 远端地址里解析出 owner/repo，省得把仓库名写死在脚本里
    $url = Get-GitText -Arguments @("remote", "get-url", "origin")
    $slug = $null
    foreach ($prefix in @("https://github.com/", "http://github.com/", "git@github.com:")) {
        if ($url.StartsWith($prefix)) {
            $slug = $url.Substring($prefix.Length)
            break
        }
    }
    if (-not $slug) { Fail "认不出 origin 的地址：$url" }
    $slug = ($slug -replace '\.git$', '').Trim('/')
    if (($slug.Split('/')).Count -ne 2) { Fail "origin 地址里没解析出 owner/repo：$url" }
    return $slug
}

function Get-GitHubToken {
    # 优先环境变量，其次本机已存的 git 凭据。
    #
    # 关键点：喂给 `git credential fill` 的 stdin 必须逐行用纯 \n。在 PowerShell 里走管道
    # 每行会被加上 CR，git 把 CR 当成值的一部分，于是报
    # "refusing to work with credential missing protocol field"（旧版 release.py 踩过这个坑）。
    foreach ($key in @("GITHUB_TOKEN", "GH_TOKEN")) {
        $value = [Environment]::GetEnvironmentVariable($key)
        if ($value -and $value.Trim()) { return $value.Trim() }
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "git"
    $psi.Arguments = "credential fill"
    $psi.WorkingDirectory = $repoRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.StandardInput.Write("protocol=https`nhost=github.com`n`n")
    $proc.StandardInput.Close()
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()

    if ($proc.ExitCode -ne 0) {
        Fail "取本机 git 凭据失败：$($stderr.Trim())（可以改用环境变量 GITHUB_TOKEN）"
    }
    foreach ($line in $stdout.Split("`n")) {
        if ($line.StartsWith("password=")) { return $line.Substring("password=".Length).Trim() }
    }
    Fail "本机 git 凭据里没有 password 字段，可以改用环境变量 GITHUB_TOKEN"
}

function ConvertFrom-Body([string]$Body) {
    if ([string]::IsNullOrWhiteSpace($Body)) { return @{} }
    try { return $Body | ConvertFrom-Json } catch { return @{} }
}

function Invoke-GitHubApi {
    # 调一次 GitHub REST API，返回 @{ Status = 整数状态码; Body = 原始响应文本 }
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Url,
        [string]$Token,
        $Payload,
        [string]$RawFile
    )

    $client = New-Object System.Net.Http.HttpClient
    # 传 80MB 产物要慢慢来（旧版给的是 1800 秒）
    $client.Timeout = [TimeSpan]::FromSeconds(1800)
    $request = $null
    $response = $null
    try {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Url)
        if ($Token) { $request.Headers.Add("Authorization", "token $Token") }
        $request.Headers.Add("User-Agent", "kuaitou-release")
        $request.Headers.Add("Accept", "application/vnd.github+json")

        if ($RawFile) {
            # 用文件流而不是整个读进内存；HttpClient 会自己带上 Content-Length，
            # 大文件走分块传输容易被上传接口拒掉（旧版注明了这一点）
            $stream = [System.IO.File]::OpenRead($RawFile)
            $request.Content = [System.Net.Http.StreamContent]::new($stream)
            $request.Content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/octet-stream")
        } elseif ($null -ne $Payload) {
            $json = $Payload | ConvertTo-Json -Depth 10 -Compress
            $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, "application/json")
        }

        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return [pscustomobject]@{
            Status = [int]$response.StatusCode
            Body   = $body
        }
    } finally {
        if ($response) { $response.Dispose() }
        if ($request) { $request.Dispose() }
        $client.Dispose()
    }
}

function Assert-ApiOk($Response, [string]$What) {
    if ($Response.Status -ge 400) {
        Fail "$What：GitHub 返回 $($Response.Status)：$($Response.Body)"
    }
}

# ---------- 各步骤 ----------

function Assert-VersionMatchesTag([string]$TagValue) {
    # 标签上的版本号必须和源码里的一致，这是最容易犯的发布事故
    $version = if ($TagValue.StartsWith("v")) { $TagValue.Substring(1) } else { $TagValue }
    $source = Read-SourceVersion
    if ($version -ne $source) {
        Fail "标签 $TagValue 与源码版本 $source 对不上：先把 Directory.Build.props 的 <Version> 改成 $version 再发"
    }
    return $version
}

function Assert-CleanTree {
    $dirty = Get-GitText -Arguments @("status", "--porcelain")
    if ($dirty) { Fail "工作区不干净，先提交或收拾掉这些改动再发版：`n$dirty" }
}

function Invoke-Build([string]$ExePath) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot "icons"))) {
        Write-Host "注意: icons/ 不在，本次产物的内置图标素材库为空——想要内置图标先跑 legacy\scripts\fetch_icons.py 再打包。"
    }
    Write-Step "打包"
    $outDir = Split-Path -Parent $ExePath
    Invoke-Native -FilePath (Get-DotNet) -Arguments @(
        "publish", "src\Kuaitou.App", "-c", "Release", "-r", "win-x64", "-o", $outDir
    )
    Write-Step "校验产物版本号"
    Invoke-Native -FilePath (Get-PowerShellExe) -Arguments @(
        "-NoProfile", "-File", (Join-Path $PSScriptRoot "check-exe-version.ps1"), "-Exe", $ExePath
    )
}

function Push-Branch([bool]$IsDryRun) {
    # 打标签之前先把当前分支推上去。
    #
    # 只推标签是不够的：标签指向的提交如果不在远端分支上，发布页看着一切正常，而默认分支
    # 上根本没有这版代码——远端 main 会一直停在上一版，直到有人发现。
    $branch = Get-GitText -Arguments @("rev-parse", "--abbrev-ref", "HEAD")
    if ($IsDryRun) {
        Write-Host "[干跑] 会推送分支 $branch"
        return
    }
    Write-Step "推送分支"
    Invoke-Native -FilePath "git" -Arguments @("push", "origin", $branch)
}

function Add-TagIfNeeded([string]$TagValue, [bool]$IsDryRun) {
    # 标签不存在就创建并推送；已存在就必须指向当前 HEAD（绝不覆盖）
    $exists = (Invoke-GitCapture -Arguments @("rev-parse", "-q", "--verify", "refs/tags/$TagValue")).Code -eq 0
    if ($exists) {
        $at = Get-GitText -Arguments @("rev-list", "-n", "1", $TagValue)
        $head = Get-GitText -Arguments @("rev-parse", "HEAD")
        if ($at -ne $head) {
            Fail "标签 $TagValue 已存在，且指向 $($at.Substring(0, 8)) 而不是当前 HEAD $($head.Substring(0, 8))；换个新版本号，或者先处理掉那个标签"
        }
        Write-Host "标签 $TagValue 已存在且指向 HEAD，跳过创建"
        return
    }
    if ($IsDryRun) {
        Write-Host "[干跑] 会创建并推送标签 $TagValue"
        return
    }
    Write-Step "打标签"
    Invoke-Native -FilePath "git" -Arguments @("tag", "-a", $TagValue, "-m", "快投 $TagValue")
    Invoke-Native -FilePath "git" -Arguments @("push", "origin", $TagValue)
}

function Set-Release {
    param(
        [string]$Slug,
        [string]$TagValue,
        $Payload,
        [string]$Token,
        [bool]$IsDryRun
    )
    $url = "https://api.github.com/repos/$Slug/releases"
    if ($IsDryRun) {
        Write-Host "[干跑] POST $url"
        Write-Host ($Payload | ConvertTo-Json -Depth 10)
        return $null
    }
    Write-Step "建发布页"
    $response = Invoke-GitHubApi -Method "POST" -Url $url -Token $Token -Payload $Payload
    if ($response.Status -eq 422) {
        # 422 通常是这个 tag 的发布页已经有了：复用，别重复建
        $response = Invoke-GitHubApi -Method "GET" -Url "$url/tags/$TagValue" -Token $Token
        Assert-ApiOk $response "取已有发布页"
        $release = ConvertFrom-Body $response.Body
        Write-Host "发布页已存在（id=$($release.id)），复用它"
        return $release
    }
    Assert-ApiOk $response "建发布页"
    $release = ConvertFrom-Body $response.Body
    Write-Host "发布页已建：$($release.html_url)"
    return $release
}

function Send-Asset {
    param(
        [string]$Slug,
        $Release,
        [string]$AssetName,
        [string]$ExePath,
        [string]$Token,
        [bool]$ForceUpload
    )
    $upload = "https://uploads.github.com/repos/$Slug/releases/$($Release.id)/assets?name=$AssetName"
    Write-Step "上传资产"
    foreach ($asset in @($Release.assets)) {
        if ($asset -and $asset.name -eq $AssetName) {
            if (-not $ForceUpload) { Fail "资产 $AssetName 已存在，要覆盖请加 -Force" }
            Assert-ApiOk (Invoke-GitHubApi -Method "DELETE" -Url $asset.url -Token $Token) "删除同名旧资产"
            Write-Host "已删掉同名旧资产"
        }
    }
    $response = Invoke-GitHubApi -Method "POST" -Url $upload -Token $Token -RawFile $ExePath
    Assert-ApiOk $response "上传资产"
    $got = ConvertFrom-Body $response.Body
    Write-Host "资产已上传：$($got.name)  $($got.size) 字节"
    Write-Host "下载地址：$($got.browser_download_url)"
}

function Set-RepoMeta {
    param([string]$Slug, [string]$Token)
    Write-Step "更新仓库门面"
    $description = Invoke-GitHubApi -Method "PATCH" -Url "https://api.github.com/repos/$Slug" -Token $Token `
        -Payload @{ description = $repoDescription }
    Assert-ApiOk $description "更新仓库描述"
    Write-Host "描述已更新：$((ConvertFrom-Body $description.Body).description)"

    $topics = Invoke-GitHubApi -Method "PUT" -Url "https://api.github.com/repos/$Slug/topics" -Token $Token `
        -Payload @{ names = $repoTopics }
    Assert-ApiOk $topics "更新 topics"
    Write-Host "topics 已更新：$(((ConvertFrom-Body $topics.Body).names) -join ', ')"
}

# ---------- 入口 ----------

function Invoke-Main {
    if (-not $Tag) {
        if ($UpdateRepoMeta) {
            Set-RepoMeta -Slug (Get-RemoteRepo) -Token (Get-GitHubToken)
            return
        }
        Fail "要么给个 tag 发版（如 v2.0.1），要么用 -UpdateRepoMeta 只改仓库门面"
    }

    $version = Assert-VersionMatchesTag $Tag
    Write-Host "版本号检查通过：$version"

    $notesPath = if ($Notes) { $Notes } else { Join-Path $notesDir "$Tag.md" }
    if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
        Fail "找不到发布说明 $notesPath：先把它写好（入库到 release_notes/），或用 -Notes 指路径"
    }
    $notesBody = Get-Content -LiteralPath $notesPath -Raw -Encoding UTF8
    Write-Host "发布说明：$notesPath（$($notesBody.Length) 字）"

    $assetName = if ($Name) { $Name } else { "Kuaitou-$Tag.exe" }
    if ($assetName -notmatch '^[\x20-\x7E]+$') {
        Fail "资产名 $assetName 含非 ASCII 字符：GitHub 会把中文名清成 default.exe，请用 -Name 换成纯英文"
    }

    if (-not $DryRun) {
        Assert-CleanTree
    } else {
        Write-Host "[干跑] 跳过工作区检查、打包与上传，只看会发出什么"
    }

    $exeArg = if ($Exe) { $Exe } else { $defaultExe }
    $exePath = if ([System.IO.Path]::IsPathRooted($exeArg)) { $exeArg } else { Join-Path $repoRoot $exeArg }
    if ($NoBuild -or $DryRun) {
        if (-not $DryRun -and -not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
            Fail "找不到产物 $exePath（去掉 -NoBuild，或先打包一次）"
        }
    } else {
        Invoke-Build $exePath
    }

    $payload = [ordered]@{
        tag_name         = $Tag
        target_commitish = $targetBranch
        name             = "快投 $Tag"
        draft            = [bool]$Draft
        prerelease       = [bool]$Prerelease
        body             = $notesBody
    }
    $slug = Get-RemoteRepo
    Write-Host "仓库：$slug   产物：$exePath   资产名：$assetName"

    $token = if ($DryRun) { $null } else { Get-GitHubToken }

    Push-Branch $DryRun
    Add-TagIfNeeded -TagValue $Tag -IsDryRun $DryRun
    $release = Set-Release -Slug $slug -TagValue $Tag -Payload $payload -Token $token -IsDryRun $DryRun
    if (-not $DryRun) {
        Send-Asset -Slug $slug -Release $release -AssetName $assetName -ExePath $exePath -Token $token -ForceUpload ([bool]$Force)
    }

    if ($UpdateRepoMeta) {
        if ($DryRun) {
            Write-Host "[干跑] 会更新仓库描述与 topics"
        } else {
            Set-RepoMeta -Slug $slug -Token $token
        }
    }

    if ($DryRun) {
        Write-Host "干跑结束：什么都没改。确认无误后去掉 -DryRun 再跑一次。"
    } else {
        Write-Host "发版完成：https://github.com/$slug/releases/tag/$Tag"
    }
}

try {
    Invoke-Main
} catch {
    [Console]::Error.WriteLine("发版中断：$($_.Exception.Message)")
    exit 1
}
exit 0
