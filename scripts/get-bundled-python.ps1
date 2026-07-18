<#
.SYNOPSIS
    Ensures a self-contained CPython 3.12 exists at <Destination>\python.exe.

.DESCRIPTION
    ShadowWhispr ships its own Python so users never have to install one, and so
    setup never has to guess which of a machine's Pythons to use. This script is
    the single place that decides which build that is.

    The runtime is a python-build-standalone release: a normal, relocatable
    CPython with pip and venv included, unlike the python.org "embeddable" zip
    which has neither. The download is pinned to an exact version and verified
    against a known SHA-256 before it is unpacked.

    Used in two places:
      - scripts\build-installer.ps1, to bake the runtime into the installer.
      - scripts\setup-stt.ps1, when running from a source checkout, where no
        installer has put one there yet.

    Keep this file pure ASCII: Windows PowerShell 5.1 reads unsigned UTF-8 as
    ANSI, and a non-ASCII character inside a quoted string breaks parsing.
#>
[CmdletBinding()]
param(
    # Folder that will contain python.exe (created if missing).
    [Parameter(Mandatory = $true)]
    [string]$Destination,

    # Re-download and replace an existing runtime.
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Pinned so every machine gets a byte-identical runtime. To move to a newer
# CPython, update all three values together.
$PythonVersion = "3.12.8"
$PythonUrl = "https://github.com/astral-sh/python-build-standalone/releases/download/20241206/cpython-3.12.8+20241206-x86_64-pc-windows-msvc-install_only.tar.gz"
$PythonSha256 = "767b4be3ddf6b99e5ade519789c1615c191d8cf99d5aff4685cc18b48931f1e6"

# Debug symbols and the Tk GUI stack are most of the download and none of what
# ShadowWhispr uses: the worker only needs the interpreter, pip and venv.
$TrimPaths = @("tcl", "Lib\test", "Lib\idlelib", "Lib\tkinter", "Lib\lib2to3")

function Test-BundledPython {
    param([string]$Exe)
    if (-not (Test-Path -LiteralPath $Exe)) { return $false }

    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $reported = & $Exe -c "import sys, venv, ensurepip; print('%d.%d' % sys.version_info[:2])" 2>$null
        if ($LASTEXITCODE -ne 0) { return $false }
        return ((($reported | Out-String).Trim()) -eq "3.12")
    }
    catch {
        return $false
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

$pythonExe = Join-Path $Destination "python.exe"

if (-not $Force -and (Test-BundledPython -Exe $pythonExe)) {
    Write-Host "Bundled Python $PythonVersion already present at $Destination"
    return
}

Write-Host "Fetching bundled Python $PythonVersion (one-time, about 40 MB)..."

$workingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("shadowwhispr-python-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $workingDirectory | Out-Null
$archive = Join-Path $workingDirectory "cpython.tar.gz"

try {
    # TLS 1.2 is not the default on stock Windows PowerShell 5.1.
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }
    Invoke-WebRequest -Uri $PythonUrl -OutFile $archive -UseBasicParsing

    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $PythonSha256) {
        throw "The downloaded Python runtime did not match its expected checksum (got $actual). Nothing was installed."
    }
    Write-Host "Checksum verified."

    # tar.exe ships with Windows 10 1803 and later.
    & tar -xzf $archive -C $workingDirectory
    if ($LASTEXITCODE -ne 0) { throw "Could not unpack the Python runtime." }

    $extracted = Join-Path $workingDirectory "python"
    if (-not (Test-Path -LiteralPath (Join-Path $extracted "python.exe"))) {
        throw "The unpacked Python runtime does not contain python.exe."
    }

    foreach ($relative in $TrimPaths) {
        $path = Join-Path $extracted $relative
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Get-ChildItem -LiteralPath $extracted -Recurse -File -Filter *.pdb -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue

    # Replace the destination only once the new runtime is complete, so an
    # interrupted download can never leave a half-built Python behind.
    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    $parent = Split-Path -Parent $Destination
    if ($parent -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    Move-Item -LiteralPath $extracted -Destination $Destination

    if (-not (Test-BundledPython -Exe $pythonExe)) {
        throw "The bundled Python runtime was unpacked but does not run."
    }

    $sizeMb = (Get-ChildItem -LiteralPath $Destination -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("Bundled Python {0} ready at {1} ({2:N0} MB)" -f $PythonVersion, $Destination, $sizeMb)
}
finally {
    Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
