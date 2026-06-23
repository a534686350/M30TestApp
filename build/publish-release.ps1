param(
    [string]$Version = "",
    [string]$Runtime = "win-x64",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\M30TestApp.Wpf\M30TestApp.Wpf.csproj"
$projectXml = [xml](Get-Content -LiteralPath $project)
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $projectXml.Project.PropertyGroup.Version
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
v1.2.24 更新：

- 工位扫码录入：刷新工位前二次确认，确认后重新生成全部工位并清空序列号，方便重新扫码录入。
- 自动续测：启动时检测到未完成测试，会弹出续测窗口，可从未完成温度点继续，并手动输入本次继续保温时长。
- 在线更新：优化更新提示框状态，下载时显示进度条百分比。
- 设置界面：修复管理员密码输入对话框布局变形。

自包含 win-x64，免装 .NET 8.0。
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

    $assetName = [IO.Path]::GetFileName($ZipFile)
    $boundary = "----gitee-release-" + [Guid]::NewGuid().ToString("N")
    $fileBytes = [IO.File]::ReadAllBytes($ZipFile)
    $lf = "`r`n"
    $body = New-Object System.Collections.Generic.List[byte]

    function Add-Text([string]$text) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($text)
        $body.AddRange($bytes)
    }

    Add-Text "--$boundary$lf"
    Add-Text "Content-Disposition: form-data; name=`"access_token`"$lf$lf$giteeToken$lf"
    Add-Text "--$boundary$lf"
    Add-Text "Content-Disposition: form-data; name=`"file`"; filename=`"$assetName`"$lf"
    Add-Text "Content-Type: application/octet-stream$lf$lf"
    $body.AddRange($fileBytes)
    Add-Text "$lf--$boundary--$lf"

    Invoke-RestMethod `
        -Uri "$baseUri/releases/$releaseId/attach_files" `
        -Method Post `
        -ContentType "multipart/form-data; boundary=$boundary" `
        -Body $body.ToArray() | Out-Null

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
