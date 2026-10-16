param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\M30TestApp.Wpf\M30TestApp.Wpf.csproj"

# 版本单一来源是根目录 Directory.Build.props（csproj 不再直接携带 Version）。
# 这里用正则直接读文本：早先用 [xml] 对象模型，在部分宿主环境下取不到值时
# 会静默回退成 0.0.0，打出来的包名就成了 v0.0.0。
$version = ""
$propsPath = Join-Path $root "Directory.Build.props"
if (Test-Path -LiteralPath $propsPath) {
    $propsText = Get-Content -LiteralPath $propsPath -Raw -Encoding UTF8
    $verMatch = [regex]::Match($propsText, '<Version>([0-9][^<]*)</Version>')
    if ($verMatch.Success) {
        $version = $verMatch.Groups[1].Value.Trim()
    }
}
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Cannot resolve version from Directory.Build.props (looked in $root)"
}

$releaseRoot = Join-Path $root "artifacts\release"
$publishDir = Join-Path $releaseRoot "publish-$Runtime"
$zipPath = Join-Path $releaseRoot "M30TestApp.V2-v$version-$Runtime-self-contained.zip"

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    /p:PublishTrimmed=false `
    /p:PublishDir="$publishDir\"

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

Write-Host "Published: $publishDir"
Write-Host "Zip:       $zipPath"
