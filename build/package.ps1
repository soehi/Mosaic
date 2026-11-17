param(
    [string]$ProductVersion = '0.9.1',
    [switch]$NoPublish,
    [string]$PublishDirectory = 'artifacts/local-run',
    [switch]$SkipValidation)
$ErrorActionPreference = 'Stop'
$mosaicRoot = Split-Path $PSScriptRoot -Parent
if (-not $NoPublish) { & (Join-Path $PSScriptRoot 'publish.ps1') }
$mosaicPublish = if ([IO.Path]::IsPathRooted($PublishDirectory)) { $PublishDirectory } else { Join-Path $mosaicRoot $PublishDirectory }
if (-not (Test-Path -LiteralPath (Join-Path $mosaicPublish 'Mosaic.Desktop.exe'))) { throw 'Publish the application first.' }
& (Join-Path $PSScriptRoot 'release-content.ps1') -ProgramVersion $ProductVersion -CheckOnly -CheckManifestPath (Join-Path $mosaicPublish 'release-manifest.json')
$mosaicRuntimeDataDirectories = @('Data', 'Config', 'Logs')
function Test-MosaicPayloadItem([IO.FileSystemInfo]$Item) {
    if ($Item.Name -eq '.mosaic.instance.lock') { return $false }
    $relative = [IO.Path]::GetRelativePath($mosaicPublish, $Item.FullName)
    $firstSegment = ($relative -split '[\\/]')[0]
    return $firstSegment -notin $mosaicRuntimeDataDirectories
}
$mosaicPackageDir = Join-Path $mosaicRoot 'artifacts/installer'
New-Item -ItemType Directory -Force $mosaicPackageDir | Out-Null
$mosaicNamespace = 'http://wixtoolset.org/schemas/v4/wxs'
$mosaicXml = [System.Xml.XmlDocument]::new()
$mosaicXml.LoadXml('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Fragment><ComponentGroup Id="Payload"/></Fragment></Wix>')
$mosaicFragment = $mosaicXml.DocumentElement.FirstChild
$mosaicGroup = $mosaicFragment.FirstChild
function Add-MosaicNode($Parent, [string]$Name, [hashtable]$Attributes) {
    $node = $mosaicXml.CreateElement($Name, $mosaicNamespace)
    foreach ($key in $Attributes.Keys) { $node.SetAttribute($key, [string]$Attributes[$key]) }
    [void]$Parent.AppendChild($node)
    return ,$node
}
function Get-MosaicId([string]$Path) { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Path.ToLowerInvariant()))).Substring(0, 24) }
$mosaicDirectories = @{ '' = 'INSTALLFOLDER' }
$mosaicDirectoryRef = Add-MosaicNode $mosaicFragment 'DirectoryRef' @{ Id = 'INSTALLFOLDER' }
$mosaicNodes = @{ '' = $mosaicDirectoryRef }
foreach ($directory in Get-ChildItem -LiteralPath $mosaicPublish -Directory -Recurse | Where-Object { Test-MosaicPayloadItem $_ } | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($mosaicPublish, $directory.FullName)
    $parent = [IO.Path]::GetDirectoryName($relative)
    $id = 'D' + (Get-MosaicId $relative)
    $mosaicDirectories[$relative] = $id
    $mosaicNodes[$relative] = Add-MosaicNode $mosaicNodes[$parent] 'Directory' @{ Id = $id; Name = $directory.Name }
}
foreach ($file in Get-ChildItem -LiteralPath $mosaicPublish -File -Recurse | Where-Object { Test-MosaicPayloadItem $_ } | Sort-Object FullName) {
    if ($file.Extension -eq '.pdb') { continue }
    $relative = [IO.Path]::GetRelativePath($mosaicPublish, $file.FullName)
    $id = Get-MosaicId $relative
    $directory = $mosaicDirectories[[IO.Path]::GetDirectoryName($relative)]
    $guidBytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('Mosaic.Installer/' + $relative.ToLowerInvariant()))
    $componentGuid = [guid]::new([byte[]]$guidBytes[0..15])
    $component = Add-MosaicNode $mosaicGroup 'Component' @{ Id = "C$id"; Directory = $directory; Guid = $componentGuid.ToString('B'); Bitness = 'always64' }
    Add-MosaicNode $component 'File' @{ Id = "F$id"; Source = $file.FullName; KeyPath = 'no' } | Out-Null
    Add-MosaicNode $component 'RegistryValue' @{ Root = 'HKCU'; Key = 'Software\Mosaic\InstallerFiles'; Name = $id; Type = 'integer'; Value = '1'; KeyPath = 'yes' } | Out-Null
}
$cleanup = Add-MosaicNode $mosaicGroup 'Component' @{ Id = 'DirectoryCleanup'; Directory = 'INSTALLFOLDER'; Guid = '{B2050352-2EBE-4E24-A5FB-FC06A716DC98}'; Bitness = 'always64' }
Add-MosaicNode $cleanup 'RemoveFolder' @{ Id = 'RemoveUserProgramsIfEmpty'; Directory = 'UserPrograms'; On = 'uninstall' } | Out-Null
foreach ($directory in $mosaicDirectories.Values) { Add-MosaicNode $cleanup 'RemoveFolder' @{ Id = 'R' + (Get-MosaicId $directory); Directory = $directory; On = 'uninstall' } | Out-Null }
Add-MosaicNode $cleanup 'RegistryValue' @{ Root = 'HKCU'; Key = 'Software\Mosaic\Installer'; Name = 'Directories'; Type = 'integer'; Value = '1'; KeyPath = 'yes' } | Out-Null
$payload = Join-Path $mosaicPackageDir 'Payload.wxs'
$mosaicXml.Save($payload)
$mosaicBuildOutput = Join-Path $mosaicPackageDir $ProductVersion
Push-Location $mosaicRoot
try {
    $mosaicBuildArguments = @(
        'build', 'build/installer/Mosaic.Installer.wixproj', '-c', 'Release', '-m:1',
        "-p:ProductVersion=$ProductVersion",
        "-p:PayloadFile=$payload",
        "-p:OutputPath=$mosaicBuildOutput/"
    )
    if ($SkipValidation) { $mosaicBuildArguments += '-p:SuppressValidation=true' }
    dotnet @mosaicBuildArguments
    if ($LASTEXITCODE) { throw 'Installer build failed.' }
    # Preserve deliverables outside MSBuild's previous-output cleanup when testing upgrades.
    $mosaicPackage = Join-Path $mosaicPackageDir "Mosaic-$ProductVersion-win-x64.msi"
    Copy-Item -LiteralPath (Join-Path $mosaicBuildOutput "Mosaic-$ProductVersion-win-x64.msi") -Destination $mosaicPackage -Force
    & (Join-Path $PSScriptRoot 'test-package-shortcuts.ps1') -PackagePath $mosaicPackage
} finally { Pop-Location }
