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
$ProjectPath = Join-Path $RepoRoot "src\WindowMute.App\WindowMute.App.csproj"
$InnoScriptPath = Join-Path $RepoRoot "installer\WindowMute.iss"
$PublishDir = Join-Path $RepoRoot "artifacts\publish\WindowMute\$Runtime"
$InstallerDir = Join-Path $RepoRoot "artifacts\installer"
$ProjectName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath)
$ProjectXml = [xml](Get-Content -LiteralPath $ProjectPath -Raw)
$TargetFramework = @($ProjectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)[0]
$BuildOutputDir = Join-Path (Split-Path -Parent $ProjectPath) "bin\$Configuration\$TargetFramework\$Runtime"

function Find-InnoCompiler {
    if ($env:ISCC_PATH -and (Test-Path -LiteralPath $env:ISCC_PATH)) {
        return (Resolve-Path -LiteralPath $env:ISCC_PATH).Path
    }

    $fromPath = Get-Command "iscc.exe" -ErrorAction SilentlyContinue
    if ($fromPath) {
        return $fromPath.Source
    }

    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

if ($StopRunningApp) {
    Get-Process "WindowMute.App" -ErrorAction SilentlyContinue | Stop-Process -Force
}

New-Item -ItemType Directory -Force -Path $PublishDir, $InstallerDir | Out-Null

dotnet publish $ProjectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $PublishDir

$AppPriSource = Join-Path $BuildOutputDir "$ProjectName.pri"
$AppPriDestination = Join-Path $PublishDir "$ProjectName.pri"
if (Test-Path -LiteralPath $AppPriSource) {
    Copy-Item -LiteralPath $AppPriSource -Destination $AppPriDestination -Force
}
elseif (-not (Test-Path -LiteralPath $AppPriDestination)) {
    throw "WinUI resource index was not found: $AppPriSource"
}

$IsccPath = Find-InnoCompiler
if (-not $IsccPath) {
    Write-Host "Publish output created at: $PublishDir"
    throw "Inno Setup 6 compiler was not found. Install Inno Setup 6 or set ISCC_PATH to ISCC.exe, then rerun this script."
}

& $IsccPath `
    "/DAppVersion=$Version" `
    "/DSourceDir=$PublishDir" `
    "/DOutputDir=$InstallerDir" `
    $InnoScriptPath

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
}

$InstallerPath = Join-Path $InstallerDir "WindowMuteSetup-$Version-x64.exe"
if (-not (Test-Path -LiteralPath $InstallerPath)) {
    throw "Installer was not created at expected path: $InstallerPath"
}

Write-Host "Installer created: $InstallerPath"
