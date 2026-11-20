param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string]$ExpectedVersion = '0.17.4',
    [switch]$SkipPayloadHash
)
$ErrorActionPreference = 'Stop'
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path

# 使用 WiX SDK 自带的 DTF 包装库读取 MSI 数据库与二进制流；它随构建工具一起安装。
$sdkRoot = Join-Path $env:USERPROFILE '.nuget/packages/wixtoolset.sdk'
if (-not (Test-Path -LiteralPath $sdkRoot)) { throw "WiX SDK NuGet package not found: $sdkRoot" }
$dtfPath = Get-ChildItem -LiteralPath $sdkRoot -Directory | Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'tools/net472/WixToolset.Dtf.WindowsInstaller.dll' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $dtfPath) { throw 'WixToolset.Dtf.WindowsInstaller.dll not found in the WiX SDK package.' }
Add-Type -Path $dtfPath

function Fail([string]$Message) { throw "Static MSI check failed: $Message" }

function Read-MsiRows($Database, [string]$Sql, [int]$FieldCount) {
    $view = $Database.OpenView($Sql)
    try {
        $view.Execute()
        $rows = @()
        while ($true) {
            $record = $view.Fetch()
            if ($null -eq $record) { break }
            $values = @()
            for ($index = 1; $index -le $FieldCount; $index++) {
                $value = ''
                try { $value = $record.GetString($index) }
                catch { try { $value = [string]$record.GetInteger($index) } catch { $value = '' } }
                $values += $value
            }
            $rows += , $values
        }
        return ,$rows
    }
    finally { $view.Close() }
}

function Find-Row($Rows, [string]$Key, [string]$Message) {
    $found = @($Rows | Where-Object { $_[0] -ceq $Key })
    if ($found.Count -ne 1) { Fail "$Message (found $($found.Count) rows for '$Key')." }
    return $found[0]
}

$database = New-Object WixToolset.Dtf.WindowsInstaller.Database($resolvedPackage)
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('mosaic-msi-check-' + [guid]::NewGuid().ToString('N'))
try {
    # 产品标识与版本必须与发布版本一致。
    $properties = @{}
    $propertyRows = Read-MsiRows $database 'SELECT * FROM Property' 2
    foreach ($row in $propertyRows) { $properties[$row[0]] = $row[1] }
    if ($properties['ProductVersion'] -cne $ExpectedVersion) { Fail "ProductVersion is '$($properties['ProductVersion'])', expected '$ExpectedVersion'." }
    if ($properties['UpgradeCode'] -cne '{4D2B073C-72D5-4C3A-B845-65FC2C52D23C}') { Fail "UpgradeCode is '$($properties['UpgradeCode'])'." }
    if ($properties['ErrorDialog'] -cne 'FatalError') { Fail "ErrorDialog is '$($properties['ErrorDialog'])', expected 'FatalError'." }
    if ($properties['DefaultUIFont'] -cne 'MosaicFont') { Fail "DefaultUIFont is '$($properties['DefaultUIFont'])'." }

    # 扫描、路径校验与复检必须全部来自包内原生动作 DLL，不能依赖安装目录。
    $expectedActions = @(
        'DetectInstallState', 'ScanPayload', 'EnforceInstallFolder', 'ValidateInstallFolder',
        'RestoreDefaultInstallFolder', 'BrowseInstallFolder', 'VerifyRepairedPayload')
    $customActions = @{}
    $customActionRows = Read-MsiRows $database 'SELECT * FROM CustomAction' 4
    foreach ($row in $customActionRows) { $customActions[$row[0]] = $row }
    foreach ($name in $expectedActions) {
        if (-not $customActions.ContainsKey($name)) { Fail "CustomAction '$name' is missing." }
        $action = $customActions[$name]
        if ($action[1] -ne '1' -or $action[2] -cne 'MosaicActions' -or $action[3] -cne $name) {
            Fail "CustomAction '$name' must be type 1 from Binary 'MosaicActions' targeting '$name'."
        }
    }
    if (-not $customActions.ContainsKey('SetArpInstallLocation')) { Fail 'SetArpInstallLocation custom action is missing.' }
    $arpAction = $customActions['SetArpInstallLocation']
    if ($arpAction[1] -ne '51' -or $arpAction[2] -cne 'ARPINSTALLLOCATION' -or $arpAction[3] -cne '[INSTALLFOLDER]') {
        Fail 'SetArpInstallLocation must be a type 51 action setting ARPINSTALLLOCATION to [INSTALLFOLDER].'
    }
    foreach ($binary in @('MosaicActions', 'MosaicPayloadManifest')) {
        # 只查名称列：提前读取 Binary 的 Data 流会让后续 GetStream 得到空值。
        $binaryRows = Read-MsiRows $database "SELECT Name FROM Binary WHERE Name='$binary'" 1
        if ($binaryRows.Count -ne 1) { Fail "Binary '$binary' is missing." }
    }
    # 扫描/复检失败必须传播为真实失败，重启安排必须单独归类；这些是行为契约的源码级回归，
    # 防止又退回「只设置提示属性、/qn 仍返回 0」。
    $actionsSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer/actions/MosaicInstallerActions.cpp') -Raw
    foreach ($needle in @('INSTALLMESSAGE_ERROR', 'IsRebootPending', 'MSIRUNMODE_REBOOTATEND', 'MOSAICMAINTBUTTONTEXT', 'PendingReboot')) {
        if ($actionsSource -notlike "*$needle*") { Fail "Native actions must implement '$needle'." }
    }
    if ([regex]::Matches($actionsSource, 'return ERROR_INSTALL_FAILURE').Count -lt 2) {
        Fail 'ScanPayload and VerifyRepairedPayload must both return ERROR_INSTALL_FAILURE on failure.'
    }

    # 执行序列：状态检测早于成本计算，扫描在成本计算前设置 REINSTALL，复检在提交之后。
    $executeSequence = Read-MsiRows $database 'SELECT * FROM InstallExecuteSequence' 3
    $detect = Find-Row $executeSequence 'DetectInstallState' 'InstallExecuteSequence entry'
    if ($detect[2] -ne '26' -or $detect[1] -notlike '*NOT REMOVE*') { Fail "DetectInstallState must run at 26 with 'NOT REMOVE'." }
    $enforce = Find-Row $executeSequence 'EnforceInstallFolder' 'InstallExecuteSequence entry'
    if ($enforce[2] -ne '950' -or $enforce[1] -notlike '*NOT Installed*') { Fail 'EnforceInstallFolder must run at 950 only for new installs.' }
    $scan = Find-Row $executeSequence 'ScanPayload' 'InstallExecuteSequence entry'
    if ($scan[2] -ne '990' -or $scan[1] -notlike '*MOSAICSTATE*Maintenance*' -or $scan[1] -notlike '*NOT REMOVE*') {
        Fail 'ScanPayload must run at 990 for maintenance only and never on uninstall.'
    }
    if ($scan[1] -notlike '*NOT MOSAICSCANSTATUS*') {
        Fail 'Silent maintenance must scan in the execute sequence, while UI scans reuse their result.'
    }
    $verify = Find-Row $executeSequence 'VerifyRepairedPayload' 'InstallExecuteSequence entry'
    if ($verify[2] -ne '6700' -or $verify[1] -notlike '*REINSTALL*') { Fail 'VerifyRepairedPayload must run at 6700 after the transaction.' }
    $arp = Find-Row $executeSequence 'SetArpInstallLocation' 'InstallExecuteSequence entry'
    if ([int]$arp[2] -le 1000 -or $arp[1] -notlike '*MOSAICLOCATIONINHERITED*') {
        Fail 'SetArpInstallLocation must run after CostFinalize and also for confirmed inherited maintenance locations.'
    }

    # 界面序列：自动路由到首次安装、维护、升级或阻断页，完成页与错误页不可缺失。
    $uiSequence = Read-MsiRows $database 'SELECT * FROM InstallUISequence' 3
    $expectedUi = @{
        FatalError = -3; UserExit = -2; FindRelatedProducts = 25; DetectInstallState = 30
        FreshStartDlg = 1001; ScanPayload = 1002; MaintenanceDlg = 1003; UpgradeDlg = 1004
        BlockedDlg = 1005; UnknownDlg = 1006; ProgressDlg = 1290
        VerifyRepairedPayload = 1301; Exit = 1303
    }
    foreach ($name in $expectedUi.Keys) {
        $sequence = $expectedUi[$name]
        if (@($uiSequence | Where-Object { $_[0] -ceq $name -and [int]$_[2] -eq $sequence }).Count -eq 0) {
            Fail "InstallUISequence must contain '$name' at $sequence."
        }
    }
    # 再次双击同版本 MSI 必须先自动扫描；健康结果才显示维护页，且显式 /f 不被健康页截断。
    $uiScan = Find-Row $uiSequence 'ScanPayload' 'InstallUISequence entry'
    if ($uiScan[1] -notlike '*MOSAICSTATE*Maintenance*' -or $uiScan[1] -notlike '*NOT REMOVE*') {
        Fail 'InstallUISequence ScanPayload must run automatically for maintenance only.'
    }
    $uiMaintenanceShow = @($uiSequence | Where-Object { $_[0] -ceq 'MaintenanceDlg' })
    if ($uiMaintenanceShow.Count -ne 1 -or $uiMaintenanceShow[0][1] -notlike '*MOSAICSCANSTATUS*Healthy*' -or $uiMaintenanceShow[0][1] -notlike '*NOT REINSTALL*') {
        Fail 'MaintenanceDlg must only be shown for a healthy scan without an explicit /f repair.'
    }
    $adminUi = Read-MsiRows $database 'SELECT * FROM AdminUISequence' 3
    if (@($adminUi | Where-Object { $_[0] -ceq 'Exit' -and [int]$_[2] -eq 1301 }).Count -eq 0) {
        Fail "AdminUISequence must show 'Exit' at 1301."
    }

    # 错误对话框必须被 MSI 识别为 ErrorDialog，且首个可聚焦控件是 ErrorText。
    $dialogs = Read-MsiRows $database 'SELECT * FROM Dialog' 10
    $fatal = Find-Row $dialogs 'FatalError' 'Dialog entry'
    if (([int]$fatal[5] -band 0x10000) -eq 0 -or $fatal[7] -cne 'ErrorText') {
        Fail 'FatalError must be the ErrorDialog and must focus ErrorText first.'
    }
    $filesInUse = Find-Row $dialogs 'FilesInUse' 'Dialog entry'
    if ($filesInUse[7] -cne 'FileList') { Fail 'FilesInUse must focus its process list first.' }
    $exit = Find-Row $dialogs 'Exit' 'Dialog entry'
    if ($exit[7] -cne 'Finish') { Fail 'Exit must focus its Finish button first.' }

    # 标准错误按钮与运行时填充的文件占用列表。
    $controls = Read-MsiRows $database 'SELECT * FROM Control' 12
    foreach ($button in @('ErrorYes', 'ErrorNo', 'ErrorOk', 'ErrorCancel', 'ErrorRetry', 'ErrorIgnore', 'ErrorAbort')) {
        if (@($controls | Where-Object { $_[0] -ceq 'FatalError' -and $_[1] -ceq $button -and $_[2] -ceq 'PushButton' }).Count -eq 0) {
            Fail "FatalError is missing standard button '$button'."
        }
    }
    $listBox = @($controls | Where-Object { $_[0] -ceq 'FilesInUse' -and $_[2] -ceq 'ListBox' })
    if ($listBox.Count -ne 1 -or $listBox[0][8] -cne 'FileInUseProcess') { Fail 'FilesInUse must contain a ListBox bound to FileInUseProcess.' }

    # 安装位置控件与按钮事件：浏览、恢复默认、校验后再确认。
    $pathEdit = @($controls | Where-Object { $_[0] -ceq 'LocationDlg' -and $_[2] -ceq 'PathEdit' })
    if ($pathEdit.Count -ne 1 -or $pathEdit[0][8] -cne 'MOSAICINSTALLPATH') { Fail 'LocationDlg must edit MOSAICINSTALLPATH.' }
    $events = Read-MsiRows $database 'SELECT * FROM ControlEvent' 5
    if (@($events | Where-Object { $_[0] -ceq 'LocationDlg' -and $_[2] -ceq 'DoAction' -and $_[3] -ceq 'ValidateInstallFolder' }).Count -eq 0) {
        Fail 'LocationDlg must validate the path before continuing.'
    }
    if (@($events | Where-Object { $_[0] -ceq 'LocationDlg' -and $_[2] -ceq 'NewDialog' -and $_[3] -ceq 'ConfirmDlg' }).Count -eq 0) {
        Fail 'LocationDlg must continue to ConfirmDlg.'
    }
    if (@($dialogs | Where-Object { $_[0] -ceq 'MaintStartDlg' }).Count -ne 0) {
        Fail 'Maintenance must not wait behind an extra MaintStartDlg page.'
    }
    # 健康维护以 Exit 结束，绝不进入 ExecuteAction 写文件；异常或显式 /f 才 Return 继续修复。
    $healthyExit = @($events | Where-Object { $_[0] -ceq 'MaintenanceDlg' -and $_[1] -ceq 'Next' -and $_[2] -ceq 'EndDialog' -and $_[3] -ceq 'Exit' })
    if ($healthyExit.Count -ne 1 -or $healthyExit[0][4] -notlike '*MOSAICSCANSTATUS*Healthy*' -or $healthyExit[0][4] -notlike '*NOT REINSTALL*') {
        Fail 'Healthy maintenance must end the UI before the file-writing execution phase.'
    }
    $repairContinue = @($events | Where-Object { $_[0] -ceq 'MaintenanceDlg' -and $_[1] -ceq 'Next' -and $_[2] -ceq 'EndDialog' -and $_[3] -ceq 'Return' })
    if ($repairContinue.Count -ne 1 -or $repairContinue[0][4] -notlike '*REINSTALL*') {
        Fail 'Maintenance must continue to MSI repair when files are missing/damaged or /f was explicit.'
    }
    if (@($events | Where-Object { $_[0] -ceq 'MaintenanceDlg' -and $_[2] -ceq 'DoAction' -and $_[3] -ceq 'ScanPayload' }).Count -eq 0) {
        Fail 'MaintenanceDlg must allow re-scanning without leaving the page.'
    }
    $scanSummary = @($controls | Where-Object { $_[0] -ceq 'ProgressDlg' -and $_[1] -ceq 'ScanSummary' -and $_[9] -like '*MOSAICSCANSUMMARY*' })
    if ($scanSummary.Count -ne 1) { Fail 'Automatic repair must show the scan summary on the progress page.' }
    $exitTitle = @($controls | Where-Object { $_[0] -ceq 'Exit' -and $_[1] -ceq 'Title' -and $_[9] -like '*MOSAICEXITTITLE*' })
    if ($exitTitle.Count -ne 1) { Fail 'Exit must show a result title that distinguishes success from reboot-pending.' }
    $styles = Read-MsiRows $database 'SELECT * FROM TextStyle' 5
    if (@($styles | Where-Object { $_[0] -ceq 'MosaicFont' }).Count -eq 0) { Fail 'TextStyle MosaicFont is missing.' }

    # 阻断条件与安装位置登记。
    $launch = Read-MsiRows $database 'SELECT * FROM LaunchCondition' 2
    if (@($launch | Where-Object { $_[0] -ceq 'NOT MOSAICBLOCKED' }).Count -eq 0) { Fail 'LaunchCondition must stop identity conflicts.' }
    $registry = Read-MsiRows $database 'SELECT * FROM Registry' 6
    $installLocation = @($registry | Where-Object { $_[2] -ceq 'Software\Mosaic\Installer' -and $_[3] -ceq 'InstallLocation' })
    if ($installLocation.Count -ne 1 -or $installLocation[0][4] -cne '[INSTALLFOLDER]' -or $installLocation[0][5] -cne 'InstallPathMetadata') {
        Fail 'InstallPathMetadata must record InstallLocation under HKCU Software\Mosaic\Installer.'
    }

    # 载荷清单：以包内清单为准，逐项与 File 表核对；同名大小篡改也会被 SHA-256 捕获。
    $manifestView = $database.OpenView("SELECT Data FROM Binary WHERE Name='MosaicPayloadManifest'")
    $manifestPath = Join-Path $temporaryRoot 'Manifest.tsv'
    try {
        $manifestView.Execute()
        $record = $manifestView.Fetch()
        if ($null -eq $record) { Fail 'Payload manifest stream is empty.' }
        New-Item -ItemType Directory -Force $temporaryRoot | Out-Null
        $stream = $record.GetStream(1)
        if ($null -eq $stream) { Fail 'Payload manifest stream could not be opened.' }
        $fileStream = [IO.File]::Create($manifestPath)
        try { $stream.CopyTo($fileStream) } finally { $fileStream.Close(); $stream.Dispose() }
    }
    finally { $manifestView.Close() }
    $manifestLines = [IO.File]::ReadAllLines($manifestPath)
    if ($manifestLines.Count -lt 2 -or $manifestLines[0] -cne 'mosaic-manifest-v1') { Fail 'Payload manifest header is invalid.' }
    $fileRows = @{}
    $fileView = $database.OpenView('SELECT File, FileSize, Component_ FROM File')
    try {
        $fileView.Execute()
        while ($true) {
            $record = $fileView.Fetch()
            if ($null -eq $record) { break }
            $fileRows[$record.GetString(1)] = @{ Size = [int64]$record.GetInteger(2); Component = $record.GetString(3) }
        }
    }
    finally { $fileView.Close() }
    $manifestFiles = @()
    $manifestShortcuts = @()
    foreach ($line in $manifestLines) {
        if ($line -like 'file*') {
            $parts = $line -split "`t"
            if ($parts.Count -ne 6) { Fail "Malformed manifest file row: $line" }
            $manifestFiles += , $parts
            if (-not $fileRows.ContainsKey($parts[5])) { Fail "Manifest file '$($parts[1])' is not in the File table." }
            if ([int64]$parts[2] -ne $fileRows[$parts[5]].Size) { Fail "Manifest size mismatch for '$($parts[1])'." }
            if ($parts[4] -cne $fileRows[$parts[5]].Component) { Fail "Manifest component mismatch for '$($parts[1])'." }
        }
        elseif ($line -like 'shortcut*') {
            $parts = $line -split "`t"
            if ($parts.Count -ne 4) { Fail "Malformed manifest shortcut row: $line" }
            $manifestShortcuts += , $parts
        }
    }
    if ($manifestFiles.Count -ne $fileRows.Count) { Fail "Manifest lists $($manifestFiles.Count) files but the File table has $($fileRows.Count)." }
    if ($manifestShortcuts.Count -ne 2) { Fail "Manifest must describe the desktop and start menu shortcuts (found $($manifestShortcuts.Count))." }
    $shortcutRows = Read-MsiRows $database 'SELECT * FROM Shortcut' 6
    foreach ($expected in @(@('desktop', 'DesktopFolder'), @('programmenu', 'MosaicMenu'))) {
        $manifestShortcut = @($manifestShortcuts | Where-Object { $_[1] -ceq $expected[0] })
        if ($manifestShortcut.Count -ne 1) { Fail "Manifest is missing the '$($expected[0])' shortcut row." }
        $linkName = [IO.Path]::GetFileNameWithoutExtension($manifestShortcut[0][2])
        $row = @($shortcutRows | Where-Object { $_[1] -ceq $expected[1] -and $_[2] -ceq $linkName })
        if ($row.Count -ne 1 -or $row[0][4] -cne '[INSTALLFOLDER]Mosaic.Desktop.exe') {
            Fail "Shortcut '$($expected[0])' must target [INSTALLFOLDER]Mosaic.Desktop.exe."
        }
    }

    # 媒体与载荷桶：单个内嵌 CAB，顺序覆盖全部文件。
    $media = Read-MsiRows $database 'SELECT * FROM Media' 4
    if ($media.Count -ne 1 -or [int]$media[0][1] -ne $fileRows.Count) { Fail "Media table must contain one cabinet covering all $($fileRows.Count) files." }
    $streams = Read-MsiRows $database 'SELECT Name FROM _Streams' 1
    if (@($streams | Where-Object { $_[0] -like '*.cab' }).Count -eq 0) { Fail 'Embedded cabinet stream is missing.' }

    if (-not $SkipPayloadHash) {
        # 逐文件解包并复核 SHA-256，证明修复能从包内恢复与清单完全一致的载荷。
        $cabPath = Join-Path $temporaryRoot 'payload.cab'
        $cabView = $database.OpenView("SELECT Data FROM _Streams WHERE Name='cab1.cab'")
        try {
            $cabView.Execute()
            $record = $cabView.Fetch()
            if ($null -eq $record) { Fail 'cab1.cab stream is empty.' }
            $stream = $record.GetStream(1)
            $fileStream = [IO.File]::Create($cabPath)
            try { $stream.CopyTo($fileStream) } finally { $fileStream.Close(); $stream.Dispose() }
        }
        finally { $cabView.Close() }
        $payloadRoot = Join-Path $temporaryRoot 'payload'
        New-Item -ItemType Directory -Force $payloadRoot | Out-Null
        & (Join-Path $env:SystemRoot 'System32/expand.exe') $cabPath -F:* $payloadRoot | Out-Null
        $hashed = 0
        foreach ($parts in $manifestFiles) {
            $member = Join-Path $payloadRoot $parts[5]
            if (-not (Test-Path -LiteralPath $member)) { Fail "Cabinet member for '$($parts[1])' is missing." }
            $hash = (Get-FileHash -LiteralPath $member -Algorithm SHA256).Hash
            if ($hash -cne $parts[3]) { Fail "Cabinet payload hash mismatch for '$($parts[1])'." }
            $hashed++
        }
        Write-Output "Checked $hashed cabinet members against manifest SHA-256."
    }

    Write-Output "PASS: MSI static structure, maintenance routing, dialogs, payload manifest and cabinet integrity verified for $ExpectedVersion."
}
finally {
    $database.Dispose()
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}