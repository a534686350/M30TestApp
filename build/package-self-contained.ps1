param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\M30TestApp.Wpf\M30TestApp.Wpf.csproj"
$projectXml = [xml](Get-Content -LiteralPath $project)
$version = $projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = "0.0.0"
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
