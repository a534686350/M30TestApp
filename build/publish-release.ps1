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
v1.2.46 更新 —— 万用表流程重排 + 工位表可编辑 + 测试前设备预检

【万用表自动测试：每个温度点的顺序改为】
- ① 断电 → ② 测电阻（无电状态）→ ③ 上电 → 各压力点测电压 → 反向回差 → ④ 断电 → ⑤ 读温度。
- 电阻改到上电之前测，日志会明确打印"断电：301-318 与总电源 319,320 全部断开"，可核对测量条件。
- 界面实时日志不再只显示数据行：压力点、上电、断电、电阻采集等关键步骤都显示。

【采集矩阵格式对齐晶圆测试模板】
- 表头：R(Ω) | Usig0(mV) | Usig50(mV) | Usig100(mV) | Usig50(mV) | Usig0(mV) | Temp(℃)。
- 异常/超量程单元格红色加粗（OL），警告橙色；973 超量程 9.9E+37 不再当有效值写进报表。
- 报表导出 R 列优先取 973 直测的 DMM_R。

【973 多台管理】
- 每台 973 独立勾选启用（不再只有第一台能勾）。
- 工位表新增「973 台号」列，写清每个工位归哪台（1-18 → 973-1，19-36 → 973-2，37-54 → 973-3）。
- 工位页自动判读需要的台数，配置不足时红字提示。
- 测试只打开本轮工位表实际用到的台（18 工位不会去连 973-2/3）。
- 工位表「通道」列填了超出 973 范围的值（电路板 DAC 通道）会自动回退并告警。

【手动调试新增「973 通道直测」】
- 独立窗口：电压 VOUT1-18（101-118）、电阻 R1-18（201-218）、电源 1-18 + 总电源（301-318/319,320）。
- 支持一键读取全部、单路电源通断、全部上电/断电；可选 973-1/2/3 台号。

【工位表可直接编辑】
- 「序列号」列可双击修改，改完自动保存。
- 支持从 Excel 复制一列序列号后 Ctrl+V 批量粘贴（超出工位表范围的行会提示忽略）。
- 生成工位表后立即落盘（CSV + 配置 + 会话），修复"界面显示 54 个、跑测试还是 10 个"。

【开始测试前设备连通性预检】
- 弹窗显示进度条 + 百分比 + 逐项状态，检测本轮需要的设备。
- 按方案勾选决定检测项：没勾烘箱就不连烘箱（列表里灰显"未启用"）。
- 每项 6 秒超时；有失败项时列红字，可选择"仍然开始"。

【界面与修复】
- 启动直接落在「工位表」，不再默认显示自动测试页。
- 测试任务窗口顶栏新增「本轮工位」，运行中可只读查看本轮用了哪些工位。
- 修复所有进度条只显示空轨道不填充的问题。
- 修复工位数量输入 9 实际跑 10 的问题（输入框回写时机）。

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
