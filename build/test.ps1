param([string]$Configuration = 'Release', [switch]$NoBuild, [switch]$SkipDesktop)
$ErrorActionPreference = 'Stop'
$mosaicRoot = Split-Path $PSScriptRoot -Parent
Push-Location $mosaicRoot
try {
    if (-not $NoBuild) {
        dotnet build Mosaic.sln -c $Configuration --no-restore -m:1
        if ($LASTEXITCODE) { throw 'Build failed.' }
    }
    # The executable xUnit runner also works in restricted hosts without MTP named pipes.
    foreach ($project in Get-ChildItem tests -Directory | Where-Object Name -Like 'Mosaic.*') {
        if ($SkipDesktop -and $project.Name -eq 'Mosaic.Desktop.SmokeTests') { continue }
        $framework = if ($project.Name -in @('Mosaic.Wpf.Tests', 'Mosaic.Desktop.SmokeTests')) { 'net10.0-windows' } else { 'net10.0' }
        dotnet (Join-Path $project.FullName "bin/$Configuration/$framework/$($project.Name).dll")
        if ($LASTEXITCODE) { throw "Tests failed: $($project.Name)" }
    }
} finally { Pop-Location }
