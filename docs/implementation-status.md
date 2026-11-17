# Mosaic：实施与验证记录

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
