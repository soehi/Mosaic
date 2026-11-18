# Mosaic：浮窗无边框控件与可选正文编辑计划

状态：2026-11-18，已随 0.12.0 实施。实测结果与剩余验收项见[实施记录](implementation-status.md)。

关联：[架构原则](architecture-principles.md)、[浮窗原始计划](floating-task-windows-plan.md)、[落点预览与标题栏拖动](floating-placement-preview-plan.md)、[实施与验证记录](implementation-status.md)。

## 1. 目标与默认行为

- 去掉浮窗内部按钮的矩形描边，包括完成按钮、标题/展开按钮、恢复主窗口/置顶/关闭工具按钮；完成状态的圆圈或勾仍是语义图标。
- 去掉浮窗详细内容的外框，包括鼠标悬停、点击获得焦点、键盘焦点和编辑时出现的矩形描边。浮窗整体轮廓保持现有设计。
- 无边框控件仍提供交互反馈：悬停、按下及键盘焦点通过主题底色/文字变化表达，不改变控件尺寸；不以直接隐藏焦点且无替代反馈的方式处理。
- 在设置的“浮窗”普通分组新增“允许编辑浮窗详细内容”，默认关闭。说明文案明确：开启后可在浮窗编辑任务备注，保存后同步到任务列表。
- 本次详细内容指 `TaskItemDto.Notes` 正文。标题、完成状态、创建/更新时间仍按各自现有入口和语义处理，时间信息不可编辑。
- 开关开启后，正文支持进入编辑态；出现有文字标签的“保存”“取消”操作和未保存状态提示。没有备注时也提供编辑入口。Ctrl+S 保存，Enter 正常换行；取消编辑恢复已保存正文。操作区同样无矩形线框。
- 保存须等待 Tasks API 确认成功，普通输入、失焦、移动窗口及设置预览不会自动写入任务。

## 2. 根因及能力缺口定位

以下行号以本次检查的 0.11.0 源码为准。

| 根因位置 | 触发条件与原因 |
| --- | --- |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Themes/Floating.xaml:3–13`，`TaskFloatingStateStyle` | 完成按钮默认设置背景、1 DIP 描边；焦点触发器还将描边加粗到 2 DIP，因此截图中出现按钮方框。 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskFloatingContentFactory.cs:52`，`TaskFloatingContentSession`；`src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/FloatingSurface.cs:144–151`，`Create` | 标题按钮和通用工具按钮复用 `QuietButtonKey`；其模板继承通用按钮焦点触发器，透明描边在获得焦点后可能变成可见焦点色。 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekDetailsView.cs:34–39`，`AddText`；`src/Mosaic.Presentation.Wpf/Themes/Theme.xaml:147–150` | 正文使用只读 TextBox，控件本身虽设 `BorderThickness=0`，通用模板却在内部 `TextBorder` 获得焦点时强制设置 2 DIP 描边，故点击正文会出现绿色外框。只在控件上设透明边框无法可靠覆盖模板内部触发器。 |
| `TaskFloatingContentFactory.cs:34、129`；`TaskPeekDetailsView.Render` | 浮窗直接复用只读速览详情，所有文本均不可编写，且每次 Render 清空并重建子控件。直接去掉 IsReadOnly 会让后续刷新覆盖草稿、选区及输入光标。 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskUiSettings.cs:6–24`，`TaskUiSettingsTarget`；`TasksUiModule.cs:21、42` | 设置目标仅保存一个主页面回调，浮窗内容工厂未接入同一运行时设置目标；不能直接复用 Attach 来注册多个浮窗，否则会替换已有订阅。 |
| `src/Mosaic.Presentation.Contracts/FloatingPresentation.cs:17–28`；`FloatingWindowCoordinator.CloseAsync`；`FloatingWindowsModule.PrepareExitAsync` | 原有内容会话只有 Flush/Dispose 等生命周期，单窗关闭在保存 IsOpen=false 后直接销毁；没有可取消的内容离开确认。新增可编辑正文时必须补上草稿保护。 |

## 3. 架构归属

| 所有者 | 本次职责 | 协作方式 |
| --- | --- | --- |
| Tasks UI | 任务正文展示、编辑草稿、编辑开关定义、字段校验提示、版本冲突呈现、离开确认 | 使用已注入的 `ITasksApi`、`IUserDialogService` 和 Settings 公开契约 |
| Tasks Core | 更新任务的业务规则、Revision 检查、提交成功后的变更事实 | 复用 `ITasksApi.UpdateAsync(UpdateTaskRequest)`；不新增另一套正文存储 |
| Settings | 设置编辑会话、预览/放弃/保存、用户值持久化 | 注册 `ISettingSource`，通过 `ApplyAsync` 分发有效值 |
| Floating Windows | 通用工具按钮样式、窗口移动/布局、内容关闭准备与窗口生命周期 | 只调用公开内容会话协议，不读取 Task DTO、Notes 或任务设置键 |
| Presentation.Contracts | 通用内容关闭准备协议 | 表达内容能否安全离开，不包含任务业务字段 |
| 宿主与存储适配器 | 继续负责既有装配与数据持久化 | 不增加任务专用装配分支、不绕过公开 API 访问数据库 |

设置的显示分组与所有者独立：建议 ID `tasks.floating.allowNotesEditing`，ModuleId 为 `tasks-ui`，Group 为 `FloatingWindows`，Category 为 `Normal`，Boolean，默认 false，Immediate，UserSettings。设置模块现有分组契约已允许该组合，不必把任务编辑设置放入通用 FloatingSettings。

本次属于一个交付单元，在同一会话完成源码、测试、记录和发布，不跨会话或代理拆分同一改动。

## 4. 具体实施设计

### 4.1 样式作用域

1. 修改 Tasks UI 的 `Themes/Floating.xaml`：调整 `TaskFloatingStateStyle`，新增浮窗标题按钮及正文样式。模板内不再出现默认、悬停、按下或焦点矩形描边；状态图标、无障碍名称和命中区域保留。
2. Floating Windows WPF 新增 `Themes/Floating.xaml`：只提供通用浮窗工具按钮样式，在 `FloatingSurface.Create` 的窗口局部资源中加载并显式引用。
3. 不修改全局 `Theme.xaml` 来解决浮窗局部需求；任务主编辑器、设置表单及速览详情的原有输入框样式不随之变化。
4. 新正文控件使用自己的无边框 TextBox 模板，保留 `PART_ContentHost`、文本选择、复制、输入法、换行、主题选区/光标及滚动能力。只读时同样允许选择复制。
5. Tab 焦点使用清晰且稳定的底色等替代反馈；明暗主题和系统高对比度均验收。字段错误用文字提示，避免全局 Validation.ErrorTemplate 再画一层红框。

### 4.2 设置分发

- 在 `TaskUiSettingSource.Definitions` 和本地化映射登记新开关，由现有设置页按分组自动生成界面。
- 扩展内部 `TaskUiSettingsTarget` 为可释放的多订阅机制，并缓存最近一次完整有效值；保持任务主页面现有 Attach/Detach 语义兼容，浮窗使用独立 Subscribe 订阅，不能覆盖主页面回调。
- `TasksUiModule.FloatingFactory` 将同一个内部 settings target 传给 `TaskFloatingContentFactory`，再传给每个内容会话。设置回调在 UI Dispatcher 应用，新窗口立即消费缓存，Dispose 注销订阅。
- 使用可逆设置预览：预览开启/关闭以及放弃均只改变编辑能力，不保存任务。关闭能力或放弃预览不得清空已有草稿；立即禁止继续输入和保存，显示“编辑已关闭，存在未保存内容”，允许复制/放弃，重新开启后可继续。
- 草稿提示不能展示成“已保存”；开关仅控制此入口的编辑能力，不作为 Tasks API 全局授权机制。

### 4.3 正文组件与保存

- 新增内部 `TaskFloatingDetailsView` 及 `TaskFloatingNotesViewModel`（最终文件组织按职责确定）：只服务浮窗正文，不将任务速览改造成编辑器。
- `TaskFloatingContentSession` 改用该浮窗组件。稳定保存正文输入控件，不再在每次 `Render` 中销毁并重建编辑器。
- ViewModel 保存基线 DTO/Revision、DraftNotes、IsDirty、IsEditing、IsSaving、字段错误及外部变更状态；View 负责呈现、焦点和输入，业务请求通过依赖注入的公开 API 发出。
- 进入编辑时保存编辑基线；保存调用 `UpdateAsync(new(id, baseline.Title, DraftNotes, baseline.Revision))`，并复用 `GetConstraintsAsync` 的正文长度约束。不在 UI 复制一套持久化规则或常量限制。
- 保存成功使用返回 DTO 更新基线并清除 dirty，已有 TaskChange 机制同步主页面和其他展示入口；不额外建立全局事件总线。
- 保存失败保留全文草稿，显示可重试错误；Revision 冲突保留草稿，提示外部内容已变更，提供复制草稿/重新读取，不悄悄把 ExpectedRevision 升成最新值后覆盖写入。
- 自身完成切换和正文保存串行化，防止两个命令使用同一旧 Revision；完成状态仍请求 SetCompletedAsync。外部变更、语言变化、展开收起、布局/设置刷新不能重置 dirty 草稿。
- 编辑时 Esc 优先由编辑器处理：取消当前编辑，有修改时确认放弃；不继续冒泡到浮窗现有 Esc 收起逻辑。普通阅读态 Esc 仍按现有行为收起。
- 正文文本选择、滚动和保存/取消按钮不能成为标题栏拖动起点；保留 0.11.0 标题栏短按/阈值拖动逻辑。

### 4.4 草稿与生命周期

- 给 `IFloatingContentSession` 增加可等待、可取消的内容关闭准备操作，例如 `PrepareCloseAsync`，结果使用现有 `OperationResult`；默认无草稿实现成功，用户取消用现有 Canceled 错误码表达。
- `IFloatingWindowSurface` 增加对应的通用准备调用；`FloatingWindowCoordinator.CloseAsync` 在将 IsOpen 持久化为 false、销毁会话之前执行准备。不能只在 × 按钮中拦截，否则程序调用 CloseAsync 会绕过草稿保护。
- 内容会话使用已有 `IUserDialogService.AskUnsavedChangesAsync` 执行保存/放弃/取消。保存失败或取消时窗口和草稿均保留；取消不显示为布局保存失败。编辑开关已关闭时不能借关闭确认绕过开关保存，需要恢复开关或复制/放弃草稿。
- 两阶段关闭：等待用户选择时不持有 Coordinator 的全局串行锁；确认结束后重新检查窗口、会话草稿代次和生命周期状态，再执行持久化/销毁。重复关闭合并为同一在途请求，避免重复对话框及死锁。
- 真正退出和模块停用也先执行所有内容会话的关闭准备，再 Freeze、Flush 和保存窗口恢复状态；取消或失败恢复模块接收能力与窗口可用性，不进入 Dispose 后才询问。
- 明确区分主动关闭与内容确认删除：任务 NotFound 后停止保存草稿，提示原任务已删除，允许复制内容；确认失效的窗口最终关闭，不用 UpdateAsync 重建任务。
- 沿用项目已确认的强制丢弃退出入口，但明确它会丢弃未保存内容；正常退出不得走此路径。普通折叠可保留会话内草稿，不必触发关闭确认。
- 公共方法变化同步检查能力兼容性与所有实现/测试替身。若新增公开类型，更新 `build/public-api.txt`；不绕过公开 API 白名单。

## 5. 拟改动文件与位置

下表为计划范围；实施后按实际 diff 补齐最终逐文件行号。

| 文件/位置 | 计划改动 |
| --- | --- |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Themes/Floating.xaml` | 完成、标题、正文及编辑操作的无边框局部样式和焦点替代反馈 |
| `src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/Themes/Floating.xaml`（新增） | 通用浮窗工具按钮样式 |
| `src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/FloatingSurface.cs`，`Create`、`FlushAsync`、关闭准备 | 应用局部工具样式、通用内容离开准备、取消/失败处理 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskFloatingDetailsView.cs`（新增） | 稳定的无边框正文和编辑操作、焦点、快捷键及无障碍呈现 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskFloatingNotesViewModel.cs`（新增） | 草稿、约束、保存、Revision 冲突及离开确认 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskFloatingContentFactory.cs`，Factory、Session | 注入设置目标、替换只读详情、同步任务事件、处理内容生命周期 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskUiSettings.cs`，Target、Source | 可释放多订阅及缓存、新设置定义和本地化键 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/TasksUiModule.cs`，FloatingFactory | 复用模块内部设置目标，不增加跨模块内部访问 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Localization/zh.xaml`、`en.xaml`，必要的 `TaskLocalization` 映射 | 开关、编辑、未保存、失败和冲突提示 |
| `src/Mosaic.Presentation.Contracts/FloatingPresentation.cs`，`IFloatingContentSession` | 通用关闭准备协议 |
| `src/Modules/FloatingWindows/Mosaic.FloatingWindows.Contracts/FloatingContracts.cs`，`IFloatingWindowSurface` | 转发通用关闭准备，不引入任务类型 |
| `src/Modules/FloatingWindows/Mosaic.FloatingWindows.Core/FloatingWindowCoordinator.cs`，`CloseAsync`、退出协调 | 在关闭记录持久化前完成内容准备，并处理重入、取消与重新校验 |
| `src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/FloatingWindowsModule.cs`，`PrepareExitAsync`、`StopAsync` | 退出/停用时先准备内容，失败可恢复 |
| `tests/Mosaic.Wpf.Tests/FloatingWindowsTests.cs`、`EditingPresentationTests.cs` | 样式、设置广播、草稿、公开 API 保存、冲突、生命周期回归 |
| `tests/Mosaic.FloatingWindows.Tests/FloatingTests.cs` | 通用关闭协议、取消不写 IsOpen=false、串行协调/重入 |
| `tests/Mosaic.Desktop.SmokeTests/FloatingDragTests.cs`；新增正文编辑 Smoke 文件 | 真实键鼠无框显示、文本选择、编辑保存、短按与拖动兼容 |
| Settings 现有测试、Architecture 现有测试、必要的 `build/public-api.txt` | 设置定义计数、默认值、预览回滚、依赖与公开类型边界 |
| 版本、发布内容源及 `docs/implementation-status.md` | 同次交付发布说明、实施记录、根因/改动位置和产物校验 |

不需要修改 Tasks Core/Sqlite 的正文写入逻辑，也不需要改变 TaskItemDto、任务表或浮窗布局记录结构。若实施暴露新的契约限制，先更新计划并说明范围，不能在 UI 中直接写数据库。

## 6. 已有安装与设置兼容

1. 新键是增量设置，不重写用户的整份配置。已有安装缺失键时由 SettingsService.GetValuesAsync 的默认值合并路径补足 false，这是本次采用的兼容初始化逻辑。
2. 用户明确保存开启状态后由现有 UserSettings 仓储持久化；重启继续生效。放弃设置预览恢复已保存值。
3. 无数据库 DDL、目录迁移或模块装配配置变更；已有任务正文、Revision、窗口位置、尺寸、模式和置顶记录保留。
4. 完整默认装配预计从 98 项增加至 99 项（17 普通/82 高级）；仅在确认现有定义基线未变时更新计数断言，禁用 Tasks UI 后相应定义按模块生命周期撤销。
5. 迁移验收：用缺失新键的 0.11.0 数据副本升级，首次仍只读；原设置不丢失；开启并重启生效；恢复默认关闭；设置预览放弃还原；窗口恢复和 Notes 内容不变。不得操作真实工作空间来制造测试数据。

## 7. 验收与交付顺序

| 验收项 | 必须达到的结果 |
| --- | --- |
| 按钮各状态 | 默认、悬停、按下、鼠标/键盘焦点均无矩形线框；可辨认焦点，圆圈/勾和置顶语义可辨认 |
| 正文各状态 | 阅读、选择、聚焦、编辑及校验错误均无正文外框；复制、中文输入、换行、滚动正常 |
| 默认关闭 | 旧安装和新安装只读，仍可复制，输入和保存入口不可用 |
| 开启与多窗 | 主页面未打开也生效；多个现有/新建浮窗都收到设置，不覆盖主页面订阅，销毁后无残留回调 |
| 设置预览 | 开启、关闭、放弃往返可逆；不写任务，不清草稿，关闭后不能继续输入/保存 |
| 保存成功 | 通过真实 Tasks API 成功写入一次，Revision 按契约递增，主页面/速览一致 |
| 保存失败/冲突 | 草稿保留、错误明确，不用新 Revision 自动覆盖；删除后不重建任务 |
| 刷新兼容 | 状态切换、外部变更、语言/布局/模式变化不覆盖正在输入的内容，光标/选区合理 |
| 生命周期 | ×、系统关闭、公开 CloseAsync、退出及模块停用都覆盖保存/放弃/取消；取消不改关闭状态，失败不丢正文，无串行锁死锁 |
| 原拖动能力 | 标题栏按钮短按和超阈值移动正常；正文选择和保存/取消不移动窗口；落点预览仍可用 |
| 升级矩阵 | 缺失新键补 false，保存后重启保留；原用户值、任务、窗口恢复信息不变 |
| 架构 | 引用和公开 API 检查通过；通用 Floating Windows 不依赖 Tasks DTO 或任务内部实现 |

实施顺序：先局部样式和设置订阅，再正文草稿/保存与通用关闭协议，随后自动化及真实桌面 Smoke；这些步骤属于同一个完整改动。

功能实施完成后按项目约定运行适当的 Release 构建、任务/设置/浮窗/WPF/架构测试与真实交互回归；从同一发布内容源生成发布说明和包内更新简要，更新实施记录，发布 `artifacts/local-run` 并构建 MSI，核对版本、release-manifest、SHA256SUMS，再同次提交。未执行的实机/DPI/安装矩阵必须明确记录，不能用规划或路由测试冒充已验证。

0.12.0 的逐文件位置见[改动清单](releases/v0.12.0-files.md)。
