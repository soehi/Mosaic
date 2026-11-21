param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string]$ExpectedVersion = '0.17.5',
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
    # 不设置 ErrorDialog：引擎错误由系统消息框显示真实错误码；FatalError 只作收尾页面。
    # 指定 ErrorDialog 时 MSI 只创建当前消息需要的 Error 按钮，ErrorText 的
    # Control_Next 指向未创建的 ErrorYes，真实错误会被 2814 掩盖。
    if ($properties.ContainsKey('ErrorDialog')) { Fail "ErrorDialog must not be set (found '$($properties['ErrorDialog'])')." }
    if ($properties['DefaultUIFont'] -cne 'MosaicFont') { Fail "DefaultUIFont is '$($properties['DefaultUIFont'])'." }

    # 扫描、路径校验与复检必须全部来自包内原生动作 DLL，不能依赖安装目录。
    $expectedActions = @(
        'DetectInstallState', 'ScanPayload', 'EnforceInstallFolder', 'ValidateInstallFolder',
        'RestoreDefaultInstallFolder', 'BrowseInstallFolder', 'PrefillInstallPath',
        'VerifyRepairedPayload')
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
    # 卸载必须恢复产品登记的位置，否则自定义安装目录的文件不会被移除（0.17.4 实机缺陷）。
    if ($actionsSource -notlike '*RestoreInstallFolderForRemoval*' -or $actionsSource -notlike '*INSTALLPROPERTY_INSTALLLOCATION*') {
        Fail 'Uninstall must restore INSTALLFOLDER from the registered product location.'
    }
    # 维护自动修复不支持在线执行：任何文件替换都会让引擎在目标卷根目录的 Config.Msi
    # 建 .rbf 回滚备份（仅 SYSTEM/Admin 可写），per-user 安装位于非系统驱动器时用户
    # 上下文无法删除备份，报错误 5 并弹出引擎强制错误框。用户须通过「应用和功能」
    # 修复或重新运行安装包；包不得自动触发 REINSTALL/REMOVE 重写。
    if ($actionsSource -like '*SetProperty(hInstall, L"REINSTALL", L"ALL");*' -or
        $actionsSource -like '*SetProperty(hInstall, L"REMOVE", L"ALL");*') {
        Fail 'Maintenance must not auto-repair via REINSTALL/REMOVE; guide users to reinstall instead.'
    }
    if ($actionsSource -like '*REINSTALLMODE", L"amus"*' -or $actionsSource -like '*REINSTALLMODE", L"emus"*') {
        Fail 'Maintenance must not rewrite files via REINSTALLMODE.'
    }
    # 扫描动作借用 Binary 表读取清单，必须关闭 MsiGetActiveDatabase 返回的数据库句柄。
    if ($actionsSource -notlike '*MsiCloseHandle(database)*') {
        Fail 'ReadBinaryStream must close the active database handle.'
    }

    # 执行序列：状态检测早于成本计算，扫描在成本计算前运行，复检在提交之后。
    # 维护自动修复通过 ApplyRepairRequest 设置 REMOVE=ALL（卸载后重装），其登
    # 记位置在 DetectInstallState 恢复；REINSTALL 重写会触发 Config.Msi 权限问题。
    $executeSequence = Read-MsiRows $database 'SELECT * FROM InstallExecuteSequence' 3
    $detect = Find-Row $executeSequence 'DetectInstallState' 'InstallExecuteSequence entry'
    if ($detect[2] -ne '26' -or $detect[1] -notlike '*NOT REMOVE*') { Fail "DetectInstallState must run at 26 with 'NOT REMOVE'." }
    $restore = Find-Row $executeSequence 'RestoreInstallFolderForRemoval' 'InstallExecuteSequence entry'
    if ($restore[2] -ne '27' -or $restore[1] -notlike '*REMOVE*') {
        Fail 'Uninstall must restore the registered install folder at 27 before CostFinalize.'
    }
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
    # 复检只在实际修复后运行：MOSAICREPAIRNEEDED 不能单独触发（维护检测到缺失/
    # 损坏但用户未点修复时，复检会误报「修复后复检仍未通过」）。
    if ($verify[2] -ne '6700' -or $verify[1] -notlike '*NOT REMOVE AND REINSTALL*' -or $verify[1] -like '*MOSAICREPAIRNEEDED*') {
        Fail 'VerifyRepairedPayload must run at 6700 after the transaction and only when REINSTALL is set.'
    }
    $arp = Find-Row $executeSequence 'SetArpInstallLocation' 'InstallExecuteSequence entry'
    if ([int]$arp[2] -le 1000 -or $arp[1] -notlike '*MOSAICLOCATIONINHERITED*') {
        Fail 'SetArpInstallLocation must run after CostFinalize and also for confirmed inherited maintenance locations.'
    }
    # 维护自动修复不支持在线执行：不得自动设置 REINSTALL/REMOVE/REINSTALLMODE。
    $badRepair = @($executeSequence | Where-Object { $_[0] -ceq 'ApplyRepairRequest' })
    if ($badRepair.Count -ne 0) { Fail 'ApplyRepairRequest must not run automatically; users reinstall to repair.' }

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

    # FatalError 只是安装失败后的收尾页，首个可聚焦控件是关闭按钮。
    $dialogs = Read-MsiRows $database 'SELECT * FROM Dialog' 10
    $fatal = Find-Row $dialogs 'FatalError' 'Dialog entry'
    if ($fatal[7] -cne 'Close') {
        Fail 'FatalError must focus its Close button first.'
    }
    $filesInUse = Find-Row $dialogs 'FilesInUse' 'Dialog entry'
    if ($filesInUse[7] -cne 'FileList') { Fail 'FilesInUse must focus its process list first.' }
    $exit = Find-Row $dialogs 'Exit' 'Dialog entry'
    if ($exit[7] -cne 'Finish') { Fail 'Exit must focus its Finish button first.' }

    # FatalError 是普通收尾页，不得带错误对话框样式（否则可能被当作 ErrorDialog 校验）。
    $controls = Read-MsiRows $database 'SELECT * FROM Control' 12
    if (([int]$fatal[5] -band 0x10000) -ne 0) {
        Fail 'FatalError must not carry the error-dialog style bit.'
    }
    if (@($controls | Where-Object { $_[0] -ceq 'FatalError' -and $_[1] -ceq 'Close' -and $_[2] -ceq 'PushButton' }).Count -eq 0) {
        Fail 'FatalError must offer a Close button.'
    }
    $listBox = @($controls | Where-Object { $_[0] -ceq 'FilesInUse' -and $_[2] -ceq 'ListBox' })
    if ($listBox.Count -ne 1 -or $listBox[0][8] -cne 'FileInUseProcess') { Fail 'FilesInUse must contain a ListBox bound to FileInUseProcess.' }

    # 安装位置控件与按钮事件：浏览、恢复默认、校验后再确认。
    $pathEdit = @($controls | Where-Object { $_[0] -ceq 'LocationDlg' -and $_[2] -ceq 'PathEdit' })
    if ($pathEdit.Count -ne 1 -or $pathEdit[0][8] -cne 'MOSAICINSTALLPATH') { Fail 'LocationDlg must edit MOSAICINSTALLPATH.' }
    $events = Read-MsiRows $database 'SELECT * FROM ControlEvent' 6
    if (@($events | Where-Object { $_[0] -ceq 'LocationDlg' -and $_[2] -ceq 'DoAction' -and $_[3] -ceq 'ValidateInstallFolder' }).Count -eq 0) {
        Fail 'LocationDlg must validate the path before continuing.'
    }
    if (@($events | Where-Object { $_[0] -ceq 'LocationDlg' -and $_[2] -ceq 'NewDialog' -and $_[3] -ceq 'ConfirmDlg' }).Count -eq 0) {
        Fail 'LocationDlg must continue to ConfirmDlg.'
    }
    # 2856 回归：Browse/Restore 不得用 NewDialog 回到 LocationDlg（PathEdit 属性一旦有
    # 变更通知，MSI 视对话框为仍被引用并拒绝销毁，NewDialog 回自身报 2856 并中断安装），
    # 必须改用 SetProperty 控制事件把新值推回 PathEdit。生成的 ControlEvent 行 Event
    # 列填 "[MOSAICINSTALLPATH]"（SetProperty 控制事件以 Event 列的 [属性名] 表示，
    # 并非空字符串），Argument 为 "[MOSAICINSTALLPATH]"。
    foreach ($button in 'Browse', 'Restore') {
        if (@($events | Where-Object { $_[0] -ceq 'LocationDlg' -and $_[1] -ceq $button -and $_[2] -ceq 'NewDialog' -and $_[3] -ceq 'LocationDlg' }).Count -ne 0) {
            Fail "LocationDlg $button must not NewDialog back to LocationDlg (causes error 2856); use a SetProperty event instead."
        }
        if (@($events | Where-Object { $_[0] -ceq 'LocationDlg' -and $_[1] -ceq $button -and $_[2] -ceq '[MOSAICINSTALLPATH]' -and $_[3] -ceq '[MOSAICINSTALLPATH]' }).Count -eq 0) {
            Fail "LocationDlg $button must publish SetProperty MOSAICINSTALLPATH (Event column '[MOSAICINSTALLPATH]') to refresh the PathEdit."
        }
    }
    if (@($dialogs | Where-Object { $_[0] -ceq 'MaintStartDlg' }).Count -ne 0) {
        Fail 'Maintenance must not wait behind an extra MaintStartDlg page.'
    }
    # 健康维护在扫描健康时提前结束 UI（不进入写文件的执行阶段）；结束动作必须是
    # EndDialog Return，Exit 参数代表用户取消（1602），会让 msiexec 误报取消。
    $healthyExit = @($events | Where-Object { $_[0] -ceq 'MaintenanceDlg' -and $_[1] -ceq 'Next' -and $_[2] -ceq 'EndDialog' -and $_[3] -ceq 'Return' -and $_[4] -like '*MOSAICSCANSTATUS*Healthy*' -and $_[4] -like '*NOT REINSTALL*' })
    if ($healthyExit.Count -ne 1) {
        Fail 'Healthy maintenance must end the UI with EndDialog Return before the file-writing execution phase.'
    }
    # 第二个 Return 事件的条件是 MOSAICSCANSTATUS <> "Healthy" OR REINSTALL（区分健康
    # 提前结束：健康条件的 NOT REINSTALL 同样包含子串 REINSTALL，需用 <> 锚定）。
    $repairContinue = @($events | Where-Object { $_[0] -ceq 'MaintenanceDlg' -and $_[1] -ceq 'Next' -and $_[2] -ceq 'EndDialog' -and $_[3] -ceq 'Return' -and $_[4] -like '*Healthy*" OR REINSTALL*' })
    if ($repairContinue.Count -ne 1) {
        Fail 'Maintenance must continue to MSI repair when files are missing/damaged or /f was explicit.'
    }
    if ($healthyExit[0][4] -ceq $repairContinue[0][4]) {
        Fail 'Healthy early-exit and repair continuation must use distinct conditions.'
    }
    if (@($events | Where-Object { $_[0] -ceq 'MaintenanceDlg' -and $_[2] -ceq 'DoAction' -and $_[3] -ceq 'ScanPayload' }).Count -eq 0) {
        Fail 'MaintenanceDlg must allow re-scanning without leaving the page.'
    }
    $scanSummary = @($controls | Where-Object { $_[0] -ceq 'ProgressDlg' -and $_[1] -ceq 'ScanSummary' -and $_[9] -like '*MOSAICSCANSUMMARY*' })
    if ($scanSummary.Count -ne 1) { Fail 'Automatic repair must show the scan summary on the progress page.' }

    # 进度页必须非模态且可见：模态页会阻塞 UI 序列，ExecuteAction 永远不被执行，安装停在空白进度页。
    $progress = Find-Row $dialogs 'ProgressDlg' 'Dialog entry'
    $progressAttributes = [int]$progress[5]
    if (($progressAttributes -band 2) -ne 0) {
        Fail 'ProgressDlg must be modeless (Dialog.Attributes must not contain msidbDialogAttributesModal=2); a modal progress page blocks ExecuteAction forever.'
    }
    if (($progressAttributes -band 1) -eq 0) {
        Fail 'ProgressDlg must remain visible (msidbDialogAttributesVisible=1).'
    }
    $executeAction = @($uiSequence | Where-Object { $_[0] -ceq 'ExecuteAction' })
    $progressShow = @($uiSequence | Where-Object { $_[0] -ceq 'ProgressDlg' })
    if ($executeAction.Count -ne 1 -or $progressShow.Count -ne 1 -or [int]$progressShow[0][2] -ge [int]$executeAction[0][2]) {
        Fail 'InstallUISequence must show ProgressDlg before an existing ExecuteAction.'
    }
    # 引擎事件订阅：ActionText 更新当前操作文字，SetProgress 驱动进度条。
    $actionTextControl = @($controls | Where-Object { $_[0] -ceq 'ProgressDlg' -and $_[1] -ceq 'ActionText' })
    if ($actionTextControl.Count -ne 1 -or $actionTextControl[0][9] -like '*[[]ActionText[]]*') {
        Fail 'ProgressDlg ActionText must subscribe to the engine event instead of reading a static [ActionText] property.'
    }
    $eventMappings = Read-MsiRows $database 'SELECT * FROM EventMapping' 4
    foreach ($mapping in @(@('ActionText', 'ActionText', 'Text'), @('ProgressBar', 'SetProgress', 'Progress'))) {
        $control = $mapping[0]; $event = $mapping[1]; $attribute = $mapping[2]
        if (@($eventMappings | Where-Object { $_[0] -ceq 'ProgressDlg' -and $_[1] -ceq $control -and $_[2] -ceq $event -and $_[3] -ceq $attribute }).Count -ne 1) {
            Fail "EventMapping must subscribe ProgressDlg.$control to $event/$attribute."
        }
    }
    # 取消必须确认，并通知安装引擎取消；非模态进度页不能直接 EndDialog 跳过确认。
    if (@($dialogs | Where-Object { $_[0] -ceq 'CancelDlg' }).Count -ne 1) { Fail 'CancelDlg must exist for progress-page cancellation confirmation.' }
    if (@($events | Where-Object { $_[0] -ceq 'ProgressDlg' -and $_[1] -ceq 'Cancel' -and $_[2] -ceq 'SpawnDialog' -and $_[3] -ceq 'CancelDlg' }).Count -ne 1) {
        Fail 'ProgressDlg Cancel must spawn CancelDlg for confirmation.'
    }
    if (@($events | Where-Object { $_[0] -ceq 'CancelDlg' -and $_[1] -ceq 'Yes' -and $_[2] -ceq 'EndDialog' -and $_[3] -ceq 'Exit' }).Count -ne 1) {
        Fail 'CancelDlg Yes must end the UI sequence so the engine cancels and rolls back.'
    }
    if (@($events | Where-Object { $_[0] -ceq 'CancelDlg' -and $_[1] -ceq 'No' -and $_[2] -ceq 'EndDialog' -and $_[3] -ceq 'Return' }).Count -ne 1) {
        Fail 'CancelDlg No must return to the progress page and continue installing.'
    }

    # 成功收尾与提前结束必须是 EndDialog Return：Exit 参数代表用户取消（1602），
    # 会让成功安装显示「安装已取消」并返回错误退出码。
    if (@($events | Where-Object { $_[0] -ceq 'Exit' -and $_[1] -ceq 'Finish' -and $_[2] -ceq 'EndDialog' -and $_[3] -ceq 'Return' }).Count -ne 1) {
        Fail 'Exit Finish must end with EndDialog Return; the Exit argument reports a user cancellation (1602).'
    }
    $healthyEnd = @($events | Where-Object { $_[0] -ceq 'MaintenanceDlg' -and $_[1] -ceq 'Next' -and $_[2] -ceq 'EndDialog' })
    if (@($healthyEnd | Where-Object { $_[3] -ceq 'Exit' }).Count -ne 0) {
        Fail 'MaintenanceDlg must not use EndDialog Exit; healthy early exit must be Return so msiexec returns success.'
    }

    # 子目录解析：每个载荷子目录必须有执行序列的 Type-51 动作，在 CostFinalize 之前
    # （ICE12 要求）且在 EnforceInstallFolder(950) 之后按当前 INSTALLFOLDER 重设目标，
    # 否则 UI 自定义路径安装时语言资源文件会落进默认目录（UI 序列 CostFinalize 先于
    # 路径校验解析了默认值，执行序列不会重算已存在的目录属性）。
    $directoryTable = Read-MsiRows $database 'SELECT * FROM Directory' 3
    $payloadDirs = @($directoryTable | Where-Object { $_[1] -ceq 'INSTALLFOLDER' })
    if ($payloadDirs.Count -eq 0) { Fail 'Directory table must contain payload subdirectories under INSTALLFOLDER.' }
    $customActions = Read-MsiRows $database 'SELECT * FROM CustomAction' 4
    $execSequence = Read-MsiRows $database 'SELECT * FROM InstallExecuteSequence' 4
    $costFinalize = @($execSequence | Where-Object { $_[0] -ceq 'CostFinalize' })
    $enforce = @($execSequence | Where-Object { $_[0] -ceq 'EnforceInstallFolder' })
    if ($costFinalize.Count -ne 1 -or $enforce.Count -ne 1) { Fail 'InstallExecuteSequence must contain EnforceInstallFolder and CostFinalize.' }
    foreach ($dirRow in $payloadDirs) {
        $dirId = $dirRow[0]
        if ($dirId -ceq 'INSTALLFOLDER') { continue }
        $actionId = 'SetDir' + $dirId
        $ca = @($customActions | Where-Object { $_[0] -ceq $actionId })
        if ($ca.Count -ne 1 -or $ca[0][1] -ne '51' -or $ca[0][2] -ne $dirId) {
            Fail "Subdirectory '$dirId' must have a Type-51 custom action (SetDir$dirId) targeting itself."
        }
        $seq = @($execSequence | Where-Object { $_[0] -ceq $actionId })
        if ($seq.Count -ne 1) { Fail "InstallExecuteSequence must schedule $actionId." }
        $seqNo = [int]$seq[0][2]
        if ($seqNo -ge [int]$costFinalize[0][2]) {
            Fail "$actionId must run before CostFinalize (ICE12: a Type-51 targeting a directory cannot follow it)."
        }
        if ($seqNo -le [int]$enforce[0][2]) {
            Fail "$actionId must run after EnforceInstallFolder so [INSTALLFOLDER] already holds the final path."
        }
    }
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