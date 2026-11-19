param([switch]$Restore)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    $dotnet = if (Test-Path "$repo/.tools/dotnet/dotnet.exe") { "$repo/.tools/dotnet/dotnet.exe" } else { 'dotnet' }
    $env:DOTNET_CLI_HOME = "$repo/.local/dotnet-home"
    $env:NUGET_PACKAGES = "$repo/.local/nuget"
    if ($Restore) {
        npm ci --ignore-scripts --no-audit --no-fund --cache .local/npm-cache
        if ($LASTEXITCODE) { throw 'npm workspace restore failed' }
    }
    node scripts/check-architecture.mjs
    if ($LASTEXITCODE) { throw 'Architecture check failed' }
    node scripts/check-docs.mjs
    if ($LASTEXITCODE) { throw 'Documentation check failed' }
    node --test tests/integration/http-readiness.test.mjs
    if ($LASTEXITCODE) { throw 'HTTP readiness regression tests failed' }
    node scripts/prepare-fixtures.mjs
    if ($LASTEXITCODE) { throw 'Fixture preparation failed' }
    node --test tests/fixtures/fixtures.test.mjs
    if ($LASTEXITCODE) { throw 'Fixture tests failed' }
    npm run test:contracts
    if ($LASTEXITCODE) { throw 'Contract tests failed' }
    npm run check:api
    if ($LASTEXITCODE) { throw 'Generated frontend DTOs are stale' }
    npm run typecheck:web
    if ($LASTEXITCODE) { throw 'Frontend typecheck failed' }
    npm run test:web
    if ($LASTEXITCODE) { throw 'Frontend tests failed' }
    npm run build:web
    if ($LASTEXITCODE) { throw 'Frontend build failed' }
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
