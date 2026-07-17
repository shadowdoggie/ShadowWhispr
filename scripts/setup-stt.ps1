<#
.SYNOPSIS
    One-time setup for ShadowWhispr's local speech engine.

.DESCRIPTION
    Creates the .venv next to the app and installs Parakeet v3 + CUDA PyTorch.
    If Python 3.12 is not installed, it is installed automatically (per-user,
    no admin) via winget, falling back to the official python.org installer.
    Requires an NVIDIA GPU.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$venvPath = Join-Path $repoRoot ".venv"
$venvPython = Join-Path $venvPath "Scripts\python.exe"
$requirementsPath = Join-Path $repoRoot "stt\requirements.txt"

# Version used if we have to download Python ourselves.
$PythonVersion = "3.12.8"

function Resolve-Python312 {
    # 1) py launcher with an explicit 3.12 request
    if (Get-Command py -ErrorAction SilentlyContinue) {
        $v = & py -3.12 -c "import sys;print('%d.%d' % sys.version_info[:2])" 2>$null
        if ($LASTEXITCODE -eq 0 -and $v -eq "3.12") { return @("py", "-3.12") }
    }
    # 2) Default per-user install location
    $direct = Join-Path $env:LOCALAPPDATA "Programs\Python\Python312\python.exe"
    if (Test-Path -LiteralPath $direct) { return @($direct) }
    # 3) A 'python' on PATH that happens to be 3.12
    if (Get-Command python -ErrorAction SilentlyContinue) {
        $v = & python -c "import sys;print('%d.%d' % sys.version_info[:2])" 2>$null
        if ($LASTEXITCODE -eq 0 -and $v -eq "3.12") { return @((Get-Command python).Source) }
    }
    return $null
}

function Install-Python312 {
    Write-Host "Python 3.12 was not found. Installing it now (one-time, no admin needed)..."

    # Preferred: winget, per-user scope.
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Write-Host "Trying winget..."
        $wingetArgs = @("install", "--exact", "--id", "Python.Python.3.12", "--scope", "user",
            "--silent", "--accept-package-agreements", "--accept-source-agreements")
        & winget @wingetArgs
        if (Resolve-Python312) { return }
        Write-Host "winget did not provide Python 3.12. Falling back to python.org."
    }

    # Fallback: official python.org installer, silent per-user.
    $url = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-amd64.exe"
    $installer = Join-Path $env:TEMP "python-$PythonVersion-amd64.exe"
    Write-Host "Downloading Python $PythonVersion from python.org..."
    Invoke-WebRequest -Uri $url -OutFile $installer -UseBasicParsing
    Write-Host "Installing Python $PythonVersion (per-user)..."
    Start-Process -FilePath $installer -Wait -ArgumentList @(
        "/quiet", "InstallAllUsers=0", "PrependPath=1", "Include_launcher=1", "Include_test=0"
    )
    Remove-Item -LiteralPath $installer -ErrorAction SilentlyContinue
}

# --- Make sure we have a Python 3.12 to build the venv with -----------------
if (-not (Test-Path -LiteralPath $venvPython)) {
    $python = Resolve-Python312
    if (-not $python) {
        Install-Python312
        $python = Resolve-Python312
    }
    if (-not $python) {
        throw "Could not find or install Python 3.12 automatically. Please install it from https://www.python.org/downloads/ and run this again."
    }

    Write-Host "Creating the local Python environment..."
    $pythonExe = $python[0]
    $pythonArgs = @()
    if ($python.Count -gt 1) { $pythonArgs = $python[1..($python.Count - 1)] }
    & $pythonExe @pythonArgs -m venv $venvPath
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create .venv with Python 3.12."
    }
}

Write-Host "Installing Parakeet v3 and CUDA support (this downloads ~2-3 GB)..."
& $venvPython -m pip install --upgrade pip wheel
if ($LASTEXITCODE -ne 0) {
    throw "Could not update pip in .venv."
}

& $venvPython -m pip install --requirement $requirementsPath
if ($LASTEXITCODE -ne 0) {
    throw "Could not install the speech-to-text requirements."
}

& $venvPython -c "import torch; assert torch.cuda.is_available(), 'CUDA is unavailable'; print('CUDA ready:', torch.cuda.get_device_name(0))"
if ($LASTEXITCODE -ne 0) {
    throw "PyTorch cannot use the NVIDIA GPU. Check that you have an NVIDIA GPU and current driver, then run this again."
}

Write-Host "Downloading the Parakeet speech model (about 2.5 GB, one-time)..."
& $venvPython -c "from huggingface_hub import snapshot_download; snapshot_download('nvidia/parakeet-tdt-0.6b-v3')"
if ($LASTEXITCODE -ne 0) {
    throw "Could not download the Parakeet speech model. Check your internet connection, then run this again."
}

# Written last: the app treats setup as finished only when this file exists,
# so an interrupted setup is retried instead of half-loading.
Set-Content -LiteralPath (Join-Path $venvPath "setup-complete") -Value "ok" -Encoding ascii

Write-Host ""
Write-Host "Speech-to-text setup is ready. You can close this window and use ShadowWhispr."
