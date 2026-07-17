$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'src\ShadowWhispr\ShadowWhispr.csproj'
$output = Join-Path $projectRoot 'build\ShadowWhispr'

dotnet publish $project --configuration Release --runtime win-x64 --self-contained false --output $output
if ($LASTEXITCODE -ne 0) {
    throw "ShadowWhispr publish failed with exit code $LASTEXITCODE."
}
Write-Host "Built ShadowWhispr at $output"
