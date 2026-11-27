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

# 环境变量没设时，回退读仓库里的发版令牌文件（两个文件都在 .gitignore 里，不进版本库）。
# 以前只认环境变量，每次发版都要先手动 Get-Content 塞进 $env:GH_TOKEN 才能跑，容易忘。
if ([string]::IsNullOrWhiteSpace($ghToken)) {
    $ghTokenFile = Join-Path $root "artifacts\.reltok_gh"
    if (Test-Path -LiteralPath $ghTokenFile) {
        $ghToken = (Get-Content -LiteralPath $ghTokenFile -Raw).Trim()
        Write-Host "GH_TOKEN 未设置，已从 artifacts\.reltok_gh 读取"
    }
}
if ([string]::IsNullOrWhiteSpace($giteeToken)) {
    $giteeTokenFile = Join-Path $root "artifacts\.reltok_gitee"
    if (Test-Path -LiteralPath $giteeTokenFile) {
        $giteeToken = (Get-Content -LiteralPath $giteeTokenFile -Raw).Trim()
        Write-Host "GITEE_TOKEN 未设置，已从 artifacts\.reltok_gitee 读取"
    }
}

if ([string]::IsNullOrWhiteSpace($ghToken) -and [string]::IsNullOrWhiteSpace($giteeToken)) {
    throw "找不到发版令牌：请设置 GH_TOKEN / GITEE_TOKEN 环境变量，或把令牌写入 artifacts\.reltok_gh 与 artifacts\.reltok_gitee。"
}
if ([string]::IsNullOrWhiteSpace($ghToken)) {
    Write-Warning "GH_TOKEN not set; skipping GitHub release."
}
if ([string]::IsNullOrWhiteSpace($giteeToken)) {
    Write-Warning "GITEE_TOKEN not set; skipping Gitee release."
}

$releaseNotes = @"
v1.2.50 更新 —— 深色主题下工位表不再是白板

【工位表深色模式】
- 修复黑色主题时工位表整块白板的问题：单元格、行列标题、网格线全部跟随深色主题。
- 文字改为浅色，网格线用主题边框色，长时间盯屏不刺眼。
- 右侧/底部滚动条、双击编辑框、右键菜单一并压成深色，不再留浅灰色带。

【换主题不用重启】
- 程序里切换深浅色，工位表自动重载换肤，表格数据不丢。

【上一版 v1.2.49 主要内容】
- 工位表换成电子表格控件（x-spreadsheet）：Excel 式单元格编辑、Ctrl+C/V 批量粘贴、
  Ctrl+D/R 智能递增填充。
- 数据安全闸门：读回全空 / 序列号大面积清空 / 序列号变成工位号 → 拒绝回写。

【上一版 v1.2.48 主要内容】
- 测试进度按「温度点 × 每温度点采集点」实际推进，不再固定 100%。
- 方案可勾选「测试回差」：3 个压力点跑 5 个点（0→50→100→50→0）。
- 采集方式（电路板/万用表）移到「配置 → 系统设置 → 行为」。


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
