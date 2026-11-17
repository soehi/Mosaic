param([Parameter(Mandatory = $true)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($resolvedPackage, 0)

function Read-Shortcuts {
    $view = $database.OpenView("SELECT Shortcut,Directory_,Name,Component_,Target,WkDir FROM Shortcut")
    try {
        $view.Execute()
        while ($record = $view.Fetch()) {
            [pscustomobject]@{
                Shortcut = $record.StringData(1)
                Directory = $record.StringData(2)
                Name = $record.StringData(3)
                Component = $record.StringData(4)
                Target = $record.StringData(5)
                WorkingDirectory = $record.StringData(6)
            }
        }
    }
    finally {
        $view.Close()
    }
}

function Read-FeatureComponents {
    $view = $database.OpenView("SELECT Feature_,Component_ FROM FeatureComponents")
    try {
        $view.Execute()
        while ($record = $view.Fetch()) {
            [pscustomobject]@{ Feature = $record.StringData(1); Component = $record.StringData(2) }
        }
    }
    finally {
        $view.Close()
    }
}

$shortcuts = @(Read-Shortcuts)
$desktop = @($shortcuts | Where-Object { $_.Directory -eq 'DesktopFolder' })
if ($desktop.Count -ne 1) { throw "Expected one DesktopFolder shortcut, found $($desktop.Count)." }
if ($desktop[0].Name -notmatch '(?i)^Mosaic(?:\.lnk)?$') { throw "Unexpected desktop shortcut name: $($desktop[0].Name)" }
if ($desktop[0].Target -notmatch '(?i)MOSAIC(?:~1)?\.EXE|Mosaic\.Desktop\.exe') { throw "Unexpected desktop shortcut target: $($desktop[0].Target)" }
if ($desktop[0].WorkingDirectory -ne 'INSTALLFOLDER') { throw "Unexpected desktop shortcut working directory: $($desktop[0].WorkingDirectory)" }

$features = @(Read-FeatureComponents)
$owned = @($features | Where-Object { $_.Component -eq $desktop[0].Component -and $_.Feature -eq 'Application' })
if ($owned.Count -ne 1) { throw 'Desktop shortcut component is not owned by the Application feature.' }
Write-Output 'PASS: MSI contains the Application-owned DesktopFolder Mosaic shortcut and INSTALLFOLDER working directory.'
