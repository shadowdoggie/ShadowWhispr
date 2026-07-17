$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'src\ShadowWhispr\ShadowWhispr.csproj'

if (-not (Test-Path (Join-Path $projectRoot '.venv\Scripts\python.exe'))) {
    Write-Host 'Parakeet is not set up yet. Running the one-time setup...'
    & (Join-Path $PSScriptRoot 'setup-stt.ps1')
}

dotnet run --project $project --configuration Release
