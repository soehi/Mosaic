param([string]$Configuration = 'Release')
# 构建安装器自定义动作 DLL。产物只依赖 Windows 系统库（MSI、BCrypt、Shell），
# 不依赖 .NET、VC 运行库或安装目录中的任何文件，因此安装目录被删除后仍可执行扫描。
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'MosaicInstallerActions.cpp'
$outDir = Join-Path $PSScriptRoot 'bin'
$output = Join-Path $outDir 'MosaicInstallerActions.dll'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw "vswhere.exe not found: $vswhere" }
$installationPath = & $vswhere -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installationPath) { throw 'No Visual Studio C++ toolset found.' }
$vcvars = Join-Path $installationPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path -LiteralPath $vcvars)) { throw "vcvars64.bat not found: $vcvars" }
New-Item -ItemType Directory -Force $outDir | Out-Null
$flags = if ($Configuration -eq 'Debug') { '/Od /Zi' } else { '/O2 /DNDEBUG' }
$object = Join-Path $outDir 'MosaicInstallerActions.obj'
$command = 'call "{0}" >nul && cl /nologo {1} /MT /EHsc /std:c++17 /utf-8 /DUNICODE /D_UNICODE /LD "{2}" /Fo"{3}" /Fe"{4}" /link /MACHINE:X64 msi.lib bcrypt.lib ole32.lib shell32.lib shlwapi.lib version.lib user32.lib advapi32.lib' -f $vcvars, $flags, $source, $object, $output
& cmd.exe /c $command
if ($LASTEXITCODE) { throw 'Installer action build failed.' }
if (-not (Test-Path -LiteralPath $output)) { throw 'Installer action DLL was not produced.' }
Write-Output "Built $output"