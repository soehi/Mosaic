# Mosaic：小窗拖拽落点预览与按钮区移动（0.11.0）

状态：2026-11-18，已实施源码与自动化回归，标题栏按钮拖动已通过真实指针 Smoke；真实指针拖出、DPI 与多屏矩阵仍需独立桌面验收。设置计数不变（98 项：16 普通/82 高级），无数据库、配置或模块装配迁移。

关联：[架构原则](architecture-principles.md)、[总实施计划](implementation-plan.md)、[悬浮任务窗](floating-task-windows-plan.md)、[悬浮窗拖动与紧凑布局](floating-drag-region-plan.md)、[实施与验证记录](implementation-status.md)。

## 1. 用户反馈与根因

用户反馈：拖出速览任务时看不到小窗将出现在哪里；小窗标题栏的按钮区域无法拖动窗口。

| 位置 | 事实与触发条件 |
| --- | --- |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs`（0.10.2 基线 `FinishDrag`） | 只有松手后才把落点 `FloatingPlacementHint` 交给 Floating Windows；拖动过程中没有目标位置计算，也没有任何预览输出。 |
| `src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/FloatingSurface.cs`（基线） | 只有真正创建窗口时才解析屏幕与夹取位置，没有独立于窗口的放置预览。 |
| `src/Modules/FloatingWindows/Mosaic.FloatingWindows.Core/FloatingWindowCoordinator.cs`（基线 `OpenCoreAsync`） | 存在历史记录时整条复用旧记录，`FloatingPlacementHint` 的落点被忽略，再次拖出会回到旧位置。 |
| `src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/FloatingWindowInput.cs`（基线 `HitTest`） | 非按钮区域映射为 `HTCAPTION` 由系统移动；按钮区域返回 `HTCLIENT` 以保留点击，但没有 WPF 层的阈值拖动兜底，按住按钮拖动完全不动。 |

## 2. 用户可见行为

- 拖出任务超过阈值后，屏幕出现与小窗同宽、预估紧凑高度的虚线幽灵窗，随指针实时移动；落点越出工作区时幽灵隐藏。
- 幽灵与真正创建的小窗共用屏幕解析、抓取偏移和边界夹取规则，预览位置即最终位置（高度为紧凑高度预估）。
- 松手或 Esc 取消时幽灵立即消失；面板关闭、拖动源丢失时同样清理。
- 小窗标题栏内的状态切换、展开按钮与工具按钮支持按住拖动窗口；移动超过系统阈值即移动窗口并抑制按钮点击，阈值内短按仍执行原操作。正文与其他区域的按钮不参与拖动，正文文本仍可正常操作。
- 关闭后再拖出已打开过的任务时，小窗按本次落点放置，不再回到上次保存位置；紧凑/展开尺寸、模式与置顶设置仍沿用旧记录。

## 3. 模块边界

| 模块 | 职责 |
| --- | --- |
| Tasks UI | 在拖动过程中提交 `FloatingPlacementHint` 并结束预览；不引用 Floating Windows 内部实现 |
| Presentation.Contracts | 扩展 `IFloatingDetachSource` 的预览申请，作为 Tasks UI 与 Floating Windows 之间的公开协议 |
| Floating Windows | 提供 `IFloatingWindowApi` 预览接口、幽灵窗、落点数学和标题栏按钮拖动；不含任务 DTO |
| Tasks Core / Storage | 不变；预览不写任务或窗口记录 |

幽灵窗是 Floating Windows WPF 内部窗口，不进入公开 API；`build/public-api.txt` 的类型清单不变。

## 4. 实施与改动位置

1. `FloatingContracts.cs`：`IFloatingWindowApi` 增加 `BeginPlacementPreview/UpdatePlacementPreview/EndPlacementPreview`；`IFloatingWindowSurface` 增加 `BeginPreview/UpdatePreview/EndPreview`。
2. `src/Mosaic.Presentation.Contracts/FloatingPresentation.cs`：`IFloatingDetachSource` 增加对应的三个预览申请。
3. `TaskDetachSource.cs`：预览申请转发给已注册的 `IFloatingWindowApi`，未启用时静默忽略。
4. `FloatingWindowCoordinator.cs`：预览方法转发到 surface（退出中忽略）；`OpenCoreAsync` 中有落点时用落点覆盖旧记录的 `MonitorId/X/Y`，尺寸、模式与置顶保留。
5. `FloatingSurface.cs`：新增虚线幽灵窗（主题强调色描边、`AccentSoft` 填充、透明、置顶、`WS_EX_NOACTIVATE/TRANSPARENT/TOOLWINDOW` 不抢焦点不接收鼠标）；`UpdatePreview` 用 `CreateRecord` + 共享夹取数学定位；`Freeze/Dispose` 关闭预览；已打开的任务不显示幽灵。
6. `FloatingScreen.cs`：新增 `Placement(screen, record, minimumWidth, height)`，与创建路径共用宽度、X/Y 夹取公式。
7. `TaskPeekView.xaml.cs`：新增 `PreviewRequested/PreviewMoved/PreviewEnded`；`ActivateDrag` 开始预览，`MoveGesture` 更新，`CancelDrag/FinishDrag` 结束；用 `Window.PointToScreen` 与行内抓取偏移构造 hint。
8. `TaskPeekViewFactory.cs`：把预览委托接到 `IFloatingDetachSource`，会话关闭时清理。
9. `FloatingWindowInput.cs`：新增 `CanStartHeaderDrag` 与 WPF 阈值手势；仅工具条和 `Header` 内的 `ButtonBase` 可作为拖动起点。超过阈值后先 `ReleaseCapture()`，再以 `WM_NCLBUTTONDOWN`/`HTCAPTION` 复用系统标题栏移动循环，并抑制随后的点击。首版使用 `Window.DragMove()`（`WM_SYSCOMMAND`/`SC_MOVE`），真实指针下移动循环在子按钮持有捕获时立即结束并回送 `WM_LBUTTONUP`，窗口不移动，故改用标题栏路径。

## 5. 验收矩阵

| 场景 | 期望结果 |
| --- | --- |
| 从速览拖出任务超过阈值 | 幽灵窗出现并跟随指针，宽高与落点合理 |
| 指针移出工作区 | 幽灵隐藏，松手不创建窗口或按既有失败反馈处理 |
| 松手/ Esc / 面板关闭 | 幽灵关闭，不残留窗口 |
| 已打开任务再次拖出 | 激活现有窗口，不显示幽灵 |
| 已有历史记录再次拖出 | 新落点生效，尺寸/模式/置顶沿用 |
| 标题栏按钮按住拖动 | 窗口移动，按钮点击被抑制，模式/完成状态不变 |
| 标题栏按钮短按 | 原有点击行为不变 |
| 正文按钮与文本 | 不触发窗口移动，正常交互 |
| 自动化 | Release 0 警告；非桌面 177 项通过；新增幽灵预览渲染图 |
| 实机 | 真实指针标题栏拖动已通过（`Floating_header_buttons_move_on_drag_and_still_click_on_short_press`，窗口移动、完成状态与紧凑高度不变、短按仍展开；完整悬浮窗 Smoke 连续 2 次通过）；真实指针拖出速览、100–200% DPI、多屏与高对比度仍需独立桌面验收 |

## 6. 兼容与迁移

无数据库结构、窗口记录字段、配置文件或模块装配变化；已有窗口记录原样读取。预览只在拖动期间存在，不写入任何存储。发布会新增 `mosaic-0.11.0`，既有工作空间按既有规则生成一次“更新简要”任务。
