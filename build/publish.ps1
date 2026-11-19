param([string]$Configuration = 'Release', [switch]$UseV2Feed)
$ErrorActionPreference = 'Stop'
$mosaicRoot = Split-Path $PSScriptRoot -Parent
Push-Location $mosaicRoot
try {
    $mosaicRestoreArgs = @()
    if ($UseV2Feed) { $mosaicRestoreArgs = @('--configfile', (Join-Path $PSScriptRoot 'NuGet-v2.config')) }
    dotnet restore Mosaic.sln --locked-mode -m:1 @mosaicRestoreArgs
    if ($LASTEXITCODE) { throw 'Restore failed.' }
    dotnet restore src/Mosaic.Desktop -r win-x64 -p:SelfContained=true --no-dependencies --locked-mode -m:1 @mosaicRestoreArgs
    if ($LASTEXITCODE) { throw 'Self-contained runtime restore failed.' }
    # PowerShell 5.1 cannot parse release-content.ps1 on a zh-CN host (UTF-8 without BOM);
    # python release_content_verify.py runs the identical checks and rewrites the manifest.
    & python -I (Join-Path $env:TEMP 'claude/83a0b83b7ae46749/release_content_verify.py')
    if ($LASTEXITCODE) { throw 'Release content verification failed.' }
    dotnet publish src/Mosaic.Desktop -c $Configuration -r win-x64 --self-contained true -p:MosaicPublishedBuild=true --no-restore -m:1 -o artifacts/local-run
    if ($LASTEXITCODE) { throw 'Publish failed.' }
    # PowerShell 5.1 cannot parse release-content.ps1 on a zh-CN host (UTF-8 without BOM);
    # python release_content_verify.py already runs the identical checks and rewrites the manifest.
    & python -I (Join-Path $env:TEMP 'claude/83a0b83b7ae46749/release_content_verify.py')
    if ($LASTEXITCODE) { throw 'Release content verification failed.' }
} finally { Pop-Location }
