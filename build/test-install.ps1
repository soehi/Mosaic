param([string]$BaseVersion = '0.1.0', [string]$UpgradeVersion = '0.1.1', [switch]$UseExistingTestInstallation)
$ErrorActionPreference = 'Stop'
$mosaicRoot = Split-Path $PSScriptRoot -Parent
$mosaicPackages = Join-Path $mosaicRoot 'artifacts/installer'
$mosaicBase = Join-Path $mosaicPackages "Mosaic-$BaseVersion-win-x64.msi"
$mosaicUpgrade = Join-Path $mosaicPackages "Mosaic-$UpgradeVersion-win-x64.msi"
if (-not (Test-Path $mosaicBase) -or -not (Test-Path $mosaicUpgrade)) { throw 'Build both test MSI versions before running this check.' }
$mosaicRegistryPaths = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
$mosaicExisting = $mosaicRegistryPaths | ForEach-Object { Get-ItemProperty $_ -ErrorAction SilentlyContinue } | Where-Object DisplayName -eq 'Mosaic'
if ($mosaicExisting -and (-not $UseExistingTestInstallation -or $mosaicExisting.DisplayVersion -ne $BaseVersion)) { throw 'An existing Mosaic installation must be left untouched. Use a clean test user.' }
function Invoke-MosaicInstaller([string]$Action, [string]$Package, [string]$LogName) {
    $arguments = @($Action, ('"{0}"' -f $Package), '/qn', '/norestart', '/l*v', ('"{0}"' -f (Join-Path $mosaicPackages $LogName)))
    $process = Start-Process msiexec.exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { throw 'Windows Installer did not finish within 60 seconds. Inspect the installer log.' }
    if ($process.ExitCode -notin @(0, 3010)) { throw "Windows Installer failed: $($process.ExitCode)" }
}
function Assert-MosaicDesktopShortcut([string]$ExpectedExecutable) {
    $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    $shortcutPath = Join-Path $desktop 'Mosaic.lnk'
    if (-not (Test-Path -LiteralPath $shortcutPath)) { throw "Desktop shortcut missing: $shortcutPath" }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $actual = [IO.Path]::GetFullPath($shortcut.TargetPath)
    $expected = [IO.Path]::GetFullPath($ExpectedExecutable)
    if ($actual -ne $expected) { throw "Desktop shortcut target mismatch: $actual" }
    $actualWorkingDirectory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($shortcut.WorkingDirectory))
    $expectedWorkingDirectory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath((Split-Path $expected -Parent)))
    if ($actualWorkingDirectory -ne $expectedWorkingDirectory) {
        throw "Desktop shortcut working directory mismatch: $($shortcut.WorkingDirectory)"
    }
}
function Test-MosaicPackageDesktopShortcut([string]$Package) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($Package, 0)
    $view = $database.OpenView("SELECT Shortcut FROM Shortcut WHERE Directory_ = 'DesktopFolder'")
    try {
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) { return $false }
        return $true
    }
    finally {
        [void]$view.Close()
    }
}
$mosaicUserData = Join-Path $env:LOCALAPPDATA 'Mosaic'
$mosaicCreatedDataRoot = -not (Test-Path -LiteralPath $mosaicUserData)
New-Item -ItemType Directory -Force $mosaicUserData | Out-Null
$mosaicSentinel = Join-Path $mosaicUserData ('installer-preservation-' + [guid]::NewGuid().ToString('N') + '.txt')
Set-Content -LiteralPath $mosaicSentinel -Value 'Mosaic installation preservation test'
$mosaicBefore = (Get-FileHash -LiteralPath $mosaicSentinel).Hash
$mosaicInstalled = [bool]$mosaicExisting
try {
    if (-not $mosaicInstalled) {
        Invoke-MosaicInstaller '/i' $mosaicBase 'install-test.log'; $mosaicInstalled = $true
        if (Test-MosaicPackageDesktopShortcut $mosaicBase) {
            Assert-MosaicDesktopShortcut (Join-Path $env:LOCALAPPDATA 'Programs/Mosaic/Mosaic.Desktop.exe')
        }
    }
    $mosaicPreviousExe = $env:MOSAIC_SMOKE_EXE
    try {
        $env:MOSAIC_SMOKE_EXE = Join-Path $env:LOCALAPPDATA 'Programs/Mosaic/Mosaic.Desktop.exe'
        dotnet (Join-Path $mosaicRoot 'tests/Mosaic.Desktop.SmokeTests/bin/Release/net10.0-windows/Mosaic.Desktop.SmokeTests.dll') -method- '*Floating_real_pointer_moves_resizes_clicks_and_restores_saved_bounds'
        if ($LASTEXITCODE) { throw 'Installed application smoke tests failed.' }
        Invoke-MosaicInstaller '/i' $mosaicUpgrade 'upgrade-test.log'
        Assert-MosaicDesktopShortcut (Join-Path $env:LOCALAPPDATA 'Programs/Mosaic/Mosaic.Desktop.exe')
        if ((Get-FileHash -LiteralPath $mosaicSentinel).Hash -ne $mosaicBefore) { throw 'Upgrade modified user data.' }
        dotnet (Join-Path $mosaicRoot 'tests/Mosaic.Desktop.SmokeTests/bin/Release/net10.0-windows/Mosaic.Desktop.SmokeTests.dll') -method- '*Floating_real_pointer_moves_resizes_clicks_and_restores_saved_bounds'
        if ($LASTEXITCODE) { throw 'Upgraded application smoke tests failed.' }
    } finally { $env:MOSAIC_SMOKE_EXE = $mosaicPreviousExe }
    Invoke-MosaicInstaller '/x' $mosaicUpgrade 'uninstall-test.log'; $mosaicInstalled = $false
    if ((Get-FileHash -LiteralPath $mosaicSentinel).Hash -ne $mosaicBefore) { throw 'Uninstall modified user data.' }
    if (Test-Path (Join-Path $env:LOCALAPPDATA 'Programs/Mosaic/Mosaic.Desktop.exe')) { throw 'Uninstall left installed binaries.' }
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) 'Mosaic.lnk'
    if (Test-Path -LiteralPath $desktopShortcut) { throw 'Uninstall left the desktop shortcut.' }
    Write-Output 'PASS: per-user install, desktop shortcut, upgrade, two desktop smoke runs, uninstall, and user data preservation.'
} finally {
    if ($mosaicInstalled) { Write-Warning 'Test installation remains present; inspect installer logs before removing it.' }
    Remove-Item -LiteralPath $mosaicSentinel
    if ($mosaicCreatedDataRoot -and @(Get-ChildItem -LiteralPath $mosaicUserData -Force).Count -eq 0) { Remove-Item -LiteralPath $mosaicUserData }
}
