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
v1.2.52 更新 —— 修复「新建的方案在自动测试下拉里找不到」

【问题】
- 配置页点「新建方案」后，方案只在内存里，没有生成 ini 文件。
- 自动测试的「方案」下拉是扫磁盘 setting\TestConfig 目录生成的，所以新方案根本不出现。
- 结果：新建完直接去自动测试，下拉里找不到刚建的方案。

【修复】
- 「新建方案」现在会立刻把 ini 写到 setting\TestConfig\<方案文件夹>\<方案名>.ini，
  和「保存」用的是同一套路径规则（没选文件夹时落到 M30测试）。
- 建完立刻刷新方案下拉，配置页和自动测试页都能看到。
- 顺手修了「删除方案」：子文件夹里的方案以前会报「方案文件不存在」，现在能正常删。

【使用提示】
- 新建方案默认名是 plan_年月日_时分秒，想改成正式名字：在「方案名称」框里改名 → 点保存，
  文件名会跟着改。
- 已经新建过但没保存的方案，升级后需要重新新建一次（旧的那份只存在内存里，重启就没了）。

【上一版 v1.2.51 主要内容】
- DAQ973A 支持 USB 接法：总线下拉新增 USB0，「VISA 地址」列可直接粘贴
  USB0::0x2A8D::0x8601::MY59018500::0::INSTR；「扫描 GPIB」改名「扫描仪器」，同时扫 GPIB 和 USB。

【上一版 v1.2.50 主要内容】
- 深色主题下工位表不再是白板：单元格、行列标题、网格线、滚动条全部跟随主题；
  程序里切深浅色，工位表自动重载换肤，数据不丢。


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
