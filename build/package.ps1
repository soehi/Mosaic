param(
    [string]$ProductVersion = '',
    [switch]$NoPublish,
    [string]$PublishDirectory = 'artifacts/local-run',
    [switch]$SkipValidation)
$ErrorActionPreference = 'Stop'
$mosaicRoot = Split-Path $PSScriptRoot -Parent
# Default to the version the build itself stamps, so the MSI can never be labelled with an
# older product version than the payload it ships.
if (-not $ProductVersion) {
    $mosaicVersionProps = [xml](Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $mosaicRoot 'Directory.Build.props'))
    $ProductVersion = @($mosaicVersionProps.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    if (-not $ProductVersion) { throw 'Directory.Build.props has no Version property.' }
}
if (-not $NoPublish) { & (Join-Path $PSScriptRoot 'publish.ps1') }
$mosaicPublish = if ([IO.Path]::IsPathRooted($PublishDirectory)) { $PublishDirectory } else { Join-Path $mosaicRoot $PublishDirectory }
if (-not (Test-Path -LiteralPath (Join-Path $mosaicPublish 'Mosaic.Desktop.exe'))) { throw 'Publish the application first.' }
& (Join-Path $PSScriptRoot 'verify-published.ps1') -PublishDirectory $mosaicPublish -ProductVersion $ProductVersion
# PowerShell 5.1 cannot parse release-content.ps1 on a zh-CN host (UTF-8 without BOM);
    # the tracked python port already runs the identical checks and rewrites the manifest.
    & python -I (Join-Path $mosaicRoot 'scripts/release_content_verify.py')
    if ($LASTEXITCODE) { throw 'Release content verification failed.' }
$mosaicRuntimeDataDirectories = @('Data', 'Config', 'Logs')
function Test-MosaicPayloadItem([IO.FileSystemInfo]$Item) {
    if ($Item.Name -eq '.mosaic.instance.lock') { return $false }
    $relative = [IO.Path]::GetRelativePath($mosaicPublish, $Item.FullName)
    $firstSegment = ($relative -split '[\\/]')[0]
    return $firstSegment -notin $mosaicRuntimeDataDirectories
}
$mosaicPackageDir = Join-Path $mosaicRoot 'artifacts/installer'
New-Item -ItemType Directory -Force $mosaicPackageDir | Out-Null
$payload = Join-Path $mosaicPackageDir 'Payload.wxs'
$mosaicBuildOutput = Join-Path $mosaicPackageDir $ProductVersion
$mosaicPackage = Join-Path $mosaicPackageDir "Mosaic-$ProductVersion-win-x64.msi"
# The payload generation below uses [IO.Path]::GetRelativePath, which does not exist on the
# .NET Framework that ships with Windows PowerShell 5.1. On that host, defer to the Python
# port (identical ids, directory tree and component GUIDs) to emit Payload.wxs and build
# the MSI; the WiX project and Package.wxs stay the single source of truth either way.
$mosaicNeedsPythonPackaging = $null -eq [IO.Path].GetMethod('GetRelativePath', [type[]]@([string], [string]))
if ($mosaicNeedsPythonPackaging) {
    & python -I (Join-Path $mosaicRoot 'scripts/package_payload.py') $ProductVersion $(if ($SkipValidation) { 'skip-validation' })
    if ($LASTEXITCODE) { throw 'Installer build failed.' }
    Copy-Item -LiteralPath (Join-Path $mosaicBuildOutput "Mosaic-$ProductVersion-win-x64.msi") -Destination $mosaicPackage -Force
    & (Join-Path $PSScriptRoot 'test-package-shortcuts.ps1') -PackagePath $mosaicPackage
    & (Join-Path $PSScriptRoot 'test-package-maintenance.ps1') -PackagePath $mosaicPackage -ExpectedVersion $ProductVersion
    exit 0
}
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
# 子目录解析修复：UI 序列的 CostFinalize 先于路径校验运行，子目录属性一旦按默认
# %LOCALAPPDATA% 路径解析，执行序列改写 INSTALLFOLDER 后不会重算，资源文件会落进
# 默认目录。这里为每个子目录生成 SetDirectory（Type-51）动作，执行序列在
# CostFinalize 之后按当前 INSTALLFOLDER 重设子目录目标；与 scripts/package_payload.py 保持一致。
# 子目录解析修复：为每个子目录生成显式 Type-51 动作（Property=子目录 Id），执行序列
# 在 CostFinalize 之后按当前 INSTALLFOLDER 重设子目录目标；与 scripts/package_payload.py
# 保持一致。
foreach ($relative in ($mosaicDirectories.Keys | Where-Object { $_ -ne '' } | Sort-Object)) {
    $id = $mosaicDirectories[$relative]
    # INSTALLFOLDER 由自定义动作设置时不带尾分隔符，必须显式补 \，否则拼成 Mosaiccs。
    Add-MosaicNode $mosaicFragment 'CustomAction' @{ Id = "SetDir$id"; Property = $id; Value = '[INSTALLFOLDER]\' + ($relative -replace '/', '\') } | Out-Null
}
$mosaicSequence = Add-MosaicNode $mosaicFragment 'InstallExecuteSequence'
$setDirSeq = 960
foreach ($relative in ($mosaicDirectories.Keys | Where-Object { $_ -ne '' } | Sort-Object)) {
    $id = $mosaicDirectories[$relative]
    # 必须在 CostFinalize(1000) 之前（ICE12），且在 EnforceInstallFolder(950) 之后。
    Add-MosaicNode $mosaicSequence 'Custom' @{ Action = "SetDir$id"; Sequence = $setDirSeq; Condition = 'NOT REMOVE' } | Out-Null
    $setDirSeq++
}
$mosaicXml.Save($payload)
# 原生自定义动作与清单必须与本次 Payload.wxs 从同一发布目录生成，才能保证三者一致。
& (Join-Path $mosaicRoot 'build/installer/actions/build-actions.ps1')
$mosaicActionsDll = Join-Path $mosaicRoot 'build/installer/actions/bin/MosaicInstallerActions.dll'
$mosaicManifest = Join-Path $mosaicPackageDir 'Manifest.tsv'
& python -I (Join-Path $mosaicRoot 'scripts/generate_installer_manifest.py') $mosaicPublish $mosaicManifest
if ($LASTEXITCODE) { throw 'Installer manifest generation failed.' }
Push-Location $mosaicRoot
try {
    $mosaicBuildArguments = @(
        'build', 'build/installer/Mosaic.Installer.wixproj', '-c', 'Release', '-m:1',
        "-p:ProductVersion=$ProductVersion",
        "-p:PayloadFile=$payload",
        "-p:ActionsDll=$mosaicActionsDll",
        "-p:ManifestFile=$mosaicManifest",
        "-p:OutputPath=$mosaicBuildOutput/"
    )
    if ($SkipValidation) { $mosaicBuildArguments += '-p:SuppressValidation=true' }
    dotnet @mosaicBuildArguments
    if ($LASTEXITCODE) { throw 'Installer build failed.' }
    # Preserve deliverables outside MSBuild's previous-output cleanup when testing upgrades.
    $mosaicPackage = Join-Path $mosaicPackageDir "Mosaic-$ProductVersion-win-x64.msi"
    Copy-Item -LiteralPath (Join-Path $mosaicBuildOutput "Mosaic-$ProductVersion-win-x64.msi") -Destination $mosaicPackage -Force
    & (Join-Path $PSScriptRoot 'test-package-shortcuts.ps1') -PackagePath $mosaicPackage
    & (Join-Path $PSScriptRoot 'test-package-maintenance.ps1') -PackagePath $mosaicPackage -ExpectedVersion $ProductVersion
} finally { Pop-Location }
