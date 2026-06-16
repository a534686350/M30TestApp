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
长期稳定性测试重构：专用表格、双行表头、按温度点（T1/T2/T3）合并显示。

新增电压/电阻采集模式（CONF:VOLT / CONF:RES）；每个温度点全部压力采完后读 1 次烘箱温度；60 工位 DMM 通道 101-120 / 201-220 / 301-320。

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

    $headerText = "--$boundary`r`nContent-Disposition: form-data; name=`"access_token`"`r`n`r`n$giteeToken`r`n"
    $fileHeader = "--$boundary`r`nContent-Disposition: form-data; name=`"file`"; filename=`"$assetName`"`r`nContent-Type: application/zip`r`n`r`n"
    $footerText = "`r`n--$boundary--`r`n"

    $bodyStream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.StreamWriter($bodyStream, [Text.Encoding]::UTF8)
    $writer.Write($headerText)
    $writer.Flush()
    $bodyStream.Write([Text.Encoding]::UTF8.GetBytes($fileHeader), 0, [Text.Encoding]::UTF8.GetByteCount($fileHeader))
    $bodyStream.Write($fileBytes, 0, $fileBytes.Length)
    $writer.Write($footerText)
    $writer.Flush()
    $bodyBytes = $bodyStream.ToArray()
    $writer.Dispose()
    $bodyStream.Dispose()

    Invoke-RestMethod `
        -Uri "$baseUri/releases/$releaseId/attach_files" `
        -Method Post `
        -ContentType "multipart/form-data; boundary=$boundary" `
        -Body $bodyBytes | Out-Null

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
