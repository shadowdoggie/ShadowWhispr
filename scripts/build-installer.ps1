<#
.SYNOPSIS
    Builds ShadowWhispr and packages a Windows installer (Setup.exe).

.DESCRIPTION
    1. Publishes the WPF app self-contained (no .NET runtime needed on the target PC).
    2. Compiles installer\ShadowWhispr.iss with Inno Setup into build\installer\.

    Any AI or human can run this to produce a release-ready installer:
        .\scripts\build-installer.ps1 -Version 1.2.3

    The same steps run in CI on every published GitHub release
    (see .github\workflows\release.yml).

.PARAMETER Version
    Version stamped into the app and installer. Defaults to 0.0.0-dev.

.PARAMETER FrameworkDependent
    Publish framework-dependent instead of self-contained (smaller, but the
    target PC must already have the .NET 10 Desktop Runtime).
#>
[CmdletBinding()]
param(
    [string]$Version = "0.0.0-dev",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'src\ShadowWhispr\ShadowWhispr.csproj'
$publishDir = Join-Path $projectRoot 'build\ShadowWhispr'
$issFile = Join-Path $projectRoot 'installer\ShadowWhispr.iss'
$installerDir = Join-Path $projectRoot 'build\installer'
$bundledPythonDir = Join-Path $projectRoot 'build\python'

# .NET assembly versions must be numeric (x.y.z). Extract that from -Version.
$numericVersion = '0.0.0'
if ($Version -match '(\d+\.\d+\.\d+)') { $numericVersion = $Matches[1] }

Write-Host "==> Publishing ShadowWhispr ($Version)..." -ForegroundColor Cyan
$selfContained = (-not $FrameworkDependent).ToString().ToLower()
dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained $selfContained `
    --output $publishDir `
    "/p:Version=$numericVersion"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

# ShadowWhispr ships its own Python, so users never install one themselves.
Write-Host "==> Preparing the bundled Python runtime..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'get-bundled-python.ps1') -Destination $bundledPythonDir
if (-not (Test-Path -LiteralPath (Join-Path $bundledPythonDir 'python.exe'))) {
    throw "The bundled Python runtime is missing; refusing to build an installer without it."
}

# Locate the Inno Setup compiler (ISCC.exe).
$iscc = (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    foreach ($candidate in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )) {
        if (Test-Path -LiteralPath $candidate) { $iscc = $candidate; break }
    }
}
if (-not $iscc) {
    throw "Inno Setup (ISCC.exe) not found. Install it with 'winget install JRSoftware.InnoSetup' or 'choco install innosetup', then rerun."
}

New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

Write-Host "==> Compiling installer with $iscc..." -ForegroundColor Cyan
& $iscc `
    "/DMyAppVersion=$Version" `
    "/DSourceDir=$publishDir" `
    "/DScriptsDir=$(Join-Path $projectRoot 'scripts')" `
    "/DPythonDir=$bundledPythonDir" `
    "/DOutputDir=$installerDir" `
    $issFile
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed with exit code $LASTEXITCODE." }

$setup = Join-Path $installerDir "ShadowWhispr-Setup-$Version.exe"
Write-Host "==> Installer built: $setup" -ForegroundColor Green
