param(
    [string]$Version = "",
    [string]$Runtime = "win-x64",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\M30TestApp.Wpf\M30TestApp.Wpf.csproj"

# 版本单一来源是根目录 Directory.Build.props（csproj 不再直接携带 Version）
if ([string]::IsNullOrWhiteSpace($Version)) {
    $propsXml = [xml](Get-Content -LiteralPath (Join-Path $root "Directory.Build.props"))
    $Version = $propsXml.Project.PropertyGroup.Version
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

if ([string]::IsNullOrWhiteSpace($ghToken)) {
    throw "Set GH_TOKEN (or GITHUB_TOKEN) for GitHub release upload."
}

$releaseNotes = @"
v1.2.40 更新:

- 新增：报表「测试设备」（AO 列）改为下拉选择，候选取自往期报表实测型号
  （只读扫描，目标目录不写不改不删），候选里没有的型号可当场手输。
- 新增：开测前可选「生成模板」——自动测试沿用全性能.xlsx（行为不变，桌面仍写旧版 CSV）；
  晶圆测试改用现场样例模板，报表在 data 目录与桌面各存一份，不再写旧版 CSV。
- 调试模式（全 SIM）模拟量按现场样例分布出数：桥阻 R ≈ 6160Ω@25℃ / +5.2Ω/℃，
  Usig 零点 ±3mV、灵敏度 6.4~8.2 mV/压力单位、幅值随温度 -0.19%/℃。
- 修复：运行方案窗口扫码录入时表格乱跳。根因是滚动单位混用——DataGrid 默认按「项」滚动，
  却把像素坐标当项单位算位移；现在录入哪一行就高亮哪一行，视野保持稳定。
- 修复：运行方案窗口左栏内容超出可视高度时无法滚动，窗口偏小则看不到下方字段。
- 界面：五段式工控外壳 + 6 个主页签，配置页统一收进「设置」；浅色主题 v2。

Self-contained win-x64 build. .NET 8.0 runtime is included.
"@

function Invoke-GitHubRelease {
    param(
        [string]$Tag,
        [string]$Title,
        [string]$Body,
        [string]$ZipFile
    )

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

$ghRelease = Invoke-GitHubRelease -Tag $tag -Title $title -Body $releaseNotes -ZipFile $zipPath
Invoke-GiteeRelease -Tag $tag -Title $title -Body $releaseNotes -ZipFile $zipPath

Write-Host ""
Write-Host "Done."
Write-Host "GitHub: $($ghRelease.html_url)"
