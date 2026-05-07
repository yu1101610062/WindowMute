[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "0.1.0",
    [switch]$StopRunningApp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$PublishDir = Join-Path $RepoRoot "artifacts\publish\WindowMute\$Runtime"
$InstallerDir = Join-Path $RepoRoot "artifacts\installer"
$ReleaseDir = Join-Path $RepoRoot "artifacts\release"
$PackageRoot = Join-Path $RepoRoot "artifacts\package"
$PortableName = "WindowMute-$Version-$Runtime"
$PortableRoot = Join-Path $PackageRoot $PortableName
$PortableZip = Join-Path $ReleaseDir "$PortableName-portable.zip"
$InstallerPath = Join-Path $InstallerDir "WindowMuteSetup-$Version-x64.exe"
$ReleaseInstallerPath = Join-Path $ReleaseDir (Split-Path -Leaf $InstallerPath)

Remove-Item -LiteralPath $PublishDir -Recurse -Force -ErrorAction SilentlyContinue

& (Join-Path $PSScriptRoot "build-installer.ps1") `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -Version $Version `
    -StopRunningApp:$StopRunningApp

if (-not (Test-Path -LiteralPath $PublishDir)) {
    throw "Publish output was not found: $PublishDir"
}

if (-not (Test-Path -LiteralPath $InstallerPath)) {
    throw "Installer was not found: $InstallerPath"
}

Remove-Item -LiteralPath $ReleaseDir, $PackageRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $ReleaseDir, $PortableRoot | Out-Null

Copy-Item -Path (Join-Path $PublishDir "*") -Destination $PortableRoot -Recurse -Force
Compress-Archive -LiteralPath $PortableRoot -DestinationPath $PortableZip -CompressionLevel Optimal -Force
Copy-Item -LiteralPath $InstallerPath -Destination $ReleaseInstallerPath -Force

Write-Host "Portable package created: $PortableZip"
Write-Host "Installer copied: $ReleaseInstallerPath"
