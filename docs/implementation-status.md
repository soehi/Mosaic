# Mosaic：实施与验证记录

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
