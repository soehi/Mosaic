# Mosaic：速览整卡拖出修复计划（0.10.1 规划）

状态：2026-11-17，0.10.1 已实施自动化部分。源码基线 0.10.0。用户反馈“从任务按钮内部按下无法拖出”，本计划定位真实指针路径下的根因，并修复后保留点击详情、完成切换与筛选的全部既有行为。设置计数不变（98 项：16 普通/82 高级），无数据库、配置或模块装配迁移。

关联：[架构原则](architecture-principles.md)、[总实施计划](implementation-plan.md)、[速览完成切换与筛选](peek-completion-filter-plan.md)、[悬浮任务窗](floating-task-windows-plan.md)、[实施与验证记录](implementation-status.md)。

## 1. 根因定位

| 位置 | 事实与触发条件 |
| --- | --- |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs:45–63`（`MoveGesture`） | 拖动超过系统阈值后：置 `_dragging`、`CaptureMouse`，随后创建拖动提示 `Popup` 并 `IsOpen = true`。WPF 的 Popup 打开时会建立自己的鼠标捕获，任务卡 Button 因此收到 `LostMouseCapture`。 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs:77`（`CaptureLost`） | `if (_dragging) CancelDrag();` 把“捕获转移给提示 Popup”当作拖拽失败，立即取消刚激活的拖动。结果是整卡拖出在跨越阈值的瞬间失效；真实鼠标拖动必然走这条路径。 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs:65–77`（`EndGesture`） | 快速拖动时按钮可能先失去捕获、随后收不到 `MouseLeftButtonUp`，`EndGesture` 不执行，`DetachRequested` 不触发。该释放路径缺少兜底。 |
| `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs:69–71` | 面板外判定使用 `PointToScreen` 再 `Window.PointFromScreen` 往返换算，多一次 DPI 折算，直接用窗口坐标即可表达同一语义。 |
| `tests/Mosaic.Wpf.Tests/PresenceTests.cs:90–115` | 现有整卡路由测试只验证 `PreviewMouseDown` 后 `_candidate` 指向任务卡，从不跨越拖动阈值，因此未覆盖 Popup 激活与捕获丢失，测试通过而真实拖动失败。 |

非问题：`CanDetach` 在完整装配下为 true；完成按钮与筛选控件的排除路由正常；速览自动收起在 `_dragging` 期间保持。缺陷集中在拖动激活后的捕获与释放路径。

## 2. 预期效果

- 按下任务卡 Button 内任意非排除控件（标题、状态文字、内边距、空隙），拖动超过系统阈值后进入拖出状态，提示出现且拖动不中断。
- 松手在速览面板/宿主窗口外时创建独立小窗；面板内松手不创建小窗、不打开详情。
- 阈值内短按仍打开详情；Esc 取消拖动且无窗口创建。
- 完成按钮、筛选控件仍不启动拖出；右键菜单与拖动互斥。
- 快速甩出鼠标（按钮先失去捕获、收不到 Up 事件）仍能按最后一次指针位置完成拖出或取消，不静默失效。

## 3. 修复方案

### 3.1 拖动提示不再抢占捕获（主修复）

把拖动提示从 `Popup` 改为挂在 `AdornerLayer` 上的内部 `DragHintAdorner`（新增 `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/DragHintAdorner.cs`）。Adorner 不接收输入、不建立自己的鼠标捕获，保持任务卡 Button 的捕获不被转移。提示定位沿用现有 `e.GetPosition(this) + 12,12` 偏移，样式复用系统 Info 提示颜色与现有文本资源 `Tasks.Floating.Drag`；`MoveGesture` 更新其位置，`CancelDrag` 移除。

不采用“打开 Popup 后把捕获抢回来”的方案：Popup 捕获转移的时序依赖 WPF 内部行为，再次抢捕获会引入新的捕获竞争。

### 3.2 释放路径加固

- `CaptureLost` 改为兜底终局处理：若 `_dragging` 且捕获意外丢失（例如系统级捕获抢占、Alt+Tab），用最后一次已知指针位置（`Mouse.GetPosition(this)` → 屏幕坐标）执行与 `EndGesture` 相同的“面板外申请拖出 / 面板内取消”判定，然后清理状态。不再无条件取消。
- `EndGesture` 与该兜底共用一个内部 `FinishDrag(TaskPeekRow, Point localWindowPos, Point screenPos, CancellationTokenSource? opening)` 逻辑，避免两处判定漂移。
- 面板外判定简化为直接用窗口坐标：`e.GetPosition(Window.GetWindow(this))` 与窗口 `ActualWidth/ActualHeight` 比较；放置提示仍由 `PointToScreen` 计算屏幕坐标。删除 screen→window 的往返换算。
- 拖动激活期间向宿主 `Window` 挂接 `PreviewKeyDown`，Esc 触发 `CancelDrag`；结束/取消时必须摘除处理器，避免窗口泄漏。

### 3.3 保持不变的行为

- 手势仍只绑在任务卡 Button（`TaskPeekView.xaml:84`），不迁移到面板容器；完成按钮与筛选控件的排除路由不变。
- 短按打开详情、`_suppressClick`、右键菜单、`DragChanged` 交互保活、公开 `IFloatingDetachSource` 申请链路与去重全部不变；Tasks UI 不读 Presence 或 Floating Windows 内部。
- `floating.*`、`desktop.*` 设置与窗口恢复记录不变；本修复不改设置。

## 4. 模块边界

| 模块 | 职责 |
| --- | --- |
| Tasks UI | 拖出手势、提示 Adorner、捕获/释放/Esc 状态机；通过已注册的公开分离来源发起申请 |
| Floating Windows | 只消费公开 `OpenFloatingItemAsync` 申请与 `FloatingPlacementHint`，本计划不改 |
| Presence | 只通过通用交互保活通知维持速览，本计划不改 |
| Storage / Tasks Core | 不变；拖出不写任务数据 |

新增 `DragHintAdorner` 为 Tasks WPF 内部类型，不进入公开 API，不修改 `build/public-api.txt`。

## 5. 实施步骤与改动位置

1. `TaskPeekView.xaml.cs`：`MoveGesture` 阈值分支改为 `ActivateDrag`（仍捕获 + `DragChanged`，但创建 Adorner）；新增 `DragHintAdorner`；`EndGesture`/`CaptureLost` 收敛到 `FinishDrag`；新增 Esc 处理；`CancelDrag` 移除 Adorner。
2. `tests/Mosaic.Wpf.Tests/PresenceTests.cs`：新增 STA 回归——
   - `ActivateDrag` 后无 `Popup`、Adorner 已挂载、按钮保持捕获、`DragChanged(true)`；
   - `CaptureLost` 在 `_dragging` 时走兜底终局（按注入位置验证面板外申请 `DetachRequested`、面板内取消）；
   - 现有整卡路由、完成/筛选排除、短按详情用例继续通过。
3. `tests/Mosaic.Desktop.SmokeTests`：新增真实指针拖出 Smoke（复用 `FloatingDragTests.PointerGesture` 的 `SendInput` 模式）：打开速览，按住任务卡中心移动出面板边界后释放，断言独立浮窗出现；另断言短按同一位置仍打开详情、完成按钮起点不触发拖出。基础桌面 Smoke 已知一次 UIA 点击时序失败，实施时先稳定其控件等待再叠加。
4. 版本与发布同批：`Directory.Build.props` 升为 0.10.1，`build/release-content.json`、`published-release-index.json`、`release-manifest.json`、`docs/releases/v0.10.1.md`（含 -files 清单）、README、本计划状态、总计划补充节、`docs/implementation-status.md` 全部一次交付，不拆分提交。
5. 验证：Release 构建 `-m:1` 0 警告/0 错误；非桌面自动化全量通过（0.10.0 基线 171 项，其中 WPF 67）；`git diff --check` 与 Markdown 链接/围栏检查。真实指针 Smoke 通过后才能把“整卡拖出”标为已验收；受限环境必须保留待验收标注。

## 6. 验收矩阵

| 场景 | 期望结果 |
| --- | --- |
| 从任务卡标题/状态文字/内边距按下并拖出面板 | 超阈值进入拖出，提示跟随，松手创建小窗，抓取偏移正确 |
| 拖动中提示出现 | 拖动不中断、捕获不丢失、按钮 `Click` 被抑制 |
| 快速甩出导致捕获丢失 | 兜底按最后指针位置申请拖出或取消，不静默失效 |
| 阈值内短按任务卡 | 打开面板内详情，不创建小窗 |
| 面板内拖动后松手 | 取消拖出，不打开详情，面板恢复正常交互 |
| Esc 拖动中按下 | 取消拖动、移除提示，无窗口创建 |
| 从完成按钮/筛选控件按下 | 不启动拖出；完成提交与筛选切换正常 |
| 右键菜单与拖动 | 菜单打开取消拖动；拖动中不弹菜单 |
| 并发/去重 | 同任务重复申请合并，失败反馈保留源列表（既有语义回归） |
| 自动化 | 新增 WPF 与桌面 Smoke 通过；现有 171 项基线不回归 |

真实指针、Esc 键盘路径、100–200% DPI 与多屏矩阵在实机桌面环境执行；自动化路由测试不能替代“整卡拖出”的物理验收结论。
