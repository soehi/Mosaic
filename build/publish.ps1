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
    & (Join-Path $PSScriptRoot 'release-content.ps1') -ManifestPath (Join-Path $PSScriptRoot 'release-manifest.json') -NotesPath (Join-Path $mosaicRoot ('docs/releases/v' + ([xml](Get-Content -Raw (Join-Path $mosaicRoot 'Directory.Build.props'))).Project.PropertyGroup.Version + '.md'))
    dotnet publish src/Mosaic.Desktop -c $Configuration -r win-x64 --self-contained true -p:MosaicPublishedBuild=true --no-restore -m:1 -o artifacts/local-run
    if ($LASTEXITCODE) { throw 'Publish failed.' }
    & (Join-Path $PSScriptRoot 'release-content.ps1') -CheckOnly -CheckManifestPath (Join-Path $mosaicRoot 'artifacts/local-run/release-manifest.json')
} finally { Pop-Location }
