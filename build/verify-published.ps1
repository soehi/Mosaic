param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$ProductVersion)
$ErrorActionPreference = 'Stop'
$mosaicRoot = Split-Path $PSScriptRoot -Parent
$publish = if ([IO.Path]::IsPathRooted($PublishDirectory)) { $PublishDirectory } else { Join-Path $mosaicRoot $PublishDirectory }
$desktopDll = Join-Path $publish 'Mosaic.Desktop.dll'
if (-not (Test-Path -LiteralPath $desktopDll)) { throw "Missing published desktop assembly: $desktopDll" }

# AssemblyMetadata values live in the #Blob heap as length-prefixed UTF-8 strings:
# <len>key<len>value. Read the value that follows MosaicPublishedBuild.
$bytes = [IO.File]::ReadAllBytes($desktopDll)
$needle = [Text.Encoding]::UTF8.GetBytes('MosaicPublishedBuild')
$latin = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
$index = $latin.IndexOf('MosaicPublishedBuild', [StringComparison]::Ordinal)
if ($index -lt 0) { throw 'Mosaic.Desktop.dll has no MosaicPublishedBuild metadata.' }
$valueLength = [int]$bytes[$index + $needle.Length]
$value = [Text.Encoding]::UTF8.GetString($bytes, $index + $needle.Length + 1, $valueLength)
if ($value -ne 'true') { throw "Published payload is not a release build (MosaicPublishedBuild=$value). Build with -p:MosaicPublishedBuild=true so the update summary is generated." }

$manifestPath = Join-Path $publish 'release-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Missing release-manifest.json in $publish." }
$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifest.currentReleaseId -ne "mosaic-$ProductVersion") {
    throw "release-manifest.json currentReleaseId '$($manifest.currentReleaseId)' does not match product version $ProductVersion."
}
Write-Output "PASS: published payload is a release build with manifest $($manifest.currentReleaseId)."
