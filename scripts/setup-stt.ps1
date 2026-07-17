[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$venvPath = Join-Path $repoRoot ".venv"
$pythonPath = Join-Path $venvPath "Scripts\python.exe"
$requirementsPath = Join-Path $repoRoot "stt\requirements.txt"

if (-not (Test-Path -LiteralPath $pythonPath)) {
    Write-Host "Creating the local Python environment..."
    & py -3.12 -m venv $venvPath
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create .venv. Install 64-bit Python 3.12 and try again."
    }
}

Write-Host "Installing Parakeet v3 and CUDA support..."
& $pythonPath -m pip install --upgrade pip wheel
if ($LASTEXITCODE -ne 0) {
    throw "Could not update pip in .venv."
}

& $pythonPath -m pip install --requirement $requirementsPath
if ($LASTEXITCODE -ne 0) {
    throw "Could not install the speech-to-text requirements."
}

& $pythonPath -c "import torch; assert torch.cuda.is_available(), 'CUDA is unavailable'; print('CUDA ready:', torch.cuda.get_device_name(0))"
if ($LASTEXITCODE -ne 0) {
    throw "PyTorch cannot use the NVIDIA GPU. Check the NVIDIA driver and rerun this script."
}

Write-Host "Speech-to-text setup is ready. The Parakeet model downloads on first launch."
