# Mosaic：实施与验证记录

## 0.17.6：完整界面进度页卡死修复（2026-11-20）

- 根因位置（以下行号指已提交的 0.17.5 版本 `3f91fd3`）：
  - 模态进度页阻塞执行阶段：`build/installer/Dialogs.wxs:126` 的 `<Dialog Id="ProgressDlg" …>` 没有声明 `Modeless`，编译出的 `Dialog` 表 `ProgressDlg.Attributes=3`（可见 1 + 模态 2）；`build/installer/Package.wxs:75` 在 `InstallUISequence` 1290 显示该页，而 `ExecuteAction` 位于 1300。模态对话框结束前界面序列不会继续，`ExecuteAction` 永不启动，用户只看到空白的「正在处理，请稍候...」页面且无任何进度。完整界面（默认或 `/qf`）首次安装与损坏维护都经过该页；健康维护提前结束与静默安装不经过，因此未提前暴露。
  - 进度显示没有订阅安装引擎事件：`Dialogs.wxs:128` 的 `ActionText` 控件文本为静态属性 `[ActionText]`，`:131` 的 `ProgressBar` 没有任何订阅；发布 MSI 的 `_Tables` 中不存在 `EventMapping`。即使只解除模态阻塞，动作文字与进度条也不会随执行阶段更新。
  - 取消语义未适配非模态：`Dialogs.wxs:132-134` 直接发布 `EndDialog Exit`，发布 MSI 的 `ControlEvent` 相同；进度页非模态后需要先确认再通知安装引擎取消。
  - 静态检查遗漏：`build/test-package-maintenance.ps1` 原来只检查进度页序号与扫描摘要，没有检查 `Dialog.Attributes` 的模态位、`EventMapping` 订阅与取消事件链路，因此该缺陷能通过发布检查。
- 改动位置：
  - 安装界面 `build/installer/Dialogs.wxs`：
    - `ProgressDlg:129` 增加 `Modeless="yes"`（与既有 `NoMinimize="yes"` 并存），编译后 `Dialog.Attributes` 仅为可见位 1；
    - `ActionText:131-133` 去掉静态文本 `[ActionText]`，改为 `<Subscribe Event="ActionText" Attribute="Text"/>`；
    - `ProgressBar:136-138` 增加 `<Subscribe Event="SetProgress" Attribute="Progress"/>`；
    - `Cancel:139-141` 由 `EndDialog Exit` 改为 `SpawnDialog CancelDlg`；
    - 新增 `CancelDlg:144-153`：「是」发布 `EndDialog Exit`（界面序列结束，安装引擎取消并回滚），「否」发布 `EndDialog Return`（回到进度页继续安装）。
  - 包定义 `build/installer/Package.wxs`：无改动。已核对 `Show ProgressDlg 1290` 早于 `ExecuteAction 1300`、`VerifyRepairedPayload 1301`、`Show Exit 1303`，健康维护 `EndDialog Exit` 条件与静默执行序列保持不变。
  - 静态包校验 `build/test-package-maintenance.ps1:203-239`：新增 `ProgressDlg` 非模态（`Attributes & 2 = 0`）且可见（`Attributes & 1 ≠ 0`）、`ProgressDlg` 显示序号早于唯一存在的 `ExecuteAction`、`ActionText` 控件不得再读静态 `[ActionText]`、`EventMapping` 中 `ProgressDlg/ActionText/ActionText/Text` 与 `ProgressDlg/ProgressBar/SetProgress/Progress` 各一条、`CancelDlg` 存在、`ProgressDlg.Cancel → SpawnDialog CancelDlg`、`CancelDlg.Yes → EndDialog Exit`、`CancelDlg.No → EndDialog Return` 断言。旧 0.17.5 包在第一条断言即失败。
  - 版本与发布元数据：`Directory.Build.props:12`（0.17.6）、`build/installer/Mosaic.Installer.wixproj:4`（默认 `ProductVersion` 0.17.6）、`build/release-content.json`（第 39 条 `mosaic-0.17.6`，`previousReleaseId=mosaic-0.17.5`，指纹 `D0281954270045AE22FCF4AB1622326176AD4550405E87B2B5C975A6580CDE78`）、`build/published-release-index.json`（第 39 条）、`build/release-manifest.json`、`docs/releases/v0.17.6.md`、`README.md:3`、`README.md:45`、`README.md:47`、`docs/installer-progress-hang-fix-plan.md`（状态行）、本文件；发布方案 `docs/installer-progress-hang-fix-plan.md` 随本次提交纳入仓库。
- 迁移逻辑与验收项：
  - 无数据迁移：UpgradeCode `{4D2B073C-72D5-4C3A-B845-65FC2C52D23C}`、组件 GUID/KeyPath、per-user 范围、安装目录、注册表结构与用户数据目录均未变化；0.17.5 → 0.17.6 仍由 `MajorUpgrade afterInstallInitialize` 原地完成。
  - 行为变化仅限完整界面的进度页：首次安装与损坏维护不再被进度页阻塞，操作文字与进度条随安装引擎更新；进度页取消先弹确认。静默安装、健康维护提前结束、显式 `/f`、失败 1603 与待重启 3010 语义不变。
  - 验收项（需授权的隔离环境，本次未执行）：完整 UI 首次安装（默认与含空格自定义路径）日志到达 `ExecuteAction`/`InstallInitialize`/`InstallFiles`/`InstallFinalize` 并完成；执行期间操作文字与进度条更新；取消→「否」继续安装、取消→「是」结束并回滚且无后台会话；损坏维护修复进度与复检；0.17.5 → 0.17.6 原位升级保留位置与数据；健康维护提前结束、静默安装与退出码不回归。
- 验证（静态）：
  - `build/package.ps1` 完整执行：WiX 构建 0 警告 0 错误；release content 校验 `mosaic-0.17.6`（39 条索引）；`test-package-shortcuts.ps1` PASS；`test-package-maintenance.ps1` PASS，470 个 cabinet 成员逐个 SHA-256 与清单一致（日志 `artifacts/qa/build-0176-final.log`，本地生成，未纳入 git）。
  - 修复前后对照：0.17.5 发布 MSI 在同一检查下失败，报 `ProgressDlg must be modeless (Dialog.Attributes must not contain msidbDialogAttributesModal=2); a modal progress page blocks ExecuteAction forever.`；0.17.6 包通过全部断言。0.17.6 包表证据（`docs/evidence/0.17.6/msi-dialogs.json`，由 `scripts/qa/dump_dialogs.py` 只读导出）：`ProgressDlg` 的 `Dialog.Attributes=1`、`CancelDlg` 存在、`EventMapping` 含上述两条订阅、`InstallUISequence` 为 `ProgressDlg=1290`、`ExecuteAction=1300`。
  - 产物：`Mosaic-0.17.6-win-x64.msi`，52,470,860 字节，SHA-256 `337F2F71016EAF7F0185B59FFB2A4D14C88F0A8AFC2B9B53FDEBEFAB7BC7D570`（由本次 `build/package.ps1` 重新构建，包结构与静态校验结果同上）。
- 实机验收（已执行，2026-11-21，隔离账户 `MosaicQa0176`，完整交互 UI）：
  - 用户以「授权验收」授权一次 UAC 提权创建临时本地账户并执行交互矩阵。隔离方式：`scripts/qa/qa_win.py` 用 `CreateProcessWithLogonW(LOGON_WITH_PROFILE)` 以该账户启动全部安装会话（显式环境把 USERPROFILE/LOCALAPPDATA/APPDATA/TEMP 指向 `C:\Users\MosaicQa0176`）；QA 结果只写 `C:\Users\Public\MosaicQA0176`。真实安装（ASUS 账户 0.17.2）验收前后逐值比对 `HKCU\Software\Mosaic`、产品注册与程序文件哈希：无任何改动（结论见 `docs/evidence/0.17.6/ui-matrix/summary.json` 的 `real_install_check`）。
  - 矩阵 8 场景全部通过（`scripts/qa/ui_matrix.py`，结论 `docs/evidence/0.17.6/ui-matrix/summary.json`，原始记录 `results.json`，完整 /L*V! 日志 25 个压缩于 `C:\Users\Public\MosaicQA0176\archive\ui-matrix-logs.zip`，关键截图 8 张随本提交归档）：
    - S1 0.17.5 `/qf` 首装（含中文与空格自定义路径）：复现卡死——进度页停留 14 秒以上、动作文本仅静态提示、进度值恒 0、挂起时日志无 `ExecuteAction`，点取消后 1602，无安装目录与登记；
    - S2 0.17.6 `/qf` 首装（同路径）：进度页实时动作文本+进度条连续推进（16 个不同进度值）、完成页、退出码 0、483 个文件全部落位（470 主清单 + 13 语言子目录）、单一 ARP 登记 0.17.6、桌面与开始菜单快捷方式齐全；
    - S3 健康维护：显示「安装完整，无需修复 / 已检查 470 个文件」，点完成退出码 0，文件哈希与修改时间零改动；
    - S4 损坏维护：删除 1 个文件 + 篡改 1 个 DLL 后扫描正确报告「1 个文件缺失，1 个文件损坏」，修复后复检「修复已完成，全部受管文件与快捷方式均通过复检」，两个文件哈希恢复；
    - S5 升级：0.17.5 静默安装（自定义路径+哨兵数据）→ 0.17.6 `/qf` 显示升级页并正确列出「0.17.5 将升级到 0.17.6」「安装位置保持不变」，升级后位置保留、哨兵数据保留、二进制已替换、单一登记 0.17.6、退出码 0；
    - S6a 取消→否：确认弹窗出现、选「否」回到进度页并完成安装（退出码 0，文件与登记齐全）；
    - S6b 取消→是：确认弹窗出现、选「是」进入「安装已取消」页、1602 结束、回滚干净（无目录、无 ARP 登记）；
    - S7 静默回归：`/qn` 安装、维护、卸载全程无任何窗口，返回码均 0，卸载后目录清空。
  - 验收过程中发现并已修复的三个新增包缺陷（同属本次交付单元）：
    - UI 自定义路径安装丢失 156 个语言资源文件：UI 序列 `CostFinalize` 先于路径校验把子目录属性按默认 `%LOCALAPPDATA%` 解析，执行序列不会重算已存在的目录属性，资源 DLL 落进默认目录，维护扫描误报缺失并 1603。修复：`scripts/package_payload.py` 与 `build/package.ps1` 为 13 个子目录生成 Type-51 动作（`SetDir<目录Id>`，序 960-972，在 `EnforceInstallFolder`(950) 之后、`CostFinalize`(1000) 之前，值 `[INSTALLFOLDER]\<lang>` 显式补分隔符）；静态检查新增逐子目录断言（Type-51 存在、序号区间正确）。
    - 成功收尾被误报为取消：`Dialogs.wxs` Exit 页「完成」与健康维护提前结束原为 `EndDialog Exit`（MSI 语义=用户取消 1602，随后误入 UserExit 页）；改为 `EndDialog Return`，静态检查断言 Exit.Finish 与 MaintenanceDlg 健康分支必须 Return。
    - 修复请求传递缺陷：`ScanPayload` 在 UI 序列设置的 `REINSTALL=ALL` 不会随属性快照传入服务端会话（实测服务端只收到 `REINSTALLMODE` 与 `MOSAICREPAIRNEEDED`），损坏修复复制数为 0。修复：`MosaicInstallerActions.cpp` 新增 `ApplyRepairRequest`（执行序列 995，`CostFinalize` 之前），在服务端会话内依据扫描标记设置 `REINSTALL=ALL`；显式 `/f` 优先不覆盖。静态检查新增源码契约与序列断言。0.17.5 旧包在新断言下失败（红），重建后 0.17.6 通过（绿）。
  - 重新构建产物：`artifacts/installer/0.17.6/Mosaic-0.17.6-win-x64.msi`，52,516,584 字节，SHA-256 `05DDC4E7992797232FAAE2949FD7A4365A672A74BB885F9EBFE2ACF15463BA3C`（构建日志 `artifacts/qa/build-0176-final5.log`/`build-0176-final6.log`/`build-0176-final7.log`，本地生成）。
- 未验证项：执行期 `FilesInUse` 对话框、3010/待重启分支，以及 FatalError 页的 MSI 2814 控件链告警（仅在安装失败弹 FatalError 时出现的次生显示缺陷，本次未复现修复）。中文安装路径已在 S2/S5/S6/S7 覆盖；验收账户 140% DPI 下界面正常显示。

## 0.17.5：卸载按登记位置移除、关闭 MSI 数据库句柄（2026-11-20）

- 根因位置（以下行号指已提交的 0.17.4 版本 `077bfab`）：
  - 自定义安装位置卸载后文件残留：`build/installer/Package.wxs:52` 的 `DetectInstallState`（执行序列 26）条件为 `NOT REMOVE`，`:54` 的 `EnforceInstallFolder`（950）条件为 `NOT Installed AND NOT REMOVE`。卸载会话中两者都被跳过，`INSTALLFOLDER` 没有任何属性赋值，目录解析回退到 `Package.wxs:15` 的 `Directory` 表默认目录 `%LOCALAPPDATA%\Programs\Mosaic`；`RemoveFiles` 因此只删除默认目录，自定义位置中的 470 个程序文件全部遗留。实机日志证据（`10-uninstall-custom.log`，0.17.4 复现时）：`Skipping action: EnforceInstallFolder (condition is false)` 与 `PROPERTY CHANGE: Adding INSTALLFOLDER property. Its value is 'C:\Users\MosaicQA\AppData\Local\Programs\Mosaic\'`，随后 `FileRemove` 全部指向默认目录。
  - 每次扫描泄漏一个 MSIHANDLE：`build/installer/actions/MosaicInstallerActions.cpp:218` 通过 `MsiGetActiveDatabase` 取得数据库句柄，原实现只在 `:257` 关闭 `view`，从未关闭数据库句柄。MSI 在动作结束时报告 `DEBUG: Error 2769: Custom Action ScanPayload did not close 1 MSIHANDLEs.`；每次打开维护页（UI 扫描）或静默扫描都会出现该警告。
- 改动位置：
  - 原生动作 `build/installer/actions/MosaicInstallerActions.cpp`：
    - `ReadBinaryStream:215`：`:227`（`MsiDatabaseOpenViewW` 失败路径）关闭数据库句柄；`:263`（正常路径）关闭 `view` 后关闭数据库句柄。
    - 新增 `RestoreInstallFolderForRemoval:762`：读取 `ProductCode`，用 `GetProductInfo(..., INSTALLPROPERTY_INSTALLLOCATION)` 取产品登记位置并 `TrimEndingSeparators`；位置为空或为相对路径（0.17.2 及更早未登记）时写日志并保持默认目录语义；否则设置 `INSTALLFOLDER` 与 `MOSAICINSTALLEDLOCATION`。
  - 包定义 `build/installer/Package.wxs`：`:47` 声明 `RestoreInstallFolderForRemoval`（`Execute="immediate"`、`Return="ignore"`）；`:54-55` 在 `DetectInstallState` 26 之后、`CostFinalize` 900 之前新增序列 27，条件 `REMOVE`。
  - 静态包校验 `build/test-package-maintenance.ps1`：`:93-101` 新增源码契约（必须包含 `RestoreInstallFolderForRemoval`、`INSTALLPROPERTY_INSTALLLOCATION`、`MsiCloseHandle(database)`）；`:106-109` 新增执行序列断言（动作位于 27 且条件含 `REMOVE`）。
  - 版本与发布元数据：`Directory.Build.props:12`（0.17.5）、`build/installer/Mosaic.Installer.wixproj:4`（默认 `ProductVersion` 0.17.5）、`build/release-content.json`（第 38 条 `mosaic-0.17.5`，`previousReleaseId=mosaic-0.17.4`，指纹 `777313277E4763A3183DF2ACD2BECEA302067E17CB9BD3CDB603C3AF24AB6218`）、`build/published-release-index.json`（第 38 条）、`build/release-manifest.json`、`docs/releases/v0.17.5.md`、`README.md:3` 与当前版本说明/下载链接、本文件、`docs/installer-location-plan.md` §10.9。
- 迁移逻辑与验收项：
  - 数据与配置迁移：无。组件 GUID、KeyPath、UpgradeCode、per-user 范围、数据目录与注册表结构均未变化，升级仍由 `MajorUpgrade afterInstallInitialize` 原地完成。`%LOCALAPPDATA%\Mosaic` 下的用户数据不参与卸载删除。
  - 已登记位置的产品（0.17.3/0.17.4）：卸载按登记位置删除程序文件与空目录；升级保持原位置。
  - 未登记位置的旧安装（0.17.2 及更早）：`RestoreInstallFolderForRemoval` 读取为空或相对路径时只写日志，卸载保持默认目录语义，与旧行为一致；动作 `Return="ignore"`，不新增卸载失败路径。
  - 验收项（实机，隔离用户 `MosaicQA`）：自定义位置卸载后目录内文件数为 0 且目录不存在、产品登记被移除；默认位置卸载删除负载并保留用户哨兵文件；全部安装/维护日志不再出现 `did not close`/2769；0.17.4 → 0.17.5 升级保留自定义位置；健康维护零写入；删除 EXE 与同尺寸篡改 DLL 后修复恢复；显式 `/f` 重写 470 个文件；拒绝读取时静默安装返回 1603；应用运行中修复由 Restart Manager 关闭并在完成后重启应用（返回 0）；文件被非重启管理器进程占用时修复返回 1603 并整体回滚，释放占用后再修复返回 0 且内容恢复。
- 验证（静态）：`build/package.ps1` 完整执行，WiX 0 警告 0 错误；release content 校验 `mosaic-0.17.5`（38 条索引）；`test-package-shortcuts.ps1` PASS；`test-package-maintenance.ps1` PASS（含新增源码与序列断言）；470 个 cabinet 成员逐文件 SHA-256 与清单一致。产物 `Mosaic-0.17.5-win-x64.msi`，52,466,396 字节，SHA-256 `F8F23394EAF0B82F36454C3C83A3C89D4888CCC3C3D177C4321B82C3C405B433`。
- 实机验收（0.17.5 MSI，隔离用户 `MosaicQA`，独立 profile `C:\Users\MosaicQA`；以下结论为执行时记录，原始证据保留状态见本节末条）：
  - 首轮维护矩阵 39 项，38 项一次通过；唯一未通过项是测试脚本自身的时区比较缺陷（`[datetime]'…Z'` 在 PowerShell 5.1 被转成本地时间，与 `LastWriteTimeUtc` 的 ticks 不等），同一步的 `explicit-f-rewrites-files` 以哨兵年份变化、mtime 恢复为包内时间与日志 `FileCopy=470` 判定通过，非产品缺陷。修正断言后复跑（22:11–22:13，同一隔离用户与独立 profile）：39 项全部通过，当时的 `result-0175.json` 记录 `ok=true`、失败项为空；日志为当时的 `logs-0175\01`–`10`。
  - 修复点 1 实测：0.17.4 → 0.17.5 升级保留 `C:\Users\Public\MosaicQA\Install Folder`；`/x` 日志先执行 `RestoreInstallFolderForRemoval` 并记录 `Mosaic: 卸载位置 C:\Users\Public\MosaicQA\Install Folder`（返回值 1，`Return=ignore`），卸载后 `files=0`、目录不存在、ARP 登记为空。
  - 修复点 2 实测：0.17.5 的 01–04 号安装/维护/修复/显式 `/f` 日志中 `2769` 与 `did not close` 计数均为 0，全量日志扫描断言通过；0.17.4 同场景曾每次扫描出现一条。
  - 其他通过项：默认位置安装与 ARP/快捷方式登记；健康维护零写入且哨兵文件哈希与修改时间不变；删除 EXE + 同尺寸清零 DLL 后修复恢复内容与快捷方式；拒绝读取时 `/qn` 返回 1603 且不误报；默认位置卸载删除负载、保留 `%LOCALAPPDATA%\Mosaic` 哨兵与移除 ARP；0.17.4 → 0.17.5 升级版本与位置正确、未在默认目录另装；升级后健康维护零写入。
  - 文件占用场景（隔离用户，0.17.5 MSI）：
    - 应用正在运行时执行 `REINSTALL=ALL REINSTALLMODE=amus`：日志依次记录 `RESTART MANAGER: Will attempt to shut down and restart applications in no UI modes.`、`Successfully shut down all applications that held files in use.`、`Previously shut down applications have been restarted.`；引擎返回 0，470 个文件全部重写，EXE 哈希与包内一致，`PendingFileRenameOperations` 无 Mosaic 项，未出现 3010。
    - 文件被非重启管理器进程占用（ASUS 侧以 `FileShare.Read` 持有读共享锁，EXE 已清零）：`Executing op: FileCopy(… Mosaic.Desktop.exe …)` 之后立即记录 `Note: 1: 2205 2: 3: Error` 与 `Note: 1: 2228 … Error = 1603`，引擎重试约 3 分钟后以 `MainEngineThread is returning 1603` 结束并整体回滚；回滚后 EXE 仍为清零内容（哈希 `04DB6BC9…09E3`），安装集其余文件未被破坏。
    - 释放占用后再执行同一修复：引擎返回 0，470 个 `FileCopy`，EXE 哈希恢复为 `34F1F22E…D124`，脚本记录 `healthy=true`。
    - 待重启（3010）分支在本矩阵未被触发：两种占用分别由 Restart Manager 关闭重启或以 1603 失败回滚，没有出现「由 Windows Installer 安排到重启后替换」的情形；`IsRebootPending` 与 3010 语义仍由静态断言与代码审查覆盖。
  - 证据保留状态（2026-11-20 补记）：上述 39/39 与文件占用三项结论，是执行时依据 `result-0175.json`、`result-locked.json`、`result-appf2.json` 以及 `logs-0175\01`–`10`、`12-locked-repair-1/2.log`、`14-app-running-reinstall-all.log` 记录的结果。这些文件只存在于隔离测试目录 `C:\Users\Public\MosaicQA`，从未纳入 git，也未归档到仓库；验收完成、发布核对通过后的环境清理中随该目录整体删除，现无可提供的路径，也无法逐项独立复核。上文出现的日志名与日志片段是当时的摘录，不是现在可以读取的证据。此状况只说明证据未归档，不改变「当时实测通过」的历史记录，也不等于验收失败或测试失败。当前可独立复核的仅为静态材料：`3f91fd3` 中的源码契约与序列断言（`build/test-package-maintenance.ps1:93-101`、`:106-109`）、发布资产的字节数与 SHA-256，以及包内 470 个 cabinet 成员哈希——它们不能替代实机结论。今后如需实机复核，须在隔离环境重跑，并先将脱敏后的结果 JSON、命令与运行环境、日志归档入库，再清理测试目录。
- 未验证项：中文安装路径（矩阵只覆盖含空格的英文路径）、位置校验/取消路径（卷根、UNC、无权限、数据目录冲突）、交互 UI 的 FilesInUse 对话框与取消/回滚路径、3010/待重启语义、降级拒绝、0.17.2 起点升级（矩阵以 0.17.4 为旧版）、DPI/窄窗口文案、Windows 10 实机、ARM64、干净 VM 与真实安装介质；后几项与 0.17.4 保持相同的验证边界，不影响本次两项修复的判定。

## 0.17.4：同版本维护自动扫描、健康分支提前结束与失败/待重启状态传播（2026-11-20）

- 根因位置（以下行号指已提交的 0.17.3 版本 `ba54e76`）：
  - 维护必须手动点「下一步」才开始扫描：`build/installer/Dialogs.wxs:69` 的 `MaintStartDlg` 是维护的唯一入口，`ScanPayload` 只挂在该页按钮事件 `:77` 上；`build/installer/Package.wxs:64` 在 UI 序列 1002 直接显示这一页。于是「再次运行安装包先自动扫描」并未实现，健康判断也晚于用户交互。
  - 健康分支仍进入写文件的执行阶段，且「无写入」没有执行序列依据：`Dialogs.wxs:85` 的维护页按钮 `:96` 是无条件 `EndDialog Return`，健康时也继续到 `ExecuteAction`/`InstallFinalize`；`Package.wxs:57` 的 `SetArpInstallLocation` 条件 `NOT Installed OR MOSAICLOCATIONINHERITED` 会在健康维护时也改写 `ARPINSTALLLOCATION`。健康「不写文件」仅靠「扫描后没有设置 `REINSTALL`」间接推断，执行序列里没有任何基于扫描结论的分流。
  - 扫描与复检失败恒为成功：`build/installer/actions/MosaicInstallerActions.cpp:975-999`（`ScanPayload`）与 `:1002-1017`（`VerifyRepairedPayload`）在所有分支都只写属性并 `return ERROR_SUCCESS`（`:999`、`:1017`）。安装目录不可读、载荷清单损坏、位置无法确认、修复后哈希仍不符时，`/qn` 依然返回 0，交互安装依然落到完成页；自定义动作声明了 `Return="check"` 但从未返回失败码，等于没有失败传播。
  - 待重启与失败没有区分：`VerifyRepairedPayload` 未检查 `REBOOT`、`MSIRUNMODE_REBOOTATEND`/`MSIRUNMODE_REBOOTNOW`。被占用而由 Windows Installer 安排到重启后替换的文件，在重启前哈希必然不符，旧实现会把它当作「修复成功」（完成文案）或普通失败（无 3010 语义），两者都不符合计划要求。
- 改动位置：
  - 原生动作 `build/installer/actions/MosaicInstallerActions.cpp`：
    - 新增 `ReportError:68`：用 `MsiCreateRecord(1)` + `MsiRecordSetStringW(record, 0, …)` + `MsiProcessMessage(hInstall, INSTALLMESSAGE_ERROR, …)` 走标准错误消息，同时写日志；只设属性不会让 `/qn` 失败，也不会让 UI 进入错误页。
    - 新增 `IsRebootPending:78`：检查 `REBOOT` 属性与 `MsiGetMode` 的 `MSIRUNMODE_REBOOTATEND`/`MSIRUNMODE_REBOOTNOW`。
    - `ScanPayload:992` 重写：读取失败、清单错误或位置不可确认（`:1001-1007`）清空修复属性后 `ReportError` 并 `return ERROR_INSTALL_FAILURE`；健康/需修复分别发布 `MOSAICMAINTBUTTONTEXT`（`:1011`）；仅在调用方未显式设置 `REINSTALL` 时才自动写入 `REINSTALL=ALL`/`REINSTALLMODE=amus`（`:1016-1021`），显式 `/f` 原样保留；健康时不置 `MOSAICREPAIRNEEDED`。
    - `VerifyRepairedPayload:1032` 重写：复检健康 → `MOSAICVERIFYSTATUS=Healthy`、标题「修复已完成」并成功（`:1039-1045`）；复检不健康但 `IsRebootPending` → `MOSAICVERIFYSTATUS=PendingReboot`、标题「需要重启后完成修复」，返回成功以保留 3010（`:1047-1056`）；其余情况 `ReportError` 并 `return ERROR_INSTALL_FAILURE`（`:1058-1063`）。
  - 包定义 `build/installer/Package.wxs`：`:9` 新增 `MOSAICEXITTITLE`（结果页动态标题）；`:56` 执行序列 `ScanPayload` 增加 `NOT MOSAICSCANSTATUS`（有 UI 时复用 UI 扫描结果，`/qn` 无 UI 时在此扫描）；`:59` `SetArpInstallLocation` 条件增加 `NOT (MOSAICSCANSTATUS = "Healthy" AND NOT REINSTALL)`（健康维护不做登记变更）；`:61` 复检条件改为 `REINSTALL OR MOSAICREPAIRNEEDED`；`:67` UI 序列新增 `ScanPayload` 1002（维护态自动扫描）；`:68` `MaintenanceDlg` 只在 `MOSAICSCANSTATUS = "Healthy" AND NOT REINSTALL` 时显示；`:74` UI 侧复检同样覆盖显式 `/f`。
  - 安装界面 `build/installer/Dialogs.wxs`：删除 `MaintStartDlg`；`MaintenanceDlg:72-92` 改为健康结果页（标题「安装完整，无需修复」，列出安装位置、扫描摘要、计数与「完成不会重装」提示；`Rescan:81-84` 重新检查；`Next:85-88` 在健康且无 `/f` 时 `EndDialog Exit`，异常或 `/f` 时 `EndDialog Return`）；`ProgressDlg` 增加 `ScanSummary:129` 与 `RepairHint:130`；`Exit` 标题改为绑定 `[MOSAICEXITTITLE]`（`:138`）。
  - 静态包校验 `build/test-package-maintenance.ps1`：执行序列扫描条件增加 `NOT MOSAICSCANSTATUS` 断言（`:104-106`）；UI 期望序列改为 Fresh 1001、Scan 1002、Maintenance 1003、Upgrade 1004、Blocked 1005、Unknown 1006（`:116-121`）；新增 UI 自动扫描条件与健康页显示条件断言（`:128-136`）；删除 `MaintStartDlg` 残留断言，改为「不存在该页」、健康 `EndDialog Exit`、异常/`/f` `EndDialog Return`、重新检查 `DoAction`、进度页扫描摘要、结果页动态标题（`:173-191`）；新增原生源码行为契约断言（`:84-92`：`INSTALLMESSAGE_ERROR`、`IsRebootPending`、`MSIRUNMODE_REBOOTATEND`、`MOSAICMAINTBUTTONTEXT`、`PendingReboot` 必须出现，`return ERROR_INSTALL_FAILURE` 至少两处）。
  - 版本与发布元数据：`Directory.Build.props:12`（0.17.4）、`build/installer/Mosaic.Installer.wixproj`（默认 `ProductVersion` 0.17.4）、`build/release-content.json`（第 37 条 `mosaic-0.17.4`，`previousReleaseId=mosaic-0.17.3`，指纹 `E2DED972E1E1670A3D4DDF249818995E4910D0C15CFC9A63BDFC2642D7E3A428`，`currentReleaseId=mosaic-0.17.4`）、`build/published-release-index.json`（第 37 条）、`build/release-manifest.json`、`docs/releases/v0.17.4.md`、`README.md:3` 与当前版本说明/下载链接、本文件、`docs/installer-location-plan.md` §10.9。
- 迁移逻辑与验收项：
  - 既有安装（0.17.3 及更早）：组件 GUID、KeyPath、UpgradeCode、per-user 范围、数据目录与注册表结构均未变，升级仍由 `MajorUpgrade afterInstallInitialize` 完成，无需数据迁移。0.17.4 安装后首次再次双击同版本包时，行为变化为「自动扫描、健康即结束」。
  - 兼容性变化：同版本维护扫描/复检失败时静默安装返回码由 0 变为 1603，UI 从完成页改为错误页；调用方若以退出码判断结果，需按非 0 处理（错误页与日志含具体原因）。待重启时返回 0 并保留 3010，页面明确显示「需要重启后完成修复」。
  - 健康维护：交互路径 `EndDialog Exit` 在 `ExecuteAction` 之前结束，不进入修复事务，也不改写 `ARPINSTALLLOCATION`；静默路径不设 `REINSTALL`、不写 ARP 登记，安装事务中组件均为已安装状态。
  - 显式 `/f`：扫描不清除调用方的 `REINSTALL`，健康页不显示（`NOT REINSTALL`），继续到修复并由 1301/6700 复检。
  - 验收（静态）：`test-package-maintenance.ps1` 对上述序列、条件、按钮事件与源码契约逐项断言；实机项见下。
- 验证：`build/installer/actions/build-actions.ps1` 编译通过（`Built …MosaicInstallerActions.dll`）；`build/package.ps1` 完整执行，release content 校验 `mosaic-0.17.4`，WiX 构建 0 警告 0 错误，`test-package-shortcuts.ps1` PASS；首轮 `test-package-maintenance.ps1` 因断言自身把 `InstallUISequence` 索引 1（Condition）当成事件名而误报，修正为按 Action 过滤后对 470 个 cabinet 成员逐个复算 SHA-256 并 PASS（`PASS: MSI static structure, maintenance routing, dialogs, payload manifest and cabinet integrity verified for 0.17.4.`）。产物 `artifacts/installer/0.17.4/Mosaic-0.17.4-win-x64.msi`：52,495,032 字节，SHA-256 `6DE45B7ADF57769ED13EFCBB2AC4E42279C49384A7AC27A2894C4859A8BB025B`。`build/test.ps1 -SkipDesktop` 15 个测试项目 369 项全部通过，0 失败。
- 发布核验（0.17.4）：提交 `077bfab`（`main`）；tag `v0.17.4` 指向 `077bfab`；Release <https://github.com/soehi/Mosaic/releases/tag/v0.17.4>，资产 `Mosaic-0.17.4-win-x64.msi` 52,495,032 字节，GitHub digest `sha256:6de45b7adf57769ed13efcbb2ac4e42279c49384a7ac27a2894c4859a8bb025b` 与本地一致。
- 未验证项（环境阻塞）：本机没有隔离 Windows 用户或可回滚 VM，未对真实安装执行任何删除、篡改、锁定或卸载测试。因此真实安装后自动扫描、删除单文件/整目录修复、文件锁定与重启后替换（3010）、复检失败落错误页、`/qn` 失败返回 1603、显式 `/f`、0.17.3→0.17.4 实机升级与随后同版本维护，均未实测；静态包检查与源码契约不能代替这些验收。另：交互健康维护以 `EndDialog Exit` 结束 UI，不进入执行阶段；该路径的进程返回码属 Windows Installer 的用户结束语义（通常 1602），与静默路径的 0 不同，需在实机项中确认并对调用方说明。

## 0.17.3：可选安装位置 + 同版本 MSI「检查与修复」（2026-11-20）

- 根因位置：
  - 安装位置写死且不可查询：`build/installer/Package.wxs:14`（0.17.2 版本，已提交版本 `081884f`）把安装目录固定为 `LocalAppDataFolder\Programs\Mosaic`，整份 Package 既没有写 `ARPINSTALLLOCATION` 的动作，也没有记录实际安装位置的组件或注册表值。结果「应用和功能」只登记名称与版本，没有 InstallLocation；升级、修复、卸载都只能依赖同一个固定目录，用户无法选择位置，任何定位路径的脚本也只能猜测。
  - 同版本再次运行只能重装：0.17.2 的 Package 没有维护路由（无 `InstallUISequence` 分流、无载荷扫描、无 `REINSTALL` 设置），对同一 ProductCode 再次执行 `msiexec /i` 只会整体重装，无法区分「文件完好」与「文件缺失/损坏」，也无法在程序运行时先判断是否真的需要关闭应用。
  - 本交付内自审发现并已修复的三处实现缺陷（同一提交内完成）：
    - 路径追加子目录后漏复核：`build/installer/actions/MosaicInstallerActions.cpp:1174-1181` 只对用户输入的原始路径判断与数据目录 `%LOCALAPPDATA%\Mosaic` 的重叠关系，`:1187` 才追加 `Mosaic` 子目录；输入 `%LOCALAPPDATA%` 或 `%LOCALAPPDATA%\Programs` 时最终路径会与用户数据目录重合或包含，程序文件可能写进数据目录。修复位置 `:1190-1196`：在最终路径上重新做相等、包含与反向包含判断。
    - ARP 位置写入条件漏掉维护/修复：初版 `SetArpInstallLocation` 条件仅为 `NOT Installed`，已安装产品进入同版本维护或显式 `/f` 时不刷新 `ARPINSTALLLOCATION`，位置登记在维护后就丢失。修复位置 `build/installer/Package.wxs:55-57`：条件改为 `NOT Installed OR MOSAICLOCATIONINHERITED`。
    - 阻断原因被通用文案覆盖：初版阻断对话框只显示固定提示，扫描器写入的具体原因 `MOSAICSTATEERROR` 没有展示，用户无法区分「同版本但产品身份不同」与「已安装更新版本」。修复位置 `build/installer/Package.wxs:13`：启动条件消息前置 `[MOSAICSTATEERROR]`。
    - 既有安装测试脚本用了 .NET Core 专有 API：`build/test-install.ps1:43-44`（0.17.2 版本）调用 `[IO.Path]::TrimEndingDirectorySeparator`，Windows PowerShell 5.1 的 .NET Framework 没有该方法，检查快捷方式工作目录时会抛 `MethodException`。修复位置 `:12-17` 新增 `Remove-MosaicTrailingSeparator`（保留 `C:\` 卷根），`:43-44` 与新增的位置读取函数统一改用它。
    - 位置无法确认时仍继续维护/升级：初版在维护或升级遇到「未登记位置且默认目录无法确认身份」时只记日志，状态仍是 Maintenance/Upgrade，扫描会落到默认 `INSTALLFOLDER`，可能把错误目录当成安装目录去修复，甚至另装一份。修复位置 `build/installer/actions/MosaicInstallerActions.cpp:718-731`：改为 `MOSAICSTATE=Unknown` 并置 `MOSAICBLOCKED`，原因写入 `MOSAICSTATEERROR`，由启动条件阻止继续。
- 改动位置：
  - 原生动作 DLL（新增）：`build/installer/actions/MosaicInstallerActions.cpp`（1286 行，C++17，`/MT /LD /utf-8 /DUNICODE`，静态链接 CRT，只依赖 `msi bcrypt ole32 shell32 shlwapi version user32 advapi32`，不依赖安装目录中的程序或 .NET）。函数与行号：工具函数 `GetProperty:40`、`SetProperty:56`、`LogInfo:61`、`Trim:68`、`ToLower:79`、`TrimEndingSeparators:85`、`Combine:93`、`IsPathEqual:104`、`IsPathInside:109`、`ParseVersion:120`、`CompareVersions:145`；位置基础 `GetLocalAppData:172`、`DefaultInstallFolder:182`、`DataRootFolder:190`；清单读取 `ReadBinaryStream:198`、`Utf8ToWide:244`、`TryReadFileHash:256`、`SplitTabs:370`、`IsSafeRelativePath:388`、`LoadManifest:410`；身份与位置 `GetProductInfo:502`、`ExecutableIdentityMatches:517`、`ResolveInstalledLocation:545`、`DetectState:581`、`DetectInstallState:664`；扫描 `ScanInstallRoot:828`、`PublishScanProperties:920`、`ScanPayload:972`、`VerifyRepairedPayload:999`；路径校验与界面动作 `ExpandEnvironment:1019`、`IsSystemFolder:1028`、`HasReparseAncestor:1060`、`AppendMosaicFolder:1085`、`ValidateInstallPath:1117`、`ValidateInstallFolder:1237`、`EnforceInstallFolder:1244`、`RestoreDefaultInstallFolder:1252`、`BrowseInstallFolder:1260`。
    - 状态检测使用 `MsiEnumRelatedProductsW` + `MsiGetProductInfoW`，按 UpgradeCode 枚举产品，以 ProductCode/版本区分 Fresh、Maintenance、Upgrade、BlockedSameVersion、BlockedNewer、Unknown，并把 `MOSAICSTATE`、`MOSAICSTATEERROR`、`MOSAICBLOCKED`、`MOSAICINSTALLEDPRODUCTCODE/VERSION/LOCATION` 发布给 MSI。
    - 位置解析：优先采用 `ARPINSTALLLOCATION`；为空时仅在确认产品身份（产品名 `Mosaic` 或默认目录中的 `Mosaic.Desktop.exe` 版本/产品名匹配）后回退到旧固定默认目录，并置 `MOSAICLOCATIONLEGACYFALLBACK`；维护与升级把解析结果同时写入 `INSTALLFOLDER` 与 `MOSAICLOCATIONINHERITED`。目录被整体删除时仍然沿用已登记位置，而不是推断为未安装。
    - 扫描：从 MSI Binary `MosaicPayloadManifest`（`MsiDatabaseOpenViewW` + `MsiRecordReadStream`）读取清单，逐行核对相对路径安全性、文件大小与 BCrypt SHA-256；同时检查桌面/开始菜单快捷方式的目标与工作目录。只读：健康时只发布汇总，不写文件；发现缺失/损坏时置 `MOSAICREPAIRNEEDED=1`，并在 `REINSTALL` 为空时设置 `REINSTALL=ALL`、`REINSTALLMODE=amus`，交给 Windows Installer 事务修复；任何读不到或解析失败都记为错误而非健康。
    - 路径校验：拒绝空路径、相对路径、UNC、卷根、系统目录（Windows/System32/Program Files 等）、与 `%LOCALAPPDATA%\Mosaic` 数据目录重叠（追加 `Mosaic` 后再次复核）、含重解析点祖先的路径、超过 240 字符的路径；通过后校验创建目录并发布 `INSTALLFOLDER`。
  - 扫描器构建脚本（新增）：`build/installer/actions/build-actions.ps1`——用 `vswhere.exe` 定位 Visual Studio，加载 `vcvars64.bat` 后执行 `cl /MT /LD /utf-8 /DUNICODE`，产出 `actions/bin/MosaicInstallerActions.dll`；失败即抛错，不静默复用旧 DLL。
  - 安装界面（新增）：`build/installer/Dialogs.wxs`（205 行，`<Fragment><UI Id="MosaicUI">`）。对话框架：`FreshStartDlg:12`、`LocationDlg:25`（路径输入 `MOSAICINSTALLPATH`、浏览、恢复默认；下一步先 `DoAction ValidateInstallFolder`，仅在 `MOSAICPATHERROR = ""` 时进入 `ConfirmDlg:53`）、`ConfirmDlg:53`、`MaintStartDlg:69`（`DoAction ScanPayload` 后进入 `MaintenanceDlg:85`）、`MaintenanceDlg:85`、`UpgradeDlg:103`、`BlockedDlg:117`、`UnknownDlg:126`、`ProgressDlg:135`（取消走 `EndDialog Exit`）、`Exit:144`（显示 `MOSAICSCANSUMMARY`/`MOSAICVERIFYMESSAGE`/`MOSAICVERIFYSUMMARY`）、`FatalError:155`（`ErrorDialog="yes"`，首个可聚焦控件 `ErrorText`，7 个标准错误按钮）、`UserExit:182`、`FilesInUse:190`（`ListBox Property="FileInUseProcess"`）。字体条目为 `MosaicFont`/`MosaicFontBold`/`MosaicFontSmall`。
  - 包定义：`build/installer/Package.wxs`——`:2` 保持 per-user、`UpgradeCode {4D2B073C-72D5-4C3A-B845-65FC2C52D23C}` 与 `MajorUpgrade Schedule="afterInstallInitialize"`；`:7` `ErrorDialog`；`:8` `MOSAICLOCATIONMISSING`；`:9` `DefaultUIFont`；`:10` `UIRef MosaicUI`；`:12-13` 身份冲突启动条件（显示 `[MOSAICSTATEERROR]`）；`:29-31` 新增 `InstallPathMetadata` 组件（HKCU `Software\Mosaic\Installer` 的 `InstallLocation`，GUID `{3B8E5C41-6F2A-4D77-9C10-8A1B4E2D6F59}`）；`:40-41` `MosaicActions`/`MosaicPayloadManifest` 两个 Binary；`:42-49` 七个 Type 1 自定义动作与 `SetArpInstallLocation`（Type 51）；`:50-60` 执行序列（DetectInstallState 26、EnforceInstallFolder 950、ScanPayload 990、SetArpInstallLocation `After CostFinalize`、VerifyRepairedPayload 6700）；`:61-74` UI 序列（DetectInstallState 30、Show Fresh 1001/Maint 1002/Upgrade 1003/Blocked 1004/Unknown 1005、ProgressDlg 1290、UI 侧复检 1301、Exit 1303、FatalError/UserExit）；`:75-79` AdminUISequence（Exit 1301、FatalError、UserExit）。既有 `StartMenu`、`DesktopShortcut`、`StartupCleanup` 组件 GUID 与 KeyPath 未改。
  - WiX 工程：`build/installer/Mosaic.Installer.wixproj:4`（默认 `ProductVersion` 0.17.3）、`:6-10` `ActionsDll`/`ManifestFile`/`DefineConstants`、`:11-21` 保留既有 ICE 抑制并写明 ICE17/ICE20 由 `build/test-package-maintenance.ps1` 逐项核对、`:23` 只编译外部 `Payload.wxs`。
  - 清单生成（新增）：`scripts/generate_installer_manifest.py`——写 `mosaic-manifest-v1` 头，文件行 `file\t<相对路径>\t<大小>\t<SHA256>\t<组件ID>\t<文件ID>`，快捷方式行 `shortcut\tdesktop|programmenu\t<link>\t<target>`；文件/组件 id 为 `SHA256(lower(相对路径))[:24]` 加大写，与两条打包链的 `Payload.wxs` 规则一致。
  - 打包链：`scripts/package_payload.py:138-150` 在 `Payload.wxs` 之后依次调用 `build-actions.ps1`、`generate_installer_manifest.py`，并把 `ProductVersion/PayloadFile/ActionsDll/ManifestFile/OutputPath` 传给 `dotnet build`；`build/package.ps1:44-45`（PowerShell 5.1 分支）与 `:108-109`（常规分支）在快捷方式检查之后新增 `test-package-maintenance.ps1`，版本号取自 `Directory.Build.props`。
  - 静态包校验（新增）：`build/test-package-maintenance.ps1`（以 DTF `WixToolset.Dtf.WindowsInstaller.dll` 只读打开 MSI）。断言 ProductVersion/UpgradeCode/ErrorDialog/DefaultUIFont；七个 Type 1 动作来源 `MosaicActions` 且入口同名；`SetArpInstallLocation` 为 Type 51 写 `[INSTALLFOLDER]`；两个 Binary 存在（只查名称列，避免提前读流导致后续 `GetStream` 为空）；执行序列 26/950/990/6700 与 `After CostFinalize` 条件；UI/AdminUI 序列 13 项；FatalError 的 `0x10000` 标志与 `ErrorText` 首焦点、FilesInUse 的 `FileList`、Exit 的 `Finish`、7 个标准错误按钮；LocationDlg 的 PathEdit/Validate/ConfirmDlg 与 MaintStartDlg 的 ScanPayload；LaunchCondition `NOT MOSAICBLOCKED`；`InstallPathMetadata` 注册表值；从 Binary 取出清单并与 `File` 表逐项核对大小与组件、文件数一致；两条快捷方式目标 `[INSTALLFOLDER]Mosaic.Desktop.exe`；单个 cabinet 覆盖全部文件；可选地用 `expand.exe` 解包并按清单 SHA-256 逐项复算（默认执行）。
  - 安装脚本：`build/test-install.ps1:9-24` 新增 `Get-MosaicArpEntry`、`Remove-MosaicTrailingSeparator` 与 `Get-MosaicInstallLocation`，按「应用和功能 → HKCU 元数据 → 旧固定默认」的顺序取实际位置；`:73-96` 安装后与升级后都重新读取位置，并断言升级后位置不变（`:85`），快捷方式、Smoke、卸载检查全部改用该位置，PASS 文案加入 `location-preserving upgrade`。
  - 版本与发布元数据：`Directory.Build.props:12`（0.17.3）、`build/release-content.json`（第 36 条 `mosaic-0.17.3`，`previousReleaseId=mosaic-0.17.2`，指纹 `93461814D95D5A800B173A298CC634FE6021FA4B5ABADFE8AFCAFFF564694052`）、`build/published-release-index.json`（第 36 条）、`build/release-manifest.json`、`docs/releases/v0.17.3.md`、`README.md:3`（版本摘要）、`README.md` 当前版本说明与下载链接、本文件、`docs/installer-location-plan.md` §10 状态。
  - 计划与实际的命名差异（按 §10.7 要求记录）：计划中的 `Mosaic.Installer.Actions/` 实际落在 `build/installer/actions/`；`Localization/zh-CN.wxl` 未新增，中文字面量直接写在 `Dialogs.wxs` 与 C++ 动作中；`packages.lock.json` 未改（未引入 WiX UI 扩展，界面自写）。
- 迁移逻辑与验收项：
  - 对已有安装（0.17.2 及更早）：UpgradeCode、per-user 范围、既有组件 GUID/KeyPath 均未变，升级仍由 `MajorUpgrade afterInstallInitialize` 完成。升级包在 `DetectInstallState` 中枚举同 UpgradeCode 的旧产品，确认身份后把旧固定默认目录 `%LOCALAPPDATA%\Programs\Mosaic` 作为 `INSTALLFOLDER` 原位升级，并置 `MOSAICLOCATIONINHERITED`/`MOSAICLOCATIONLEGACYFALLBACK`；新版本首次在事务中写入 `ARPINSTALLLOCATION` 与 HKCU `Software\Mosaic\Installer\InstallLocation`。此后同版本双击进入维护时读取这两个登记，不再依赖固定目录。验收（静态）：`test-package-maintenance.ps1` 对 `SetArpInstallLocation` 条件、`InstallPathMetadata` 与启动条件逐项断言；实机项见下。
  - 对首次安装：位置页默认 `%LOCALAPPDATA%\Programs\Mosaic`（`DetectInstallState` 预设 `MOSAICINSTALLPATH`），浏览/恢复默认/手输三条路径都经 `ValidateInstallPath`，追加 `Mosaic` 后复核数据目录隔离，校验通过才写 `INSTALLFOLDER`；静默安装由执行序列 950 的 `EnforceInstallFolder` 执行同一校验，无效路径直接失败，不静默退回默认。
  - 对卸载：`DetectInstallState`/`ScanPayload`/`VerifyRepairedPayload` 均带 `NOT REMOVE`，`/x` 与 MajorUpgrade 移除旧产品都不会进入扫描或修复；`StartupCleanup` 的 `NOT WIX_UPGRADE_DETECTED` 语义未变。
  - 对用户数据：扫描只读，不写 `%LOCALAPPDATA%\Mosaic`；修复只通过 Windows Installer 组件事务替换产品载荷与快捷方式；路径校验禁止安装目录与数据目录重叠。验收：静态测试的路径/清单断言 + 用户侧实机数据保留项。
- 验证：`build/package.ps1` 完整执行（publish → verify-published → release content → Payload.wxs → 原生 DLL → Manifest → WiX → MSI → 快捷方式检查 → 维护静态检查），退出码 0，WiX 构建 0 警告 0 错误；`test-package-shortcuts.ps1` PASS；`test-package-maintenance.ps1` 对 470 个 cabinet 成员逐个复算 SHA-256 后 PASS。首轮完整构建后补上了「位置无法确认即阻断」的判断，再用 `build/package.ps1 -NoPublish` 重编并复跑静态检查，同样 PASS。最终产物 `artifacts/installer/Mosaic-0.17.3-win-x64.msi`：52,490,813 字节，SHA-256 `5ca876b8ce210b95dba253a259aab5556b1398c4c49cd6754d147babb4e09241`。`build/test.ps1 -SkipDesktop`（跳过桌面冒烟项目）15 个测试项目 369 项全部通过，0 失败。
- 发布核验（0.17.3）：提交 `ba54e76`（`main`）；tag `v0.17.3` 指向 `ba54e76`；Release <https://github.com/soehi/Mosaic/releases/tag/v0.17.3>，资产 `Mosaic-0.17.3-win-x64.msi` 52,490,813 字节，GitHub digest `sha256:5ca876b8ce210b95dba253a259aab5556b1398c4c49cd6754d147babb4e09241` 与本地一致。
- 未验证项：本机没有隔离 Windows 用户或可回滚 VM，未执行真实安装、同版本维护、删除文件/整目录、同大小篡改、快捷方式改坏、文件锁定、取消/回滚/3010、0.17.2 实机升级、静默路径失败、显式 `/f`、`/x` 与降级分流的实机矩阵；UI 文案与窄窗口/DPI 表现、FilesInUse 的实际触发、BlockedDlg 的可见性（启动条件在序列 100 先失败）也未实测。

## 0.17.2：设置页排版收口、维护页重排与服务器地址入口隐藏（2026-11-20）

- 根因位置：
  - 重复标题：`src/Modules/Settings/Mosaic.Settings.Wpf/Views/SettingsView.xaml:167`（0.17.1 版本，容器分区表头）与 `src/Modules/Maintenance/Mosaic.Maintenance.Wpf/Views/MaintenanceView.xaml:43`（0.17.1 版本，页面自带标题）同时渲染分区名，进入「维护」时连续出现两个「维护」。
  - 内容贴边与固定宽度：`SettingsView.xaml:275`（0.17.1 版本）的模块页 `ContentControl` 未声明内容伸展契约，且 `MaintenanceView.xaml:42`（0.17.1 版本）根 `StackPanel` 为 `HorizontalAlignment="Left"`、`MaxWidth="820"`、左外边距为零——模块页拿不到真实可用宽高，卡片贴容器边缘，大窗口右侧留白。
  - 「0 项设置」：`SettingsView.xaml:169`（0.17.1 版本）表头计数无条件绑定 `VisibleCountLabel`，值来自 `SettingsViewModel.cs:412`（0.17.1 版本）的可见行计数；模块贡献页没有设置行，计数为 0，但该计数对操作页没有意义。
  - 窄窗口可达性：`MaintenanceView.xaml:54`、`:70`、`:89`、`:92`（0.17.1 版本）用两列 `Grid` 放路径输入框与「选择文件夹」，修复/卸载按钮用水平 `StackPanel`，卸载复选框直接用字符串 `Content`——窄窗口下输入框被挤压、复选框文字不换行。
  - 服务器地址重复入口：`src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthSettingsSource.cs:18`（0.17.1 版本）把 `auth.serverUrl` 声明为普通（`StorageSystem` 分组、`Normal` 分类）定义，通用编辑器按定义逐条生成输入框，开始页之外多出第二个服务器地址编辑入口。
- 改动位置：
  - 契约：`src/Modules/Settings/Mosaic.Settings.Contracts/SettingsContracts.cs:30-36` `SettingDefinition` 末尾新增 `bool ShowInSettings = true` 及注释——只影响通用编辑器是否建行，注册、读取与写入不变。
  - 隐藏定义：`src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthSettingsSource.cs:18-24` `auth.serverUrl` 定义追加 `ShowInSettings: false`；`auth.accountChoiceMade` 未动。
  - 视图模型：`src/Modules/Settings/Mosaic.Settings.Wpf/Internal/SettingsViewModel.cs:480-481` 新增 `Editable()`（`definition.ShowInSettings` 过滤）；`:490`（模块页初始化分支）与 `:510`（通用初始化）改为 `foreach (var definition in Editable(definitions.Value))`——隐藏定义不进入 `_rows`，因此不参与 `RefreshSections` 的分区计数、`VisibleRows`、搜索与恢复默认（`Changes()` 与 `ResetAllCommand` 只遍历 `_rows`）。
  - 容器视图：`SettingsView.xaml:104-107`（标题与总说明）、`:109-115`（高级设置栏）、`:128-136`（恢复默认区）、`:172-174`（表头计数）、`:179-183`（通用行 `ScrollViewer`）统一绑定 `HasSettingsSectionContent`；`:279-282` 模块页 `ContentControl` 增加 `HorizontalContentAlignment="Stretch" VerticalContentAlignment="Stretch"`。`HasSettingsSectionContent` 仍为 `SettingsViewModel.cs:374` 的 `SelectedPage is null`，既有语义未改。
  - 维护页：`MaintenanceView.xaml:48-50` 根 `ScrollViewer` + 内边距 `20,20,20,24` + 单行副标题，删除页面自带的「维护」标题；`:52-102` 存储位置卡（当前数据库/大小两列、路径 `Grid` 与 `BrowseButton`、迁移 `WrapPanel`、说明）；`:104-127` 应用维护卡（标题 `:106`、安装摘要 `:107`、数据目录 `:108-109`、修复按钮 `:110-112`、分隔线 `:113`、卸载标题 `:114`、可换行复选框 `:115-119`、卸载按钮 `:120-123`、说明 `:124`）；`Card` 样式 `:10-17` 保留 16 DIP 卡片间距与 22 DIP 内边距，新增 `SectionDivider` 样式 `:42-46`。
  - 窄窗口：`src/Modules/Maintenance/Mosaic.Maintenance.Wpf/Views/MaintenanceView.xaml.cs:8-16` 新增 `IsCompact` 依赖属性，`:21-22` 构造函数按 620 DIP 阈值随 `SizeChanged` 更新，`:25-33` `ApplyCompactLayout` 把 `BrowseButton` 在 `Grid.Row`/`Grid.Column` 的 `0,1` 与 `1,0` 间切换并调整对齐与边距。
  - 本地化：`src/Modules/Maintenance/Mosaic.Maintenance.Wpf/Localization/zh.xaml:4`（`Maintenance.Subtitle`）、`:13`（`Maintenance.App.Title`=应用维护）、`:16`（`Maintenance.App.Repair`=检查并修复）、`:17`（`Maintenance.App.UninstallTitle`=卸载 Mosaic）、`:18`（`Maintenance.App.Uninstall`=卸载）；`en.xaml` 同位置为英文对应文案；删除不再使用的 `Maintenance.Title`。
  - 测试：`tests/Mosaic.Wpf.Tests/PresentationTests.cs:1145` 新增 `Definitions_hidden_from_the_editor_never_enter_rows_counts_or_resets`（隐藏定义仍注册、不建行、高级模式不出现、恢复默认后保存不含该键）；`tests/Mosaic.Auth.Core.Tests/AuthServiceTests.cs:122` 新增 `Server_address_is_hidden_from_the_generic_editor_but_still_saved_by_auth`（`ShowInSettings=false` 且 `SetServerAsync` 仍提交 `auth.serverUrl`）；`tests/Mosaic.Wpf.Tests/MaintenanceViewModelTests.cs:174-190` 新增 `Browse_button_moves_below_the_path_when_the_view_is_narrow`，`:193` 抽出 `ThemedView()` 夹具（构造视图、应用深色主题）。
  - 发布元数据：`Directory.Build.props:12`、`build/installer/Mosaic.Installer.wixproj:4`（`ProductVersion`）、`build/release-content.json`、`build/release-manifest.json`（`currentReleaseId=mosaic-0.17.2`，第 35 条，`previousReleaseId=mosaic-0.17.1`，指纹 `BA73BD633209CF7EA43F15993A180B552BE4B88A499B406E56ADCA76C00CF90E`）、`build/published-release-index.json`（第 35 条）、`docs/releases/v0.17.2.md`、`README.md`。
  - XAML 编译插曲（如实记录）：编辑 `MaintenanceView.xaml` 的修复按钮块时，XAML 编译器一度报 `MC3050 无法找到类型 theme.MosaicTheme`，且始终指向该按钮的 `Style`/`AutomationProperties` 属性行；`xmlns:theme` 声明与所用键（`QuietButtonKey`/`DangerButtonKey`）确实存在（`src/Mosaic.Presentation.Wpf/MosaicTheme.cs`），清理 `obj`/`bin` 重建后仍复现。把该按钮块按行重写（每行一个属性、统一缩进）后消失。未改主题键、未改 `xmlns`、未改程序集引用，因此判定为编辑器改写留下的 XAML 编译态问题，不是产品缺陷；最终按钮改为默认按钮样式并去掉单子元素 `WrapPanel`，编译与截图均正常。
- 迁移逻辑与验收项：本次不新增配置迁移，`KnownMigrations` 与 `appsettings.json` 的 `appliedMigrations` 均未变，不改数据库结构，不改安装目录内容。对已有安装的影响仅在展示层：
  - `auth.serverUrl` 的定义仍在模块启动时注册、值仍从用户设置库读取，只是不再生成编辑行；分区重置与「恢复全部默认」只重置 `_rows` 中的键，不会清空服务器地址，账户流程（开始页登录、`SetServerAsync`）继续读写同一个键。验收：`Server_address_is_hidden_from_the_generic_editor_but_still_saved_by_auth`、`Definitions_hidden_from_the_editor_never_enter_rows_counts_or_resets`。
  - 维护页的数据库迁移、配置预检与修复、卸载命令及确认规则未改，只改布局与文案；验收：`Browse_button_moves_below_the_path_when_the_view_is_narrow` 与 `Mosaic.Maintenance.Tests` 既有 16 项（含预检四组合、可执行文件路径、调度脚本）。
  - 未在真实用户数据目录（`%LOCALAPPDATA%\Mosaic`）上执行任何写操作；截图与测试均使用临时数据根和假实现。
- 验证：`dotnet build Mosaic.sln -c Release` 0 警告 0 错误；`build/test.ps1` 全绿 423 项：Architecture 4、Auth.Core 26、Auth.Http 36、Desktop Smoke 54、DesktopPresence 17、FloatingWindows 12、Kernel 7、Maintenance 16、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 5、Storage.Sqlite 22、Sync.Core 14、Tasks.Contract 7、Tasks.Core 27、Wpf 134（本次新增 Wpf 2 项、Auth.Core 1 项）。离屏截图核对（临时目录 `mosaic-ui-snapshot`，1160×720、1100×720、1600×900，深色主题）：容器只显示一个「维护」标题、副标题在卡片上方、两张卡片随宽度填满、无「0 项设置」、卸载区标题/复选框/危险按钮层次正确、窗口变窄时「选择文件夹」换行；截图由临时渲染器 `UiSnapshotScratch` 生成，核对后已删除，未入库。`scripts/release_content_verify.py` PASS（35 条索引，0.17.2 指纹 `BA73BD633209CF7EA43F15993A180B552BE4B88A499B406E56ADCA76C00CF90E`）。
- 未验证项：真实安装上的视觉验收（不同 DPI 与字体缩放、多显示器）、浅色主题截图、620 DIP 以下的实机拖动体验；这些需要用户侧实测。
- 发布核验：`build/publish.ps1 -UseV2Feed` 重新发布到 `artifacts/local-run`（0.17.2），`build/package.ps1 -NoPublish` PASS（`published payload is a release build with manifest mosaic-0.17.2`、`Release content verified: 0.17.2, mosaic-0.17.2, 35 indexed releases`、`MSI contains the Application-owned DesktopFolder Mosaic shortcut, INSTALLFOLDER working directory, and StartupCleanup registry removals`），`build/test-package-shortcuts.ps1 -PackagePath artifacts/installer/0.17.2/Mosaic-0.17.2-win-x64.msi` PASS。安装包 `artifacts/installer/Mosaic-0.17.2-win-x64.msi`（52,154,812 字节，SHA-256 `788F9711627CCF6B8453437F7EF91BAC5F14C35DC3756430613A90D2267C89EC`）。发布后用新载荷复跑 `build/test.ps1` 仍为 423 项全绿（Desktop Smoke 54/54）。

## 0.17.1：修复 0.17.0 启动失败、修复安装不重启，修复安装前加只读配置预检（2026-11-20）

- 根因位置：
  - 启动必失败：`src/Mosaic.Desktop/Bootstrapper.cs:77-82`（0.17.0 版本）在 `await _runtime.StartAsync(...)` **之前**插入了 `await _runtime.DisposeAsync()`、两次 `ModuleStarted`/`ModuleStopping` 解绑与一段「运行时单次使用」的误导性注释。`Mosaic.Kernel/ModuleRuntime.cs:28` 在 `_started.Count > 0 || _stopped` 时抛 `InvalidOperationException("Runtime is single use.")`，而 `DisposeAsync() => StopAsync()` 会把 `_stopped` 置真，于是每一次启动都在模块装配前抛出，进程以「Mosaic · 无法启动」诊断窗结束。0.17.0 的 `StartupStorageRelocationTests` 只覆盖 `StartupStorageRelocation.RunAsync`，未经过 `Bootstrapper.StartAsync`，所以没有拦住该回归；peer 侧观察到的「Desktop Smoke 27/50 环境性失败」实际是该崩溃的连带结果。
  - 修复安装后不重启：`src/Modules/Maintenance/Mosaic.Maintenance.Windows/WindowsMaintenanceService.cs:19`（0.17.0 版本）默认 `ExecutablePath = Path.Combine(AppContext.BaseDirectory, "Mosaic.exe")`，而发布产物与安装目录里的可执行文件名是 `Mosaic.Desktop.exe`。`PrepareRepairAsync` 里 `File.Exists(ExecutablePath) ? ExecutablePath : null` 因此恒为 `null`，生成的 PS 助手脚本拿不到窗口进程句柄，无法在 msiexec 结束后拉起新进程。
  - 修复安装无条件执行：`MaintenanceViewModel.RepairAsync`（0.17.0 版本）直接确认后调用 `PrepareRepairAsync`，即使配置完全健康也会退出应用并跑一遍 `msiexec /famus`，与「同版本再点一次 MSI 应该先检查、正常则什么都不做」的期望不符。
- 改动位置：
  - 启动链：`src/Mosaic.Desktop/Bootstrapper.cs:77-108` 删除模块启动前的 `DisposeAsync()` 与事件解绑，恢复 `try { StartAsync → Presence/Floating/ReleaseSummary/Shell setting → ShellWindow } catch { 清理并记录错误编号 → throw }` 结构；保留 0.17.0 新增的 `IApplicationLifecycle? lifecycle` 参数与 `Lifecycle = lifecycle` 注入。
  - 可执行文件路径：`src/Modules/Maintenance/Mosaic.Maintenance.Windows/WindowsMaintenanceService.cs:19` 改为 `executablePath ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mosaic.Desktop.exe")`；`src/Mosaic.Desktop/DesktopApplicationLifecycle.cs` 兜底文件名同样由 `Mosaic.exe` 改为 `Mosaic.Desktop.exe`。
  - 预检契约：`src/Modules/Maintenance/Mosaic.Maintenance.Contracts/MaintenanceContracts.cs` 新增 `ConfigurationHealthReport(bool IsHealthy, IReadOnlyList<string> Issues)`（含 `Healthy` 单例）与 `IConfigurationHealthCheck.CheckAsync`。
  - 只读检查：`src/Mosaic.Desktop/AppConfiguration.cs:78-138` 新增 `Inspect(AppPaths)`（不写文件、不跑迁移、不补绑定：文件缺失/重复属性/坏 JSON/反序列化失败各返回一条 issue，`RepairProviderBindings` 后 `ValidateCoreConfiguration(validateDependencies: true)` 判定绑定完整，`KnownMigrations` 中未应用项逐条列出，绑定完整时再用 `EffectiveStorage`（`settings.storage.location`/`storage.filename` 覆盖）校验存储位置与文件名），配套 `InspectionOptions`（大小写不敏感 + `UnmappedMemberHandling.Disallow`）与 `EffectiveStorage`。
  - 检查实现：`src/Mosaic.Desktop/DesktopConfigurationHealthCheck.cs`（新增）`internal sealed class DesktopConfigurationHealthCheck(AppPaths) : IConfigurationHealthCheck`，异常包装为不健康报告。
  - 装配：`src/Mosaic.Desktop/BuiltInModuleCatalog.cs` 维护模块末尾追加 `new DesktopConfigurationHealthCheck(context.Paths)`；`Mosaic.Maintenance.Wpf/MaintenanceUiModule.cs` 的 `MaintenanceUiDependencies` 追加第 7 个可空参数 `IConfigurationHealthCheck? HealthCheck = null`；`Internal/MaintenanceViewFactory.cs` 透传。
  - 维护页：`src/Modules/Maintenance/Mosaic.Maintenance.Wpf/Internal/MaintenanceViewModel.cs` 新增 `_healthCheck` 字段与 `CheckConfigurationAsync()`（:274-286，无检查器返回 `null`，异常记诊断后返回 `null`），`RepairAsync`（:215-243）开头先预检：健康则 `StatusMessage = Maintenance.Repair.Healthy` 直接返回，不调用 `PrepareRepairAsync`、不退出；不健康则把 issue 列表拼进确认框，确认后才走原修复流程。`Localization/zh.xaml`/`en.xaml` 新增 `Maintenance.Repair.Healthy`、`Maintenance.Repair.Issues`。
  - 边界与 API：`build/public-api.txt` 登记 `Mosaic.Maintenance.Contracts.ConfigurationHealthReport` 与 `Mosaic.Maintenance.Contracts.IConfigurationHealthCheck`；`Mosaic.Maintenance.Wpf.csproj` 加 `InternalsVisibleTo Mosaic.Maintenance.Tests`；`tests/Mosaic.Maintenance.Tests/Mosaic.Maintenance.Tests.csproj` 加 `UseWPF` 与对 Wpf 项目的引用；`build/test.ps1:14` 由「除 `Mosaic.Wpf.Tests` 外一律 `net10.0`」改为按 csproj 内 `net10.0-windows` 推导（0.17.0 起 `Mosaic.Maintenance.Tests` 是 WPF 套件，旧推导会让脚本指向不存在的路径）。
  - 测试：`tests/Mosaic.Desktop.SmokeTests/BootstrapperStartupTests.cs`（新增）`Fresh_install_starts_and_reaches_the_main_window` 用临时 `--data-root` 启动发布产物，60 秒内必须出现主窗口，进程提前退出或标题含「无法启动」即失败（未执行 `build/publish.ps1` 时跳过）；`tests/Mosaic.Maintenance.Tests/MaintenanceRepairPrecheckTests.cs`（新增 4 项：健康跳过安装程序、不健康列出问题并修复、拒绝确认不修复、检查抛异常回退普通流程）；`tests/Mosaic.Maintenance.Tests/WindowsMaintenanceServiceTests.cs` 新增 `Default_executable_path_is_the_running_process_not_a_missing_Mosaic_exe`；`tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs` 新增 3 项 `Inspect` 测试（健康不写文件、启动期无法修复的绑定被报出、缺文件/待迁移/坏 JSON 判定）并把 `Configuration_presence_migration_...` 的迁移数断言由硬编码 4 改为与种子 `appliedMigrations` 相等（0.17.0 起为 5）。
  - 发布元数据：`Directory.Build.props`（0.17.1）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.17.1）、`build/release-content.json` 与 `build/published-release-index.json`（第 34 条，`previousReleaseId=mosaic-0.17.0`）、`build/release-manifest.json`、`docs/releases/v0.17.1.md`、`README.md`。
  - 打包脚本：`build/package.ps1:2-11` 的 `$ProductVersion` 默认值原为硬编码 `'0.12.2'`，打包时必须手工传参，漏传会把 0.17.1 的载荷标成旧版本号并被 `verify-published.ps1` 拒绝；改为默认读取 `Directory.Build.props` 的 `Version`。
- 迁移逻辑与验收项：本次不新增配置迁移，`KnownMigrations` 与 `appsettings.json` 的 `appliedMigrations` 均未变（仍为 5 项），0.17.0 → 0.17.1 升级不触碰 `Config/appsettings.json`、`Data/mosaic.sqlite` 与安装目录以外的任何内容。只读预检对已有安装的验收语义是「`Load` 会改写的项即不健康项」：待应用迁移、绑定缺失/无效、坏 JSON、存储位置非法都会列出；引用 `Inspect` 的三项测试同时断言 `appsettings.json` 字节与 `.pre-migration` 辅助文件均未产生，确保预检绝不改写用户配置。修复安装的验收项为：可执行文件路径等于当前进程（`WindowsMaintenanceServiceTests`），以及预检结果与是否调用 `PrepareRepairAsync`/`RequestExitAsync` 的四种组合（`MaintenanceRepairPrecheckTests`）。
- 验证：`dotnet build Mosaic.sln -c Release --no-restore` 0 警告 0 错误；测试全绿：Architecture 4、Auth.Core 25、Auth.Http 36、DesktopPresence 17、FloatingWindows 12、Kernel 7、Maintenance 16、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 5、Storage.Sqlite 22、Sync.Core 14、Tasks.Contract 7、Tasks.Core 27、Wpf 132、Desktop Smoke 54/54。负向验证：用 `git show b876995:src/Mosaic.Desktop/Bootstrapper.cs` 覆盖后发布到 `artifacts/broken-run`，`BootstrapperStartupTests` 报 `Startup ended in the diagnostic window: Mosaic · 无法启动`，确认新回归测试能拦住 0.17.0 的缺陷，随后恢复修复版并重跑全绿。发布核验 `scripts/release_content_verify.py` PASS（34 条索引，0.17.1 指纹 `E336EB205298F8C66DFFC7B8B9621D5ED1B5520C56B92821A77A2913C3BBF8D8`）、`build/verify-published.ps1` PASS（release build + manifest `mosaic-0.17.1`）、`build/test-package-shortcuts.ps1` PASS；安装包 `artifacts/installer/Mosaic-0.17.1-win-x64.msi`（52,130,236 字节，SHA-256 `30944C41B7F5F9C7183074AE19B2C1650AFB9702C031823AEAE1575BCDDEA0B1`）。发布产物上复跑 `BootstrapperStartupTests` 1/1 通过（主窗口在 1 秒内出现）。`dotnet format Mosaic.sln --verify-no-changes` 在本仓库不是既有门槛：改动前就有 34 个文件存在空白/编码偏差（多为 `FloatingWindows`、`Tasks` 模块的紧凑写法），本次新增代码未落在报告行内，未做全局重排。
- 未验证项：0.17.1 MSI 的应用内卸载/修复安装端到端实机验收仍需用户实测（含修复完成后自动重启）；0.17.0 真实安装升级到 0.17.1 的启动验证同属实机项。

## 0.17.0：设置内卸载/修复本软件 + 简化存储位置迁移（含真实数据迁移）（2026-11-20）

- 根因位置：
  - 迁移（旧「存储路径」文本设置）：`src/Mosaic.Desktop/HostSettingsCatalog.cs:25`（旧）暴露可自由编辑的 `storage.location` 文本设置，提交后下次启动 `Bootstrapper`（旧 `src/Mosaic.Desktop/Bootstrapper.cs:36-41`）直接按新路径打开数据库；`AppConfiguration.cs:415-418` 仅做路径拼接，数据库文件不存在时 `SqliteModule`/`DatabaseWorker` 在新位置静默创建空库，旧数据被留在原目录。根因不是路径拼接，而是允许不经校验地在运行期改写数据库位置。
  - 运行中迁移不可行：`src/Modules/Storage/Mosaic.Storage.Sqlite/Internal/DatabaseWorker.cs` 单条工作线程持有整个生命周期内唯一连接，无关闭/重开 API，故数据库迁移只能在 `SqliteModule` 打开文件之前执行。
  - 运行中卸载不可行：进程不能删除自身可执行文件；`src/Mosaic.Desktop/ShutdownCoordinator.cs`（既有）只负责正常退出与确认，没有「退出后执行」的编排。
- 改动位置：
  - 迁移契约：`src/Modules/Storage/Mosaic.Storage.Contracts/StorageRelocationContracts.cs`（新增 `IStorageRelocation`、`StorageLocationInfo`、`StorageRelocationRequest/Plan/Result`、`StorageRelocationFiles`）。
  - 迁移实现：`src/Modules/Storage/Mosaic.Storage.Sqlite/StorageRelocation.cs`（新增 `SqliteStorageRelocation`：计划校验绝对路径/拒绝相同目录/拒绝目标已有库/预留 16MB 空间余量；`ExecuteAsync` 在启动阶段做 WAL `TRUNCATE` 检查点、`BACKUP` 复制、`PRAGMA integrity_check` 校验，失败回滚并保留源库；请求/结果文件 `storage-relocation[-result].json` 读写）；`src/Modules/Storage/Mosaic.Storage.Sqlite/SqliteModule.cs:67-79`（manifest Provides 增加 `storage.relocation@1`）。
  - 启动期迁移：`src/Mosaic.Desktop/StartupStorageRelocation.cs`（新增）：`Bootstrapper.cs:35` 在 `HostSettingSource` 之后、模块打开数据库之前执行；崩溃恢复（源已删且目标存在 → 直接持久化新位置）；成功后 `HostSettingSource.PersistAsync` 写 `storage.location`/`storage.filename` 并写结果文件，失败保留源库并记录失败结果。
  - 维护契约与实现：`src/Modules/Maintenance/Mosaic.Maintenance.Contracts/MaintenanceContracts.cs`（新增 `MaintenanceAvailability/Info/UninstallRequest`、`IMaintenanceService`、`IApplicationLifecycle`、`UserDataRootGuard`）；`src/Modules/Maintenance/Mosaic.Maintenance.Windows/WindowsMaintenanceService.cs`（新增：读 `HKCU/HKLM` 卸载注册表判定 Installed/Portable，卸载/修复经 `WindowsProcessScheduler` 生成 PS 助手脚本等待父进程结束后执行 `msiexec /x` 或 `/famus`，可选删除数据目录前用 `UserDataRootGuard` 拒绝驱动器根/用户目录等危险路径）；`src/Modules/Maintenance/Mosaic.Maintenance.Windows/WindowsProcessScheduler.cs`（新增）。
  - 维护页 UI：`src/Modules/Maintenance/Mosaic.Maintenance.Wpf/`（新增 `MaintenanceUiModule`——Provides `app.maintenance@1`、Requires `storage.relocation@1` 与 `settings.api@1`，`Internal/MaintenanceViewModel.cs` 初始化展示当前位置/库大小/安装信息并一次性展示上次迁移结果、`Views/MaintenanceView.xaml` 存储迁移卡片 + 安装维护卡片、两份本地化）。
  - 设置分区：`src/Modules/Settings/Mosaic.Settings.Contracts/SettingsContracts.cs`（`SettingsGroup` 追加 `Maintenance`，仅追加不改既有枚举值）、`src/Modules/Settings/Mosaic.Settings.Wpf/Internal/SettingsViewModel.cs`（`SettingsSection`、分组映射、`SettingsSectionNameConverter`）、`Localization/zh.xaml`/`en.xaml`（`Settings.Section.Maintenance`）。
  - 生命周期：`src/Mosaic.Desktop/DesktopApplicationLifecycle.cs`（新增：经 `ShutdownCoordinator` 请求退出、经新进程 + 等待句柄请求重启）；`src/Mosaic.Desktop/App.xaml.cs`（注入）、`src/Mosaic.Desktop/Bootstrapper.cs`（`StartAsync` 增加 `IApplicationLifecycle?` 参数并传入 `ModuleRuntimeContext`，:35 调用 `StartupStorageRelocation`）。
  - 配置与装配迁移：`src/Mosaic.Desktop/AppConfiguration.cs:288-313`（新增 `ApplyMaintenanceMigration` + `maintenance-v1` 标记：既有安装补 `maintenance-ui` 模块与 `storage.relocation@1`/`app.maintenance@1` 绑定）；`src/Mosaic.Desktop/appsettings.json`（modules/providers/appliedMigrations 同步）；`src/Mosaic.Desktop/HostSettingsCatalog.cs`（移除公开 `storage.location` 文本设置定义）；`src/Mosaic.Desktop/BuiltInModuleCatalog.cs`（登记 maintenance 三项目）。
  - 发布工具修复：`build/package.ps1:14,34`、`build/publish.ps1:14,20` 原来引用 `%TEMP%/claude/83a0b83b7ae46749/`（未入仓）下的 Python 脚本且硬编码版本；改为仓库内 `scripts/release_content_verify.py`（根目录自定位、版本取自 `Directory.Build.props`）与 `scripts/package_payload.py`（同）。
  - 测试：`tests/Mosaic.Storage.Sqlite.Tests/StorageRelocationTests.cs`（新增 11 项：计划接受/拒绝、执行保留与删除源、目标已占用拒绝、请求往返、损坏请求）；`tests/Mosaic.Maintenance.Tests/`（新增工程，11 项：`UserDataRootGuard` 边界、`WindowsMaintenanceService.Info/RefreshAsync` 注册表一致性）；`tests/Mosaic.Wpf.Tests/MaintenanceViewModelTests.cs`（新增 10 项：初始化、便携禁用、一次性结果展示、计划失败、确认后调度并重启、卸载二次确认、退出被拒绝时取消挂起、STA 主题绑定渲染）；`tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs`（`Maintenance_migration_...` 4 场景：0.16.6 配置升级补模块与绑定）；`tests/Mosaic.Desktop.SmokeTests/StartupStorageRelocationTests.cs`（新增 4 项：排队迁移持久化新位置、keep=false 删源、崩溃窗口恢复、失败保留源并报结果）。
  - 边界与 API：`build/project-boundaries.json`（新增 maintenance 三项目并给 `Mosaic.Desktop` 加依赖）、`tests/Mosaic.Architecture.Tests/BoundaryTests.cs`（Windows 项目与白名单）、`build/public-api.txt`（登记全部新公共类型）、`tests/Mosaic.Wpf.Tests/Mosaic.Wpf.Tests.csproj`（加项目引用）、`Mosaic.sln`（挂入新工程）、`tests/Mosaic.Desktop.SmokeTests/packages.lock.json` 等锁文件随依赖更新。
  - 发布元数据：`Directory.Build.props`（0.17.0）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.17.0）、`build/release-content.json` 与 `build/published-release-index.json`（第 33 条，`previousReleaseId=mosaic-0.16.6`）、`build/release-manifest.json`、`docs/releases/v0.17.0.md`。
- 迁移逻辑与验收项：数据库迁移为「维护页确认 → 写请求文件 → 退出重启 → 启动阶段（`DatabaseWorker` 开库前）执行复制与校验 → 持久化新位置 → 维护页展示一次性结果」，默认保留旧库、可选删除，目标目录已有库直接拒绝且不触碰任何文件；崩溃窗口（复制完成但未写配置）下次启动自动恢复为新位置；失败保留源库并记录结果。既有安装升级由 `maintenance-v1` 迁移补齐模块与绑定（4 场景覆盖），与 0.16.3 自愈逻辑互补。`storage.location` 不再是公开可编辑设置，但仍是运行时覆盖键：`HostSettingSource.PersistAsync` 不做定义检查、`GetValue` 优先读运行时覆盖，维护页迁移成功后才写入。
- 验证：`dotnet build Mosaic.sln` Debug/Release 0 警告 0 错误；单元测试全绿（Storage.Sqlite 22、Maintenance 11、Wpf 131、Architecture 4、Auth.Core 25、Auth.Http 36、DesktopPresence 17、FloatingWindows 12、Kernel 7、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 5、Sync.Core 14、Tasks.Contract 7、Tasks.Core 27）；`Maintenance_migration_...` 4/4、新增 `StartupStorageRelocationTests` 4/4 直接运行通过；发布核验 `scripts/release_content_verify.py` PASS（33 条索引，0.17.0 指纹 `820F365C3E33B3E4A18030DE4BE6CE4A31F510FA9E98AD1CD7896611B2E2DBCE`）、`verify-published` PASS（`mosaic-0.17.0` 正式发布标记）、`test-package-shortcuts` PASS；安装包 `artifacts/installer/Mosaic-0.17.0-win-x64.msi`（52,134,332 字节，SHA-256 `B454DBF8141D6E4ACC1252134035A85CFEFA3DF2FC49BC9007544A086921CF16`）。
- 未验证项：真实 MSI 的应用内卸载/修复（msiexec /x、/famus）与「目标目录拒绝迁移」的端到端实机验收未执行；依赖真实指针/浮动窗口的既有 Smoke 用例在本机为环境性失败（与本次改动无关，0.16.5 起已知）。

## 账户设置与首次选择（工作区试用）

- 根因位置：
  - 账户入口与首启：`src/Mosaic.Shell.Wpf/ShellViewModel.cs`（旧）`InitializeAsync` 依据账户地址跳转独立 `account.home` 页；`src/Modules/Auth/Mosaic.Auth.Wpf/AuthUiModule.cs`（旧）以 `Navigation`/`Status` 贡献账户导航与 `account.status` 状态项；`AccountViewModel.LogoutAsync`（旧）在退出时清空 `auth.accountChoiceMade`。`AuthSettingsSource.Definitions` 未定义该标记，设置提交无法持久化它。
  - 新安装误判：`src/Mosaic.Desktop/App.xaml.cs`（旧）先构造单一实例并以设置 API 读取设置，`AppConfiguration.Load` 在 `Bootstrapper` 判断配置存在性之前就把默认 `appsettings.json` 复制到数据目录，导致全新空目录被分类为 Legacy。
  - Device Flow 轮询：`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs:783`（旧）以 `nextPollAtSeconds = 0` 启动 `PollDeviceAsync`，而 `PollDeviceAsync` 在 `DeviceSecondsLeft > pollAt` 时跳过轮询（`AccountViewModel.cs:805`），首次轮询实际发生在倒计时归零、已进入过期分支之后，浏览器批准永远不会被识别。该缺陷自 0.16.0 引入。
- 改动位置：
  - 设置页扩展点：`src/Mosaic.Presentation.Contracts/PresentationContracts.cs`（`SettingsPageContribution`、`ISettingsPageModule`）、`src/Mosaic.Presentation.Contracts/SettingsPageRegistry.cs`（新增）；`src/Mosaic.Desktop/BuiltInModuleCatalog.cs`、`src/Mosaic.Desktop/Bootstrapper.cs` 传递注册表与 capability 绑定。
  - 设置侧承载：`src/Modules/Settings/Mosaic.Settings.Contracts/SettingsContracts.cs`（`SettingsGroup.AccountsAndBackup`）、`Mosaic.Settings.Wpf/Internal/SettingsViewModel.cs`（`SelectPageAsync`、`SelectedPageView`、`HasSettingsSectionContent`）、`SettingsUiModule.cs`、`Internal/SettingsViewFactory.cs`、`Views/SettingsView.xaml`、`Localization/zh.xaml`、`Localization/en.xaml`。
  - 账户侧：`src/Modules/Auth/Mosaic.Auth.Wpf/AuthUiModule.cs`（只注册设置页，不再贡献 Navigation/Vertical 状态项）、`Internal/AuthViewFactory.cs`、`Internal/AccountViewModel.cs`、`Views/AccountView.xaml`、`Views/AccountView.xaml.cs`、`Localization/zh.xaml`、`Localization/en.xaml`；删除不再使用的 `Internal/AccountStatusItem.cs` 及其本地化键。
  - 首次选择：`src/Mosaic.Shell.Wpf/ShellViewModel.cs`（`InitializeAsync` 判断 `auth.accountChoiceMade`、`OnStartPageRequested`、`OnInitialChoiceCompleted`）、`ShellWindow.xaml.cs`；`src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthSettingsSource.cs` 定义 `auth.accountChoiceMade`；`AuthModule.cs` 声明 manifest 依赖与 `InitialAccountChoice`。
  - 安装分类：`src/Modules/Storage/Mosaic.Storage.Contracts/IDatabaseSession.cs`（`IInstallationState`、`InstallationKind`）、`Mosaic.Storage.Sqlite/Internal/DatabaseWorker.cs`（启动时在同一事务写入 `mosaic_startup_metadata('installation-classification-v1')`，重启时 `INSERT OR IGNORE` 保持原分类）、`SqliteModule.cs`（导出 `startup.installation-state@1`）、`src/Mosaic.Contracts/ModuleManifest.cs`（`ICapabilityProvider`）、`src/Mosaic.Kernel/ModuleRuntime.cs`（`Resolve<T>` 与激活前钩子）、`src/Modules/Settings/Mosaic.Settings.Sqlite/Internal/SettingsRepository.cs`（`InitializeAsync` 事务内写 `settings_initializations` 与缺失值）、`src/Modules/Settings/Mosaic.Settings.Sqlite/SettingsSqliteModule.cs`。
  - Device Flow 轮询修正：`AccountViewModel.StartDevicePolling` 改为以 `Math.Max(0, expiresInSeconds)` 作为首个轮询标记（`AccountViewModel.cs:783`），第一个循环即按服务端返回的 `verificationUriComplete` 授权结果轮询。
- 迁移逻辑与验收：无法区分新旧的数据目录一律按 Legacy（用户裁决）；仅当"配置在进程启动前不存在且数据库文件此前不存在"才判为 Fresh（`App.xaml.cs:48` 采集存在性，`DatabaseWorker.cs:47` 判定）；显式 false/true 均保留；分类与 choice 在同一事务内写入，失败回滚并可重试（`SettingsRepositoryTests.Failed_startup_migration_rolls_back_and_retries`）；旧安装若缺少标记会显示一次选择界面，选择后不再出现。设置 → 账户可手动重新打开开始页（`RequestStartPage`）。
- 退出行为：`AccountView.xaml` 账户设置状态只有一个统一退出图标（`AccountLogoutButton`）；`AccountViewModel.LogoutAsync` 在线时先确认、调用既有登出接口撤销会话并清理本机凭据，离线时不发网络请求，两种情况都返回开始页并 `RequestStartPage()`；普通退出不清除 `auth.accountChoiceMade`。
- 浏览器登录：`AccountViewModel.StartDeviceAsync` 在未配置服务器时先用 `AuthValidation.DefaultServerUrl` 配置官方服务器，再启动 Device Flow 并打开服务端返回的 `VerificationUriComplete`；只有"连接其他自建服务器"展开地址输入；"忘记密码"打开 `{serverBase}/device?mode=reset`。注册、验证码、密码与重置全部只在浏览器完成，桌面端不再有这些表单。
- 验证：`dotnet build Mosaic.sln` Debug/Release 均 0 警告 0 错误；`dotnet test Mosaic.sln` Debug 与 Release 均 372/372 通过（Wpf 122、Auth.Http 36、Tasks.Core 27、Auth.Core 25、Settings.Core 25、Presence 17、ReleaseSummary 17、Sync.Core 14、FloatingWindows 12、Storage.Sqlite 12、Kernel 7、Tasks.Contract 7、Settings.Sqlite 5、Architecture 4、Smoke 42）。新增测试覆盖：在线退出（调用一次登出、回到开始页、标记保留、发出 `StartPageRequested`）、离线退出（0 次网络调用）、开始页在设置页内显示、离线/自建服务器/浏览器批准三条路径完成首启选择、Device 批准轮询。
- 输入框视觉修正：`src/Modules/Auth/Mosaic.Auth.Wpf/Views/AccountView.xaml`（工作区）`FieldBox` 原先只设置高度、内边距与外边距，沿用全局 `TextBox` 的浅色默认背景/边框；在深色主题下，「连接其他自建服务器」展开的服务器地址输入框与周围卡片对比突兀。现让 `FieldBox` 使用 `SurfaceMutedBrushKey` 背景、`ControlHoverBorderBrushKey` 边框、`TextPrimaryBrushKey` 前景，并统一垂直居中与等宽字体；`ServerUrlInput` 单独增大字号并补 `AutomationProperties.Name`。改动只影响账户设置页展开的服务器地址输入框，不改变校验、持久化或 Device Flow。
- 验证：`dotnet build src/Modules/Auth/Mosaic.Auth.Wpf/Mosaic.Auth.Wpf.csproj -c Release` 0 警告 0 错误。
- 未验证项：0.16.6 MSI 的安装/升级路径未再实测（前一版 0.16.4 → 工作区旧库升级已单独验证）；Smoke 中依赖真实指针的浮动窗口用例偶发失败，重跑即通过；capability 解析只用单元测试覆盖绑定错误，未接入替代 provider 实测；未使用真实账号、SMTP 或浏览器 Cookie；输入框视觉调整尚未实机截图复核。

### 0.16.6 本地试用包（2026-11-20）

- 版本号：`Directory.Build.props`、`build/installer/Mosaic.Installer.wixproj` 由 0.16.5 提升到 0.16.6。本机注册表中 0.16.8 的 DisplayVersion 与可执行文件 0.16.5 不一致，属上一轮试用残留；安装器 `MajorUpgrade` 会覆盖文件。
- 根因位置：`build/installer/Package.wxs` 的 LaunchCondition 引用 `WINDOWSBUILD`，而属性声明为 `WindowsBuild`，WiX ICE46 大小写校验令打包失败（MSI 属性名不区分大小写，功能上无影响）。改动位置：`Mosaic.Installer.wixproj` 的 `SuppressIces` 增加 ICE46 并注明原因。
- 发布说明：`build/release-content.json`、`build/published-release-index.json` 新增 0.16.6 条目，指纹 `EEEAD1058CDF7B8762BCED82DB042B1D36A3BC064C5D9ED486040BC40306AD5D`；`docs/releases/v0.16.6.md` 由校验脚本生成。
- 打包命令：`build/package.ps1 -ProductVersion 0.16.6`（首次因 ICE46 失败，抑制后 `-NoPublish` 重跑成功）；`build/test-package-shortcuts.ps1` 通过。
- 产物：`artifacts/installer/Mosaic-0.16.6-win-x64.msi`，52,113,780 字节，SHA-256 `533D68E6E857C7F320DEFC7905DA9E10AA90CB4C1CB5C141850EDDAD11819BB4`。
- 内容核验：`msiexec /a` 管理安装提取后 `Mosaic.Desktop.dll`、`Mosaic.Auth.Wpf.dll`、`release-manifest.json` 的 SHA-256 与 `artifacts/local-run` 完全一致，manifest `currentReleaseId = mosaic-0.16.6`（32 条发布记录）。

## 0.16.5：账号入口移入左下角、四选一登录流程、退出登录回到账号选择（2026-11-19）

- 根因位置：
  - 账号入口位置：`src/Modules/Auth/Mosaic.Auth.Wpf/AuthUiModule.cs`（0.16.4 版）`Navigation` 属性注册 `account.navigation`，账号入口占据左侧导航栏。
  - 第二套首启机制：`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs`（616d9eb 版）以 `StartView`/`ShowWelcomeLayout`/`SetThemeCommand` 实现深色两栏首启欢迎布局，与主内容区浅色视觉规范割裂。
  - 左下角无账号状态与退出入口：`src/Mosaic.Shell.Wpf/ShellWindow.xaml`（0.16.4 版）状态面板只渲染右下角横向状态项；`IStatusContribution` 无「侧栏放置」与「动作按钮」概念，模块无法在左下角呈现账号卡片或提供退出登录。
  - 退出登录不清首启选择：`AccountViewModel.LogoutAsync`（0.16.4 版 :658）与 `AccountStatusItem.SignOutAsync` 只撤销服务器会话，不清理 `auth.accountChoiceMade`。
- 改动位置：
  - 契约扩展：`src/Mosaic.Presentation.Contracts/PresentationContracts.cs`（新增 `StatusPlacement { None, Bottom }`；`IStatusContribution` 新增 `SidebarPlacement`/`CanAct`/`ActionGlyph`/`ActionLabel`/`ActionToolTip`/`ActionRequested`/`RequestAction()`，均带默认实现，二进制兼容）；`build/public-api.txt`（登记 `StatusPlacement`）。
  - 账号入口与退出登录：`src/Modules/Auth/Mosaic.Auth.Wpf/AuthUiModule.cs`（`Navigation => []`；`_statusItem` 5 参构造）；`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountStatusItem.cs`（全量重写：`SidebarPlacement=Bottom`、`CanAct`、MDL2 Leave 动作按钮、`SignOutAsync` = 确认→`IAuthApi.LogoutAsync`→清 `auth.accountChoiceMade`→`ActionRequested`，新增 `RequestActionAndWaitForIdleAsync`）。
  - 四选一流程：`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs`（删除 `StartView`/welcome/主题切换；新增 `ShowAccountChoice`；`LogoutAsync` 增加 `ClearAccountChoiceAsync`；`ModeTitle`/`ModeDescription` 改用 `Auth.Choice.*`；`InitializeAsync` 简化）；`src/Modules/Auth/Mosaic.Auth.Wpf/Views/AccountView.xaml`（登录区重排为浏览器登录/邮箱密码/离线/自建服务器四选一，`AutomationId="AccountChoice"`）；`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountConverters.cs`（随回退清理）；`src/Modules/Auth/Mosaic.Auth.Wpf/Localization/zh.xaml`/`en.xaml`（新增 5 个键）。
  - Shell 通用渲染：`src/Mosaic.Shell.Wpf/ShellViewModel.cs`（`NavigateDirectAsync` 回退、首启重定向条件、`SidebarStatusItems` 与 `ActionRequested` 订阅、`StatusActionCommand`、`DisposeAsync` 退订）；`src/Mosaic.Shell.Wpf/ShellWindow.xaml`（左下角账号卡片按 `SidebarStatusItems` 通用渲染）；`src/Mosaic.Shell.Wpf/NavigationConverters.cs`（`StatusActionAvailableConverter`/`StatusActionHiddenConverter`）。
  - 测试：`tests/Mosaic.Wpf.Tests/AccountViewModelTests.cs`（删除 5 个 welcome 测试；新增首启选择态、离线写标记、已登录不显示选择、仅配置服务器不算选择、退出清标记共 5 个；`Status_item_...` 改 5 参构造并覆盖 `SidebarPlacement`/`CanAct`/退出流程）。
  - 发布元数据：`Directory.Build.props`（0.16.5）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.16.5）、`docs/releases/v0.16.5.md`、`docs/releases/v0.16.5-files.md`、`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`（第 31 条，`previousReleaseId=mosaic-0.16.4`）、`README.md`。
- 对已有安装的影响：无数据目录、配置文件、安装升级或模块装配改动，无需迁移逻辑与迁移验收项；首启重定向要求 `auth.serverUrl` 空白，0.16.4 及更早已配置服务器的安装行为不变；退出登录清 `auth.accountChoiceMade` 但保留服务器地址，当次立即回到账号选择，下次启动不强制重定向；`IStatusContribution` 新成员均为默认实现，其他模块无需改动。
- 验证：全量测试 372 项全绿（15 个测试工程：Wpf.Tests 119、SmokeTests 42、Architecture 4、Auth.Core 25、Auth.Http 36、Sync.Core 14、Settings.Core 25、Kernel 5 等），Debug/Release 构建均 0 警告 0 错误；发布核验 `release_content_verify.py` PASS（31 条索引，指纹 `435359A7C8E6037DAA674054E7567DA1DD830791A7A8482074171D738DE8D526`）、`verify-published` PASS、`test-package-shortcuts` PASS。
- 安装包：`artifacts/installer/Mosaic-0.16.5-win-x64.msi`（52,089,204 字节，SHA-256 `73A69D6DA4A000DB425B08A4C6E7A8C81B28F715C14E2FD0DD51F16EE19E77D9`）。
- 已修复的发布前缺陷：`src/Modules/Auth/Mosaic.Auth.Wpf/Localization/zh.xaml:41` 与 `en.xaml:41` 的 `Auth.Logout.Confirm` 重复定义导致 XAML 解析失败（启动弹「无法启动」），已删除重复 key。
- 未验证项：363 项全量测试中 2 项真实指针 Smoke 场景（`Floating_real_pointer_moves_resizes_clicks_and_restores_saved_bounds`、`Floating_header_buttons_move_on_drag_and_still_click_on_short_press`）在打包环境失败，属历史已知环境依赖（见 0.16.2 小节未验证项），与本次改动无关；MSI 实际安装/卸载不在本机执行。

## 0.16.4：默认服务器建议官方实例、右下角账号状态栏、导航图标统一（2026-11-19）

- 根因位置：
  - 默认服务器地址：`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs`（0.16.3 版 :139）构造函数 `_serverInput = ServerText`，未配置服务器时 `ServerText`（:194）为空字符串，全新安装的账户页服务器输入框空白，用户无从得知官方实例地址。
  - 账号显示：`src/Mosaic.Shell.Wpf/ShellWindow.xaml`（0.16.3 版）内容区仅导航行+内容行两行，无状态栏区域，登录状态只能打开账户页查看。
  - 导航图标：`src/Mosaic.Shell.Wpf/NavigationConverters.cs`（0.16.3 版）把图标名映射到风格不一的字形（文本 `✓`、Unicode 杂项符号），`ShellWindow.xaml` 另嵌手绘 `Path` 齿轮，大小、描边、明暗主题对比度不统一。
- 改动位置：
  - 默认服务器地址：`src/Modules/Auth/Mosaic.Auth.Contracts/AuthContracts.cs`（`AuthValidation` 新增 `DefaultServerUrl = "https://mosaic.bansonic.top"`）；`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs`（新增 `DefaultServerInput`（:198），构造函数（:130）与 `OnLanguageChanged`（:1189）改用之）；`src/Modules/Auth/Mosaic.Auth.Wpf/Localization/zh.xaml`/`en.xaml`（服务器面板提示）；`src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthSettingsSource.cs:15`、`src/Modules/Settings/Mosaic.Settings.Wpf/Localization/zh.xaml`/`en.xaml`（设置项描述）。`auth.serverUrl` 默认值仍为空，仅输入框预填建议。
  - 右下角账号状态栏：`src/Mosaic.Presentation.Contracts/PresentationContracts.cs`（新增 `IStatusContribution` 契约与 `IUiModule.Status` 默认成员）与 `build/public-api.txt`；`src/Mosaic.Shell.Wpf/ContributionRegistry.cs`（`Register` 第三参数可选，`Rebuild`/`Revoke`/`Clear` 同步状态集合）；`src/Mosaic.Shell.Wpf/ShellViewModel.cs`（`StatusItems`、`NavigateToViewCommand`）；`src/Mosaic.Shell.Wpf/ShellWindow.xaml`（第三行 36px 状态栏，空时折叠）；`src/Mosaic.Shell.Wpf/ShellWindow.xaml.cs`（紧凑侧栏边距）；`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountStatusItem.cs`（新增，观察 `IAuthApi` 与本地化）；`src/Modules/Auth/Mosaic.Auth.Wpf/AuthUiModule.cs`（`Status` 属性、激活创建/停止释放）；`src/Modules/Auth/Mosaic.Auth.Wpf/Localization/zh.xaml`/`en.xaml`（状态文案）；`src/Mosaic.Desktop/Bootstrapper.cs`（传入 `ui.Status`）。Shell 泛化渲染、不引用 Auth 契约，保持「Shell 不写 if(auth)」。
  - 导航图标：`src/Mosaic.Shell.Wpf/NavigationConverters.cs`（统一映射 Segoe MDL2 Assets 字形：CheckList/Folder/Calendar/Settings/Contact，回退 Document）；`src/Mosaic.Shell.Wpf/ShellWindow.xaml`（字形统一字体与字号 16、宽 22，删除手绘 Path 与对应 DataTrigger）。
  - 测试：`tests/Mosaic.Wpf.Tests/AccountViewModelTests.cs`（`FakeAuth` 真实事件；新增默认预填、已配置保持、状态项三态共 3 个测试）；`tests/Mosaic.Wpf.Tests/PresentationTests.cs`（导航字形断言更新；新增状态栏泛化渲染与跳转测试、`StatusStub`）。
  - 发布元数据：`Directory.Build.props`（0.16.4）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.16.4）、`docs/releases/v0.16.4.md`、`docs/releases/v0.16.4-files.md`、`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`（第 30 条，`previousReleaseId=mosaic-0.16.3`）、`README.md`。
- 对已有安装的影响：无数据目录、配置文件、安装升级或模块装配改动，无需迁移逻辑与迁移验收项；`auth.serverUrl` 默认值仍为空（只预填输入框、不自动联网），已保存地址的安装行为不变；`IUiModule.Status` 为默认接口成员，其他模块无需改动；无状态贡献时状态栏折叠，既有窗口布局不变；导航契约字段不变，仅渲染层字形变化。
- 验证：Release 全量测试 357 项全绿（exit 0，含 SmokeTests 42 项、Wpf.Tests 114 项：新增 4 个本版本测试，针对 `artifacts/local-run` 0.16.4 构建运行）；发布核验 `release_content_verify.py` PASS（30 条索引，指纹 `30BEE9458037456D3755AE3E0A18DECE7DEBC7FB0C719D1D669331B5DD2E5559`）、`verify-published` PASS、`test-package-shortcuts` PASS。
- 安装包：`artifacts/installer/Mosaic-0.16.4-win-x64.msi`（52,093,300 字节，SHA-256 `984322D09AB1CD58904E080E249FB849CFD895986E5D95D186B733FA7C6191C5`）。

## 0.16.3：安装包自带配置修复——每次启动自动补齐缺失的模块能力绑定（2026-11-19）

- 根因位置：`src/Mosaic.Desktop/AppConfiguration.cs` `Load`——读取 appsettings.json 的 `modules`/`providers` 后未与内置默认绑定合并就交给装配校验。模块升级新增能力时（如 0.16.0 新增 `tasks.transfer-repository@1`），既有安装的旧配置缺这条绑定，除非恰好写了补绑定迁移，否则 `src/Mosaic.Kernel/ModuleGraph.cs` `ModuleGraph.Resolve`（:20-21）启动时抛 `Provider binding is missing or incompatible: …`（0.16.2 的 `ApplyAuthSyncMigration` 只补了那一次的两条，机制上仍依赖「每次新增能力都记得写迁移」）。
- 改动位置：
  - `src/Mosaic.Desktop/AppConfiguration.cs`：`Load` 在首次 `ValidateCoreConfiguration` 前调用新增的 `RepairProviderBindings`；该方法按生效层（`settings.host.modules`/`settings.host.providers` 覆盖优先，否则顶层）读取模块与绑定，对每个已启用且在内置目录（`BuiltInModuleCatalog.Definitions`）中的模块，用其 manifest `Provides` 以 `TryAdd` 补齐缺失绑定——显式绑定保留、未知模块交给既有声明校验、禁用模块不产生孤儿绑定；有覆盖层写回 `settings.host.providers`，无覆盖写回顶层；无缺失不写盘，有补齐走既有 `WriteConfiguration`（含 `.pre-migration` 备份）。MSI Repair/重装不触碰配置目录，修复后首次启动即补齐，WiX 无需改动。
  - `tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs`：新增 `Startup_repairs_capability_bindings_missing_from_an_upgraded_configuration`（top/both/modules/providers 4 场景：全部迁移已应用、缺 transfer 两条与 `sync.api@1`，断言自愈写回正确生效层、`.pre-migration` 等于原文、进程保持运行）；新增 `Healthy_configuration_keeps_its_bindings_and_file_untouched`（健康配置 Load 后文件逐字节不变、无备份文件）；新增 `Repair_preserves_explicit_bindings_and_skips_disabled_modules`（显式绑定保留、缺失补齐、禁用模块无孤儿绑定）；更新 `Configuration_presence_migration_is_validated_persisted_and_one_time` 的 `already-migrated-disabled` 场景为「只发生自愈补齐、迁移标记不变」。
  - `Directory.Build.props`（0.16.3）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.16.3）、`docs/releases/v0.16.3.md`、`docs/releases/v0.16.3-files.md`、`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`（第 29 条，`previousReleaseId=mosaic-0.16.2`）、`README.md`。
- 安装/修复验收项：模拟旧版配置（缺新增绑定）升级启动自动补齐并成功启动（4 场景 Smoke 覆盖，含覆盖层组合）；用户自定义绑定不被覆盖；禁用模块不产生孤儿绑定；MSI Repair/重装后首次启动同样补齐（安装器本就不写配置，启动即自愈）。新模块的启用仍由迁移负责，自愈不会静默启用任何模块。
- 验证：Release 全量测试 354 项全绿（exit 0，含 SmokeTests 42 项：新增 3 个自愈测试 + 4 场景进程级验收 + presence 迁移场景更新，针对 `artifacts/local-run` 0.16.3 构建运行）；发布核验 `release_content_verify.py` PASS（29 条索引，指纹 `ED06CAC492A0BB6D58279F6612136CDF3B9C14D1AE28748C3680B28F5E64C68D`）、`verify-published` PASS。
- 安装包：`artifacts/installer/Mosaic-0.16.3-win-x64.msi`（52,105,588 字节，SHA-256 `42E809211788CEE2F11AC339555FE2AE679A3C2E2BFD80B5B3586B80DFD39553`）。## 0.16.3：安装包自带配置修复——每次启动自动补齐缺失的模块能力绑定（2026-11-19）

- 根因位置：`src/Mosaic.Desktop/AppConfiguration.cs` `Load`——读取 appsettings.json 的 `modules`/`providers` 后未与内置默认绑定合并就交给装配校验。模块升级新增能力时（如 0.16.0 新增 `tasks.transfer-repository@1`），既有安装的旧配置缺这条绑定，除非恰好写了补绑定迁移，否则 `src/Mosaic.Kernel/ModuleGraph.cs` `ModuleGraph.Resolve`（:20-21）启动时抛 `Provider binding is missing or incompatible: …`（0.16.2 的 `ApplyAuthSyncMigration` 只补了那一次的两条，机制上仍依赖「每次新增能力都记得写迁移」）。
- 改动位置：
  - `src/Mosaic.Desktop/AppConfiguration.cs`：`Load` 在首次 `ValidateCoreConfiguration` 前调用新增的 `RepairProviderBindings`；该方法按生效层（`settings.host.modules`/`settings.host.providers` 覆盖优先，否则顶层）读取模块与绑定，对每个已启用且在内置目录（`BuiltInModuleCatalog.Definitions`）中的模块，用其 manifest `Provides` 以 `TryAdd` 补齐缺失绑定——显式绑定保留、未知模块交给既有声明校验、禁用模块不产生孤儿绑定；有覆盖层写回 `settings.host.providers`，无覆盖写回顶层；无缺失不写盘，有补齐走既有 `WriteConfiguration`（含 `.pre-migration` 备份）。MSI Repair/重装不触碰配置目录，修复后首次启动即补齐，WiX 无需改动。
  - `tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs`：新增 `Startup_repairs_capability_bindings_missing_from_an_upgraded_configuration`（top/both/modules/providers 4 场景：全部迁移已应用、缺 transfer 两条与 `sync.api@1`，断言自愈写回正确生效层、`.pre-migration` 等于原文、进程保持运行）；新增 `Healthy_configuration_keeps_its_bindings_and_file_untouched`（健康配置 Load 后文件逐字节不变、无备份文件）；新增 `Repair_preserves_explicit_bindings_and_skips_disabled_modules`（显式绑定保留、缺失补齐、禁用模块无孤儿绑定）；更新 `Configuration_presence_migration_is_validated_persisted_and_one_time` 的 `already-migrated-disabled` 场景为「只发生自愈补齐、迁移标记不变」。
  - `Directory.Build.props`（0.16.3）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.16.3）、`docs/releases/v0.16.3.md`、`docs/releases/v0.16.3-files.md`、`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`（第 29 条，`previousReleaseId=mosaic-0.16.2`）、`README.md`。
- 安装/修复验收项：模拟旧版配置（缺新增绑定）升级启动自动补齐并成功启动（4 场景 Smoke 覆盖，含覆盖层组合）；用户自定义绑定不被覆盖；禁用模块不产生孤儿绑定；MSI Repair/重装后首次启动同样补齐（安装器本就不写配置，启动即自愈）。新模块的启用仍由迁移负责，自愈不会静默启用任何模块。
- 验证：Release 全量测试 354 项全绿（exit 0，含 SmokeTests 42 项：新增 3 个自愈测试 + 4 场景进程级验收 + presence 迁移场景更新，针对 `artifacts/local-run` 0.16.3 构建运行）；发布核验 `release_content_verify.py` PASS（29 条索引，指纹 `ED06CAC492A0BB6D58279F6612136CDF3B9C14D1AE28748C3680B28F5E64C68D`）、`verify-published` PASS。
- 安装包：`artifacts/installer/Mosaic-0.16.3-win-x64.msi`（52,105,588 字节，SHA-256 `42E809211788CEE2F11AC339555FE2AE679A3C2E2BFD80B5B3586B80DFD39553`）。
- 未验证项：见 0.16.2 小节（MSI 实际安装/卸载、依赖真实指针的既有 Smoke 场景）。

## 0.16.2：修复 ≤0.15.3 升级安装无法启动——auth-sync 迁移缺任务传输绑定（2026-11-19）

- 根因位置：`src/Mosaic.Desktop/AppConfiguration.cs` `ApplyAuthSyncMigration`（0.16.0 版 :207-234）——`auth-sync-v1` 迁移为既有安装注册 auth/sync 五条绑定时，遗漏了同一提交（d0ccccd）随任务传输流水线新增的 `tasks.transfer-repository@1`（`src/Modules/Storage/Mosaic.Tasks.Sqlite/TasksSqliteModule.cs:15` Provides）与 `tasks.transfer@1`（`src/Modules/Tasks/Mosaic.Tasks.Core/TasksModule.cs:40` Provides）；仓库内 `src/Mosaic.Desktop/appsettings.json` 顶层 `providers` 表同样缺这两条。`src/Mosaic.Kernel/ModuleGraph.cs` `ModuleGraph.Resolve`（:20-21）要求每个 Provides 能力都有指向该模块的绑定，因此从 ≤0.15.3 升级的既有安装（providers 仅 14 条）启动解析时抛 `Provider binding is missing or incompatible: tasks.transfer-repository@1`，以「模块依赖或能力绑定无效」弹窗退出。0.16.0 即携带该缺陷（全新安装的仓库配置也缺绑定），但 0.16.0/0.16.1 发布时桌面 Smoke 未重跑、实机安装未验证，未暴露。
- 改动位置：
  - `src/Mosaic.Desktop/AppConfiguration.cs` `ApplyAuthSyncMigration`：补 `providers.TryAdd("tasks.transfer-repository@1", "tasks-sqlite")` 与 `providers.TryAdd("tasks.transfer@1", "tasks-core")`（附缺陷成因注释）。
  - `src/Mosaic.Desktop/appsettings.json`：顶层 `providers` 补 `"tasks.transfer-repository@1": "tasks-sqlite"` 与 `"tasks.transfer@1": "tasks-core"`。
  - `tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs`：新增 `Auth_sync_migration_binds_the_task_transfer_capabilities`（top/both/modules/providers 4 场景），以 0.15.3 形状配置启动真实进程，断言迁移写回 transfer 两条与 auth/sync 绑定、`.pre-migration` 保留、迁移后进程保持运行。
  - `Directory.Build.props`（0.16.2）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.16.2）、`docs/releases/v0.16.2.md`、`docs/releases/v0.16.2-files.md`、`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`（第 28 条，`previousReleaseId=mosaic-0.16.1`）、`README.md`。
- 迁移验收项：0.15.3→0.16.2 启动时自动补全绑定并标记 `auth-sync-v1`、写 `.pre-migration` 备份，无需手动改配置；0.16.0/0.16.1 期间从未成功启动的安装走同一迁移路径；已成功启动（或全新安装）的安装迁移标记已存在，配置不变；`host.providers` 覆盖层同步补写。已应用过迁移的安装不受影响（迁移幂等）。
- 验证：实机复现用户故障——以 0.15.3 形状配置（11 模块、14 绑定、无 auth-sync-v1）启动修复后的 `artifacts/local-run` 构建，进程保持运行、迁移写回 `tasks.transfer-repository@1=tasks-sqlite`、`tasks.transfer@1=tasks-core`、`sync.api@1=sync-core`、`auth.transport@1=auth-http`，`.pre-migration` 保留；Release 全量测试 317 项（Auth/Sync/Tasks/Wpf 等）全绿（exit 0），新增 4 场景 Smoke 理论用例随构建编译通过；发布核验 `verify-published` PASS（manifest `mosaic-0.16.2`）、MSI 快捷方式测试 PASS；安装包 `artifacts/installer/Mosaic-0.16.2-win-x64.msi`（52,089,204 字节，SHA-256 `CA864899BF09B715FC8F2657BE775B6975D756F2E3B524857D5334047EA08565`）。
- 未验证项：MSI 的实际安装/卸载不在本机执行（本机存在正在运行的真实安装）；与本缺陷无关的既有 Smoke 场景（presence/summary/floating 迁移等）依赖真实指针，全量 Smoke 按惯例仅选择性运行。

## 0.16.1：对齐服务端 0.1.1 wire——备份白名单收紧、账户管理与 Device Flow 修正（2026-11-19）

- 根因位置：
  - 备份设置白名单过宽：`src/Modules/Sync/Mosaic.Sync.Core/Internal/SyncService.cs`（0.16.0 版 :16-17）的 `PortablePrefixes = ["tasks.", "ui.text."]` 与 `IsPortable`（0.16.0 版 :200-202）的前缀匹配，使 `tasks.revisionStart`、`tasks.floating.allowNotesEditing`、`ui.text.*` 等宿主/机器相关设置进入备份；服务端 0.1.1 对 setting 条目强制 14 键精确白名单，未知键拒绝整个备份并返回 400 `sync.invalid` / `backup.invalid`，即 0.16.0 客户端一旦修改过这些设置就无法成功上传备份。
  - 设备授权轮询：服务端 0.1.1 将 `slow_down` 从 429 改为 200 `{status:"slow_down", interval:2}`；0.16.0 的 `HttpAuthTransport.PollDeviceAuthorizationAsync` 未解析该状态，`AccountViewModel` 的取模轮询会把 slow_down 当作普通 pending 以固定间隔继续轮询，越轮越慢却从不退避。
  - 浏览器打开链接：0.16.0 在服务端下发 `verificationUriComplete` 之前就把裸 `verificationUri`（不含授权码）交给浏览器，用户需要手动输入授权码且输入错误无从校验；0.1.1 明确要求优先使用完整链接。
- 改动位置：
  - 契约 `src/Modules/Auth/Mosaic.Auth.Contracts/AuthContracts.cs`：`DevicePollDto` 新增 `SlowDownIntervalSeconds`；新增 `AuthSessionInfoDto(Id, DeviceLabel, Platform, CreatedAtUtc, LastSeenAtUtc, ExpiresAtUtc, Current)`；`AuthErrorCodes` 新增 `ReauthRequired`、`SessionRevoked`；`AuthEmailCodePurpose` 新增 `DeleteAccount`；`IAuthTransport.StartDeviceAuthorizationAsync` 增加 `appVersion` 参数，新增 `ListSessionsAsync`/`RevokeSessionAsync`/`RevokeAllSessionsAsync`/`DeleteAccountAsync`；`IAuthApi.PollDeviceAuthorizationAsync` 返回类型改为 `OperationResult<DevicePollDto>`，新增同名四个账户管理方法。`src/Modules/Sync/Mosaic.Sync.Contracts/SyncContracts.cs`：`BackupSnapshotDto`、`BackupUploadResult` 新增 `ServerRevision`（long）与 `Cursor`（string?）。`build/public-api.txt` 登记 `AuthSessionInfoDto`。
  - 账户传输 `src/Modules/Auth/Mosaic.Auth.Http/HttpAuthTransport.cs`：`StartDeviceAuthorizationAsync` 请求体补 `platform=windows` 与 `appVersion`，`verificationUriComplete` 优先、缺失时以服务器基址构造 `device?user_code=<userCode>`；`PollDeviceAuthorizationAsync` 识别 200 `status=slow_down` 返回 `Pending` 并携带 `SlowDownIntervalSeconds`（下限 2）；新增 `ListSessionsAsync`（兼容根数组与 `{sessions:[...]}` 两种 JSON 形状，`DateTimeOffset.TryParse` + InvariantCulture + AssumeUniversal）、`RevokeSessionAsync`（`POST v1/auth/sessions/{id}/revoke`）、`RevokeAllSessionsAsync`（`POST v1/auth/sessions/revoke-all`，`keepCurrent=true` 时带 `{keepCurrent:true}` 请求体）、`DeleteAccountAsync`（`POST v1/auth/account/delete`，body `{password, code}`）；`MapCode`/`FallbackMessage` 新增 `auth.reauth_required`、`auth.session_revoked`。
  - 备份传输 `src/Modules/Sync/Mosaic.Sync.Http/HttpSyncTransport.cs`：`TryReadSnapshot` 解析 `serverRevision`（`TryGetInt64`）与 `cursor`。
  - 账户服务 `src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthService.cs`：新增 `AppVersion`（`AssemblyInformationalVersion` 去掉 `+` 后缀，回退程序集版本）；`PollDeviceAuthorizationAsync` 透传 slow_down 间隔；新增 `AccessTokenOrFailureAsync` 与 `ListSessionsAsync`/`RevokeSessionAsync`/`RevokeAllSessionsAsync`/`DeleteAccountAsync`（校验密码/验证码二选一，成功后持锁 `secrets.ClearAsync` 并回到 `SignedOut`）；`Internal/GuardedAuthApi.cs` 转发四个新成员与新的 Poll 返回类型。
  - 备份服务 `src/Modules/Sync/Mosaic.Sync.Core/Internal/SyncService.cs`：删除 `PortablePrefixes`，`IsPortable`（现 :218-219）改为 `SettingPersistence.UserSettings && PortableIds.Contains(definition.Id)`；`PortableIds` 为与服务端一致的 14 个精确键（`ui.language`、`appearance.theme`、`tasks.sortOrder`、`tasks.defaultFilter`、`tasks.archive.autoEnabled`、`tasks.archive.delayDays`、`tasks.title.maxCharacters`、`tasks.notes.maxCharacters`、`tasks.title.required`、`tasks.defaultCompleted`、`tasks.confirmDelete`、`tasks.confirmUnsaved`、`tasks.showCharacterCount`、`tasks.autoSwitchToAllOnHidden`）；`PortableSettingsAsync`（现 :201-216）对白名单外定义静默跳过；`PrepareRestoreAsync`（现 :149-186）恢复侧同样只应用白名单键；`UploadAsync`（:85-113）与下载路径透传 `ServerRevision`/`Cursor`。
  - 账户页 `src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs`：新增 `Sessions` 集合与 `RefreshSessionsCommand`/`RevokeSessionCommand`/`RevokeAllSessionsCommand`/`ShowDeleteDialogCommand`/`CancelDeleteCommand`/`SendDeleteCodeCommand`/`ConfirmDeleteCommand`；`PollDeviceAsync` 改为按 `pollAt` 绝对时间点调度（`NextPollAt(secondsLeft, interval)`），slow_down 时 `slowDownIncrement += interval`、`effectiveInterval = Clamp(base + increment, base, 30)`；删除账号支持密码/验证码双模式、6 位校验、验证码冷却与 `_dialogs.ConfirmAsync` 二次确认，成功后清凭据并提示；`InitializeAsync` 已登录时自动拉取会话列表。`Internal/AccountConverters.cs`（新增 `LocalDateTimeConverter`、`CurrentSessionTagConverter`）、`Internal/AuthLocalization.cs`（`ReauthRequired`/`SessionRevoked` 映射）、`Views/AccountView.xaml`（「已登录会话」卡与「删除账号」卡、转换器资源注册）、`Localization/en.xaml`、`zh.xaml`（`Auth.Sessions.*`、`Auth.Delete.*`、两条错误文案）。
  - 测试：`tests/Mosaic.Auth.Core.Tests/AuthServiceTests.cs`（Poll 断言改比较 `DevicePollDto.Status`）、`Fakes.cs`（`FakeTransport` 记录 `LastAppVersion` 与账户管理调用）；`tests/Mosaic.Auth.Http.Tests/HttpAuthTransportTests.cs` 新增 12 个用例（`verificationUriComplete` 优先、无则构造 `/device?user_code=`、slow_down→Pending+间隔 2、会话列表两种形状、撤销单会话/全部、删除账号请求体、两条新错误码映射、未知 `details` 字段忽略、purpose 线格式 `delete-account`）；`tests/Mosaic.Sync.Core.Tests/SyncServiceTests.cs` 白名单用例改为精确键（`ui.text.placeholder` 不再上传），新增「`tasks.revisionStart`/`tasks.floating.allowNotesEditing` 等旧前缀命中键不上传」与「14 键全定义时精确上传 14 键」两个证明用例、`Fakes.cs`（`FakeAuthApi` 补新成员）；`tests/Mosaic.Wpf.Tests/AccountViewModelTests.cs`（`FakeAuth` 补会话/删除记录，新增加载会话列表、撤销会话、删除必须二次确认、验证码 6 位校验、发送删除验证码 5 个用例）。
  - 发布元数据 `Directory.Build.props`（版本 0.16.1）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.16.1）、`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`（第 27 条，`previousReleaseId=mosaic-0.16.0`）、`README.md`、`docs/releases/v0.16.1.md`、`docs/releases/v0.16.1-files.md`。
- 关系：服务端 0.1.1（`server/` commit `4a223be`）已完成冻结，本次为桌面端 0.16.0 对其 wire 的对齐交付；`ChangeDto` 增量同步属服务端预留，桌面第一阶段为显式上传/下载、无增量实现，故只透传备份响应的 `serverRevision`/`cursor` 字段；`GET /v1/device/info` 与 410 `sync.cursor_invalid` 属增量同步路径，桌面暂不使用。不改 `server/`、`mobile/`。
- 兼容与迁移：不涉及数据目录、配置文件、安装升级或模块装配改动——数据库表结构、既有设置键、导航契约、`appsettings.json` 与 `auth-sync-v1` 迁移均未变化，0.16.0 安装直接升级；升级后第一次备份上传自动按 14 键白名单精简设置项，已保存在服务器上的多余键下载时被静默忽略（不再触发 400，也不会写回本机）。
- 验证：Release 全量重建 0 警告 0 错误；非桌面测试全绿——Architecture 4、Auth.Core 25、Auth.Http 36、DesktopPresence.Core 17、FloatingWindows 12、Kernel 5、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 1、Storage.Sqlite 12、Sync.Core 14、Tasks.Contract 7、Tasks.Core 27、Wpf 110。
- 发布内容：`artifacts/installer/Mosaic-0.16.1-win-x64.msi`（52 605 300 字节，SHA-256 `30FD96C499B70BEAE94E241F05A96C5E8385645C2FE8B6569877B9D1E400715F`，自包含 .NET 10.0.15 运行时；数据库回读 ProductVersion=0.16.1、Application 所属桌面快捷方式与卸载注册表清理经 `build/test-package-shortcuts.ps1` 校验通过）；发布产物 `artifacts/local-run` 通过 `verify-published.ps1`（`PASS: published payload is a release build with manifest mosaic-0.16.1`）；发布内容指纹、历史链、索引不可变性按 `build/release-content.ps1` 算法以兼容脚本（Windows PowerShell 5.1 无法解析无 BOM 原脚本）独立复算通过；`docs/releases/v0.16.1.md` 为发布说明。
- 未验证项：MSI 的实际安装/升级/卸载不在本机执行（本机存在正在运行的真实安装）；桌面 Smoke 未在本版本重跑（它驱动真实指针会抢占用户鼠标）；与真实自建服务器 0.1.1 的端到端会话管理/删除账号/设备授权 slow_down 退避由服务端实施记录与后续联调覆盖。

## 0.16.0：可选账户、浏览器授权登录与任务/设置备份（2026-11-19）

- 根因位置：0.15.x 及更早版本的任务与设置只存在于本机 SQLite 与 settings 表，且没有任何经过 HTTPS 的可信服务器身份：`src/Mosaic.Desktop/BuiltInModuleCatalog.cs`（0.15.3 版）只装配任务、存储、设置、浮窗、桌面驻留与发布摘要模块，没有 `auth.api@1`、`sync.api@1`、`tasks.transfer@1` 三项能力，因此换机、重装或另一台设备上的 Mosaic 无法获得既有任务（只能手动复制 `mosaic.sqlite`），旧任务也不会以原 ID/修订号/时间戳恢复；用户数据目录丢失即数据丢失。
- 改动位置：
  - 账户契约 `src/Modules/Auth/Mosaic.Auth.Contracts/AuthContracts.cs`：`AuthStatus`、`AuthErrorCodes`、`AuthValidation`（邮箱、密码 10–256、6 位验证码、服务器必须 HTTPS、无 userinfo/query/fragment、路径为 `/`）、`AuthAccountDto`、`AuthPolicyDto`、`DeviceAuthorizationDto`、`AuthState`、`AuthSecrets`，以及 `IAuthApi`、`IAuthTransport`、`IAuthSecretStore`。
  - 账户核心 `src/Modules/Auth/Mosaic.Auth.Core/AuthModule.cs`（模块 `auth-core`，`Definition:34-38` 提供 `auth.api@1`、依赖 `auth.transport@1`/`auth.secrets@1`/`settings.api@1`）；`Internal/AuthService.cs`：注册/密码登录/验证码登录/找回与重置密码、设备授权启动-轮询-取消-过期、`CompleteSignInAsync:298` 统一处理登录成功与凭据持久化、`WithServerAsync:317` 保证离线启动只读本地凭据不联网、`StateChanged:41/360` 通知；`Internal/AuthSettingsSource.cs` 注册设置项 `auth.serverUrl`；`Internal/GuardedAuthApi.cs` 经 `IModuleExecutionGate` 与诊断包装。
  - 账户传输 `src/Modules/Auth/Mosaic.Auth.Http/HttpAuthTransport.cs`（`/v1` 账户端点、错误码映射、强制 HTTPS，回环开发例外）与 `HttpAuthModule.cs`（模块 `auth-http`，唯一持有 `HttpClient` 的账户实现）。
  - 凭据存储 `src/Modules/Auth/Mosaic.Auth.Windows/WindowsAuthSecretStore.cs`：DPAPI（entropy `Mosaic.Auth.Secrets.v1`）加密 `<Root>/Auth/credentials.dat`，原子写入；损坏或异账户文件按未登录处理；拒绝 http 服务器与空令牌；`AuthWindowsModule.cs`（模块 `auth-windows`）。
  - 账户页 `src/Modules/Auth/Mosaic.Auth.Wpf/AuthUiModule.cs`、`Internal/AuthViewFactory.cs`、`Internal/AuthLocalization.cs`、`Internal/AccountViewModel.cs`（登录/注册/忘记密码表单、设备授权码 `BAAD-C832` 展示与轮询、服务器地址编辑、离线使用、备份上传/下载确认、同步开关经设置编辑会话持久化、退出登录确认）、`Views/AccountView.xaml(.cs)`、`Localization/en.xaml`、`Localization/zh.xaml`。
  - 备份契约 `src/Modules/Sync/Mosaic.Sync.Contracts/SyncContracts.cs`：`SyncItemKinds`、`SyncErrorCodes`、`BackupItemDto`、`BackupSnapshotDto`、`BackupSnapshotContentDto`、`BackupUploadResult`、`RestoreSummary`、`SyncState`、`ISyncApi`、`ISyncTransport`。
  - 备份核心 `src/Modules/Sync/Mosaic.Sync.Core/SyncModule.cs`（模块 `sync-core`，提供 `sync.api@1`）；`Internal/SyncService.cs`：`UploadAsync:67`（导出经 `ITaskTransferApi` 取归档全量、设置只取 `PortablePrefixes:16`/`PortableIds:17` 白名单、上传前校验快照 SHA-256）、`DownloadAndReplaceAsync:97`（先 `PrepareRestoreAsync:131` 校验快照，再单事务替换任务，最后 `ApplySettingsAsync:170` 只应用可移植项）、`ContextAsync:274`（未登录或关闭同步短路为 `SYNC_NOT_SIGNED_IN`/`SYNC_DISABLED`）；`Internal/SyncSettingsSource.cs` 注册 `sync.enabled`、`sync.includeSettings`；`Internal/GuardedSyncApi.cs`。
  - 备份传输 `src/Modules/Sync/Mosaic.Sync.Http/HttpSyncTransport.cs`（`/v1/backups` 上传、latest 元数据、按 ID 下载、错误码映射、HTTPS 强制）与 `HttpSyncModule.cs`（模块 `sync-http`）。
  - 任务全量导出/替换 `src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskTransferContracts.cs`（`ITaskTransferApi`）、`src/Modules/Tasks/Mosaic.Tasks.Persistence.Contracts/ITaskTransferRepository.cs`、`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskTransferService.cs`（`ExportAllAsync:14-18` 使用 `ArchiveScope = All` 的既有 `ITasksApi.ListAsync`；`ReplaceAllAsync:20` 逐行校验——Id 非空不重复、Revision ≥ 1、时间戳顺序、标题长度，上限 100 000 行——失败即拒绝，成功后经 `TaskChangeSource` 发布变更）、`Internal/GuardedTaskTransferApi.cs`、`TasksModule.cs`（`Transfer:47`，依赖注入 `TasksDependencies.TransferRepository`）。
  - SQLite 替换仓库 `src/Modules/Storage/Mosaic.Tasks.Sqlite/Internal/SqliteTaskTransferRepository.cs`（单事务删除并重插，保留原 Id/Revision/时间戳）、`TasksSqliteModule.cs`（新增能力 `tasks.transfer-repository@1`）、`Internal/SqliteTaskRepository.cs`（`Values(TaskItemDto)` 改为 `internal static` 供替换仓库复用映射）。
  - 宿主装配 `src/Mosaic.Desktop/BuiltInModuleCatalog.cs`（using 行 1-4、16-17；属性 57-61；装配顺序 `auth-http` → `auth-windows` → `auth-core` → `sync-http` → `sync-core` → `auth-ui`，并把 `tasks.transfer-repository@1` 注入 `TasksDependencies`；缺失依赖时抛出明确异常）、`src/Mosaic.Desktop/AppConfiguration.cs`（迁移 `auth-sync-v1:19` 登记模块/provider/三项设置，`AuthSecrets:337`）、`src/Mosaic.Desktop/appsettings.json`、`Mosaic.Desktop.csproj`、`Mosaic.sln`、`src/Modules/Settings/Mosaic.Settings.Wpf/Localization/en.xaml`、`zh.xaml`。
  - 构建契约 `build/project-boundaries.json`（账户/同步各项目允许依赖；`HttpClient` 只在 `Mosaic.Auth.Http`、`Mosaic.Sync.Http`；DPAPI 只在 `Mosaic.Auth.Windows`）、`build/public-api.txt`、`tests/Mosaic.Architecture.Tests/BoundaryTests.cs`（`IsWindowsProject` 统一判定 Windows 目标框架；模块→程序集表补充 `auth-core`、`sync-core`）。
  - 测试：`tests/Mosaic.Auth.Core.Tests/AuthServiceTests.cs`、`Fakes.cs`；`tests/Mosaic.Auth.Http.Tests/HttpAuthTransportTests.cs`、`HttpSyncTransportTests.cs`、`LoopbackServer.cs`；`tests/Mosaic.Sync.Core.Tests/SyncServiceTests.cs`、`Fakes.cs`；`tests/Mosaic.Tasks.Core.Tests/TaskTransferTests.cs`；`tests/Mosaic.Wpf.Tests/AccountViewModelTests.cs`、`WindowsAuthSecretStoreTests.cs`、`Mosaic.Wpf.Tests.csproj`。
  - 发布元数据 `Directory.Build.props`（版本 0.16.0）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.16.0）、`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`（第 26 条，`previousReleaseId=mosaic-0.15.3`，指纹 `626351271AF88E7D52EFD01AAAC3DD0FCF2A74E988893EFDB4D78B0858FB3C73`）、`README.md`、`docs/releases/v0.16.0.md`、`docs/releases/v0.16.0-files.md`。
- 关系：账户与备份完全可选，不连接服务器时行为与 0.15.3 一致（本地优先、启动不联网）；恢复任务复用 `ITaskTransferApi`/`ITaskTransferRepository` 保留原 ID 与修订号，任务区、已归档页与浮窗经既有变更通知自动刷新；设置恢复只写白名单（`appearance.theme`、`ui.language`、`tasks.*`、`ui.text.*`），不触碰服务器地址、模块装配、机器路径、驻留/自启、令牌、pending journal 与日志。
- 兼容与迁移：新迁移 `auth-sync-v1` 为已有安装登记六个新模块、五项能力 provider 与 `auth.serverUrl`/`sync.enabled`/`sync.includeSettings` 三项设置；既有任务、归档与设置原样保留，卸载不删除数据目录（含 `Auth/credentials.dat`）。数据库表结构未变化。
- 验证：Release 全量重建 0 警告 0 错误；非桌面测试全绿——Architecture 4、Auth.Core 25、Auth.Http 24、DesktopPresence.Core 17、FloatingWindows 12、Kernel 5、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 1、Storage.Sqlite 12、Sync.Core 12、Tasks.Contract 7、Tasks.Core 27、Wpf 105。发布内容指纹、历史链、索引不可变性、更新简要长度按 `build/release-content.ps1` 算法以兼容脚本（Windows PowerShell 5.1 在 zh-CN 主机无法解析无 BOM 原脚本）独立复算通过；`build/release-manifest.json` 与 `docs/releases/v0.16.0.md` 由该复算脚本按同一算法重新生成，格式与 0.15.x 条目一致。
- 发布内容：`artifacts/installer/Mosaic-0.16.0-win-x64.msi`（52 560 244 字节，SHA-256 `229B455B034B431D9413223EB0DC36AA69C34F5D75C1FBE10EBD41C5009B774E`，自包含 .NET 10.0.15 运行时；数据库回读 ProductVersion=0.16.0、Application 所属桌面快捷方式与卸载注册表清理经 `build/test-package-shortcuts.ps1` 校验通过）；`docs/releases/v0.16.0.md` 为发布说明。
- 未验证项：MSI 的实际安装/升级/卸载不在本机执行（本机存在正在运行的真实安装，避免打断用户数据）；桌面 Smoke 未在本版本重跑（它驱动真实指针会抢占用户鼠标，且新测试已覆盖账户页 ViewModel 层）；账户页在 150% DPI、高对比度、中英文切换下的逐像素截图未逐项落地；与真实自建服务器（`server/` 0.1.x）的端到端备份往返由服务端实施记录与后续联调覆盖。

## 0.15.3：自动归档启停路径的断言补强（2026-11-19）

- 测试缺口位置：0.15.2 新增的两个测试只断言 `ITasksApi.SetArchivedAsync` 的调用次数与仓库中的归档状态（`tests/Mosaic.Tasks.Core.Tests/AutoArchiveTests.cs` 0.15.2 版 :129-176），没有断言归档循环本身是否启动。`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskAutoArchive.cs:12-14` 的 `_delayDays`、`_cancellation`、`_loop` 是启停状态的真实来源：如果 `ApplyAsync` 把关闭误判为启用（循环启动但本轮候选为空，或轮询间隔尚未到），`SetArchivedAsync` 仍可能一次都不被调用，0.15.2 的测试就会空过。
- 改动位置（仅测试与发布元数据，产品源码未改）：
  - `tests/Mosaic.Tasks.Core.Tests/AutoArchiveTests.cs`：`Disabled_auto_archive_never_archives_due_completed_tasks` 更名为 `Auto_archive_disabled_policy_never_archives_due_completed_tasks`，候选任务改为等待期 7 天、完成于 8 天前，并在两次 `ApplyAsync`（空字典默认值与显式 `autoEnabled=false`）后新增断言：`_delayDays == 0`、`_cancellation` 为 null、`_loop` 仍是 `Task.CompletedTask`，证明归档循环没有启动；`Enabled_auto_archive_archives_due_candidates_and_stops_when_disabled_again` 在开启后断言 `_delayDays == 7`、`_cancellation` 非空，关闭后断言 `_delayDays == 0`、`_cancellation` 为 null，再插入 30 天前完成的已完成任务并确认不再归档；新增 `Field` 辅助方法反射读取 `TaskAutoArchive` 私有字段。
  - `Directory.Build.props`、`build/installer/Mosaic.Installer.wixproj`：版本 0.15.3。
  - `build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`：追加 `mosaic-0.15.3`（`previousReleaseId=mosaic-0.15.2`，指纹 `CD85654836A042B3600E16914C2AA166E86536A6502E51877F3642E56E5216E6`），索引第 25 条；0.14.0、0.15.0、0.15.1、0.15.2 的已发布条目与哈希未改动（逐条复算一致，索引仅新增）。
  - `README.md`、`docs/releases/v0.15.3.md`、`docs/releases/v0.15.3-files.md`：版本说明与交付清单。
- 与 0.15.2 的关系：0.15.2 已在收到本次要求前发布（tag v0.15.2），其内容不可修改，因此断言补强作为 0.15.3 独立交付；两版均为纯测试补丁，应用行为不变。
- 兼容与迁移：产品行为、数据库结构、设置键与配置格式均未变化；已有安装直接升级，不需要迁移。
- 验证：Release 全量重建 0 警告 0 错误；非桌面测试 204 项全绿——Architecture 4、DesktopPresence.Core 17、FloatingWindows 12、Kernel 5、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 1、Storage.Sqlite 12、Tasks.Contract 7、Tasks.Core 22、Wpf 82。桌面 Smoke 未重跑：它驱动真实指针会抢占用户鼠标，且本机仍有用户正在运行的真实安装，无法独立确认。
- 发布内容：`build/published-release-index.json` 25 条；发布产物 `artifacts/local-run` 通过 `verify-published.ps1`（`PASS: published payload is a release build with manifest mosaic-0.15.3`）。MSI：`Mosaic-0.15.3-win-x64.msi`，52469940 字节，SHA-256 `60379E4CCD71A8ED27DF0C2DA1C31CDF0AB615EA5BD1D873A6F843E46E547637`，数据库回读 ProductVersion=0.15.3，`test-package-shortcuts.ps1` PASS；发布产物 ProductVersion 为 `0.15.3+9f7f1b4...`（工作树改动尚未提交时的构建）。
- 未验证项：MSI 的实际安装/升级/卸载不在本机执行（本机存在正在运行的真实安装）；桌面 Smoke 未在本版本重跑。

## 0.15.2：自动归档启停行为回归测试（2026-11-19）

- 覆盖缺口位置（0.15.1 的测试未断言行为）：`tests/Mosaic.Tasks.Core.Tests/AutoArchiveTests.cs`（0.15.1 版）只断言 `TaskSettingsSource` 的默认值与 `ITaskRepository.QueryArchiveCandidatesAsync` 的候选查询（:87-127），从未调用 `TaskAutoArchive.ApplyAsync`。因此 `src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskAutoArchive.cs:16-19` 的关闭分支（`autoEnabled` 不是 `true` 时传 0）与 `TaskAutoArchive.SetDelayAsync`（:21-41）的启动/取消循环路径都没有行为断言：如果关闭策略仍然启动归档循环，或开启后归档了非候选任务，现有测试不会发现。
- 改动位置（仅测试与发布元数据，产品源码未改）：
  - `tests/Mosaic.Tasks.Core.Tests/AutoArchiveTests.cs`：新增 `Disabled_auto_archive_never_archives_due_completed_tasks`（空设置字典与显式 `autoEnabled=false` 两次调用 `ApplyAsync`，断言 `ITasksApi.SetArchivedAsync` 零调用、超过等待期 60 天的已完成任务与未完成任务均保持未归档）；新增 `Enabled_auto_archive_archives_due_candidates_and_stops_when_disabled_again`（`autoEnabled=true`、`delayDays=7` 后只归档 8 天前完成的候选，6 天前完成与未完成任务不受影响；随后关闭策略，插入 30 天前完成的已完成任务并等待，断言仍只有一次归档调用、新任务未归档，覆盖 `SetDelayAsync` 的取消与循环退出）；新增辅助方法 `ApplyPolicy`、`StopPolicy`（反射调用内部 `ApplyAsync`、`SetDelayAsync`）与 `Until`（5 秒轮询，超时 `Assert.Fail`）。
  - `Directory.Build.props`、`build/installer/Mosaic.Installer.wixproj`：版本 0.15.2。
  - `build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`：追加 `mosaic-0.15.2`（`previousReleaseId=mosaic-0.15.1`，指纹 `3421D9EAAD6EF8417E29D74528342FBCEF545CA894C2AD1755E41E4A273FEA90`），索引第 24 条；0.14.0、0.15.0、0.15.1 的已发布条目与哈希未改动（与 HEAD 索引逐条比对，仅新增）。
  - `README.md`、`docs/releases/v0.15.2.md`、`docs/releases/v0.15.2-files.md`：版本说明与交付清单。
- 0.15.0 核验反馈的处理：对方提出的“归档页仍可切走筛选/新建”已在 0.15.1 修复并发布（`TasksViewModel` 构造期 `archivedOnly` 锁定 `_filter=3`、`FilterIndex` setter 忽略非 3 写入、`ApplySettings` 跳过 `tasks.defaultFilter`、`NewAsync` 首行返回、`TasksView.xaml` 隐藏新建按钮与筛选下拉）；0.15.1 为已发布版本，本次不修改其内容，仅对 0.15.0 记录中的测试缺口补强。
- 兼容与迁移：产品行为、数据库结构、设置键与配置格式均未变化；0.15.1 安装直接升级，不需要迁移。
- 验证：Release 构建 0 警告 0 错误；非桌面测试 204 项全绿——Architecture 4、DesktopPresence.Core 17、FloatingWindows 12、Kernel 5、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 1、Storage.Sqlite 12、Tasks.Contract 7、Tasks.Core 22（新增 2 项）、Wpf 82。本版本未改动产品源码，未重跑桌面 Smoke（其驱动真实指针会抢占用户鼠标，且 0.15.1 已记录该套件唯一的失败为环境干扰）。
- 发布内容：`build/published-release-index.json` 24 条；发布产物 `artifacts/local-run` 通过 `verify-published.ps1`（`PASS: published payload is a release build with manifest mosaic-0.15.2`）。MSI：`Mosaic-0.15.2-win-x64.msi`，52478132 字节，SHA-256 `E670F56484C05F1EC6EC0B2E289FCDF2C63E8A73B4DAC1D1B9CD1FB95782D70A`，数据库回读 ProductVersion=0.15.2，`test-package-shortcuts.ps1` PASS；发布产物 ProductVersion 为 `0.15.2+ec8ee3f...`（工作树改动尚未提交时的构建）。
- 未验证项：MSI 的实际安装/升级/卸载不在本机执行（本机存在正在运行的真实安装）；桌面 Smoke 未在本版本重跑。

## 0.15.1：归档页锁定筛选并隐藏新建与筛选入口（2026-11-19）

- 根因位置：
  - 筛选被覆盖：0.15.0 的归档页是在构造完成后设置 `FilterIndex = 3`（`TasksViewModel` 的公开 setter），而 `TasksViewModel.ApplySettings` 的 `tasks.defaultFilter` 分支会无条件写回筛选。当页面在设置到达之前创建（`TaskUiSettingsTarget.Attach` 时还没有 `_latest`），首次 `ApplySettings` 在页面创建后到达，就把「已归档」覆盖成「全部/未完成/已完成」；设置再次应用时也会重复覆盖。根因即 `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TasksViewModel.cs` 的 `TasksViewModel.ApplySettings`（0.15.0 版本 :853-862）缺少归档页判断。
  - 入口失步：同一页面上「新建任务」按钮与四项筛选下拉始终可见可用（0.15.0 的 `Views/TasksView.xaml` 中 `NewTask` 按钮与 `TaskFilter` 下拉），归档页可以借此新建任务或切换到非归档筛选，与「固定查看已归档」的页面语义冲突。
- 改动位置：
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TasksViewModel.cs`：构造函数新增 `bool archivedOnly = false`（:62），构造期写入 `_archivedOnly` 并把 `_filter` 置 3（:42、:64-65），不再依赖构造后赋值；`FilterIndex` setter 忽略归档页上非 3 的写入（:132-140）；`ApplySettings` 的 `tasks.defaultFilter` 分支只在非归档页执行（:871-883），`_defaultFilterApplied` 仍统一置位；`NewCommand` 的 `CanExecute` 排除归档页（:78），`NewAsync` 首行直接返回（:301）；新增每实例恒定的 `ShowNewTask`、`ShowFilterChooser`（:199-200）；`OnEditorPropertyChanged` 只保留「归档页编辑器空态自动关闭」，删除无消费方的 `IsArchivedPage` 与不再变化的可见性通知。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskViewFactory.cs`：构造函数新增 `bool archivedOnly = false` 并透传给 `TasksViewModel`。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/TasksUiModule.cs`：归档视图 `tasks.archived.list` 使用 `new TaskViewFactory(dependencies, _settingsTarget, archivedOnly: true)`；主任务页继续使用默认工厂。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml`：`NewTask` 按钮（:54）与 `TaskFilter` 下拉（:96）按 `ShowNewTask`/`ShowFilterChooser` 绑定 `BoolVisibility`，归档页隐藏。
  - `tests/Mosaic.Wpf.Tests/PresentationTests.cs`：`Archived_navigation_opens_an_independent_archived_page` 增加两个控件的 `Collapsed` 断言；新增 `Archived_page_locks_its_filter_when_settings_arrive_late` 覆盖构造期筛选、非 3 写入忽略、`tasks.defaultFilter=Open` 晚到不覆盖、`NewCommand` 不可执行与初始化后仍为 3。
- 兼容与迁移：数据目录、设置键、ViewId 与导航契约均未变化；`tasks.defaultFilter` 仍作用于主任务页，归档页只是不再消费它。升级不需要迁移，已有安装的归档数据与筛选偏好照旧。
- 验证：Debug/Release 构建 0 警告 0 错误；`Mosaic.Wpf.Tests` 82/82（含新增用例）；Architecture 4、DesktopPresence.Core 17、FloatingWindows 12、Kernel 5、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 1、Storage.Sqlite 12、Tasks.Contract 7、Tasks.Core 20 全绿；Desktop Smoke 31/32。未通过项为 `Floating_real_pointer_moves_resizes_clicks_and_restores_saved_bounds`，已定位为环境干扰：点击命中窗口为 `pid 42580`（本机已安装并运行的 Mosaic 0.14.0）的置顶浮窗（`HwndWrapper[Mosaic.Desktop;;fe0fb391-...]`，`WS_EX_TOPMOST`），它覆盖了测试卡片取消置顶后的同一坐标，UIA 命中同样来自该进程的元素，与本仓库被测进程（`hwnd=37817946`）无关；0.14.0 记录中同一用例也曾出现两次同类抖动。该用例在 0.15.0 发布时通过，本轮代码不涉及浮窗路径。
- 发布内容：`build/release-content.json` 追加 `mosaic-0.15.1`（`previousReleaseId=mosaic-0.15.0`，指纹 `A05423517DE48F37E7D27E4C00AA65702673B719E367896A9329750506FC4698`）；`build/published-release-index.json` 追加为第 23 条，既有 22 条与 0.14.0、0.15.0 的哈希未改动（以 HEAD 索引逐条比对）。MSI：`Mosaic-0.15.1-win-x64.msi`，52482228 字节，SHA-256 `52D56AC3CAE0F85827ED57D8A2F7C4C76FC01F41CA4D7F78DA7135FEAA1042EA`，数据库回读 ProductVersion=0.15.1；发布产物 ProductVersion 为 `0.15.1+1419bba...`（工作树改动尚未提交时的构建）。
- 未验证项：MSI 的实际安装/升级/卸载不在本机执行（本机存在正在运行的真实安装，避免干扰用户环境）；被环境遮挡的真实指针用例未在其干扰消失后重跑。

## 0.15.0：已归档任务导航与未完成不自动归档（2026-11-19）

- 根因位置：
  - 需求一（左侧没有独立的已归档入口）：`src/Modules/Tasks/Mosaic.Tasks.Wpf/TasksUiModule.cs`（改动前 :32-35）的 `Navigation` 只贡献一项 `tasks.navigation`（ViewId `tasks.list`），`Views` 只贡献一个 `tasks.list`，归档只能从主任务页的第四项筛选进入；同时 `src/Mosaic.Shell.Wpf/NavigationService.cs:9-29` 对相同 `ViewId` 直接返回（`if (CurrentViewId == viewId) return true;`），若两个导航项共用 `tasks.list`，第二次导航不会重建页面，归档页会沿用主列表实例与筛选，无法做到两页独立。
  - 需求二（未完成任务的自动归档边界缺少显式锁定）：`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskAutoArchive.cs` 的候选查询已限定 `completed = 1`，`TaskSettingsSource` 的 `tasks.archive.autoEnabled` 默认 `false`、`tasks.archive.delayDays` 默认 7，行为本身正确；但设置描述只写“自动归档到期的已完成任务”，没有说明未完成任务不受影响，且没有测试显式锁定“未完成超期也不归档”。
- 改动位置：
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/TasksUiModule.cs`：`Navigation` 增加 `tasks.archived.navigation`（LabelKey `Tasks.Archived.Navigation`，图标键 `folder`，ViewId `tasks.archived.list`，Order 110）；`Views` 增加 `tasks.archived.list`，使用 `TaskViewFactory(dependencies, _settingsTarget, 3)`。图标键沿用 `src/Mosaic.Shell.Wpf/NavigationConverters.cs:15` 已支持的 `folder`，Shell 不依赖 Tasks 模块。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskViewFactory.cs:6-15`：构造函数增加可选 `int? initialFilter`，`Create()` 构造 `TasksViewModel` 后设置 `FilterIndex`，归档页初始即“已归档”，且不改写用户保存的默认筛选。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TasksViewModel.cs:193-195`：新增 `ListTitle`（`FilterIndex == 3` → `Tasks.ListTitle.Archived`，否则 `Tasks.ListTitle`）；`SetFilter`（:630）与 `RefreshLocalizedText`（:916）同步通知，切换筛选与中英文时标题一致刷新。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml:83`：标题由 `DynamicResource Tasks.ListTitle` 改为 `Binding ListTitle`，归档页显示“已归档任务”。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Localization/zh.xaml:117-118`、`en.xaml` 对应位置：新增 `Tasks.Archived.Navigation`、`Tasks.ListTitle.Archived`。
  - `src/Modules/Settings/Mosaic.Settings.Wpf/Localization/zh.xaml:265`、`en.xaml` 对应位置：`Settings.Setting.AutoArchive.Description` 改为“仅自动归档等待期已满且未恢复的已完成任务；未完成任务永远不会被自动归档；关闭时不自动归档任何任务”。
  - `src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskSettingsSource.cs:11`：设置定义内联描述同步为同一文案，避免无本地化资源时的回退文本与界面不一致。
  - 测试：`tests/Mosaic.Tasks.Core.Tests/AutoArchiveTests.cs`（新增，6 项，反射访问 internal 类型）锁定“未完成无论多旧都不是候选”“已完成仅等待期已满才归档”“已归档不重复归档”“恢复后从 `RestoredAtUtc` 重计”“默认关闭且说明含未完成策略”“归档轮次只写候选查询返回的任务”；`tests/Mosaic.Wpf.Tests/PresentationTests.cs` 更新导航/Shell/语言切换/重复注册断言为两项，新增 `Archived_navigation_opens_an_independent_archived_page`、`Archived_page_restores_a_task_and_keeps_the_page_on_the_archived_list`、`Archived_page_keeps_its_archived_filter_and_active_page_follows_the_default_filter`；`tests/Mosaic.Wpf.Tests/ResponsiveLayoutTests.cs` 侧栏 rail 断言按 ToolTip 定位“任务”按钮并容纳两个导航标签；`tests/Mosaic.Wpf.Tests/PreviewCaptureTests.cs` 设置页截图按唯一 `settings.home` 视图取页。
  - 发布：`Directory.Build.props:12`、`build/installer/Mosaic.Installer.wixproj:4` 升为 0.15.0；`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json` 追加 `mosaic-0.15.0`（指纹 `053E513BEF2A5D9178FEB96DF3DEDEAD7FB18715315DC8D853EF3CF2F19AF9FD`，索引 22 条，既有条目与 0.14.0 已发布哈希未改动）；`README.md`、`docs/releases/v0.15.0.md`、`docs/releases/v0.15.0-files.md` 同步。
- 迁移与兼容：无数据库结构变化、无新增设置项、无配置迁移。归档页复用既有 `SetArchivedAsync`（CAS 版本校验）与 `Tasks.Unarchive.Success` 提示；两页各自创建 `TasksViewModel`，草稿、冲突提示、删除确认互不影响。旧安装升级后导航多出一项，任务数据与设置保持原样。
- 附带修复（安装包校验脚本查错表）：`build/test-package-shortcuts.ps1:51-66` 原先在 `Registry` 表中过滤 `Registry = -1` 查找卸载清理项，且 SQL 写在双引号里导致 `` `Key` `` 的反引号被 PowerShell 当作转义符。根因是 WiX 的 `RemoveRegistryValue` 写入的是 `RemoveRegistry` 表而非 `Registry` 表，因此该校验自 0.13.0 起对任何 MSI 都会抛 `COMException: OpenView,Sql`，无法真正验证卸载清理。改为查询 `RemoveRegistry` 的 `RemoveRegistry,Root,Key,Name,Component_` 并匹配 `Root = 1`（HKCU），0.14.0 与 0.15.0 安装包均返回 PASS。
- 产物：`artifacts/local-run` 为 0.15.0 自包含目录（`Mosaic.Desktop.exe` FileVersion 0.15.0.0），`verify-published.ps1` 返回 PASS，发布目录内 `release-manifest.json` 与 `build/release-content.json` 完全一致；MSI `artifacts/installer/Mosaic-0.15.0-win-x64.msi`，52,474,036 字节，SHA-256 `DB2398067BABEE1E56F0D23ED575510717A99BF1541088E1EBFEC0D484C8DA26`，校验文件 `artifacts/SHA256SUMS-0.15.0.txt`。数据库回读确认 `ProductVersion=0.15.0`、沿用 `UpgradeCode {4D2B073C-72D5-4C3A-B845-65FC2C52D23C}`、`Upgrade` 表含 0.15.0 以下升级与以上降级阻断、桌面与开始菜单快捷方式均指向 `[INSTALLFOLDER]Mosaic.Desktop.exe` 且工作目录 `INSTALLFOLDER`、`Application` Feature 层级 1、459 个文件条目。
- 验证：Release 构建 0 警告 0 错误；全部 12 个测试程序集 233 项通过（Architecture 4、Desktop Smoke 32、DesktopPresence.Core 17、FloatingWindows 12、Kernel 5、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 1、Storage.Sqlite 12、Tasks.Contract 7、Tasks.Core 20、Wpf 81）。发布内容指纹、历史关系、索引不可变性与更新简要长度以兼容脚本独立复算通过。未验证项：本机已存在用户正在使用的 0.14.0 真实安装（`%LOCALAPPDATA%\Programs\Mosaic`，进程运行中），本轮未执行真实安装/升级/卸载以免打断用户数据与运行中的应用，安装包仅做数据库回读与快捷方式校验，升级路径以既有 `Upgrade`/`Downgrade` 行与 `build/test-install.ps1` 流程留待独立验收；真实指针下的归档页交互与 150% DPI、高对比度、中英文组合截图未逐项落地。

## 0.14.0：浮窗空白区拖动与速览入口显示时机（2026-11-19）

- 根因位置：
  - 需求一（浮窗空白区不可拖动）：`src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/FloatingWindowInput.cs` 的 `HitTest`（改动前 :111-112）把命中的所有可见 `TextBoxBase` 整个边界框判为 HTCLIENT；`OnMouseDown`（:39-46）与 `OnMouseMove`（:47-53）的阈值拖动候选只来自 `_tools`/`_header` 中的 `ButtonBase`；HTCAPTION 尾部（:113-121）只覆盖顶部拖动带与 header。同时 `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskFloatingDetailsView.cs:25,30` 与 `TaskFloatingContentFactory.cs:170` 让只读备注 `TextBox` 拉伸占满正文区，于是正文大片空白、底部按钮周边与标题下方空白全部落进 HTCLIENT，无法拖动窗口。
  - 需求二（速览入口出现时机不可配置）：`src/Modules/DesktopPresence/Mosaic.DesktopPresence.Core/PresenceController.cs`（改动前 :15-32）只有 `RequestParkAsync` 调用 `showEdge`；`RequestRestoreAsync`（:34-41）恢复后必隐藏；`RequestPopupAsync`（:45）对 `MainVisible` 直接返回；`src/Mosaic.Desktop/DesktopWindowHost.cs:12-23` 不向 Presence 通知主窗口可见/最小化/隐藏状态；`src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSettings.cs:10-40` 没有显示时机设置项。三者叠加导致入口只在“关闭主窗口后驻留”时出现，最小化或保持显示均不可选。
  - 顺带修复（布局快照跨窗口泄漏）：`src/Mosaic.Shell.Wpf/ShellSettingSource.cs` 的 `ShellSettingsApplier.Detach`（改动前 :110-114）只清 `_current`，静态 `_latest` 快照保留上一个 `ShellWindow` 应用过的布局值；新窗口 `Attach` 时（:104-108）把旧快照套到新实例，侧栏宽度等布局被错误继承。
- 改动位置：
  - `src/Mosaic.Presentation.Contracts/FloatingPresentation.cs`：新增 `FloatingContentDragKind { None, Immediate, Deferred }`（:5-8）与 `IFloatingContentSession.QueryContentDrag(Point)`（:25-27，默认 `None`）。
  - `src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/FloatingWindowInput.cs`：新增 `_content`/`_contentDrag` 字段与可选构造参数；`OnMouseDown` 在非按钮命中且 `QueryContentDrag == Deferred` 时记录 `_pressContent`；`OnMouseMove` 阈值判断条件加入 `!_pressContent`；`HitTest` 中 `TextBoxBase` 命中先询问内容层，`Immediate` 时跳过 HTCLIENT 判定，HTCAPTION 尾部对内容层报 `Immediate` 的点返回 2；新增 `MouseToElement` 坐标换算。
  - `src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/FloatingSurface.cs:167-169`：把 `session.View` 与 `session.QueryContentDrag` 传入 `FloatingWindowInput`。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskFloatingContentFactory.cs`：`TaskFloatingContentSession` 新增 `QueryContentDrag(Point)`——详情不可见时返回 `Immediate`，否则经 `TransformToAncestor` 逆变换转到详情视图坐标后转发。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskFloatingDetailsView.cs`：新增 `_editing` 字段并在 `Render` 中同步；新增 `QueryContentDrag`（按钮/滚动条/Thumb 返回 `None`，只读备注非编辑态转 `DragKindForNotes`，其余空白 `Immediate`）、`DragKindForNotes`（空文本立即拖；`GetCharacterIndexFromPoint(snapToText: true)` + `GetRectFromCharacterIndex` 判定字形，跨行回退 `FontSize` 步进，`GlyphSlack` 容差内命中字形返回 `Deferred`，否则 `Immediate`）、`GlyphSlack`、`TreeParent`。
  - `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Contracts/PresenceContracts.cs`：新增 `MainWindowPresentation { Visible, Minimized, Hidden }`；`IDesktopWindowHost` 新增 `CurrentPresentation` 与 `PresentationChanged`；`IDesktopPresenceSurface` 新增 `SetEdgeVisibleAsync(bool)`。
  - `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Core/PresenceController.cs`：新增 `_showTiming`（默认仅 `Hidden`）与 `_subscribed` 字段；新增 `ConfigureShowTiming(MainWindowPresentation[])`（幂等订阅 `PresentationChanged` 并触发 `EvaluateEdgeAsync`）、`OnPresentationChanged`、`EvaluateEdgeAsync`（驻留或挂起时按时机集合判定，否则恒显示）；`RequestRestoreAsync` 改为按 `CurrentPresentation` 是否在时机集合内决定隐藏；`RequestPopupAsync` 仅驻留挂起时直接返回，主窗口可见但不在时机内也返回。
  - `src/Mosaic.Desktop/DesktopWindowHost.cs`：`Window` 属性改为带 `Attach`/`Detach` 的后备字段；新增 `CurrentPresentation`（无窗口按 `Visible`，`!IsVisible` 为 `Hidden`，最小化为 `Minimized`）与 `PresentationChanged`，订阅 `StateChanged`/`IsVisibleChanged`。
  - `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/DesktopPresenceModule.cs`：设置应用回调接入 `_controller.ConfigureShowTiming(ShowTiming(values))`；新增 `ShowTiming` 映射（Always→全部、Minimized→最小化+隐藏、默认 Resident→仅隐藏）。
  - `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSettings.cs`：新增 `desktop.edge.showTiming`（Normal 类别，默认 `Resident`，选项 `Resident/Minimized/Always`）及显示名、描述与取值映射。
  - `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceLocalization.cs`：新增设置名、描述与三个取值的本地化键。
  - `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSurface.cs`：新增幂等 `SetEdgeVisibleAsync(bool)`，不为未创建的窗口构造实例。
  - `src/Mosaic.Shell.Wpf/ShellSettingSource.cs`：`ShellSettingsApplier.Detach` 清空 `_latest`（:110-117）。
  - `build/public-api.txt`：登记 `Mosaic.Presentation.Contracts.FloatingContentDragKind` 与 `Mosaic.DesktopPresence.Contracts.MainWindowPresentation`。
  - 测试：`tests/Mosaic.Wpf.Tests/FloatingWindowsTests.cs` 的 `Unified_hit_regions` 改为按文本为空/字形点断言，并新增 `Notes_blank_and_footer_drag_the_window_while_glyphs_and_editing_stay_interactive`；`tests/Mosaic.Wpf.Tests/PresenceTests.cs` 行数 28→29、Normal 5→6 并验证新设置默认值与选项；`tests/Mosaic.DesktopPresence.Core.Tests/PresenceTests.cs` 覆盖显示时机配置与恢复行为。
  - 发布：`Directory.Build.props`、`build/installer/Mosaic.Installer.wixproj` 版本升为 0.14.0；`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json` 追加 mosaic-0.14.0（指纹 8CCA2A8E…FE8087，21 条索引，未改动既有条目）；`README.md`、`docs/releases/v0.14.0.md`、`docs/releases/v0.14.0-files.md` 同步。
- 迁移与兼容：无数据库结构变化；`desktop.edge.showTiming` 默认 `Resident`（仅主窗口关闭后显示），与旧行为完全一致，既有安装升级后无需任何迁移操作；浮窗拖动为纯交互层变化，持久化的浮窗位置/尺寸记录格式不变。
- 验证：全部测试程序集通过（Architecture 4/4、DesktopPresence.Core 17/17、FloatingWindows 12/12、Kernel 5/5、ReleaseSummary、Settings、Storage、Tasks 各套件全绿；Wpf 78/78；桌面真实指针 Smoke 32/32 第三次全量通过，前两次各有一项拖动坐标断言抖动、单独重跑均通过，判定为时序抖动非功能缺陷）。发布内容指纹、索引不可变性、范围汇总长度按 `build/release-content.ps1` 算法以兼容脚本独立复算通过。未验证项：真实指针矩阵中的 150% DPI、高对比度主题与中英文界面组合未逐项落地；MSI 实际安装/升级/卸载在打包后以既有流程验收。

## 0.13.1：速览详情卡片与更新简要补发（2026-11-18）

- 根因位置：`build/publish.ps1` 的 0.13.0 发布流程未以 `-p:MosaicPublishedBuild=true` 构建，`BuiltInModuleCatalog.cs:126–134` 因此将已安装程序判定为开发构建，`ReleaseSummaryModule` 在启动时直接返回 `DevelopmentBuild`，用户数据目录没有 `mosaic-0.13.0` 回执。`TaskPeekDetailsView.Render` 此前把标题、状态、备注等以多条 `TextBox` 逐条拼接，点击标题或正文即触发全局 TextBox 模板的 2px 焦点框（图 1、图 2）。
- 改动位置：`TaskPeekDetailsView` 改为卡片式只读视图——标题、状态、备注收敛到圆角卡片 `Card`，时间信息独立显示；备注改用 `Peek.xaml` 中的 `TaskPeekNotesStyle`（无边框、可选中复制、键盘焦点沿用共享短条）。新增 `build/verify-published.ps1` 在打包前校验 `MosaicPublishedBuild` 元数据与 `release-manifest.json`，并在 `build/package.ps1` 中调用；`tests/Mosaic.ReleaseSummary.Tests/ReleaseSummaryTests.cs` 新增“清单覆盖所有未公告版本”的回归；`tests/Mosaic.Wpf.Tests/PeekDetailsTests.cs` 覆盖卡片布局、只读备注、紧凑模式与三主题渲染截图。
- 修复效果：以正式发布构建重新打包后，在复制真实数据目录的隔离实例上首次启动即生成 `mosaic-0.13.1` 更新简要，正文覆盖 0.13.0 与 0.13.1 全部条目；第二次启动不回执重复生成。0.13.0 用户在升级后启动 0.13.1 时同样会收到这条合并的更新简要。
- 验证：非桌面回归 187/188 通过（1 项 PresenceTests 断言详情失败分支仍查找 TextBox，新视图改为 TextBlock，已修正断言并重跑通过）；WPF 回归含浅色、深色、高对比度速览详情截图（各文件及验证入口见[改动清单](releases/v0.13.1-files.md)）；MSI 经 `msi.dll` 表查询确认 `StartupCleanup` 组件及卸载清理项存在，快捷方式与安装目录工作目录保持 0.13.0 行为。桌面真实登录与 MSI 实际安装/升级仍以既有流程另行验收。

## 0.13.0：任务归档、自动归档与登录自启（2026-11-18）

- 根因位置：`TaskService.SetCompletedAsync` 只保存布尔完成状态，自动归档缺少完成时间基线；`SqliteTaskRepository` 没有归档时间、恢复时间和到期候选查询；`HostSettingSource.ApplyAsync` 为空操作，宿主设置保存后不会同步系统启动项。
- 改动位置：`TaskItemDto` 增加 `CompletedAtUtc`、`ArchivedAtUtc`、`RestoredAtUtc`；`TaskQuery` 增加 `ArchiveScope`；`ITasksApi` 增加 `SetArchivedAsync`；`TaskService` 的完成切换记录完成时间，归档/恢复写入对应时间并拒绝归档后的普通编辑；`SqliteTaskRepository` 增加归档范围查询与到期候选查询，`TasksSqliteModule` 注册 `003_TaskArchiving`；`TasksModule` 接入 `TaskAutoArchive` 策略，`TaskSettingsSource` 增加自动归档开关与等待天数；`TasksViewModel` 增加归档筛选、归档/恢复命令和外部归档保护，`TaskEditorView` 对归档任务只读；`TaskFloatingNotesViewModel` 阻止归档任务进入编辑并保护草稿；`HostSettingsCatalog` 增加 `startup.runAtLogon`，`HostSettingSource` 在持久化、回滚和即时应用时同步 `WindowsStartupRegistration`；`SettingsService` 将宿主源纳入即时应用；`Package.wxs` 新增 `StartupCleanup` 组件在卸载时移除本工作区的登录启动项。逐文件清单见[改动清单](releases/v0.13.0-files.md)。
- 迁移与兼容：`003_TaskArchiving.sql` 为旧 `tasks` 表补充三列，旧已完成任务以 `updated_at` 回填 `completed_at`，旧未完成时间为空，全部任务初始未归档；创建凭据与 Revision 语义不变。自动归档默认关闭、默认等待 7 天；开机自启默认关闭，启用后绑定当前工作区路径，不修改 Windows 任务管理器的系统级禁用状态。
- 验证：核心与契约测试覆盖归档往返、草稿保护、旧库回填和默认未归档；`MemoryTaskRepository` 与 SQLite 查询行为一致；宿主设置测试覆盖启用、回滚和工作区绑定。Release 构建 0 警告、0 错误，非桌面回归 169/170 通过（1 项为发布清单未同步导致的失败，已修复并重跑通过）。桌面 Smoke 与真实 Windows 登录矩阵待验收。

## 0.12.2 应用内按钮反馈（2026-11-18）

- 0.12.1 的 `Theme.xaml:64` 在鼠标点击留下键盘焦点时持续显示描边。`FloatingWindows.Wpf/Themes/Floating.xaml:36` 给已置顶的图钉持续填色，第 44 行又给获得焦点的按钮填色。`Tasks.Wpf/Themes/Floating.xaml:10` 的焦点与按压共用底色。速览 `TaskPeekView.xaml:45` 和设置 `SettingsView.xaml:44` 也以输入焦点直接绘制边框。旧版回归复现了 `IsPressed` 和 `IsMouseOver` 均为 `false` 时置顶按钮仍有底色。
- 共享 `MosaicButtonFocusVisual` 使用 WPF 焦点装饰显示短条。若鼠标点击已由键盘聚焦的按钮，`ButtonFocusMark` 隐藏短条。再次使用键盘时恢复提示。主窗口、速览、设置及浮窗使用该资源，按压由 `IsPressed` 驱动，悬停由 `IsMouseOver` 驱动。置顶图钉继续用空心与实心表示状态。各文件及验证入口见[改动清单](releases/v0.12.2-files.md)。
- 旧任务、配置及浮窗记录沿用原有存储格式和读取路径。MSI 继续使用原有每用户 `UpgradeCode` 和 `MajorUpgrade`。
- 首轮发布版回归中，`Tasks.Wpf/Themes/Floating.xaml` 的禁用态触发器使用 `TemplateBinding` 作为 `Setter.Value`，导致浮窗模板加载失败。改用启用态与悬停态的 `MultiTrigger` 后，两个浮窗可正常恢复。

- 最终 Release 构建为 0 警告、0 错误。181 项非桌面回归和 29 项桌面 Smoke 全部通过。真实鼠标与键盘覆盖按下、松开、移出、拖出取消、禁用、Tab、Shift+Tab、Space 和 Enter。发布版像素采样确认图钉仅在悬停及按压时显示底色。旧 SQLite 数据的浮窗恢复、备注编辑以及快捷键均通过现有回归。干净环境的 MSI 实际安装与升级尚未验收。
- 最终 Windows x64 MSI 为 `Mosaic-0.12.2-win-x64.msi`，51,990,660 字节，SHA-256 为 `FEA88285110E33F8010BBDA524857EB99850EB9B0EAA270F9F6D96FE6A9C9910`。已核对版本、升级范围和桌面快捷方式组件。发布内容指纹为 `D9AA5388262D8585F7C0448F527CD986B7414A29850F2F9D1D6D59C11143A32D`。

## 0.12.1：浮窗工具栏矢量图标（2026-11-18）

- 0.12.0 的 `FloatingSurface.Create` 在第 147–154 行直接将 `↗`、`◇`、`×` 用作按钮内容。`FloatingSurface.Layout` 在第 200 行用 `◆/◇` 表示置顶状态。字体字形各自渲染，导致图标笔画、大小和重心不一致，菱形也缺少图钉语义。
- `FloatingSurface.Create` 为打开主窗口、置顶和关闭按钮加载矢量资源，点击区域间隔 2 DIP。`UpdatePin` 从 `FloatingWindowRecord.IsTopmost` 同时更新置顶图标和操作名称。`Layout` 将工具栏实测宽度传给内容布局。`FloatingLocalization.Dictionary` 提供中英文的置顶和取消置顶名称。[逐文件改动清单](releases/v0.12.1-files.md)
- 兼容和升级沿用原任务、设置、浮窗记录及每用户安装器 `UpgradeCode`。旧 SQLite 工作空间由现有存储模块读取，浮窗还原时直接用已有的 `IsTopmost` 生成新图标，无需改写旧记录。MSI 继续由现有 `MajorUpgrade` 更新程序文件。

- 锁定还原、Release 构建和 Windows x64 自包含发布通过。181 项非桌面回归和 28 项发布版桌面 Smoke 全部通过。验收覆盖 220 DIP 紧凑窗、展开窗、多浮窗、置顶保存失败及重启恢复、三个按钮中央的鼠标拖动、短按和未保存草稿的关闭保护。使用浅色、深色和高对比度主题生成 96、144、192 DPI 的 WPF 渲染图。实际多屏切换、不同系统缩放下的指针交互及干净环境中的 MSI 安装与升级尚未验收。
- Windows x64 MSI 为 `Mosaic-0.12.1-win-x64.msi`，51,982,468 字节，附 `SHA256SUMS-0.12.1.txt`。安装包版本、原有升级标识和桌面快捷方式组件检查通过。MSI SHA-256 为 `726F26D343687FA1B6DB87AB4E75852A8C3A2F8D5C0572E11028C475E1368F17`。发布内容指纹为 `941AE6ACDC7F2C4DA6F424B14BD2A211FFB81DB22381F3533904265A55A4E02B`。

## 0.12.0：浮窗无边框控件与可选正文编辑（2026-11-18）

- 根因位置：0.11.0 的 `TaskFloatingStateStyle`（`src/Modules/Tasks/Mosaic.Tasks.Wpf/Themes/Floating.xaml:3`）及 `TaskFloatingContentSession` 构造函数和 `FloatingSurface.Create` 复用的 `QuietButtonKey`，会在默认或焦点状态绘制描边。`TaskPeekDetailsView.AddText` 通过通用 TextBox 模板渲染只读正文，焦点触发器会绘制内容外框。`TaskPeekDetailsView.Render` 会重建控件，无法保留输入或选区。`TaskUiSettingsTarget.Apply` 只向主页面分发设置。`FloatingWindowCoordinator.CloseAsync` 和 `PrepareExitAsync` 在缺少内容关闭协议时直接保存布局，无法确认未保存的正文。
- 改动位置：`TaskFloatingDetailsView.Render` 保持正文输入控件持久并分开展示阅读、编辑和错误状态。`TaskFloatingNotesViewModel.SaveAsync` 使用 Tasks API 的长度约束和编辑时的任务 Revision。`TaskFloatingContentSession.PrepareCloseAsync` 等待已有任务操作并保护草稿。`FloatingWindowCoordinator` 在锁外准备内容、合并重复关闭、确认后保存窗口状态。浮窗局部模板提供底色焦点反馈。完整位置见[逐文件清单](releases/v0.12.0-files.md)。
- 兼容与迁移：旧工作空间缺少 `tasks.floating.allowNotesEditing` 时由 Settings 合并默认值 `false`。保存开关后使用原用户设置存储，沿用已有任务和浮窗记录。普通设置增至 17 项，高级设置 82 项，共 99 项。WPF 回归覆盖缺键、预览放弃、持久化重启、草稿冲突与关闭失败。发布版 Smoke 使用旧设置缺键的 SQLite 工作空间验证只读、原窗口恢复和启用后更新备注。
- 验证：锁定还原和 Release 构建通过，0 警告、0 错误。非桌面测试 180 项通过，发布版桌面 Smoke 3 项通过，覆盖旧工作空间恢复、备注保存、标题栏按钮短按与真实指针拖动。干净环境 MSI 安装与升级、不同 DPI、多屏、高对比度和输入法矩阵待验收。
- 产物：`artifacts/local-run/Mosaic.Desktop.exe` 与 `artifacts/installer/Mosaic-0.12.0-win-x64.msi`。安装包 51,966,084 字节，`ProductVersion=0.12.0`，沿用原 `UpgradeCode`，WiX 构建和快捷方式检查通过。因本机使用 PowerShell 5.1，以等效路径和哈希 API 执行打包。发布清单和历史版本指纹校验通过，MSI 的 SHA-256 为 `FD544B599AABFFE165B12AB2CCD08905D7F4BD7FFEDF61FCFDB800F034C8233A`，校验文件为 `artifacts/installer/SHA256SUMS-0.12.0.txt`。
- 已核对 GitHub 上 MSI 和校验文件的字节数与 SHA-256。发布时 README 第 13 行仍指向上一版 MSI，已改为 v0.12.0 安装包直链。[GitHub Release v0.12.0](https://github.com/soehi/Mosaic/releases/tag/v0.12.0) · [README.md](../README.md)

## 0.11.0：小窗落点预览与标题栏按钮拖动（2026-11-18）

- 根因位置：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs`（0.10.2 基线 `FinishDrag`）只在松手后提交 `FloatingPlacementHint`，拖动过程中没有目标位置计算或预览输出；`FloatingSurface` 没有独立于窗口的放置预览；`FloatingWindowCoordinator.OpenCoreAsync` 存在历史记录时整条复用旧记录，落点被忽略；`FloatingWindowInput.HitTest` 对按钮返回 `HTCLIENT` 以保留点击，但没有 WPF 阈值拖动兜底，按住标题栏按钮拖动无效。真实指针复核还发现：`FloatingWindowInput.OnMouseMove` 首版用 `Window.DragMove()` 启动移动，而它发出的 `WM_SYSCOMMAND`/`SC_MOVE` 在按压起点为已捕获鼠标的子按钮时会被系统移动循环立即结束并回送 `WM_LBUTTONUP`，窗口不移动（跟踪记录：`WM_SYSCOMMAND 0x0112` 后 1 ms 内收到 `WM_LBUTTONUP 0x0202`）。
- 改动位置：`FloatingContracts.cs`（`IFloatingWindowApi`/`IFloatingWindowSurface` 预览成员）、`FloatingPresentation.cs`（`IFloatingDetachSource` 预览申请）、`TaskDetachSource.cs`（转发）、`FloatingWindowCoordinator.cs`（预览转发；`OpenCoreAsync` 用落点覆盖旧 `MonitorId/X/Y`）、`FloatingSurface.cs`（幽灵窗、主题描边、点击穿透、`Freeze/Dispose` 清理）、`FloatingScreen.cs`（`Placement` 共享夹取）、`TaskPeekView.xaml.cs`（`PreviewRequested/PreviewMoved/PreviewEnded` 与 `PointToScreen` hint）、`TaskPeekViewFactory.cs`（接线与清理）、`FloatingWindowInput.cs`（`CanStartHeaderDrag`、阈值后 `ReleaseCapture` + `WM_NCLBUTTONDOWN`/`HTCAPTION` 复用系统标题栏移动循环、点击抑制）。测试：`FloatingTests.cs`、`FloatingWindowsTests.cs`、`PresenceTests.cs`、`FloatingDragTests.cs`（新增 `Floating_header_buttons_move_on_drag_and_still_click_on_short_press`）。
- 模块边界：Tasks UI 只通过公开 `IFloatingDetachSource` 提交预览；Floating Windows 不取得任务 DTO；预览不写存储。无公开类型新增，`build/public-api.txt` 不变；无设置、数据库、配置或模块装配迁移，设置维持 98 项（16 普通/82 高级）。
- 验证：Release 构建 0 警告/0 错误；非桌面自动化 177/177 通过（WPF 72/72）。新增 `artifacts/ui-preview/floating-placement-preview.png` 显示幽灵窗样式；真实指针 Smoke 通过：`Floating_header_buttons_move_on_drag_and_still_click_on_short_press`（从 `FloatingTaskState`/`FloatingTaskExpand` 拖动移动窗口、完成状态与紧凑高度不变、阈值内短按仍展开），`Floating_real_pointer_moves_resizes_clicks_and_restores_saved_bounds` 连续 2 次通过（移动、缩放、按钮短按、重启恢复与关闭）。真实指针拖出速览、100–200% DPI、多屏与高对比度仍待独立桌面验收。
- 兼容：窗口记录结构不变，旧记录按原字段读取；关闭后再拖出会使用新落点，尺寸/模式/置顶沿用旧记录。
- 本机产物（2026-11-18）：自包含目录 `artifacts/local-run`，`Mosaic.Desktop.exe` ProductVersion 0.11.0；MSI `artifacts/installer/Mosaic-0.11.0-win-x64.msi`，51,961,988 字节，ProductVersion `0.11.0`，SHA-256 `B52BBE79D5A95DB95B2D5E8FF055752C83B931B7D3C47217244C4680C89DB435`，校验文件 `artifacts/installer/SHA256SUMS-0.11.0.txt`。MSI 数据库回读确认桌面快捷方式归属 `Application` 且工作目录为 `INSTALLFOLDER`；打包使用 `SuppressValidation`（本机无 pwsh，按 `build/package.ps1` 步骤复现），真实安装、升级与 ICE 矩阵未在本轮执行。

## 0.10.2：速览整行拖出与筛选视觉（2026-11-18）

- 根因位置：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml:87`（0.10.1 基线）四个鼠标处理器只绑在详情 Button 上，完成按钮和两列间约 4 DIP 留白不属于可拖出元素；`TaskPeekView.xaml.cs:42–47`（`StartGesture`）只接受 `Button` 起点。`DragHintAdorner` 只显示跟随指针的文字，拖动时任务标签本身没有标记。筛选视觉位于 `TaskPeekView.xaml:51–58` 与 `:14–43`：摘要与筛选放在 `WrapPanel`，单选按钮使用透明底与临时边框，选中态和间距不统一。
- 改动位置：`TaskPeekView.xaml` 把 `PreviewMouseLeftButtonDown/Move/Up`、`LostMouseCapture` 移到带 `Background="Transparent"` 的 `TaskRow`，`TaskCard` 只保留 `Click` 与右键菜单；筛选区改为“摘要 + 轨道”的 `Grid`，`PeekFilterButton` 重写为轨道内药丸模板并新增 `FilterOptions`。`TaskPeekView.xaml.cs`：`_candidate` 改为 `FrameworkElement`，`StartGesture/ActivateDrag/EndGesture/FinishDrag/CancelDrag` 使用行容器，`CaptureLost` 通过 `ReferenceEquals(e.OriginalSource, _candidate)` 忽略子按钮交还捕获的冒泡，`CompleteTask` 增加 `_suppressClick` 保护。`DragHintAdorner.cs`：新增整标签虚线 `Rectangle` 与主题化提示。`TaskPeekViewFactory.cs`：筛选遍历与焦点回退改用 `FilterOptions.Children`。`tests/Mosaic.Wpf.Tests/PresenceTests.cs`：整标签路由覆盖标题、状态文字、任务卡片、完成按钮，筛选仍不建立候选；新增窄宽度布局回归。
- 模块边界：拖出仍由 Tasks UI 通过公开 `IFloatingDetachSource` 申请；Floating Windows、Presence、Storage、Tasks Core 未改；无公开 API 变化；无设置、数据库、配置或模块装配迁移，设置维持 98 项（16 普通/82 高级）。
- 验证：Release 构建 0 警告/0 错误；非桌面自动化 173/173 通过（WPF 69/69）。`presence-popup-light/dark`、`presence-tasks-readonly` 预览图已重拍，新增 `peek-drag-frame.png` 显示整标签虚线框；窄宽度回归断言 236 DIP 内容宽下筛选完整位于视图内。真实指针拖出、100–200% DPI、多屏与高对比度仍待独立桌面验收，不以路由测试代替。
- 构建环境修复：此前锁文件被带 `-r win-x64` 的还原写入了 RID 目标，未声明 `RuntimeIdentifiers` 的项目在 `dotnet restore Mosaic.sln --locked-mode` 时报 NU1004。本轮为这些项目重新生成非 RID 锁文件，仅 `Mosaic.Desktop` 与 `Mosaic.Settings.Wpf`（声明 `RuntimeIdentifiers=win-x64`）保留 `win-x64` 目标；锁定解决方案还原、Desktop 自包含还原与 `dotnet publish` 全部通过，产品依赖未变。发布内容指纹用同一算法复核了全部 14 条记录。
- 本机产物（2026-11-18）：自包含目录 `artifacts/local-run`；MSI `artifacts/installer/Mosaic-0.10.2-win-x64.msi`，ProductVersion 0.10.2，校验值见 `artifacts/installer/SHA256SUMS-0.10.2.txt`。MSI 数据库回读确认桌面快捷方式归属 `Application` 且工作目录为 `INSTALLFOLDER`。打包使用 `SuppressValidation`，真实安装、升级与 ICE 矩阵未在本轮执行。

## 0.10.1：整卡拖出捕获修复与速览筛选样式（2026-11-17）

- 根因位置：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs:45–77`（`MoveGesture`/`EndGesture`/`CaptureLost`）超过拖动阈值后打开 `Popup`，`Popup` 打开时抢占鼠标捕获，按钮收到 `LostMouseCapture` 后被旧逻辑当作失败取消；快速拖动时按钮收不到 `MouseLeftButtonUp`，缺少终局兜底。视觉问题位于 `TaskPeekView.xaml:14–43`（`PeekFilterButton`）：分段控件缺少统一高度与清晰选中边界，弱化可读性。
- 改动位置：新增 `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/DragHintAdorner.cs`，把拖动提示改为 `AdornerLayer` 上的只呈现组件；`TaskPeekView.xaml.cs` 新增 `ActivateDrag`，`EndGesture`/`CaptureLost` 收敛到 `FinishDrag`，按最后指针位置申请拖出或取消，并挂接/清理 Esc 处理器；`TaskPeekView.xaml` 优化筛选分段控件高度、光标、焦点与选中边界；`tests/Mosaic.Wpf.Tests/PresenceTests.cs` 新增“拖动提示不抢捕获、捕获丢失仍按窗口外位置终局”回归。版本同步到 `Directory.Build.props`、`build/package.ps1`、`build/installer/Mosaic.Installer.wixproj`。
- 模块边界：Tasks UI 继续通过公开 `IFloatingDetachSource` 申请拖出；Presence/Floating Windows/Storage 不改；无设置、数据库、配置或装配迁移。
- 验证：Release 构建 0 警告/0 错误；非桌面自动化 172/172 通过（WPF 68/68）。真实指针拖出、DPI 与多屏矩阵仍待独立桌面验收。

## 0.10.0：速览完成切换、筛选与整卡拖动起点（2026-11-17）

- 用户要求：任务速览左侧圆圈可标记完成；面板支持全部/未完成/已完成筛选且默认全部；任务拖拽框覆盖整个任务标签，起点在任务按钮内也可拖出，点击标签详情功能不变。
- 根因/能力缺口位置：`TaskPeekView.xaml:31–48` 左侧 `Path` 只是状态图形且嵌在整行详情 Button 内；`TaskPeekRow` 不保存 `Revision`，`TaskPeekViewModel` 未接线 `ITasksApi.SetCompletedAsync`；列表固定使用 `CompanionViewOptions.Filter`，无会话内筛选；`TaskPeekView.xaml.cs:20–37` 手势绑定整行 Button，当前理论上已覆盖标题起点，但加入完成按钮/筛选后需统一排除并建立回归。
- 改动位置：`PresentationContracts.cs` 扩展 `CompanionViewOptions`；`PresenceSettings.cs`/`PresenceLocalization.cs`/`PresenceSurface.cs` 新增并传递 2 个普通展示开关；`TaskPeekView.xaml/.cs` 拆分完成按钮、筛选分段控件并保持整标签拖动手势；`TaskPeekViewFactory.cs` 增加 Revision、完成操作、筛选状态与公开 API 申请；Tasks 中英文资源补充文案。详见 [速览完成切换与筛选计划](peek-completion-filter-plan.md)。
- 职责边界：Tasks Core 拥有完成语义；Tasks UI 只通过公开 API 写入；Presence 只读设置并通过通用选项传参；Floating Windows、Storage 与主任务页不改。无数据库、模块装配或配置迁移。
- 本轮仅修改规划文档，未改源码、设置计数、测试或发布产物；真实指针、读屏、DPI 与桌面 Smoke 均待实施后执行。

- 实现：`TaskPeekViewModel.CompleteAsync/SubmitCompletionAsync` 按任务 ID 防重，携带行 Revision 提交公开 `SetCompletedAsync`，等待真实结果后重查；冲突重查后使用新版本，错误可关闭并在六秒后收起。`SelectFilterAsync/InitializeAsync/LoadAsync` 保存会话筛选、取消旧请求并用代次隔离迟到结果。`CloseAsync` 取消会话请求，交互状态在提交期间保持面板。
- 界面位置：`TaskPeekView.xaml/.cs` 的 `CompleteTask/FilterClicked` 与卡片 `StartGesture/MoveGesture/EndGesture` 使用独立同级控件，标题、状态和卡片留白仍路由到详情卡片；完成区和筛选不会进入拖出手势。行忙碌显示省略符、禁用并更新无障碍名称；列表刷新恢复同任务操作焦点或筛选入口。Tasks `Localization/zh.xaml/en.xaml` 增加两种语言的操作和筛选资源。
- 配置位置：`PresentationContracts.cs.CompanionViewOptions` 增加两个默认 true 的可选字段；`PresenceSettings.Definitions/DisplayNames`、`PresenceLocalization.Dictionary` 和 `PresenceSurface.ShowPopupAsync` 定义、翻译并传入选项。完整配置 98 项（16 普通/82 高级）；Presence 停用后定义撤销。`build/public-api.txt` 按导出类型登记，本轮无新增导出类型，原 `CompanionViewOptions` 条目继续有效。
- 测试位置：`tests/Mosaic.Wpf.Tests/PresenceTests.cs` 新增真实公开 API 替身的完成/冲突/失败/取消/快速筛选用例、整卡路由与控件排除回归，并验证两个设置预览与放弃的选项往返；新增 `tests/Mosaic.Desktop.SmokeTests/PeekCompletionTests.cs` 验证实际发布进程完成、恢复、筛选和数据库 Revision 递增。路由自动化不能代替物理指针拖出验收。
- 发布与文档位置：`Directory.Build.props`、`build/package.ps1`、`build/installer/Mosaic.Installer.wixproj`、生产项目 `packages.lock.json` 同步 0.10.0；`build/release-content.json`、`published-release-index.json`、`release-manifest.json` 同源生成更新简要和 `docs/releases/v0.10.0.md`；README、总计划阶段 16、设置计划第 17 节及专项计划同步。逐文件清单见 `docs/releases/v0.10.0-files.md`。
- 迁移与旧安装影响：无数据库结构、配置结构或模块装配变化，无需迁移；旧设置未存两个开关时由模块默认 true 补足。既有用户自选初始筛选不变；会话筛选不会写入设置、任务数据库或主页面筛选，浮窗布局记录保留。预览放弃恢复已保存值，禁用模块不删除历史值。
- 待独立验收：物理指针从标题/状态/内边距拖出及控件排除、真实键盘焦点/读屏、100–200% DPI、多屏与系统高对比度矩阵、干净 Windows 升级安装和 MSI ICE；这些不以自动化路由测试代替。

- 最终验证（2026-11-17）：Release 构建 0 警告/0 错误；非桌面测试 171/171（WPF 67/67）通过；最终自包含 EXE 的速览桌面 Smoke 1/1 通过，完成/恢复后按筛选移除，重开数据库确认原任务 Revision 1→3、最终未完成。MSI 打包与桌面快捷方式数据库检查通过。安装包 51,974,276 字节，SHA-256 `466689E2640F916D3AB7A725D81C4482EB0A4601DA76184160F1322C4A787963`，版本 0.10.0；EXE FileVersion 0.10.0.0。MSI 使用 `-SkipValidation`，本轮没有执行真实安装升级或完整桌面矩阵。
- 桌面验证补充：`TaskPeekView.xaml` 筛选改用 Checked 事件，保证 UIA SelectionItem、键盘与鼠标选择一致；测试夹具通过公开设置仓储关闭更新简要任务和自动收起，避免误选另一任务及面板自动消失。测试不改变真实工作空间。

### 已确认待修复：整卡拖出在真实指针下失效（0.10.1 规划）

- 用户反馈：从任务按钮内部按下无法拖出小窗；本轮仅定稿规划，未改源码、测试或发布产物，设置维持 98 项。
- 根因位置：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs:45–63`（`MoveGesture`）超过阈值后打开拖动提示 `Popup`，Popup 打开时抢占鼠标捕获；`:77`（`CaptureLost`）把这次捕获转移当作失败调用 `CancelDrag()`，导致拖动在激活瞬间被取消。快速拖动时按钮收不到 `MouseLeftButtonUp`，`:65–77`（`EndGesture`）不执行，也缺少兜底。既有 `tests/Mosaic.Wpf.Tests/PresenceTests.cs:90–115` 只验证按下路由、不跨阈值，因此测试通过而真实拖动失败。
- 拟改动位置：`TaskPeekView.xaml.cs` 改用不抢捕获的 `DragHintAdorner`（新增内部类型）呈现提示，`CaptureLost` 改为按最后指针位置执行终局判定，`EndGesture`/兜底共用 `FinishDrag`，面板外判定改为窗口坐标并补 Esc 取消；新增 WPF 捕获/终局回归与真实指针拖出桌面 Smoke。详见 [专项计划](peek-card-drag-fix-plan.md)。

## 0.9.1：安装器自动创建桌面快捷方式（2026-11-17）

- 根因位置：`build/installer/Package.wxs:10-14` 只声明了 `ProgramMenuFolder` 下的开始菜单快捷方式，没有受 MSI 组件管理的 `DesktopFolder` 入口，因此安装成功后不会自动创建用户桌面快捷方式；`build/test-install.ps1` 也只验证安装、升级和卸载，没有验证桌面入口及其目标。应用运行时创建快捷方式会重复安装器职责，所以修复留在安装器边界内。
- 安装器改动位置：`build/installer/Package.wxs` 新增 `DesktopFolder/DesktopShortcut` 组件、`DesktopMosaic` 快捷方式和稳定组件 GUID，并把 `DesktopShortcut` 加入 `Application` Feature；目标为 `[INSTALLFOLDER]Mosaic.Desktop.exe`，工作目录为 `INSTALLFOLDER`。升级、修复和卸载由 Windows Installer 组件生命周期处理。
- 验证工具改动位置：新增 `build/test-package-shortcuts.ps1`，通过 Windows Installer 数据库回读 `Shortcut` 与 `FeatureComponents`，断言唯一桌面快捷方式、目标、工作目录和 `Application` Feature 归属；`build/package.ps1` 在复制最终 MSI 后调用该检查；`build/test-install.ps1` 的 `Assert-MosaicDesktopShortcut` 验证真实安装和升级，并在卸载后断言快捷方式删除。升级矩阵首次运行又暴露出旧版基础包没有桌面入口时被错误断言的问题；新增 `Test-MosaicPackageDesktopShortcut` 先回读基础包是否声明该入口，仅对具备功能的基线检查安装结果，升级到 0.9.1 后仍无条件验证入口存在。
- 套件稳定性根因与改动位置：`tests/Mosaic.Wpf.Tests/PreviewCaptureTests.cs:28` 在独立 STA 测试线程创建应用级 `Application`，线程结束后 `Application.Current.Dispatcher` 仍指向已停止的 Dispatcher；`src/Mosaic.Shell.Wpf/ShellSettingSource.cs:115-116` 的 `ShellSettingsApplier.ApplyAsync` 又优先向该 Dispatcher 投递设置预览，后续 `ResponsiveLayoutTests.Shell_consumes_window_and_layout_settings_from_the_settings_api` 会跨线程等待至取消。现改为优先使用已附着的 `ShellWindow.Dispatcher`，只在没有窗口时回退到应用级或当前 Dispatcher；产品调用链和设置语义不变，完整 WPF 套件复跑通过。
- 桌面 Smoke 稳定性根因与改动位置：`tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs:124、129、142` 在驻留速览顶层窗口出现后立即用 `Id(...)!` 点击触发、恢复或更多按钮，但 WPF Automation 树中子控件晚于顶层窗口生成，升级后第二次运行会得到空引用。现通过既有 `Until` 等待每个具体控件进入自动化树后再点击；重跑升级前后桌面 Smoke 各 24/24 通过。
- 版本与发布位置：`Directory.Build.props`、`build/installer/Mosaic.Installer.wixproj`、`build/package.ps1` 更新为 `0.9.1`；全部项目锁文件同步项目依赖版本；`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`、`README.md`、`docs/implementation-plan.md`、`docs/installer-location-plan.md`、`docs/releases/v0.9.1.md` 和 `docs/releases/v0.9.1-files.md` 同批交付。逐文件定位见 [0.9.1 文件清单](releases/v0.9.1-files.md)。
- 自动化验证：Release 构建 0 警告、0 错误；非桌面测试 166/166 通过，其中 WPF 62/62；完整打包流程通过，WiX 0 警告、0 错误，打包后 MSI 数据库检查通过。完整测试首次运行暴露出 `PresenceTests.Popup_chrome_renders_in_both_themes_and_compacts_at_small_heights` 的时序竞态：测试未关闭产品默认的 250 ms 自动收起，负载较高时弹窗在截图前关闭；测试现通过公开 `ISettingsApi.ProposeChangesAsync` 将 `desktop.popup.autoHide` 设为 `false`。远端基线克隆复跑又暴露出上述跨 Dispatcher 等待；修复后 WPF 62/62 和全量 166/166 通过。自包含程序 `artifacts/local-run/Mosaic.Desktop.exe` 的 FileVersion 为 `0.9.1.0`、ProductVersion 为 `0.9.1+552fba51718f71fd083cff1c535ec099a07b956e`。
- 真实安装链验证：当前 `Mosaic-0.9.1-win-x64.msi` 的全新安装返回 0，桌面 `Mosaic.lnk` 和开始菜单入口均存在，桌面目标精确为 `%LOCALAPPDATA%\Programs\Mosaic\Mosaic.Desktop.exe`，工作目录为对应安装目录；卸载返回 0，桌面与开始菜单快捷方式、安装二进制均删除，`%LOCALAPPDATA%\Mosaic` 用户数据仍存在。随后完成 0.9.0→0.9.1 实机升级：升级前后桌面 Smoke 各 24/24 通过，升级后快捷方式目标正确，升级不改变用户数据哨兵，卸载清理入口和程序文件。测试中唯一不含的是依赖桌面前台焦点和物理指针时序的 `Floating_real_pointer_moves_resizes_clicks_and_restores_saved_bounds`，它仍归入独立交互式桌面验收。验证安装已卸载，当前没有 Mosaic 进程或安装残留，本地只保留 `artifacts/local-run` 一个完整程序目录。
- 安装产物：`artifacts/installer/Mosaic-0.9.1-win-x64.msi`，51,953,796 字节，ProductVersion `0.9.1`，SHA-256 `620F7FDF5245B84DE35ED776B495742029C15F4280BFAB5C6A7BFE78CFD4A4D7`；校验文件为 `artifacts/installer/SHA256SUMS-0.9.1.txt`。
- 迁移与旧安装影响：没有配置、数据库、窗口记录或用户数据迁移。已有安装可直接升级，桌面和开始菜单入口指向安装后的最终目录；卸载只删除 MSI 登记的快捷方式、程序文件和空目录，保留 `%LOCALAPPDATA%\Mosaic`。
- 未完成验收：MSI 以 `-SkipValidation` 生成，ICE01–ICE105 尚未执行；自定义安装位置、跨位置替换、取消/回滚、负载外文件保留、干净 Windows 虚拟机和包含真实鼠标拖拽焦点的完整交互式桌面 Smoke 仍待独立环境验收。这些边界不阻塞当前默认位置功能的自动化、全新安装和 0.9.0→0.9.1 实机升级验证。

## 0.9.0：悬浮任务窗统一拖动命中与紧凑布局（2026-11-17）

- 根因位置：`src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/FloatingSurface.cs:81` 的 12 DIP 内边距和 `Create` 中独立工具栏只把 `DragMove` 绑定到 `⠿` TextBlock，顶部空白和边线没有移动入口；`FloatingScreen.NaturalHeight` 又强制最少 88 DIP 并在测量后额外加高，`TaskFloatingContentSession` 的固定最大标题高度与空反馈占位进一步放大空白。
- 改动位置：新增 `FloatingWindowInput.HitTest/Hook` 集中处理原生命中、圆角外轮廓、工具/控件祖先排除、顶部移动及下部缩放；`FloatingSurface.Create/Layout/QueueBounds/DestroyAsync` 改为工具覆盖内容首行、统一配置密度并沿用既有移动与保存管线；`FloatingScreen.NaturalHeight` 改为按实际内容测量，保留紧凑最小可操作高度。
- Tasks 模块改动位置：`TaskFloatingContentSession.Header/ConfigureLayout/Render` 通过公开 `FloatingContentLayoutOptions` 为工具保留首行空间，改用最小高度与自然换行，并折叠空反馈；`TaskPeekDetailsView.ConfigureDensity/AddText` 收紧展开正文间距且仅在紧凑浮窗隐藏空备注占位。窗口模块不读取 Tasks DTO，标题和任务字段仍由 Tasks UI 渲染。
- 设置改动位置：`FloatingSettings.Definitions`、`FloatingLocalization.Dictionary` 增加 `floating.dragBandHeight`、`floating.cardPadding`、`floating.contentGap` 三项高级 Immediate/Reversible 设置及中英文资源；设置总数由 93 增至 96（14 普通 / 82 高级）。
- 构建/发布位置：`Directory.Build.props`、`build/package.ps1`、`build/installer/Mosaic.Installer.wixproj` 版本同步 0.9.0；`build/public-api.txt` 登记 `FloatingContentLayoutOptions`；`release-content.json`、`published-release-index.json`、`release-manifest.json` 与 `docs/releases/v0.9.0.md` 同源生成。完整逐文件定位见 [0.9.0 文件清单](releases/v0.9.0-files.md)。
- 测试位置：`tests/Mosaic.Wpf.Tests/FloatingWindowsTests.cs` 覆盖 8/12/20 DIP 命中和 4/8/16 DIP 内边距、透明圆角外区域、按钮中心与边缘、禁用按钮、内容控件、顶部/侧边/底边、紧凑与展开、长标题自然增高、空反馈折叠、预览放弃不改持久化尺寸；`tests/Mosaic.Desktop.SmokeTests/FloatingDragTests.cs` 使用真实 `SendInput` 验证顶边及顶部圆角移动、侧边缩放、工具区排除和重启后位置恢复。
- 迁移与旧安装影响：没有数据库结构、窗口记录或配置文件迁移；新增设置缺失时使用默认值，旧窗口记录和保存方式原样兼容。用户数据目录与旧安装位置不变，正常升级后回到已存在的悬浮窗即使用新命中与密度。
- 本机验证：Release 构建 0 警告 / 0 错误；非桌面测试 166 项全部通过，其中 WPF 62 项；悬浮窗正常恢复、独立关闭和正常退出重启 Smoke 通过。真实指针测试在修正测试自身的前置窗口顺序后，产品路径已通过移动、缩放、按钮命中、重启恢复，最终“关闭按钮”失败已由既有独立 Smoke 单独覆盖。自动审批对再次发送系统鼠标输入的复核连续超时，自动化记录保留该限制；100–200% DPI、多屏、高对比度实机矩阵和干净 Windows 安装升级仍待独立环境验收。
- 本机产物：唯一可运行目录 `artifacts/local-run/Mosaic.Desktop.exe`，FileVersion 0.9.0.0、ProductVersion 0.9.0；隔离数据目录启动 6 秒后仍是响应进程。MSI 为 `artifacts/installer/Mosaic-0.9.0-win-x64.msi`，51,949,676 字节，ProductVersion 0.9.0，SHA-256 `947410B72690EC8AB52878653BD2E5E6AB3DE65C2FCA213578D097AC9B395BCF`，校验文件 `artifacts/installer/SHA256SUMS-0.9.0.txt`。

## 0.8.0：独立悬浮任务窗口（2026-11-17）

- 根因/能力缺口位置：TaskPeekView/TaskPeekViewModel 只有点击详情；ICompanionViewFactory 只有单一速览会话；PresenceSurface 只拥有驻留入口和速览窗口；TasksModule 没有专用公开提交变更源；原存储没有窗口身份与恢复状态。上述职责不能通过复制主任务页或读取任务内部对象实现。
- 全部逐文件定位见 [0.8.0文件清单](releases/v0.8.0-files.md)。
- 新增文件/改动位置：Modules/FloatingWindows 下5个项目及各csproj。Contracts/FloatingContracts.cs 定义不透明引用、窗口记录与公开申请/Surface；Persistence.Contracts/IFloatingWindowRepository.cs 定义原子窗口记录；Core/FloatingWindowCoordinator.cs 串行处理开关去重、保存/失败、恢复和退出；Sqlite/FloatingWindowsSqliteModule.cs 独占窗口表、参数化SQL、乐观版本和独立V1迁移；Wpf/FloatingWindowsModule.cs/FloatingSurface.cs/FloatingScreen.cs/FloatingSettings.cs/FloatingLocalization.cs 负责注册、窗口与会话释放、移动缩放、工作区/DPI、预览与9项设置。
- Tasks改动位置：新增 Contracts/TaskChanges.cs 和 Core/Internal/TaskChangeSource.cs；TaskService 在成功提交后发布事实，TasksModule 暴露 tasks.changes@1。Tasks.Wpf/TasksUiModule.cs、Internal/TaskDetachSource.cs、TaskFloatingContentFactory.cs 提供公开申请来源和独立任务内容会话；TaskPeekView.xaml/.cs、TaskPeekViewFactory.cs 处理系统阈值拖动/捕获/取消、菜单、反馈与交互租约；TaskPeekDetailsView.cs 复用只读正文，Themes/Floating.xaml 复用主题语义颜色渲染状态按钮。TaskViewFactory.cs/TasksViewModel.cs 订阅合并刷新且保留未保存编辑；Tasks.Wpf/Localization/en.xaml、zh.xaml 新增分离入口文字。
- 通用/宿主改动位置：Presentation.Contracts/FloatingPresentation.cs 定义内容工厂、申请来源和辅助交互协议；PresenceSurface.cs 只消费通用保活/取消与菜单生命周期；Settings.Contracts/SettingsContracts.cs、Settings.Wpf/Internal/SettingsViewModel.cs 增加动态分组；Desktop/BuiltInModuleCatalog.cs、Bootstrapper.cs、ShutdownCoordinator.cs 完成公开装配、启动恢复与接受退出前排空（失败可重试/取消/明确不保存退出）；AppConfiguration.cs/appsettings.json 增加 floating-windows-v1 生效层迁移。
- 构建/发布位置：Directory.Build.props、WiX、package.ps1、解决方案、各ProjectReference、project-boundaries.json/public-api.txt 与锁文件同步。release-content.json/published-release-index.json/release-manifest.json 和 v0.8.0 发布说明同源登记全部新增/改进/兼容项。当前26个生产程序集，12个测试项目；93项设置、14普通/79高级、6个分区。
- 测试位置：新增 FloatingWindows.Tests/FloatingTests.cs（并发去重、失败重试、关闭/退出区别、恢复上限、缺失/暂时失败、模式尺寸隔离、SQLite往返/版本/损坏）；Wpf.Tests/FloatingWindowsTests.cs（独立窗去重、状态写入、模式/置顶、恢复、保存失败、预览及移动不覆盖偏好、关闭自动恢复）；Tasks.Core.Tests/CreateOnceTests.cs（成功提交事实及观察者隔离）；Architecture.Tests/BoundaryTests.cs 检查新增项目边界；Desktop.SmokeTests/DesktopTests.cs 增加新能力迁移覆盖组合与停用/空配置。
- 旧安装影响：有任务UI/任务/设置/SQLite完整依赖时，旧配置一次性接入两个窗口模块与能力绑定，顶层或settings.host覆盖层同批写回并备份。已迁移后主动停用不会加回；空模块不启用数据库。独立 floating-windows-sqlite V1 迁移复用Storage事务与备份，保留任务表；同一数据目录的恢复记录不存任务标题/备注副本。
- 当前验证：Release构建0警告/0错误；162项非桌面自动化通过（含WPF58项），14项配置迁移与1项真实进程恢复/退出/单独关闭往返通过；浅色/深色/高对比度预览已生成并检查；实机负坐标多屏、100/125/150/200%DPI与热插拔、系统会话、完整鼠标捕获矩阵、干净Windows安装升级/ICE仍待独立验收。下文历史0.7测试不作为小窗验收证据。


## 已确认待实施：独立置顶任务小窗（2026-11-17）

- 用户已确认紧凑标题/状态、点击展开备注/时间、可调整大小、可复制与切换完成状态，并在重启恢复未关闭小窗的位置/大小；见 [完整计划](floating-task-windows-plan.md)。当前源码仍为 0.7.0，小窗尚未实施。
- 能力缺口位置：`TaskPeekView`/`TaskPeekViewModel` 只有点击详情；`ICompanionViewFactory` 只创建速览会话；`PresenceSurface` 只有边缘入口与单个速览弹窗生命周期；Tasks 无公开专用变更源，存储无独立窗口恢复记录。本期方案分别由公开拖出申请、独立内容工厂、窗口管理模块、Tasks 变更源与窗口存储适配器负责。
- 文档改动位置：新增 `docs/floating-task-windows-plan.md`；同步 `docs/implementation-plan.md` 阶段 13、`docs/settings-plan.md` 第 15 节、`docs/peek-task-details-plan.md` 的后续规则、`docs/desktop-presence-plan.md` 的边界入口、README 与本记录。本轮未改程序、数据库或发布产物；新功能测试未执行。

## 0.7.0：速览任务按钮与只读详情（2026-11-16）

- 根因位置：Tasks.Wpf/Internal/TaskPeekView.xaml 使用不可操作 Border；TaskPeekRow 不保留 TaskId；TaskPeekViewModel 只有列表状态；PresenceSurface.ShowPopupAsync 的 PreviewKeyDown 先行吞掉 Esc 与方向键，无法容纳内容内部返回与正文阅读。
- 改动位置：Tasks.Wpf/Internal/TaskPeekView.xaml/.cs 改为 Button 并转发稳定行身份；TaskPeekViewFactory.cs 的 TaskPeekRow/TaskPeekViewModel 增加 TaskId、详情查询、取消与请求代次、返回及滚动/焦点恢复；新增 TaskPeekDetailsView.cs 独占只读任务字段和可复制备注呈现；Tasks.Wpf/Localization/zh.xaml、en.xaml 补详情资源。
- 改动位置：Presentation.Contracts/PresentationContracts.cs 新增可选 ICompanionNavigation；PresenceSurface.cs 消费通用返回协议、移除方向键拦截、协调键盘阅读与外部点击；Presence 不读取任务身份或字段。Tasks Core、SQL、设置定义无需改动，旧配置与任务库无迁移项。
- 验证位置：Wpf.Tests/PresenceTests.cs 新增同名身份、A→B迟到、删除重试、关闭取消测试；public-api.txt 同步新增通用协议。Directory.Build.props、WiX、package.ps1、锁文件、发布内容源/索引/清单、README及计划同步0.7.0。本次发布内容从同源生成更新任务。
- Release构建0警告/0错误；148项非桌面自动化全部通过，含WPF54项。正式本地EXE与MSI已更新为0.7.0；旧0.6.0测试工作空间连续启动两次只有一条0.7.0更新任务，旧任务保留；无需迁移。MSI跳过ICE，完整安装升级验收未执行。完整实机DPI、多屏、鼠标捕获矩阵及干净Windows安装升级仍待独立验收。实际拖出与多置顶任务窗未实施。


## 已确认待实施计划：速览按钮、任务详情与拖出准备（2026-11-16）

- 用户已确认：点击在速览面板内查看只读标题、备注、完成状态与时间，可返回列表；未来拖出成为多个独立置顶任务小窗。本轮仅定稿规划，程序基线为 0.6.0；尚未实现按钮详情或拖出悬浮，见 [专项计划](peek-task-details-plan.md)。
- 当前缺口位置：`TaskPeekView.xaml` 使用不可点击的 Border；`TaskPeekRow` 无稳定 TaskId；`TaskPeekViewModel` 只有列表查询；`PresenceSurface.ShowPopupAsync` 的 PreviewKeyDown 当前会先处理 Esc/上下键，需要与详情返回及正文滚动协调。
- 文档改动位置：新增 `docs/peek-task-details-plan.md`；同步总实施计划阶段 12、旧速览专项计划与驻留计划的后续入口、README 和本记录。新功能的源码测试与视觉验收尚未执行。

## 0.6.0：任务速览与更新简要（2026-11-16）

- 根因位置：PresenceSurface.PositionWindows 把 maxHeight 当实际高度；TaskPeekViewModel.UpdateView 逐项大卡片和 PresencePopupView.xaml 的重操作栏形成空白与视觉负担。Tasks 的 TaskService.CreateAsync / ITaskRepository / SqliteTaskRepository 原本没有可持久化的幂等创建事实。
- 改动位置：PresentationContracts.cs 的 CompanionViewOptions / ICompanionViewSession；Tasks.Wpf/Internal/TaskPeekViewFactory.cs 与新增 TaskPeekView.xaml/.cs；PresenceSurface.cs、PresencePopupView.xaml/.cs、PresenceSettings.cs、PresenceLocalization.cs。容器只测通用 DesiredSize，任务内容仍由 Tasks UI 拥有；更多菜单、提示、Esc、旧会话通知隔离纳入处理。
- 改动位置：Tasks.Contracts/TaskCreateOnce.cs；Tasks.Core 的 TaskService.CreateOnceAsync、GuardedTasksApi、TasksModule；Persistence.Contracts/ITaskRepository；Tasks.Sqlite 的 SqliteTaskRepository、TasksSqliteModule 与 002_CreateTaskReceipts.sql。任务和回执同事务写入，删除任务保留回执，历史回执优先于新限额校验。
- 新增 ReleaseSummary/Mosaic.ReleaseSummary 的 ReleaseManifest、ReleaseSummaryComposer、ReleaseSummaryModule，独占版本范围、正文及启动策略。更新开关为下次启动生效且不预览。Desktop 的 AppConfiguration、BuiltInModuleCatalog、Bootstrapper、appsettings.json、csproj 完成一次性生效层迁移和任务首屏查询前的就绪申请。
- 构建位置：build/release-content.json、published-release-index.json、release-content.ps1、release-manifest.json 为发布内容权威及生成/校验；publish.ps1、package.ps1、WiX、Directory.Build.props、解决方案、项目引用边界、public-api 与锁文件同步 0.6.0。开发构建不会自动写更新任务，正式发布使用 MosaicPublishedBuild 标记。
- 测试位置：新增 Tasks.Core.Tests/CreateOnceTests.cs、Tasks.Contract.Tests/CreateOnceRepositoryTests.cs、ReleaseSummary.Tests；更新 Shared/MemoryTaskRepository.cs、Wpf.Tests/PresenceTests.cs 和 Desktop.SmokeTests/DesktopTests.cs。146 项非桌面自动化通过（WPF 52）；Release 构建 0 警告/0 错误。正式发布包两次启动独立工作空间，确认只有一条未完成更新任务和一条回执；8 项旧配置迁移黑盒通过。
- 原配置自动补入 release-summary-v1（仅任务和设置能力已启用时接入），成功后原子写回并备份；已迁移后用户停用不会加回。任务回执表使用 Tasks V2 迁移及既有备份机制，不覆盖用户任务数据。
- 产物：唯一完整程序 artifacts/local-run/Mosaic.Desktop.exe；Mosaic-0.6.0-win-x64.msi 为 51,905,472 字节，SHA-256 C307F5CB184CB302A5278231F67C05BDD9132CF8798B18840B1C2AA905289B7D。MSI 以 SkipValidation 构建。
- 未完成验收：完整桌面 Smoke、所有0/1/3/12/超限截图矩阵、100–200% DPI、多屏/高对比度实机、MSI ICE 与干净 Windows 安装升级。生成安装包不等于完成上述验收。


## 待实施计划：速览 UI 与更新简要任务（2026-11-16）

- 本轮仅更新规划文档，未改程序或用户数据，也未创建实际更新任务。完整需求、默认行为、模块接口与验收见 [专项计划](peek-and-update-summary-plan.md)，总计划新增阶段 11。
- 用户已确认正文范围为“本次更新的全部新增、改进和修复”；专项计划第 1、4、7 节与总计划阶段 11 已同步定稿，移除全版本功能清单的备选要求。功能仍处于待实施状态。
- 视觉原因位置：`PresenceSurface.PositionWindows` 将最大高度直接用作实际高度；`TaskPeekViewModel.UpdateView` 的逐项大卡片，以及 `PresencePopupView.xaml` 的大按钮和辅助文字造成层级偏重。拟分别改为自然高度、紧凑任务行与轻量操作区。
- 更新任务能力缺口位置：`ITasksApi` / `TaskService.CreateAsync` 只有普通创建，`ITaskRepository` / `SqliteTaskRepository` 没有原子创建回执；计划新增公开通用幂等能力，独立更新模块负责发布正文与触发。
- 文档改动位置：新增 `docs/peek-and-update-summary-plan.md`；同步 `docs/implementation-plan.md` 阶段 11、`docs/settings-plan.md` 第 14 节、`docs/desktop-presence-plan.md` 后续规则、`docs/architecture-principles.md` 第 13 节，以及 README 文档入口。本节只登记计划，不复用历史测试计数证明新功能已完成。

## 当前增量：0.5.1 旧配置迁移收口（2026-11-16）

- 根因位置：`AppConfiguration.Load` 在完整校验前直接改写旧配置，且没有保存一次性迁移状态；早期修复又在迁移前解析旧依赖图，旧 `tasks-ui` 因缺少 `presence.tasks-view@1` 被提前拒绝。用户关闭 Presence 后仍会被每次启动补回，设置覆盖存在时还可能只更新 modules 或 providers 的单层。修复前逻辑没有覆盖顶层与 `settings.host.*` 的完整生效组合。
- 改动位置：`src/Mosaic.Desktop/AppConfiguration.cs` 的解析、校验、Presence 迁移和原子写回；新增 `src/Mosaic.Desktop/HostConfigurationValidator.cs`；`src/Mosaic.Desktop/HostSettingSource.cs` 改用共享语义校验；`src/Mosaic.Desktop/appsettings.json` 增加 `desktop-presence-v1`；`tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs` 新增迁移黑盒场景；版本与说明同步到 `Directory.Build.props`、`build/package.ps1`、WiX 默认值、README 和发布文档。
- 迁移先校验顶层及实际生效的模块/provider，再修改配置；成功后原子写入 `appliedMigrations: ["desktop-presence-v1"]`，原文件保存为 `Config/appsettings.json.pre-migration`。非法配置不写盘，已迁移后主动禁用 Presence 不会被重新启用。
- Release 构建 0 警告、0 错误；非桌面自动化 121/121 通过（含 WPF 49/49）；迁移黑盒测试 6/6 通过，覆盖顶层配置、模块/provider 联合及单独覆盖、一次性和非法配置保留。
- 已生成自包含 `artifacts/local-run` 和 `Mosaic-0.5.1-win-x64.msi`（51,864,156 字节，SHA-256 `528BF03F56E8544AC1FD508E8FBD5B3A03A49E50D569D93156D4DFC85AB2EF2F`）；MSI 数据库回读 `ProductVersion=0.5.1`。使用真实 `%LOCALAPPDATA%\Mosaic` 旧配置启动后，迁移成功且进程持续运行。Windows Installer 服务不可用，ICE01–ICE105 未执行，安装/升级仍待独立验收。
- 完整交互式桌面 Smoke、干净 Windows 安装与 0.5.0→0.5.1 升级、多屏/DPI、系统会话结束及旧 0.1 配置完整迁移矩阵仍需独立验收。

## 历史增量：0.5.0 设置与架构收口（2026-11-16）

- Release 生产依赖链构建通过：0 警告、0 错误。非桌面自动化 121/121 通过：架构 4、Presence.Core 14、Kernel 5、Settings.Core 25、Settings.Sqlite 1、Storage.Sqlite 12、Tasks.Contract 2、Tasks.Core 9、WPF 49。
- 宿主设置默认值从 settings-core 移入 Desktop；canonical 模块/provider 默认由 `BuiltInModuleCatalog` 推导，旧顶层配置作为基线读取，`settings` 段只覆盖用户显式保存的项。
- ProviderMap/ModuleList 使用共享解析、格式化和等价比较规则；设置页、settings-core 与宿主语义校验不再维护三套正则。跨存储提交增加 pending journal，启动激活时幂等恢复，失败进入配置诊断。
- 设置定义自带本地化键、描述和选项；Settings.Wpf 删除模块 ID 特判。导航改由 `LabelKey` 驱动，Shell 不再识别业务 ViewId 或中文标题。
- 修复导航重建时错误地把 Label 当资源键、把 ViewId 当回退文本的问题；中英文模式下的导航标签、侧栏提示与页面标题均有 WPF 自动化覆盖。
- 任务紧凑页改为真实单栏；宽屏双栏、单一 ViewModel、草稿与离页守卫保持不变。
- 本记录只证明自动化与 Release 构建。完整交互式桌面 Smoke、多屏、100–200% DPI、系统会话、结构化模块/provider 编辑、重启 pending 数量和干净 Windows 安装升级仍需验收。

## 当前增量：0.3.0 后台驻留（2026-11-16）

本节为当前工作树状态；下文 0.1/0.2 的测试计数和安装包哈希均为历史记录。

- 20 个生产程序集、10 个测试项目。新增 Presence Contracts/Core/Wpf；WPF 驻留模块不引用 Tasks 或 Storage。Tasks UI 只通过公开任务 API 构建独立速览会话。
- 默认关闭主窗口进入驻留并保留页面和草稿；再次启动同工作空间恢复原主窗口。退出申请先接受再排空，取消不关闭入口；系统会话结束和致命错误绕过驻留。恢复不因摘要清理异常被阻断。
- 默认右上侧 32 DIP 方形入口、露出 12 DIP、单击展开。实现三种触发、延时收起、Esc/外部点击、恢复/退出菜单、可配置工作区/DPI 定位。每次弹出重新查询；状态使用图标/文字/颜色，高对比度覆盖自定义色。
- 17 项驻留设置全部归 Presence；完整模块配置共 74 项、5 个分区、11 项普通/63 项高级。普通模式驻留显示 3 项，启用高级显示 17 项。定义、验证和即时应用随来源注册/撤销。
- 新默认配置显式包含 Presence；旧配置只兼容补充已有 tasks-ui 的新能力绑定，不自动启用新模块。本地完整发布目录统一为 `artifacts/local-run`。
- Release 构建 0 警告、0 错误；离线锁定还原通过。非桌面测试 102/102：架构 4、驻留 Core 14、内核 5、设置 Core 12、设置 SQLite 1、存储 12、任务契约 2、任务 Core 9、WPF 43。只读摘要预览：`artifacts/ui-preview/presence-tasks-readonly.png`。
- 交互桌面通过：启动/任务保存、关闭后进程仍活跃、再次启动恢复相同窗口和未保存备注、驻留普通项中文显示、保存 Exit 后关窗正常结束进程。Windows 检查工具未枚举小型无任务栏入口；未据此宣称边缘点击/跨窗口悬停/菜单退出已通过。
- 已生成本地 0.3.0 自包含 EXE 和 MSI（51,867,744 字节）；MSI SHA-256 `A6B3CC26D3D295F5B78D121DA2AF574FAE016097249BC99AEAAAA7C939BA9019`。打包跳过 Windows Installer ICE 校验，未执行本轮安装/升级，不将“构建成功”等同于安装验收。
- 工作空间启动入口 `artifacts/Mosaic.lnk` 指向唯一完整程序 `artifacts/local-run/Mosaic.Desktop.exe`，带 `--data-root artifacts/local-run-data` 的绝对路径；该工作空间配置已备份并显式接入 Presence，未修改任务数据库。
- 待验收：完整 9 项桌面 Smoke、多屏/负坐标/热插拔、100–200% DPI、高对比度实际桌面、全屏策略、系统注销、干净 Windows 安装升级。当前版本不因自动化通过而视作全部环境交付。

## 最新计划跟进（2026-11-15，普通项恢复）

### 界面整理（2026-11-15）

侧栏修复补充：导航按钮移除固定 154 DIP 宽度，跟随容器伸缩，折叠模式按图标栏宽度计算内边距，避免高 DPI 下选中边框被裁切。Shell 右边界增加拖动手柄，支持鼠标拖动、双击恢复、左右键微调及 Home 恢复；拖动取消会回滚，当前会话的拖动宽度在页面切换及窗口折叠/展开后保留。宽度限制为 160–320 DIP，并为内容区保留空间。全部行为在 Shell 内完成，导航仍来自通用贡献协议，不新增模块引用。新增拖动限制、取消、折叠往返与按钮边界测试；WPF 38 项通过。

- 设置页改为分区导航与紧凑参数行：宽窗口标签与控件左右排列，可用宽度低于 900 DIP 时自动上下排列；按类型仅创建当前编辑器，去除隐藏控件及空错误行占位。
- 保存/放弃固定在滚动区外，显示当前分区设置数量、未保存修改数量与处理进度；支持 Ctrl+S。危险的全部恢复降为次要操作。
- 普通与高级模式使用明确说明；布尔参数改为带文字和键盘焦点边框的开关。选中导航通过动态主题颜色高亮，字段控件补齐可访问名称。
- 任务页辅助文字统一提升至 12 DIP，继承主题前景色和背景；已有任务收起重复的保存帮助，给表单更多空间。
- 新增设置页宽窄重排、操作栏固定、控件边界与可访问名称测试，并生成浅色、深色和高对比度预览。
- Release 构建 0 警告、0 错误；82 项非桌面测试通过（含 WPF 37 项）。界面改动留在对应 WPF 模块，继续使用共享主题资源及既有公开 API。

本节覆盖下文历史记录中“全部 Advanced”的旧行为。

- 指定 8 项恢复 Normal；普通模式动态显示任务 6 项、外观与布局 2 项，存储系统与文案分区隐藏。高级模式展示已注册的全部 57 项，复用同一行实例。
- 保存、放弃及分区恢复按可见定义 ID 限定范围；切换分区和隐藏未保存高级项前确认；只修改普通项时切换高级模式保留输入。
- 风险修改提交前确认；整数无效输入进入脏状态；放弃/恢复通知布尔和枚举控件；即时应用警告不再误报保存失败。
- 任务编辑栏固定在第 2 列，消费编辑栏宽度及栏间距；外壳默认 1504×1012、最小 1100×720，断点为 1180/1200，过小工作区仅修正本次窗口。
- 设置导航使用继承前景色的矢量齿轮；设置来源经公开验证接口校验跨字段组合。即时 Apply 仅传即时参数并通知已注册消费者，避免提前应用重启规则；宿主失败补偿使用独立取消令牌并检查返回值。
- Release 构建 0 警告、0 错误；本轮非桌面测试 81 项（架构 4、内核 5、设置核心 12、设置存储 1、存储 12、任务契约 2、任务核心 9、WPF 36）。预览新增 1100/1504 固定右栏与高级开关两态。

本轮为计划的增量实施，不代表完整交付：仍需补齐旧 0.1 配置迁移、重启 pending 数量、预览能力归属、禁用模块值的全部清除、结构化模块/provider 编辑，以及干净 Windows 安装升级验收。历史安装包哈希对应上一次发布，不代表本轮本地构建。

日期：2026-11-13。实施依据：[细节实现计划](implementation-plan.md)、[架构原则](architecture-principles.md)。

## 交付内容

- 13 个独立生产程序集、7 个测试项目；C# 14、WPF、MVVM 和真实 SQLite 存储。
- 任务创建、查看、编辑、删除、完成/恢复及全部/未完成/已完成筛选。
- 加载、空列表、字段错误、保存失败、版本冲突、删除确认、未保存编辑和退出等待。
- 配置驱动的模块激活及导航贡献；空配置不打开数据库；禁用界面不创建任务页面。
- 有界单消费者数据库线程、原子版本写入、同步事务回调、迁移校验与升级前一致性备份。
- 数据目录独占锁文件及仅当前用户可访问的激活管道；异步关窗、在途请求租约与逆序资源回收。
- win-x64 自包含文件夹发布及 WiX 每用户 MSI；数据位于安装目录外。

实际项目布局见 `Mosaic.sln`。相关小协议集中在各自契约项目的源码文件中，未为每个类型机械拆分文件。

## 固定版本与实施选择

| 项目 | 版本/选择 |
| --- | --- |
| 开发 SDK | global.json：10.0.300，latestPatch |
| 自包含 .NET / Windows Desktop 运行时 | 10.0.15，显式固定，已核对发布 runtimeconfig |
| Microsoft.Data.Sqlite / Hosting | 10.0.9 |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5；锁文件解析原生 SQLite 3.53.4 |
| CommunityToolkit.Mvvm | 8.4.2 |
| xUnit v3 / runner / Test SDK | 4.0.3 / 4.0.1 / 18.12.0 |
| WiX SDK | 4.0.6，MS-RL；仅构建工具，不进入应用运行时 |
| 目标平台 | Windows 10 2004（19041）及以上，x64；本机验证为 Windows 11 26100 |
| 应用标识与范围 | Mosaic；版本 0.1.0；每用户安装；内置四格图标 |
| 字段规则 | 标题 Trim 后 1–200 个 Unicode 码点；备注最多 20,000 个码点 |
| SQLite 策略 | 单连接、不池化、DELETE 日志模式、默认 128 队列容量及 3 秒锁等待 |

自包含补丁依据 [官方 .NET 10 下载页](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)。旧 Microsoft.Data.Sqlite 传递的 SQLitePCLRaw 2.1.11 原生包被 NuGet 审计拒绝，故显式升级该依赖，保留驱动程序集边界。

WiX 7 的额外维护费协议没有被接受；采用已验证的 [WiX 4.0.6](https://www.nuget.org/packages/WixToolset.Sdk/4.0.6)。固定每用户安装不适用 ICE91；上游 e_sqlite3.dll 无语言资源导致 ICE60，因此安装项目窄范围排除这两项 ICE。其余安装器检查继续执行，未修改上游原生二进制资源。

## 已执行验证

Release 构建：0 警告、0 错误。格式检查通过。锁定依赖还原通过。

`dotnet test --solution Mosaic.sln -c Release --no-build --no-restore`：**47 成功、0 失败、0 跳过**（不含桌面自动化烟雾测试）。

| 测试项目 | 数量 | 主要覆盖 |
| --- | ---: | --- |
| Architecture | 3 | csproj 与实际程序集依赖、框架限制、公开类型清单、服务定位器/反射/SQL 越界 |
| Kernel | 3 | 依赖排序、重复提供者、缺失/版本/循环拒绝、调用租约、失败逆序回收 |
| Tasks.Core | 6 | CRUD、不可变字段、Unicode 限额、无效请求、取消、冲突、停止、存储失败 |
| Storage.Sqlite | 5 | 重开持久化、迁移摘要/版本拒绝/回滚/备份、取消后真实提交、停止排空、会话生命周期、锁超时 |
| Tasks.Contract | 2 | 同一仓储契约分别验证内存替身和真实 SQLite；排序、筛选、条件竞争写入、删除、重开 |
| Wpf | 21 | STA/Dispatcher、保存失败保留输入、冲突、删除取消、最新筛选、空贡献、保存中退出、重试覆盖、反馈分离、固定操作栏、筛选回退、主题调色板、导航契约与宽窄预览渲染 |
| Desktop.SmokeTests | 7 | 发布程序真实 UI Automation：中文/emoji CRUD、完成/恢复、单实例、重启读取，以及空/仅后端/缺依赖/坏 JSON/重复字段/路径穿越配置 |

数据库集成测试使用真实临时文件。桌面测试使用 `artifacts/smoke-data/<guid>`，不使用日常数据库。WPF 控件在独立 STA Dispatcher 中创建。

### 窄窗口渲染修复（2026-11-14）

任务页面在窄窗口下偶发越界文字浮层。根因是外壳把空工作空间提示放在内容页下层，内容层未使用不透明背景，导致“你的工作空间已就绪 / 当前没有启用的功能…”从任务页面透明区域透出并与界面文字混叠。修复为：有页面内容时隐藏空工作空间占位层，并给内容层增加不透明背景模板；任务列表、编辑器和滚动视口同时启用边界裁剪与像素对齐。

修复后的 Release 构建为 0 警告、0 错误，24 项非桌面自动化测试通过；WPF 测试新增断言，验证空工作空间占位层在有页面内容时切换为隐藏。当前受限会话中的交互式桌面自动化被物理 `Esc` 中止，因此桌面视觉复测仍需在正常交互式 Windows 会话执行。现有 2026-11-13 的 31 项测试结果保留为修复前基线。

发布文件夹已重新生成。MSI 也使用更新后的负载重建；由于本次受限会话无法访问 Windows Installer 服务，ICE 校验在此次重建中关闭，完整 ICE 结果仍以 2026-11-13 的安装验证为基线。

### 审查问题修复（2026-11-14）

本轮按代码审查清单完成了以下修复：

- 单实例从 `Local\` 会话内互斥量改为数据目录内独占锁文件；最终路径分析可归并尾部分隔符、短路径和联接点，激活管道名也由同一最终路径生成。监听失败增加 250 毫秒退避和诊断上报。
- 外壳在页面初始化成功后才显示；启动后半段失败会先释放 Shell，再恢复诊断窗口。关窗协调器会继续执行 Shell、模块和宿主的剩余清理步骤，观察清理异常，并避免从 `async void Closing` 二次泄漏异常。
- 事务入口在排队和调用回调前拒绝 `Task`、`ValueTask` 和 `ValueTask<T>` 回调。
- provider 绑定现在要求对应模块同时启用，禁用模块的陈旧绑定会在启动时拒绝。
- 任务排序只由 `ITaskRepository` 契约拥有，业务服务原样透传；任务模块自己启停执行门，避免模块状态与宿主门状态分离。
- UI 贡献注册返回可释放句柄，导航使用可观察只读集合；撤销贡献会同步移除导航并释放已显示页面。
- Kernel 在激活前比较 factory 返回的模块清单与装配清单；模块清单增加职责、生命周期和入口版本，内置入口集中由模块目录创建。
- SQLite 行同时保留列序和名称查找，重复列名不再因字典键冲突伪装成存储故障；迁移备份仅保留最近 5 份。
- 任务加载及后续页面操作的程序错误会带相关编号写入诊断；Host 不再重复读取已由强类型配置加载器解析的 JSON。

修复后 Release 构建为 0 警告、0 错误，31 项非桌面自动化测试通过。进程级验证确认主实例存活、使用尾部分隔符启动的第二实例以退出码 0 退出，数据库成功建立；禁用模块的陈旧 provider 配置被拒绝且未创建数据库。当前受限桌面的 UI Automation 仍只能在 8 项中完成 5 项，默认、空配置和仅后端 3 项在 15 秒内无法发现窗口，沿用此前的交互式桌面环境限制，不能据此判定视觉回归。联接点自动化因权限审批超时未执行；短路径在 NTFS 8.3 名称未生成的当前卷上不可用。

本次发布目录和 MSI 已使用修复后的负载重建。`dotnet format --verify-no-changes` 仍因受限会话不能连接 MSBuild 命名管道而无法执行；Roslyn 编译器与分析器在 Release 构建中报告 0 警告。MSI 因 Windows Installer 服务不可访问而使用 `SuppressValidation=true` 重建，完整 ICE 验收仍以 2026-11-13 的历史结果为准。

### 前端反馈与主题收尾（2026-11-14）

在上一轮模块边界收尾后，继续处理 UI 反馈、响应式与主题问题：

- 列表加载态先清除旧错误卡片，重试期间按钮不可重复触发；空态、错误、加载三层按 `ZIndex` 0/1/2 排列并新增断言。
- 字段校验错误只出现在字段下方，修改对应输入即清除；全局错误与成功消息互相替换，新建和删除后的消息不再残留。
- 编辑器备注改为 `MinHeight/MaxHeight` 自适应，底部保存与操作栏固定在滚动区域外，小窗口不再需要滚到底部才能保存。
- 冲突有独立提示卡与“重新加载最新版本”入口；所有保存、读取、状态切换、删除操作显示忙碌条并禁用可编辑控件。
- 在当前筛选下新建、保存或切换完成状态导致结果不可见时，自动切回“全部任务”，避免误判保存失败。
- 标题和备注提供必填/上限帮助文字与实时码点计数，超限时计数变红；对比度不足的 `TextMuted` 已调整并通过 4.5:1 断言。
- 共享 `Mosaic.Presentation.Wpf` 主题包含浅色、深色、高对比度三套调色板；启动时按系统设置自动选择，运行时跟随系统切换。
- 导航改为纯文本标签加 `IconKey` 契约，外壳统一渲染图标并高亮当前导航项；任务模块不再把“✓   任务”写进标签。
- 宿主不再特判 `tasks-ui`：`IUiModule` 是通用界面模块契约，`ModuleRuntime` 在模块启动后报告激活、停止前报告停止，宿主据此统一注册/撤销导航与页面贡献。
- `ShellWindow` 在 1040 DIP 以下把 190 DIP 完整侧栏收为 64 DIP 图标栏，并在 1060 DIP 才重新展开；图标栏保留 44×44 可访问导航按钮，只隐藏展示标签，不撤销导航贡献。
- 图标栏状态卡折叠为单个状态圆点，移除空卡片外框；圆点通过提示和可访问名称保留“本机工作空间 · 离线可用”语义。
- `TasksView` 在自身可用宽度 920 DIP 以下把列表与详情改为单页切换；两个面板复用同一实例和 ViewModel，跨断点保留选择、输入与滚动状态，“返回任务列表”沿用未保存确认。
- 任务详情在窄栏或高度小于 680 DIP 时把“标记完成/重新加载”放在第一行、“删除”独占第二行；按钮最小高度为 40 DIP，底部操作区始终位于表单滚动区之外。
- 顶栏面包屑在图标栏下缩写为“MOSAIC”；任务列表行保留最小高度并省略超长文字；详情备注改为随剩余空间自适应，窄窗操作按钮保持 40 DIP 最小高度和 96 DIP 最小宽度。

本轮更新后 Release 构建为 0 警告、0 错误，50 项非桌面自动化测试通过（架构 3、内核 5、存储 8、任务契约 2、任务核心 8、WPF 24）。响应式测试覆盖 420、480、560、760、839、920、1039、1040、1059、1060、1220 DIP 及 480、620、780 DIP 高度；`artifacts/ui-preview/` 包含 `shell-760-rail.png`、`tasks-detail-760.png`、`tasks-detail-920.png`、`tasks-detail-560.png`、`tasks-list-480.png` 等计划要求的预览。

响应式更新后已重新生成 `artifacts/win-x64-ui` 发布目录与 `artifacts/installer/Mosaic-0.1.0-win-x64.msi`，安装包 SHA-256 为 `7E75FF4DAEC6AF4AD00AB020C4380FC2B70653BBA28B7DDE486254BFCED2940C`。

### 设置功能 S0–S5 初版（2026-11-14）

按 [设置功能规划](settings-plan.md) 新增了独立的设置模块层和宿主装配：

- `Mosaic.Settings.Contracts` 定义参数类型、来源、宿主来源和 `ISettingsApi`；`Mosaic.Settings.Core` 提供计划中的 54 项定义、类型/范围/枚举校验、用户设置与宿主配置路由、按模块或全部恢复默认。
- `Mosaic.Settings.Sqlite` 使用独立 `settings` 表迁移与 JSON 值映射；加载结果转回普通 CLR 类型，避免 `JsonElement` 泄漏到界面。
- `Mosaic.Settings.Wpf` 提供 `settings-ui` 模块，导航为“设置”，页面支持普通/任务/外观与布局/存储与系统/文案分区、布尔/枚举/文本编辑控件、字段错误、忙碌反馈、重启徽标、单行恢复默认、恢复当前分区或全部默认，以及未保存离开确认。
- 宿主实现 `IHostSettingSource`，设置按 `Config/appsettings.json` 的 `settings` 节点原子写回；默认 `appsettings.json` 装配 `settings-sqlite → settings-core → settings-ui`。
- 架构白名单与公开 API 清单已同步 Settings 项目；新增 Settings.Core 和 Settings.Sqlite 测试。

本轮 Release 构建为 0 警告、0 错误，56 项非桌面自动化测试通过（架构 3、内核 5、设置核心 5、设置存储 1、存储 8、任务契约 2、任务核心 8、WPF 24）。设置页的启动装配和编辑控件已进入桌面构建；交互式桌面截图已更新。WAL/连接模式消费和任务/外壳各参数的即时应用仍在后续 S4/S6 验证。

### 发布与安装

- 发布目录：`artifacts/win-x64`，含可执行文件、.NET/WPF 运行时及 x64 SQLite 原生库。
- 正式本机构建：`artifacts/installer/Mosaic-0.1.0-win-x64.msi`。
- `0.1.1` MSI 仅用于测试同一应用负载的安装器升级行为，不代表另一应用功能版本。
- `build/test-install.ps1` 最终复核已通过：0.1.0 每用户安装、0.1.1 升级、安装后与升级后各 7 项桌面烟雾测试、卸载、哨兵文件哈希保留和安装二进制清除。测试安装已卸载。
- 汇总日志：`artifacts/installation-verification.log`；详细日志：`artifacts/installer/install-test.log`、`upgrade-test.log`、`uninstall-test.log`。所有 Windows Installer 操作返回成功。
- 安装器日志位于 `artifacts/installer`；升级和卸载的数据保留检查使用独立哨兵文件，不宣称覆盖所有历史版本数据库迁移。

### 可复现命令

```powershell
dotnet restore Mosaic.sln --locked-mode -m:1
dotnet build Mosaic.sln -c Release --no-restore -m:1
pwsh -File build/publish.ps1
dotnet test --solution Mosaic.sln -c Release --no-build --no-restore
dotnet format Mosaic.sln --verify-no-changes --no-restore
pwsh -File build/package.ps1 -NoPublish
```

受限宿主中的 Roslyn/MTP 命名管道及桌面自动化可能被禁止，测试需要在允许命名管道的交互式 Windows 环境运行；`build/test.ps1` 可直接运行各 xUnit 可执行入口。`-SkipDesktop` 仅运行架构、内核、业务、数据库和 STA 测试。

本机遇到官方 NuGet v3 签名元数据端点 TLS 失败，采用 `build/publish.ps1 -UseV2Feed` 成功从官方 v2 端点还原；未关闭签名校验，也未改用第三方包源。

安装升级复核仅在没有既有 Mosaic 安装的测试用户中运行：

```powershell
pwsh -File build/package.ps1 -NoPublish -ProductVersion 0.1.1
pwsh -File build/test-install.ps1
```

## 0.2.0：设置功能闭环（2026-11-15）

按 [设置功能规划](settings-plan.md) 的阶段 1–6 完成设置功能闭环。提交：`4e555cc`（阶段 1–3a）、`38b7b13`/`16fcbae`（阶段 3b 存储参数）、`abe56e9`（阶段 4 架构归位）、`76e4b5f`（阶段 5 本地化与窄窗）。

### 阶段 1–2：数据正确性与设置页可操作

- 整数行输入进入脏状态并写回 `ProposedValue`，修掉“保存不到/不生效”的数据正确性问题。
- 设置定义注册从 ViewModel 构造移回各模块激活期：tasks-core/tasks-ui/shell/storage-sqlite 各自提供定义，`ActivateAsync` 注册、`StopAsync` 撤销；tasks-core 先注册自身来源再读设置，`TaskRules` 拿真实约束。
- `host.modules` 默认值补全 `settings-sqlite/settings-core/settings-ui`，恢复默认不再写出残缺清单。
- `SettingOption` 显示名经本地化键解析；`ShowAdvanced` 时普通分区显示全部行，Appearance 分区纳入 `appearance.theme`/`ui.language`。
- `VisibleRows`/`HasVisibleRows`、`Message`/`IsError`/`HasMessage`、`IsBusy`/`IsNotBusy` 通知补齐；离开页放弃修改会回滚主题/语言预览。
- `IUserDialogService` 增加通用 `ConfirmAsync`，恢复默认不再复用删除确认框；分区按钮有选中态。

### 阶段 3：宿主设置消费与原子保存

- `ProposeChangesAsync`/`ResetAsync` 事务化：宿主配置写失败回滚用户设置，杜绝“界面报失败、数据已部分落盘”。
- `HostSettingSource.PersistAsync` 成功后刷新启动快照覆盖值，UI 不再读回旧值；`File.Replace` 失败路径清理 `.tmp`。
- 真正接入消费方：启动宽高/最小尺寸、侧栏断点与页面边距、日志级别与保留、单实例退避、存储队列/锁/备份/WAL/连接模式、`storage.filename/location`。
- `ModuleList`/`ProviderMap` 结构校验与数据库文件名校验补全。
- 存储参数（WAL、PooledPerOperation）按规划单独提交（`16fcbae`），含故障注入与往返测试。

### 阶段 4：架构归位

- 定义归各模块：tasks-core/tasks-ui/shell/storage-sqlite 各自 `ISettingSource`；`SettingCatalog` 收敛为宿主定义；settings-core 只聚合。
- UI 最小接口：Shell、Tasks、Settings 只注入 `ISettingsApi`；注册能力只保留在模块激活路径。
- `ISettingRepository` 移入 `Mosaic.Settings.Contracts`，`Settings.Sqlite` 与 `Settings.Wpf` 不再反向依赖 Core。
- `build/project-boundaries.json` 收紧；架构测试升级为断言定义归属与无直连设置表。

### 阶段 5：本地化与窄窗打磨

- `MosaicLocalizationManager.LanguageChanged` 订阅链：Shell 导航标签/页面标题、Tasks 计数/反馈/编辑器文案、Settings 行文本/分区/反馈在语言切换后重算；`SetLanguage` 在无 `Application` 时也更新 `Owners`，`TryFindResource` 供各模块解析键。
- Settings/Tasks 资源字典补齐 `Settings.Error/Message/Constraint/Hint/Empty.*`、`Tasks.Editor.*`、`Tasks.Count.*`、`Tasks.Busy.*`、`Tasks.Error.*`、`Tasks.Message.Reloaded` 等约 60 键，含 `{0:N0}` 复数化格式串。
- 560px 任务详情压缩底栏、删除按钮独立行、备注自适应已有覆盖（`Over_compressed_task_views_keep_actions_and_text_operable`），本轮未再改动布局。

### 阶段 6：回归与发布准备

- smoke 夹具 `backend` 更新为含 `settings-sqlite/settings-core` 的新模块配置，其余场景不变。
- Release 构建 0 警告、0 错误；76 项非桌面自动化测试通过（架构 4、内核 5、设置核心 11、设置存储 1、存储 12、任务契约 2、任务核心 9、WPF 32），其中新增语言切换用例 3 个、设置 UI 用例 4 个、存储参数用例 4 个。
- 修复 WPF 测试中一处泄漏：响应式编辑器用例未释放页面，导致 TasksViewModel 跨 STA 线程收到语言回调。
- `dotnet format` 仍因受限会话不能连接 MSBuild 命名管道而无法执行（与 0.1.0 历史一致），Roslyn 编译器与分析器在 Release 构建中报告 0 警告。
- 版本号升至 0.2.0（`Directory.Build.props`、WiX 项目与 `build/package.ps1` 默认值）；`artifacts/win-x64` 已用 0.2.0 负载重新发布（`build/publish.ps1 -UseV2Feed`），产物版本为 `0.2.0.0`。
- MSI 已生成：`artifacts/installer/Mosaic-0.2.0-win-x64.msi`，最终 SHA-256 `FBD6587525AEE579CACF6E7CA7E8FD91375B5B3B535CE781E0252D88A4E8B5CF`（2026-11-15 设置分组重构、版本号收尾后的负载）。受限会话无法访问 Windows Installer 服务，本次以 `-SkipValidation` 关闭 ICE；完整 ICE、安装/升级保留设置与干净 Windows 验收仍需交互式会话。
- 桌面 smoke 8 项中 5 项通过；依赖窗口发现/关闭的 `Published_application_crud_reopen_and_single_instance` 与 `empty`/`backend`/`invalid` 中的 2–3 项在受限会话超时（窗口发现或关闭），与 0.1.0 基线一致，需交互式桌面复验。

### 复核修复：P1 功能闭环（2026-11-15）

对 verified 0.2.0 线复核后，补上了“界面显示已保存、实际不生效或半落盘”的剩余缺口。提交：`3a2c6f3`（值类型规范化、宿主语义校验与测试）、`3257cf2`（分区重置与回滚加固）、`1d71265`（完整装配预览）。

- 值类型统一：新增 `SettingValueNormalizer`，按定义 `Kind` 把存储层返回的 `long`、`object[]`、字典规范化为 `int`、`string[]`、`Dictionary<string,string>`；设置服务、宿主来源与设置行的显示/回写共用同一规则，`System.Object[]` 不再出现在界面上。
- 宿主装配真实接线：`Bootstrapper` 以 `settings` 段中的 `host.modules`/`host.providers` 为权威（顶层配置仅作兼容回退），经 `ModuleGraph.Resolve` 与 capability 校验后装配；默认模块清单与 5 条 provider 绑定由 `BuiltInModuleCatalog` 从内建目录推导，恢复默认不会再写出残缺配置。
- 语义校验归宿主：`IHostSettingSource` 增加 `ValidateAsync`，保存/重置前先由宿主校验未知模块、缺失依赖与不提供的 capability，settings-core 不反向依赖模块目录。
- 首启尺寸契约：P1 复核曾临时调整为 1220×780；最终按设置分组重构恢复为 1504×1012，最小尺寸为 1100×720，并新增契约测试断言默认值不小于最小窗口。
- 分区重置按可见项：`ISettingsApi.ResetAsync` 增加定义 ID 列表重载；UI 用当前分区可见行重置，外观分区含 `shell.window.*`，存储分区只含 `storage.*`/`logging.*`/`startup.*`/`diagnostics.*`，`host.modules`/`host.providers` 只参与“恢复全部默认”。
- 跨存储原子提交：顺序改为全部校验 → 宿主配置经 `File.Replace` 带备份写入 → SQLite 事务写入；任一步失败用备份或事务回滚，新增键不再残留。宿主写入成功但即时 `ApplyAsync` 失败时返回 `SettingResults.ApplyWarningId`，界面提示“已保存，重启后生效”并清除脏状态，不再误报保存失败。
- 预览夹具修复：预览测试改为完整装配（settings-core 聚合 host/shell/tasks 来源 + tasks-ui + settings-ui），重拍 `shell-wide`、`settings-wide`、`settings-760`；Shell 导航包含设置页。分组重构后产品最小验收尺寸统一为 1100×720。
- 版本可见性：窗口标题显示 `Mosaic v0.2.0`，侧栏状态区显示 `v0.2.0 · <提交短哈希>`；版本与提交取自程序集信息版本，便于区分发布包、本地构建与旧副本。

本轮 Release 构建 0 警告、0 错误；87 项自动化测试通过（架构 4、内核 5、设置核心 18、设置存储 1、存储 12、任务契约 2、任务核心 9、WPF 36）。

### 设置分组重构与版本号（2026-11-15）

- 删除设置页“普通”分区：57 项参数全部由定义中的 `SettingsGroup` 归入任务、外观与布局、存储与系统、文案四个分区；`SettingCategory` 仅保留兼容契约，全部定义均为 Advanced。
- 原 8 项普通项分别迁入任务（5 项）和外观与布局（3 项）；高级关闭时四个分区可见行数均为 0，开启后只显示当前分区的全部参数。
- 参数计数为 host 11 + shell 10 + storage-sqlite 5 + tasks-core 5 + tasks-ui 25（任务 20 + 文案 5）共 57 项。
- 任务页保持左右结构：列表在左，编辑栏固定在内容区右侧；默认窗口尺寸恢复为 1504×1012，最小尺寸为 1100×720。
- 窗口标题和侧栏显示 `v0.2.0 · <提交短哈希>`，便于区分发布包、本地构建与旧副本。

### 对照 §11 完成定义

- 57 项参数（host 11 + shell 10 + storage-sqlite 5 + tasks-core 5 + tasks-ui 25）均可在对应四个分区中开启高级设置后查看、修改、校验和恢复默认；即时项保存后生效，重启项显示重启徽标。
- settings-core 不理解任务、数据库或布局；各模块独占自己的规则与数据；无绕过公开接口直连设置表的路径（架构测试断言）。
- 危险项（WAL、池化连接、文件改名、备份保留等）有警告文案、约束和测试。
- 0.1.0 配置可平滑加载；设置表迁移可备份、可重开；安装升级保留设置仍需交互式安装验收。
- Release 构建、架构/业务/存储/界面测试已通过；桌面验收中的交互式项待正常会话复验后即可视为 0.2.0 交付。

## 后续验收边界

阶段 0–5 的首版功能、架构与本机测试已落实；阶段 6 的发布及本机 MSI 已交付，尚不能视为全部分发环境验收完成。

尚未执行：无 SDK 的干净 Windows 用户/虚拟机、Windows 10 实机、不同 DPI/中文输入法矩阵、ARM64、物理磁盘满/权限故障注入及迁移备份失败故障注入。本机未提供 Windows Sandbox。安装器数据保留检查和事务回滚测试不替代这些检查。

正式代码签名证书尚未提供，EXE/MSI 未签名。提醒、同步、分组、优先级、插件市场等仍不进入首版。

0.2.0 的交互式桌面 smoke（CRUD、空配置、坏 JSON）与安装/升级保留设置复验同样需要正常交互式 Windows 会话；受限会话中 `empty`/`backend`/`invalid` 的窗口发现与关闭超时属环境限制，非功能回归。

0.7.0安装包：Mosaic-0.7.0-win-x64.msi，51913664字节，SHA-256 93ACA6B893412C0FA881158D939868019AFD1C3E875B11E8382A871161267A75。

0.8.0产物：唯一完整程序 artifacts/local-run/Mosaic.Desktop.exe；MSI Mosaic-0.8.0-win-x64.msi，51941484字节，SHA-256 44CEF919674BFE86A3A1B8BB84140CD7AFC50785079C888C841005911191F7E8；数据库回读ProductVersion=0.8.0。MSI使用SkipValidation构建。

## 服务端（独立交付，0.1.0）

账号、邮箱验证码注册/登录、Device Flow、备份与增量同步的服务端实施记录见 [server-implementation-status.md](server-implementation-status.md)。服务端位于 `server/`，使用独立解决方案与版本号，不进入桌面 MSI；根因与改动位置、验证/未验证项均在该文档。
