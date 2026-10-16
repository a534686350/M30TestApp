param(
    [string]$Version = "",
    [string]$Runtime = "win-x64",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\M30TestApp.Wpf\M30TestApp.Wpf.csproj"

# 版本单一来源是根目录 Directory.Build.props（csproj 不再直接携带 Version）。
# 用正则直接读文本：早先用 [xml] 对象模型，在部分宿主环境下取不到值时
# 会静默回退成 0.0.0。
if ([string]::IsNullOrWhiteSpace($Version)) {
    $propsPath = Join-Path $root "Directory.Build.props"
    if (Test-Path -LiteralPath $propsPath) {
        $propsText = Get-Content -LiteralPath $propsPath -Raw -Encoding UTF8
        $verMatch = [regex]::Match($propsText, '<Version>([0-9][^<]*)</Version>')
        if ($verMatch.Success) {
            $Version = $verMatch.Groups[1].Value.Trim()
        }
    }
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Cannot resolve version from Directory.Build.props"
}

$releaseRoot = Join-Path $root "artifacts\release"
$zipPath = Join-Path $releaseRoot "M30TestApp.V2-v$Version-$Runtime-self-contained.zip"

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "package-self-contained.ps1") -Runtime $Runtime
}

if (-not (Test-Path -LiteralPath $zipPath)) {
    throw "Release zip not found: $zipPath"
}

$ghToken = $env:GH_TOKEN
if ([string]::IsNullOrWhiteSpace($ghToken)) {
    $ghToken = $env:GITHUB_TOKEN
}

$giteeToken = $env:GITEE_TOKEN

if ([string]::IsNullOrWhiteSpace($ghToken) -and [string]::IsNullOrWhiteSpace($giteeToken)) {
    throw "Set GH_TOKEN (or GITHUB_TOKEN) and/or GITEE_TOKEN for release upload."
}
if ([string]::IsNullOrWhiteSpace($ghToken)) {
    Write-Warning "GH_TOKEN not set; skipping GitHub release."
}
if ([string]::IsNullOrWhiteSpace($giteeToken)) {
    Write-Warning "GITEE_TOKEN not set; skipping Gitee release."
}

$releaseNotes = @"
v1.2.43 更新:

【修复「选择运行方案 → 工位」页】
- 修复「起始工位 / 所需工位」只能填 1 的问题。这两个输入框原来逐字符回写配置，
  想输 129 时中途的 "1129" 会被当成最终值夹到上限 256，接着工位数量被连带压成 1、
  工位勾选格也只剩 1 个，现场看到的就是「工位起始这块只能填 1」、工位表只剩一行。
  现在改为输完（失焦）再生效。
- 修复「起始板卡 / 起始板卡工位」改不动、一改就弹回 1 的问题。原来每次重建工位表
  都会按起始工位重新推导并覆盖手填值；现在只在起始工位**真的变化**时重新推导一次，
  手填值原样保留（点 Apply 后写入配置，下次打开仍在）。
- 「选择测试工位」新增「全不选」按钮，一键取消全部勾选、回到连续模式。
- 手改「起始编号」时自动取消零散勾选（勾选是按绝对工位号生成的，不清掉的话
  改了起始编号工位表不会跟着变）。
- 「起始阀位」标注为「按工位自动」并改为只读 —— 阀门恒按工位号推导（每 32 工位一个阀），
  原来的输入框改了并不生效，属误导。

本版不影响采集与计算逻辑，历史数据格式完全兼容。

Self-contained win-x64 build. .NET 8.0 runtime is included.
"@

function Invoke-GitHubRelease {
    param(
        [string]$Tag,
        [string]$Title,
        [string]$Body,
        [string]$ZipFile
    )

    if ([string]::IsNullOrWhiteSpace($ghToken)) {
        Write-Warning "GH_TOKEN not set; skipping GitHub release."
        return $null
    }

    $headers = @{
        Authorization = "Bearer $ghToken"
        Accept        = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
        "User-Agent"  = "M30TestApp-release-script"
    }

    $existing = $null
    try {
        $existing = Invoke-RestMethod `
            -Uri "https://api.github.com/repos/a534686350/M30TestApp/releases/tags/$Tag" `
            -Headers $headers `
            -Method Get
    }
    catch {
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode.value__ -ne 404) {
            throw
        }
    }

    if ($existing -and $existing.id) {
        Write-Host "GitHub release $Tag already exists (id=$($existing.id))."
        return $existing
    }

    $payload = @{
        tag_name = $Tag
        name     = $Title
        body     = $Body
        draft    = $false
        prerelease = $false
    } | ConvertTo-Json

    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/a534686350/M30TestApp/releases" `
        -Headers $headers `
        -Method Post `
        -Body $payload `
        -ContentType "application/json; charset=utf-8"

    Write-Host "Created GitHub release $Tag (id=$($release.id))."

    $assetName = [IO.Path]::GetFileName($ZipFile)
    $uploadUri = "$($release.upload_url -replace '\{\?name,label\}', '')?name=$([Uri]::EscapeDataString($assetName))"

    Invoke-RestMethod `
        -Uri $uploadUri `
        -Headers @{
            Authorization = "Bearer $ghToken"
            Accept        = "application/vnd.github+json"
            "X-GitHub-Api-Version" = "2022-11-28"
            "User-Agent"  = "M30TestApp-release-script"
            "Content-Type" = "application/zip"
        } `
        -Method Post `
        -InFile $ZipFile `
        -OutVariable uploadedAsset | Out-Null

    Write-Host "Uploaded GitHub asset: $assetName"
    return $release
}

function Invoke-GiteeRelease {
    param(
        [string]$Tag,
        [string]$Title,
        [string]$Body,
        [string]$ZipFile
    )

    if ([string]::IsNullOrWhiteSpace($giteeToken)) {
        Write-Warning "GITEE_TOKEN not set; skipping Gitee release."
        return
    }

    $owner = "hl515"
    $repo = "m30-test-app"
    $baseUri = "https://gitee.com/api/v5/repos/$owner/$repo"

    function Find-GiteeReleaseByTag {
        $releases = Invoke-RestMethod `
            -Uri "$baseUri/releases?access_token=$giteeToken&page=1&per_page=100" `
            -Method Get
        return @($releases) | Where-Object { $_.tag_name -eq $Tag } | Select-Object -First 1
    }

    $existing = $null
    try {
        $existing = Invoke-RestMethod `
            -Uri "$baseUri/releases/tags/$Tag?access_token=$giteeToken" `
            -Method Get
    }
    catch {
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode.value__ -ne 404) {
            throw
        }
    }

    if ($existing -and $existing.id) {
        Write-Host "Gitee release $Tag already exists (id=$($existing.id))."
        $releaseId = $existing.id
    }
    else {
        $existing = Find-GiteeReleaseByTag
        if ($existing -and $existing.id) {
            Write-Host "Gitee release $Tag already exists (id=$($existing.id))."
            $releaseId = $existing.id
        }
        else {
            try {
                $payload = @{
                    access_token = $giteeToken
                    tag_name = $Tag
                    name = $Title
                    body = $Body
                    target_commitish = "main"
                    prerelease = $false
                } | ConvertTo-Json

                $created = Invoke-RestMethod `
                    -Uri "$baseUri/releases" `
                    -Method Post `
                    -Body $payload `
                    -ContentType "application/json; charset=utf-8"

                $releaseId = $created.id
                Write-Host "Created Gitee release $Tag (id=$releaseId)."
            }
            catch {
                $message = $_.ErrorDetails.Message
                if ([string]::IsNullOrWhiteSpace($message)) {
                    $message = $_.Exception.Message
                }

                if ($message -notmatch "tag already exists|already exists") {
                    throw
                }

                $existing = Find-GiteeReleaseByTag
                if (-not ($existing -and $existing.id)) {
                    throw
                }

                $releaseId = $existing.id
                Write-Host "Gitee release $Tag already exists (id=$releaseId)."
            }
        }
    }

    $assetName = [IO.Path]::GetFileName($ZipFile)
    $boundary = "----gitee-release-" + [Guid]::NewGuid().ToString("N")
    $fileBytes = [IO.File]::ReadAllBytes($ZipFile)
    $lf = "`r`n"
    $multipartBytes = New-Object System.Collections.Generic.List[byte]

    function Add-Text([string]$text) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($text)
        $multipartBytes.AddRange($bytes)
    }

    Add-Text "--$boundary$lf"
    Add-Text "Content-Disposition: form-data; name=`"access_token`"$lf$lf$giteeToken$lf"
    Add-Text "--$boundary$lf"
    Add-Text "Content-Disposition: form-data; name=`"file`"; filename=`"$assetName`"$lf"
    Add-Text "Content-Type: application/octet-stream$lf$lf"
    $multipartBytes.AddRange($fileBytes)
    Add-Text "$lf--$boundary--$lf"

    Invoke-RestMethod `
        -Uri "$baseUri/releases/$releaseId/attach_files" `
        -Method Post `
        -ContentType "multipart/form-data; boundary=$boundary" `
        -Body $multipartBytes.ToArray() | Out-Null

    Write-Host "Uploaded Gitee asset: $assetName"
}

$tag = "v$Version"
$title = "M30TestApp V2 v$Version"

Write-Host "Publishing $title ..."
Write-Host "Zip: $zipPath"

# GitHub 与 Gitee 互为备份：任一渠道网络不通时只警告，不阻断另一条。
# 现场多数环境只有 Gitee 能通，所以 Gitee 失败才算整体失败。
$ghRelease = $null
try {
    $ghRelease = Invoke-GitHubRelease -Tag $tag -Title $title -Body $releaseNotes -ZipFile $zipPath
}
catch {
    Write-Warning "GitHub release failed (network or API): $($_.Exception.Message)"
    Write-Warning "GitHub 渠道跳过，稍后网络恢复可用同一命令重跑补发（已存在的 release 会复用）。"
}

Invoke-GiteeRelease -Tag $tag -Title $title -Body $releaseNotes -ZipFile $zipPath

Write-Host ""
Write-Host "Done."
if ($ghRelease -and $ghRelease.html_url) {
    Write-Host "GitHub: $($ghRelease.html_url)"
} else {
    Write-Host "GitHub: 未发布（网络不通或未提供 GH_TOKEN）"
}
Write-Host "Gitee : https://gitee.com/hl515/m30-test-app/releases/tag/$tag"
