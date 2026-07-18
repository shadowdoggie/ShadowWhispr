<#
.SYNOPSIS
    One-time setup for ShadowWhispr's local speech engine.

.DESCRIPTION
    Creates the .venv next to the app and installs Parakeet v3 + CUDA PyTorch.
    Requires an NVIDIA GPU.

    ShadowWhispr ships its own Python, so nothing is ever installed system-wide
    and no interpreter on the user's machine is touched, inspected or relied on.
    The runtime lives in {app}\python and is put there by the installer; a source
    checkout fetches the same pinned build on first run (scripts\get-bundled-python.ps1).

    Everything is logged to setup-log.txt next to the app. On failure the
    window stays open with the error instead of closing, and download steps
    retry automatically before giving up.

    Progress is also emitted as machine-readable "##SW## percent|message" lines
    so ShadowWhispr can show an in-app progress screen instead of making the
    user read a console. Failures emit "##SWERR## message". Keep this file pure
    ASCII: Windows PowerShell 5.1 reads unsigned UTF-8 as ANSI and a non-ASCII
    character inside a quoted string breaks parsing.
#>
[CmdletBinding()]
param(
    # Reports which Python would be used and stops before changing anything.
    # Useful for checking a machine without a multi-gigabyte download.
    [switch]$DetectOnly
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$venvPath = Join-Path $repoRoot ".venv"
$venvPython = Join-Path $venvPath "Scripts\python.exe"
$requirementsPath = Join-Path $repoRoot "stt\requirements.txt"
$logPath = Join-Path $repoRoot "setup-log.txt"
$bundledPythonDir = Join-Path $repoRoot "python"
$bundledPython = Join-Path $bundledPythonDir "python.exe"

# Emits one progress line for ShadowWhispr's in-app setup screen, plus the same
# text for anyone watching the console or reading setup-log.txt afterwards.
function Write-Step {
    param(
        [int]$Percent,
        [string]$Message
    )
    Write-Host ""
    Write-Host "[$Percent%] $Message"
    Write-Host "##SW## $Percent|$Message"
}

# ShadowWhispr ships its own Python. There is deliberately no search of the
# user's machine: guessing which of several installed interpreters to use was
# the single largest source of setup failures, and installing one system-wide
# was never something the user asked for.
function Resolve-BundledPython {
    if (-not (Test-Path -LiteralPath $bundledPython)) {
        # A source checkout has no installer to place the runtime, so fetch the
        # same pinned build the installer would have shipped.
        Write-Host "No bundled Python found at $bundledPythonDir"
        $fetch = Join-Path $PSScriptRoot "get-bundled-python.ps1"
        if (-not (Test-Path -LiteralPath $fetch)) {
            throw "ShadowWhispr's Python runtime is missing and scripts\get-bundled-python.ps1 was not found. Please reinstall ShadowWhispr."
        }
        & $fetch -Destination $bundledPythonDir
    }

    if (-not (Test-Path -LiteralPath $bundledPython)) {
        throw "ShadowWhispr's Python runtime is missing from $bundledPythonDir. Please reinstall ShadowWhispr."
    }

    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $reported = $null
    try {
        $reported = & $bundledPython -c "import sys, venv; print('%d.%d' % sys.version_info[:2])" 2>$null
        $exitCode = $LASTEXITCODE
    }
    catch {
        $exitCode = 1
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($exitCode -ne 0 -or (($reported | Out-String).Trim()) -ne "3.12") {
        throw "ShadowWhispr's bundled Python at $bundledPython could not run. Please reinstall ShadowWhispr."
    }

    Write-Host "Using ShadowWhispr's bundled Python: $bundledPython"
    return $bundledPython
}


# An existing .venv is only worth keeping if it actually runs. One left behind
# by an older ShadowWhispr - built from a system Python that has since moved,
# broken or been uninstalled - looks perfectly fine on disk while every command
# in it fails instantly. Reusing one of those made setup unusable and blamed the
# user's internet for it.
function Test-VenvUsable {
    param([string]$VenvPython)

    if (-not (Test-Path -LiteralPath $VenvPython)) {
        Write-Host "  the environment has no python.exe"
        return $false
    }

    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $global:LASTEXITCODE = 0
        $output = & $VenvPython -c "import sys, pip, venv; print('ok')" 2>&1
        if ($LASTEXITCODE -ne 0 -or (($output | Out-String) -notmatch "ok")) {
            Write-Host "  the environment does not run: $((($output | Out-String).Trim()))"
            return $false
        }
        return $true
    }
    catch {
        Write-Host "  the environment could not be started: $($_.Exception.Message)"
        return $false
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

# Runs an external command, retrying genuine transient failures. Every attempt's
# output is echoed so setup-log.txt shows what actually went wrong: the previous
# version swallowed it and always reported "check your internet connection",
# which hid a broken environment behind a network error that never happened.
#
# $LASTEXITCODE alone is not a reliable signal - when a command fails to launch
# it is never set and keeps its previous value, so a failure can read as success.
function Invoke-WithRetry {
    param(
        [string]$Exe,
        [string[]]$Arguments,
        [string]$Description,
        [int]$Attempts = 3
    )
    $lastError = "no output"

    for ($i = 1; $i -le $Attempts; $i++) {
        $launchFailed = $false
        $global:LASTEXITCODE = 0
        $previous = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = & $Exe @Arguments 2>&1
            $text = ($output | Out-String).Trim()
            if ($text) { Write-Host $text }
        }
        catch {
            $launchFailed = $true
            $text = $_.Exception.Message
            Write-Host "  could not run ${Exe}: $text"
        }
        finally {
            $ErrorActionPreference = $previous
        }

        if (-not $launchFailed -and $LASTEXITCODE -eq 0) { return }

        $lastError = if ($text) { $text } else { "exit code $LASTEXITCODE, no output" }
        if ($i -lt $Attempts) {
            Write-Host ""
            Write-Host "$Description failed (attempt $i of $Attempts). Retrying in 10 seconds..." -ForegroundColor Yellow
            Start-Sleep -Seconds 10
        }
    }

    # Keep the real reason in the message; an instant failure with no output is
    # never a slow connection, and saying so sends people hunting the wrong fault.
    $tail = ($lastError -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 3) -join " | "
    throw "$Description failed after $Attempts attempts. Last error: $tail"
}

try { Start-Transcript -Path $logPath | Out-Null } catch {}

try {
    # --- Build the local environment from ShadowWhispr's own Python ---------
    Write-Step -Percent 2 -Message "Preparing ShadowWhispr's Python"
    $python = Resolve-BundledPython

    # A working environment is kept as-is, so upgrading an install that already
    # finished setup changes nothing. A broken one is rebuilt rather than used:
    # an environment built by an older ShadowWhispr from a system Python that is
    # now gone fails every command instantly, which used to end setup here.
    $rebuildReason = $null
    if (Test-Path -LiteralPath $venvPath) {
        Write-Host "Checking the existing local Python environment at $venvPath"
        if (Test-VenvUsable -VenvPython $venvPython) {
            Write-Host "The existing environment works; keeping it."
        }
        else {
            $rebuildReason = "the existing environment could not run"
        }
    }
    else {
        $rebuildReason = "no environment exists yet"
    }

    if ($DetectOnly) {
        Write-Host ""
        if ($rebuildReason) {
            Write-Host "Detection only: would build the environment with $python ($rebuildReason)"
        }
        else {
            Write-Host "Detection only: the existing environment is fine and would be kept"
        }
        try { Stop-Transcript | Out-Null } catch {}
        exit 0
    }

    if ($rebuildReason) {
        Write-Step -Percent 14 -Message "Creating the local Python environment"
        Write-Host "Building a fresh environment because $rebuildReason."

        if (Test-Path -LiteralPath $venvPath) {
            Write-Host "Removing the unusable environment first..."
            Remove-Item -LiteralPath $venvPath -Recurse -Force -ErrorAction Stop
        }

        & $python -m venv $venvPath
        if ($LASTEXITCODE -ne 0) {
            throw "Could not create the local Python environment with $python."
        }
        if (-not (Test-VenvUsable -VenvPython $venvPython)) {
            throw "The local Python environment was created but does not run. Please reinstall ShadowWhispr."
        }
        Write-Host "Local Python environment created."
    }

    Write-Step -Percent 18 -Message "Preparing the package installer (pip)"
    Invoke-WithRetry -Description "Updating pip" `
        -Exe $venvPython -Arguments @("-m", "pip", "install", "--upgrade", "pip", "wheel")

    if (-not (Test-Path -LiteralPath $requirementsPath)) {
        throw "The package list is missing from $requirementsPath. Please reinstall ShadowWhispr."
    }

    Write-Step -Percent 22 -Message "Downloading speech and CUDA packages (about 2 GB)"
    Invoke-WithRetry -Description "Installing the speech-to-text packages" `
        -Exe $venvPython -Arguments @("-m", "pip", "install", "--requirement", $requirementsPath)

    Write-Step -Percent 58 -Message "Checking your NVIDIA GPU"
    & $venvPython -c "import torch; assert torch.cuda.is_available(), 'CUDA is unavailable'; print('CUDA ready:', torch.cuda.get_device_name(0))"
    if ($LASTEXITCODE -ne 0) {
        throw "PyTorch cannot use the NVIDIA GPU. Check that you have an NVIDIA GPU and current driver, then run this again."
    }

    # Download the pinned model revision into a plain folder of real files next
    # to the app. The worker loads only from this folder (see stt\worker.py) -
    # no Hugging Face cache, no symlinks, no network at engine start.
    Write-Step -Percent 62 -Message "Downloading the speech model (about 2.4 GB)"
    $modelDir = Join-Path $repoRoot "speech-model"
    $downloadScript = @"
from huggingface_hub import snapshot_download
snapshot_download(
    'nvidia/parakeet-tdt-0.6b-v3',
    revision='7c35754d166cca382ad1e53e68b01e7c575f3a1d',
    local_dir=r'$modelDir',
    allow_patterns=[
        'config.json',
        'generation_config.json',
        'model.safetensors',
        'processor_config.json',
        'tokenizer.json',
        'tokenizer_config.json',
    ],
)
"@
    Invoke-WithRetry -Description "Downloading the speech model" `
        -Exe $venvPython -Arguments @("-c", $downloadScript)

    # Prove the engine really starts before declaring setup finished: launch the
    # worker exactly like the app does and wait for its ready message. Freshly
    # downloaded model files can be briefly locked by antivirus scanning, so a
    # failed first check gets one more chance after a cool-down.
    Write-Step -Percent 90 -Message "Starting the speech engine for the first time"
    $workerPath = Join-Path $repoRoot "stt\worker.py"
    $readyLine = $null
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $venvPython
        $psi.Arguments = "`"$workerPath`" --server"
        $psi.WorkingDirectory = $repoRoot
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardInput = $true
        $psi.UseShellExecute = $false
        $proc = [System.Diagnostics.Process]::Start($psi)
        $readyLine = $proc.StandardOutput.ReadLine()
        try { $proc.Kill() } catch {}
        if ($readyLine -match '"ready":true') { break }
        if ($attempt -lt 2) {
            Write-Host "The engine did not start on the first check. Waiting 15 seconds and trying once more..." -ForegroundColor Yellow
            Start-Sleep -Seconds 15
        }
    }
    if (-not $readyLine -or $readyLine -notmatch '"ready":true') {
        throw "The speech engine could not start. It reported: $readyLine"
    }
    Write-Host "Speech engine verified."

    # Written last: the app treats setup as finished only when this file exists,
    # so an interrupted setup is retried instead of half-loading.
    Set-Content -LiteralPath (Join-Path $venvPath "setup-complete") -Value "ok" -Encoding ascii

    Write-Step -Percent 100 -Message "Setup complete"
    Write-Host "Speech-to-text setup is ready. You can close this window and use ShadowWhispr."
    try { Stop-Transcript | Out-Null } catch {}
}
catch {
    Write-Host ""
    Write-Host "##SWERR## $($_.Exception.Message)"
    Write-Host "SETUP FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "A full log was saved to: $logPath" -ForegroundColor Yellow
    Write-Host "The most common cause is an unstable internet connection."
    Write-Host "Click 'Set up speech now' in ShadowWhispr to try again - it resumes where it left off."
    try { Stop-Transcript | Out-Null } catch {}
    if (-not $env:SHADOWWHISPR_SETUP_NOPAUSE) {
        try { [void](Read-Host "Press Enter to close this window") } catch {}
    }
    exit 1
}
