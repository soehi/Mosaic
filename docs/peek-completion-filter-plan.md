# Mosaic：任务速览完成切换与筛选计划

状态：2026-11-17，0.10.0 已实施源码与回归；实际验证和剩余实机矩阵见实施记录。本计划新增速览内完成状态切换和会话内筛选切换，不改变任务数据语义、浮窗恢复规则或现有设置预览协议。

关联：[架构原则](architecture-principles.md)、[总实施计划](implementation-plan.md)、[任务速览按钮与详情](peek-task-details-plan.md)、[速览 UI 与更新简要任务](peek-and-update-summary-plan.md)、[设置规划](settings-plan.md)。

## 1. 根因与能力缺口

| 位置 | 当前事实与问题 |
| --- | --- |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml:31–48` | 左侧 `Path` 是状态图形，位于整行 Button 内部；点击整行只会打开详情，没有完成操作入口。 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekViewFactory.cs:23–34` | `TaskPeekRow` 只保存 `Id`、标题、状态和颜色，不保存乐观 `Revision`，无法发起完成切换。 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekViewFactory.cs:75–141、222–234` | 速览 ViewModel 只消费 `GetAsync` 和变更通知，未接线公开 `ITasksApi.SetCompletedAsync`。 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekViewFactory.cs:191` | 列表查询固定使用 `CompanionViewOptions.Filter`；会话内没有筛选切换状态和重新查询入口。 |
| `src/Mosaic.Presentation.Contracts/PresentationContracts.cs:15` | 通用速览选项只有初始筛选，没有完成开关和筛选开关的显示参数。 |
| `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSettings.cs:14` | 已有 `desktop.popup.filter` 作为默认筛选，但只是下次弹窗初始值，不是面板内控件。 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs:20–37` | 拖出手势绑定在整行 Button 的 `PreviewMouseLeftButtonDown/Move/Up`；当前实现已允许从按钮内部拖出，但用户反馈仍要求明确“整个任务标签”覆盖，包括起于标题区域。需要统一命中与点击判定，并在后续加入圆圈/筛选控件后排除新控件。 |

职责归属不变：完成语义和持久化归 Tasks Core，通过 `tasks.api` 公开申请提交；速览 UI 归 Tasks UI；面板默认选项由 Presence 读取设置后通过 `CompanionViewOptions` 传递；Floating Windows、Storage 和主任务页不需要改动。

## 2. 用户可见行为

### 2.1 左侧圆圈切换完成状态

- 每行左侧圆圈改为独立可点击的完成按钮：未完成任务显示空心圆，点击后提交“标记完成”；已完成任务显示对勾，点击后提交“恢复未完成”。
- 任务标题/卡片仍是详情入口；点击圆圈不打开详情、不触发拖出。整个任务标签（按钮边框内包括标题、状态文字、留白和圆圈以外的内容区域）都可作为拖动起点；圆圈完成按钮是排除拖动的独立操作控件。
- 提交使用当前行 `Revision` 调用 `SetCompletedAsync`。成功后由现有 `tasks.changes` 通知刷新列表；若当前筛选会因状态变化隐藏该任务，按新筛选结果显示，不本地伪造条目。
- 提交期间该行圆圈禁用并显示进行中语义；重复点击、面板关闭、会话取消都会终止后续应用。操作失败在面板内显示一次可消失的错误反馈；冲突时先刷新行，再让用户重试。
- 完成切换改变任务数据，必须等待真实成功回执；不使用乐观本地翻转、不预览、不在浮窗内容中复制第二份完成逻辑。
- 按钮、图形和整行都有中英文无障碍名称；未完成/已完成通过图形加文字表达，不只靠颜色。键盘可用 Tab 聚焦圆圈，Enter/Space 提交；焦点不被列表刷新打断到错误控件。

### 2.2 速览面板筛选

- 列表头部增加三段筛选：全部 / 未完成 / 已完成。默认选中项来自现有 `desktop.popup.filter`，新用户默认 `All`。
- 切换后立即按 `TaskQuery(filter)` 重新查询；沿用请求代次、取消、加载、失败、重试、空列表和 `MaxItems`/更多提示规则。
- 筛选是会话内显示状态：只影响当前速览弹窗，不写设置、不写数据库、不改主任务页筛选。弹窗关闭后下次按设置默认重新开始。
- 切换筛选时取消在途列表查询；详情页仍按原任务 ID 查询，返回列表后显示当前筛选结果。筛选为空时显示对应空态文案。
- 头部 Summary 继续显示当前筛选结果数量；筛选控件保持紧凑，不增加额外空白行，避免压缩小高度面板中的任务区域。

## 3. 通用选项与新增设置

为了保持“展示能力由宿主选项控制”，扩展公开 `CompanionViewOptions`：

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `ShowCompletionToggle` | `true` | 是否显示左侧完成切换按钮 |
| `ShowFilterSwitch` | `true` | 是否显示头部三段筛选 |

Presence 新增 2 项设置，继续归“驻留与边缘”分区：

| ID | 默认 / 范围 | 类别 | 作用 |
| --- | --- | --- | --- |
| `desktop.popup.completionToggle` | true / bool | Normal | 显示速览行完成切换；关闭后左侧只保留状态图形 |
| `desktop.popup.filterSwitch` | true / bool | Normal | 显示速览筛选切换；关闭后只用设置默认筛选 |

两项都是界面展示开关，使用 `Immediate` + `Reversible` 可逆预览；不含写入任务副作用。设置总数从 96 项增至 98 项，普通项从 14 项增至 16 项。`desktop.popup.filter` 语义保持不变：它是新弹窗的初始筛选，不是会话内临时筛选。禁用 Presence 后这两个开关定义随之消失；Tasks UI 收到关闭值时不创建对应控件。

实施时同步 `public-api.txt`、Presence 本地化、Tasks 本地化和中英文资源；不提前在文档中宣称新计数已生效。

## 4. 布局与交互规则

行结构调整为横向三列：完成按钮、任务卡片、状态文字（如有）。任务卡片仍保留现有按钮样式、边距、标题换行、拖出手势和右键菜单；完成按钮宽度复用约 20 DIP 标记空间，不额外撑高行。状态文字被关闭时卡片右侧仍保持原有留白规则。

筛选控件放在 Summary 同一行的右侧或下方，按小宽度换行；三个选项使用单选语义按钮，视觉上是紧凑分段控件。控件加载失败或选项被设置关闭时，不保留空占位。

拖动命中优先级为：完成按钮和筛选控件按各自交互处理，不启动拖出；任务标签 Button 及其内部非控件区域（标题 TextBlock、状态 TextBlock、内边距和空隙）可启动拖出手势。系统阈值内松手仍触发原有 `Click` 打开详情；达到拖动阈值后 `_suppressClick` 阻止点击。当前 `TaskPeekView` 的预览手势已绑定整行 Button，因此标题起点本应可拖；规划实现后必须新增自动化断言，证明从标题、状态文字和内边距区域拖动都命中，并把这一覆盖写入回归，防止后续模板改动只把手势绑回标题控件。

若实施时把行拆为完成按钮 + 任务卡片两列，只能把手势绑到任务卡片 Button；不得把拖动手势绑定到速览面板容器并吞掉列表滚动或筛选。右键菜单仍在任务卡片上；菜单打开与拖动手势互斥。

## 5. 实施步骤与改动位置

1. `PresentationContracts.cs` 扩展 `CompanionViewOptions` 可选字段，并同步 `build/public-api.txt`。
2. `PresenceSettings.cs`、`PresenceLocalization.cs` 新增两个 Normal 定义、选项资源与值映射；`PresenceSurface.ShowPopupAsync` 读取并传入新字段。
3. `TaskPeekView.xaml` 拆分完成按钮与任务卡片，新增筛选分段控件；`TaskPeekView.xaml.cs` 处理完成点击、筛选选择和既有拖动/菜单逻辑。
4. `TaskPeekViewFactory.cs` 给 `TaskPeekRow` 增加 `Revision`；ViewModel 增加 `SelectedFilter`、行级完成操作队列/取消、失败反馈和筛选重载；所有写入只走 `ITasksApi.SetCompletedAsync`，列表只走 `ListAsync`。
5. `TaskLocalization` 中英文资源补充圆圈、完成中、失败/冲突、筛选三段和无障碍文案；主题资源复用现有语义颜色，不硬编码新颜色。
6. 测试同步：WPF 测试覆盖圆圈提交、成功刷新、冲突/失败反馈、筛选重查、空态、选项关闭、焦点与自动化名称；Presence 测试覆盖新设置默认值、预览还原和选项传递；桌面 Smoke 覆盖点击圆圈后状态变化和筛选切换。
6a. 拖动覆盖回归：WPF/桌面测试从任务标题、状态文字和卡片内边距作为拖动起点执行拖出；从完成按钮和筛选控件起点不得触发拖出；短按起点仍打开详情。手势捕获、取消、面板关闭和菜单打开的既有语义保持不变。
7. 发布同批更新 `Directory.Build.props` 新版本、发布内容源/索引/清单、更新简要任务、README、总计划阶段 16、设置规划第 17 节和实施记录。

不改数据库结构、模块清单、provider 绑定或配置迁移；已有安装升级后无需用户操作。若设置值为旧安装缺失，使用默认开启。

## 6. 验收矩阵

| 场景 | 期望结果 |
| --- | --- |
| 未完成任务点击圆圈 | 成功提交后状态变为已完成；主列表、浮窗和变更订阅看到同一事实 |
| 已完成任务点击圆圈 | 成功恢复未完成；行显示和筛选结果更新 |
| 当前筛选为“未完成”时完成任务 | 刷新后该行从速览列表消失，计数正确 |
| Revision 冲突 | 显示冲突反馈并刷新；再次点击使用新 Revision |
| 存储失败或功能停用 | 面板内明确错误；任务状态不变；可重试 |
| 面板关闭或快速重复点击 | 在途请求取消或结果丢弃；不产生半应用状态 |
| 从任务标题、状态文字或卡片内边距拖动 | 达到阈值后可拖出；松手在面板外创建小窗，起点位置和抓取偏移正确 |
| 从任务标签短按标题 | 仍打开详情；不因新增拖动覆盖产生误拖或丢点击 |
| 从完成按钮或筛选控件拖动 | 不触发拖出；各自点击/选择功能正常 |
| 筛选切换 | 每次发出对应 `TaskQuery`；加载/失败/空态/重试和 More 上限正确 |
| 设置关闭完成开关 | 圆圈只显示状态图形，不可提交；筛选开关关闭时头部无筛选控件 |
| 键盘与读屏 | Tab 可达圆圈和筛选；名称、状态、快捷键反馈明确 |
| 浅色/深色/高对比度与 100–200% DPI | 状态图形、反馈、分段控件对比度和布局可用 |
| 自动化回归 | Release 0 警告；新增 WPF/Presence 测试通过；现有 166+ 基线不回归 |

受限环境只执行非桌面自动化时，必须把真实指针、读屏、DPI 和完整桌面 Smoke 记录为待验收，不得用静态检查代替。
