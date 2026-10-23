param([switch]$Restore)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    $dotnet = if (Test-Path "$repo/.tools/dotnet/dotnet.exe") { "$repo/.tools/dotnet/dotnet.exe" } else { 'dotnet' }
    $env:DOTNET_CLI_HOME = "$repo/.local/dotnet-home"
    $env:NUGET_PACKAGES = "$repo/.local/nuget"
    node scripts/check-architecture.mjs
    if ($LASTEXITCODE) { throw 'Architecture check failed' }
    if ($Restore) {
        & $dotnet restore src/backend/Matigr.slnx --locked-mode
        if ($LASTEXITCODE) { throw 'Restore failed' }
    }
    & $dotnet build src/backend/Matigr.slnx --no-restore -c Release
    if ($LASTEXITCODE) { throw 'Build failed' }
    & $dotnet test src/backend/Matigr.slnx --no-build --no-restore -c Release
    if ($LASTEXITCODE) { throw 'Tests failed' }
}
finally { Pop-Location }
