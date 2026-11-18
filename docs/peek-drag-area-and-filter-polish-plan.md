# Mosaic：速览整标签拖出与筛选视觉收口（0.10.2）

状态：2026-11-18，已实施源码、回归测试与发布记录；真实指针拖出和 100–200% DPI 仍需独立桌面验收。设置计数不变（98 项：16 普通/82 高级），无数据库、配置或模块装配迁移。

关联：[架构原则](architecture-principles.md)、[总实施计划](implementation-plan.md)、[速览完成切换与筛选](peek-completion-filter-plan.md)、[速览整卡拖出修复](peek-card-drag-fix-plan.md)、[实施与验证记录](implementation-status.md)。

## 1. 用户反馈与根因

用户要求：拖拽区域覆盖整个任务标签，起点落在任务按钮（含完成按钮与任务卡片）也能拖出；点击任务标签打开详情的行为不变；同时重做速览筛选控件的视觉。

| 位置 | 事实与触发条件 |
| --- | --- |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml:87`（0.10.1 基线） | 四个鼠标处理器只绑在 `TaskCard` Button 上。完成按钮（`Grid.Column="0"`）和两列之间约 4 DIP 的留白不属于任何可拖出元素，起点落在该按钮上时不会建立拖动候选。 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs:42–47`（`StartGesture`） | 只接受 `Button` 起点；`_candidate` 是按钮，无法覆盖整行。 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/DragHintAdorner.cs`（0.10.1 基线） | 提示只有跟随指针的一段文字，拖动时任务标签本身没有任何视觉标记，用户无法确认整标签已成为拖出对象。 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml:51–58`（0.10.1 基线） | 摘要与筛选放在 `WrapPanel`：摘要带独立底边距、单选按钮用透明背景和临时边框，选中态只是浅色底，三项宽度不一且与摘要挤在同一行，视觉上零散。 |

非问题：0.10.1 的 `DragHintAdorner` 不抢占捕获、`CaptureLost` 终局兜底、Esc 取消与公开拖出申请链路均正确，本计划不改变这些语义。

## 2. 用户可见行为

- 拖出手势绑定到每行最外层 `TaskRow`（透明背景，覆盖完成按钮、任务卡片和两列留白）。从行内任意位置按下并超过系统拖动阈值即进入拖出；松手在速览面板外创建独立小窗。
- 拖动激活后，除跟随指针的提示文字外，整个任务标签显示主题强调色虚线框，明确拖出对象。
- 阈值内短按任务标签仍打开详情；短按完成按钮仍提交完成/恢复；阈值内短按行内留白不产生任何操作。
- 拖动激活后完成按钮的 `Click` 被抑制，避免“拖出后又切换状态”。完成按钮、筛选控件和右键菜单的点击语义保持原样。
- 筛选改为带轨道的三段分段控件：轨道使用 `SurfaceMuted` 背景与 `Border` 边框，选中段使用 `Surface` 药丸、强调色文字与半粗体，悬停仅提亮文字，键盘焦点使用 `Focus` 边框；摘要与筛选同排，宽度不足时摘要以省略号截断而不挤压筛选。
- 窄宽度下筛选始终完整显示；高度与 0.10.1 保持一致（紧凑弹窗 300 DIP 高度仍可完整渲染）。

## 3. 模块边界

| 模块 | 职责 |
| --- | --- |
| Tasks UI | 行级拖动命中、终局判定、整标签虚线框、筛选视觉与状态文案 |
| Floating Windows | 只消费公开 `OpenFloatingItemAsync` 申请与放置提示，本计划不改 |
| Presence | 只通过通用 `CompanionViewOptions` 控制筛选默认值和展示开关，本计划不改 |
| Storage / Tasks Core | 不变；拖出不写任务数据，完成切换仍走 `ITasksApi.SetCompletedAsync` |

`DragHintAdorner` 继续是 Tasks WPF 内部类型，不进入公开 API；`build/public-api.txt` 不变。

## 4. 实施与改动位置

1. `TaskPeekView.xaml`：`TaskRow` Grid 增加 `Background="Transparent"` 与 `PreviewMouseLeftButtonDown/Move/Up`、`LostMouseCapture` 处理器；`TaskCard` 只保留 `Click` 与右键菜单；筛选区改为 `Grid`（摘要 `*` + 轨道 `Auto`），轨道内为 `FilterOptions` 三枚 `RadioButton`；`PeekFilterButton` 模板改为轨道内药丸。
2. `TaskPeekView.xaml.cs`：`_candidate` 改为行级 `FrameworkElement`；`StartGesture` 接受行容器并记录行内抓取偏移；`ActivateDrag` 把行容器交给 `DragHintAdorner` 并捕获行容器；`CaptureLost` 只处理行容器自身的捕获丢失，忽略子按钮交还捕获时冒泡到行的事件；`FinishDrag` 接收行容器；`CompleteTask` 尊重 `_suppressClick`。
3. `DragHintAdorner`：新增整行虚线 `Rectangle`（`Accent` 描边、圆角、不参与命中），提示文字改用主题 `Surface/Border/TextSecondary`，避免深色与高对比度主题下仍使用系统 Info 颜色。
4. `TaskPeekViewFactory.cs`：筛选状态遍历与焦点回退改用 `FilterOptions.Children`。
5. `tests/Mosaic.Wpf.Tests/PresenceTests.cs`：整标签路由用例覆盖标题、状态文字、任务卡片与完成按钮四个起点，筛选仍不建立候选；新增窄宽度布局回归，断言筛选完整位于视图内。

## 5. 验收矩阵

| 场景 | 期望结果 |
| --- | --- |
| 从标题、状态文字、内边距、完成按钮或列间留白拖出 | 超过阈值进入拖出，整标签虚线框出现，松手面板外创建小窗 |
| 拖动中提示与虚线框 | 捕获不丢失、拖动不中断、按钮 `Click` 被抑制 |
| 阈值内短按任务标签 | 打开详情 |
| 阈值内短按完成按钮 | 提交完成/恢复，不打开详情、不拖出 |
| 筛选切换 | 重查列表，控件选中态与焦点清晰 |
| 窄宽度（236 DIP 内容宽） | 摘要截断，筛选完整可见，不横向溢出 |
| 紧凑弹窗 300 DIP 高度 | 底栏按钮仍完整可见 |
| 自动化 | Release 0 警告；WPF 69 项、非桌面全量 173 项通过 |
| 实机 | 真实指针、100–200% DPI、多屏与高对比度仍需独立桌面验收 |

## 6. 兼容与迁移

无数据库结构、窗口记录、配置文件或模块装配变化，已有安装直接升级；不新增设置，旧值语义不变。发布内容源新增 `mosaic-0.10.2`，既有工作空间会按既有规则生成一次“更新简要”任务。
