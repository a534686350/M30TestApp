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
v1.2.48 更新 —— 进度按实际采集走 + 方案可选回差 + 采集方式进系统设置

【测试进度不再一直 100%】
- 原来进度按任务步骤算，而万用表测试整个流程只有 1 个步骤 → 永远 100%。
- 现在按「温度点 × 每温度点采集点」算：3 温度点 × 5 采集点 = 15 格，采完一个压力点走一格。
- 顶部同时显示阶段、温度点 x/y、压力点 Px/y、保温 a/b min。

【方案可勾选是否测试回差】
- 运行方案窗口新增「测试回差」勾选，旁边直接写明本轮采集几个点。
- 勾选：3 个压力点跑 5 个点（0→50→100→50→0），多出 Usig50_R / Usig0_R 两列。
- 不勾：只跑正向 3 个点，日志写「跳过回差测试（方案未勾选）」。
- 勾选状态存进方案 ini，下次选同一方案自动带出。

【采集方式移到系统设置】
- 「配置 → 系统设置 → 行为」新增「采集方式」：电路板采集 / 万用表采集 (DAQ973A)。
- 一套测试系统只有一种接法，配一次即可；运行方案窗口不再每个方案各选一遍。

【上一版 v1.2.47 主要内容】
- 测试页顶部阶段行（保温中 / 加压中 / 保压中 / 采集中）。
- 保温日志每 10 分钟一条；加压、保压进实时日志。

【测试页顶部阶段行】
- 顶部新增一行绿色阶段信息，运行中实时变化：
  保温中 · 温度点 1/3 · 压力点 P2/3 · 保温 35/120 min
- 阶段会随流程切换：保温中 → 采集中（电阻）→ 加压中 → 保压中 → 采集中 → 采集中（回差）。
- 温度点进度带总数（x/3），压力点同样带总数（P2/3），保温带已保温/总时长。

【实时日志更清楚】
- 保温：每 10 分钟一条（原来每分钟一条，120 分钟刷 120 行），
  格式「保温中：T1 已保温 30/120 min，剩余 90 min」，结束时补一条「保温结束」。
- 加压：「加压中：P2 → 50 kPa [表压]，等待稳定」。
- 保压：「保压中：P2 = 50 kPa，保持 60s」。
- 之前这两类日志挂在 Pressure 通道被界面过滤掉，现在都能看到。

【上一版 v1.2.46 主要内容（如未升级请一并参考）】
- 万用表每温度点顺序：断电 → 测电阻 → 上电 → 各压力点 → 回差 → 断电 → 读温度。
- 采集矩阵表头对齐晶圆模板（R/Usig0/Usig50/Usig100/Usig50/Usig0/Temp），异常红色加粗。
- 973 每台独立启用、工位表标「973 台号」、工位页自动判读台数、只连本轮用到的台。
- 手动调试「973 通道直测」面板（VOUT/R/电源通断）。
- 工位表序列号可编辑、Excel 复制后 Ctrl+V 批量粘贴，改动即时落盘。
- 开始测试前设备连通性预检（进度条+百分比，按方案勾选决定检测项）。
- 修复进度条不填充、工位数量输入 9 跑 10、生成工位表不落盘。


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
