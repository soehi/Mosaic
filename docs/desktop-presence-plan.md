# Mosaic：后台驻留与边缘任务面板规划

状态：2026-11-16 已实现 0.3.0 本地功能与自动化回归。默认采用右边缘靠上、32 DIP 方形/12 DIP 可见、单击触发和只读任务速览。多屏/DPI 实机矩阵、系统注销和完整桌面烟雾仍待验收；不将它们标为通过。

相关文档：

- [整体架构思想](architecture-principles.md)
- [细节实现计划](implementation-plan.md)
- [设置功能规划](settings-plan.md)

## 1. 目标与边界

关闭 Mosaic 主窗口后，程序继续在本机运行；屏幕右上侧出现一个低干扰的方形边缘入口。按照设置，入口可通过鼠标悬停、单击或双击弹出一个小面板。面板只呈现任务及其完成状态，并用颜色、图标和文字同时表达状态。

本功能不增加第二套任务业务规则，不复制主任务页，也不让界面直接读取数据库。驻留窗口只是新的展示与调用入口：

```text
Desktop 宿主
  └─ 申请：关闭主窗口、恢复主窗口、真正退出
Desktop Presence
  └─ 拥有：驻留状态、边缘入口、弹窗窗口、定位、触发计时
Tasks UI
  └─ 拥有：任务摘要视图，只调用 ITasksApi
Tasks Core
  └─ 拥有：任务查询、完成状态和排序的权威语义
Settings
  └─ 拥有：定义聚合、可见性、校验、保存和恢复
```

第一版不加入系统托盘图标、后台轮询、系统通知、独立提醒模块、拖动入口位置或在弹窗内编辑任务。没有任务能力贡献时，不显示一个没有内容的长期驻留入口。

## 2. 职责唯一归属

| 所有者 | 唯一职责 | 明确不负责 |
| --- | --- | --- |
| Desktop 宿主 | 进程启动与结束、单实例、主窗口对象、系统会话结束、真正退出前的资源排空 | 边缘入口布局、任务查询、完成状态解释 |
| `desktop-presence-core` | 驻留状态机、关闭决策、触发模式和计时规则；不引用 WPF 或任务模块 | 创建 WPF 窗口、执行 SQL、直接读取任务仓储 |
| `desktop-presence-wpf` | 方形边缘入口、弹出窗口、屏幕定位、DPR 与工作区处理、全部驻留与弹窗设置、恢复/退出申请 | 任务 DTO、任务规则、数据库、主任务页内部对象 |
| `tasks-ui` | `TaskPeekViewModel` 与任务摘要视图；调用公开 `ITasksApi`；向用户表达加载、空、错误和完成状态 | 进程驻留、系统窗口定位、设置持久化 |
| `tasks-core` | `ListAsync` 返回的任务快照、排序和 `IsCompleted` 权威值 | 弹窗配色、窗口交互 |
| `tasks-sqlite` / `storage-sqlite` | 任务与设置的持久化；仍只处理仓储/数据库请求 | 判断何时驻留或弹窗 |
| `settings-*` | 聚合驻留设置定义、按普通/高级规则投影、即时应用与持久化 | 执行窗口操作或解释任务状态 |
| Shell / Tasks 主页面 | 原有主窗口导航、任务列表、固定右侧编辑栏 | 边缘窗口、驻留状态、任务弹窗 |

禁止的实现方式：

- 让 `desktop-presence-wpf` 引用 `Mosaic.Tasks.Wpf` 并取得 `TasksViewModel`。
- 让任务摘要绕过 `ITasksApi` 访问 `TaskService`、仓储或 SQLite。
- 复制 `TasksView` 作为预览；它会带入编辑器和第二套设置订阅。
- 在 `desktop-presence` 与 `tasks-ui` 中各实现一份完成状态颜色或筛选规则。
- 通过反射、全局服务定位器或扫描另一个模块的内部文件取得任务。

## 3. 生命周期与状态机

进程继续使用现有的 `OnExplicitShutdown`。主窗口 `Closing` 事件只代表“关闭主窗口”意图，不能直接推出“退出进程”。

```text
Uninitialized
  └─ 启动成功 → MainVisible

MainVisible
  ├─ 关闭 + Reside → ParkPending → ResidentIdle
  ├─ 关闭 + Exit  → ConfirmAndExit → Exiting
  └─ 单实例再次启动 → Restore → MainVisible

ResidentIdle
  ├─ Hover 延迟到期 / 单击 / 双击 → PopupLoading
  ├─ 恢复主窗口 → MainVisible
  └─ 退出 → ConfirmAndExit → Exiting

PopupLoading → PopupOpen | PopupError

PopupOpen
  ├─ 鼠标离开入口与弹窗 → DismissPending → ResidentIdle
  ├─ Esc / 外部点击 / 再次切换 → ResidentIdle
  ├─ 打开主窗口 → MainVisible
  └─ 退出 → ConfirmAndExit → Exiting

任意运行状态
  ├─ 致命错误 → Exiting
  └─ Windows 会话结束 → Exiting，不进入驻留
```

### 3.1 关闭主窗口

1. `ShutdownCoordinator` 取消 WPF 默认关闭。
2. 先读取关闭策略。`Exit` 直接走退出；需要驻留时，若模块未激活、没有可用的任务摘要贡献，或边缘入口创建失败，则不隐藏主窗口；记录诊断并保持当前状态。
3. 默认配置为 `Reside`：创建/显示边缘入口成功后隐藏主窗口。窗口、页面控制器、ViewModel、设置订阅和数据库队列全部保留。
4. 驻留不是“离开编辑器”，因此不调用 `CanCloseAsync`，不丢弃未保存输入。用户恢复主窗口后仍看到原编辑内容和滚动位置。
5. 配置为 `Exit` 时才走原有 `CanCloseAsync` → 停止新请求 → 等待在途写入 → 逆序释放 → `App.FinishAsync`。
6. 系统关闭、进程致命错误和受控故障回收直接走退出流程；这些路径不能因为驻留配置而留下后台进程。

关闭请求的分流必须按以下顺序判断：

| 条件 | 结果 |
| --- | --- |
| `closeBehavior=Exit` | 直接走既有退出流程；不因任务摘要不可用而阻止 |
| Presence 模块未启用 | 采用既有退出行为；没有注册驻留设置时不能假装驻留 |
| Presence 已启用、`Reside`，但没有任务摘要贡献或入口创建失败 | 主窗口保持可见，报告诊断；不能留下不可恢复的隐藏进程 |
| Presence 已启用、`Reside` 且入口创建成功 | 进入 `ResidentIdle`，保留主窗口对象和编辑草稿 |

### 3.2 恢复主窗口

恢复是唯一的主窗口入口：

- 边缘入口上的恢复动作、单实例再次启动和系统激活请求都申请同一个 `RestoreMainWindow`。
- 恢复时先取消悬停/关闭计时器和在途任务查询，关闭弹窗，再显示并激活主窗口。
- 单实例回调不再直接操作 `MainWindow`；它通过公开的驻留控制器申请恢复。驻留模块不存在或未启动时，由 Desktop 生命周期层执行最小恢复回退。
- 恢复后不重建 Shell 或任务页面，不重新打开数据库；第一版回到进入驻留前的页面。

### 3.3 真正退出

“隐藏到驻留”和“退出 Mosaic”必须是两个公开申请。退出入口放在边缘入口的上下文菜单，并可同时在设置页提供“退出 Mosaic”命令；不新增系统托盘菜单，除非后续明确要求。

退出复用现有 `CanCloseAsync`、执行门、数据库排空、模块逆序释放、单实例释放和 `ShutdownCoordinator`。驻留模块在停止接受新请求后：

1. 取消悬停、收起和查询计时器。
2. 释放任务摘要页面及其订阅。
3. 关闭并释放边缘入口和弹窗。
4. 注销设置来源与窗口事件。
5. 再允许宿主继续原有退出清理。

退出申请必须返回可区分的结果：

```text
Cancelled：草稿守卫拒绝离开，或保存失败后取消 → 回到请求前的 ResidentIdle / PopupOpen
Failed   ：宿主协调器发生错误 → 保留入口并显示错误，可重试或恢复主窗口
Accepted ：守卫通过且宿主已提交清理 → 状态立即变为 Exiting，拒绝新的恢复/驻留/弹窗申请
```

Presence 用一个单消费者门串行化 Exit 请求。宿主把现有关闭协调器拆成“判定并接受退出”和“执行已接受退出”两段：`RequestApplicationExitAsync` 在返回 `Accepted` 前只完成守卫、设置一次性的退出门并提交清理意图，不在该调用栈内等待本模块释放。调用返回后由宿主执行资源排空、模块逆序释放和进程结束。

`StopAsync` 只是幂等的“释放本模块资源”申请，不能反过来再次调用宿主的 Exit，避免清理链互相等待。Accepted 后由宿主独占资源排空、模块逆序释放和进程结束；Presence 只释放自己的窗口、计时器、页面与事件。

## 4. 模块与公开契约

### 4.1 新增程序集

建议新增：

| 程序集 | 内容 |
| --- | --- |
| `Mosaic.DesktopPresence.Contracts` | 关闭决策、状态、宿主窗口申请、恢复/驻留/退出接口及公开结果 |
| `Mosaic.DesktopPresence.Core` | 状态机、触发模式解析、计时状态、可用性判断；纯 .NET 测试 |
| `Mosaic.DesktopPresence.Wpf` | WPF 边缘窗口、弹窗容器、坐标/DPI、设置来源、模块激活入口 |

`DesktopPresence.Wpf` 只能引用 Contracts、Core、Presentation 契约、Settings 契约和通用界面设施；不得引用 Tasks 或 Storage 的实现程序集。

### 4.2 生命周期申请

契约草案如下，具体命名可在实施时调整：

```csharp
public enum DesktopCloseBehavior { Reside, Exit }
public enum EdgeActivationMode { Hover, SingleClick, DoubleClick }
public enum DesktopPresenceState { MainVisible, ParkPending, ResidentIdle, PopupLoading, PopupOpen, PopupError, Exiting }

public interface IDesktopPresenceApi
{
    Task<OperationResult> RequestParkAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> RequestRestoreAsync(CancellationToken cancellationToken = default);
    Task<ExitRequestResult> RequestExitAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> StopAsync(CancellationToken cancellationToken = default);
}

public interface IDesktopWindowHost
{
    Task HideMainWindowAsync();
    Task RestoreMainWindowAsync();
    Task<ExitRequestResult> RequestApplicationExitAsync(CancellationToken cancellationToken = default);
}

public enum ExitRequestResult { Cancelled, Failed, Accepted }
```

`IDesktopWindowHost` 由 Desktop 实现并作为显式依赖注入驻留模块。它不是全局服务容器；模块只能向宿主申请已列出的三类动作。Desktop 仍然独占主窗口和进程。

### 4.3 任务摘要贡献

任务摘要不能在 Presence 模块里实现，也不能把完整 `TasksView` 缩小复用。通用界面契约增加一种带独立生命周期的辅助视图，例如：

```csharp
public interface ICompanionViewFactory
{
    ICompanionViewSession Create(CompanionViewOptions options);
}

public interface ICompanionViewSession : IAsyncDisposable
{
    FrameworkElement View { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task CloseAsync();
}

public sealed record CompanionViewOptions(
    string Filter,
    int MaxItems,
    string OpenColor,
    string CompletedColor);
```

`tasks-ui` 实现并提供 `presence.tasks-view@1`，返回自己的 `TaskPeekViewFactory`。工厂创建 `TaskPeekViewModel`，只注入 `ITasksApi`、`IUiDispatcher` 和允许使用的界面服务；选项中的 `All / Open / Completed` 由它映射为公开 `TaskFilter`，不复制业务规则。

Presence 模块在 manifest 中要求 `presence.tasks-view@1`，因此模块图保证 `tasks-ui` 先激活；Desktop 装配根把 `tasks-ui` 的公开工厂显式传给 Presence，不通过反射或容器查找。没有该能力时 Presence 配置失败，不能启动一个没有内容的驻留入口。

每次弹窗打开时创建一个新的辅助视图会话，初始化并查询；关闭或恢复主窗口时先让界面消失，再调用公开 `CloseAsync`。会话自己持有取消源、取消任务查询、拒绝晚到结果，并由 Presence 在有界后台释放中等待它结束。`CloseAsync` 与 `DisposeAsync` 都必须幂等；Presence 不取得内部 `TaskPeekViewModel` 或取消源。这样不会出现主任务页 `TaskUiSettingsTarget` 只有一个设置处理器、第二次挂接覆盖第一次的问题。

### 4.4 任务查询

`TaskPeekViewModel` 只调用已有的：

```csharp
ITasksApi.ListAsync(new TaskQuery(filter, sortOrder), cancellationToken)
```

它使用任务 API 返回的 `Title`、`IsCompleted` 和公开排序结果。Presence 把公开显示选项传给会话，tasks-ui 将选项映射为 `TaskFilter.All / Open / Completed`。默认 `All`，因此未完成和已完成任务都显示。完成状态的颜色由 tasks-ui 根据传入颜色规格表达，但“完成”这一事实仍来自 tasks-core。

每次打开都重新查询，避免驻留期间显示上次打开时的旧数据。第一版不做后台轮询。每次打开增加一个会话代次；筛选参数即时变化或同一弹窗被后续打开请求刷新时，只接受当前代次结果。旧会话关闭后，晚到响应不得写回新界面。

## 5. 边缘入口与弹窗

### 5.1 视觉与定位

建议默认参数：

| 项目 | 建议默认 | 说明 |
| --- | --- | --- |
| 位置 | 主显示器右侧靠上 | 具体方向待确认 |
| 入口形状 | 32×32 DIP 方形 | 仅露出一部分，视觉上是从屏幕边缘伸出的方形短舌 |
| 可见深度 | 12 DIP | 大小、可见深度均可调 |
| 顶部偏移 | 24 DIP | 以目标显示器工作区坐标计算 |
| 层级 | 置顶 | 仍服从系统工作区和全屏应用策略 |
| 弹窗尺寸 | 宽 380 DIP，最大高 560 DIP | 实际高度不超过工作区 |

入口和弹窗使用独立、无边框、不进入任务栏的 WPF 窗口。WPF 的 `Left/Top` 使用设备无关单位，定位前按目标显示器的 DPI 换算；负坐标副屏、显示器移除和主屏变化后重新夹取到工作区。

弹窗出现时尽量不抢主窗口焦点。鼠标进入入口和弹窗之间的连续区域不能误触发关闭；离开两者后经过收起延迟再关闭。Windows 高对比度主题优先使用系统颜色，自定义颜色不能成为唯一状态表达。

### 5.2 触发规则

设置值为互斥枚举：`Hover`、`SingleClick`、`DoubleClick`。

- `Hover`：指针进入入口并经过打开延迟后展开；离开入口和弹窗并经过收起延迟后关闭。
- `SingleClick`：单击切换弹窗；双击不产生第二次切换。
- `DoubleClick`：使用系统双击判定；单击不展开，避免与双击冲突。
- `Esc`、外部点击和再次执行当前触发都收起。
- 恢复主窗口和退出的入口不依赖任务行操作。建议在弹窗标题区保留两个紧凑图标，或在入口上提供右键菜单；最终选择待确认。
- 键盘聚焦入口时，Enter/Space 执行展开，Escape 收起，方向键移动只读任务焦点；恢复主窗口使用明确的恢复动作。

### 5.3 任务内容

弹窗内只呈现任务内容，不提供新建、编辑、删除或正文操作：

- 每行显示任务标题和完成状态。
- 未完成、已完成分别使用主题状态色；同时保留图标和可访问文字，不依赖颜色辨别。
- 超出可见高度时只在任务区域滚动。
- 零任务、加载中、查询失败分别显示明确状态；错误不阻塞恢复主窗口或退出。
- 默认最多显示 12 项；其余任务通过“打开主窗口”继续查看，不静默遗漏。
- 第一版任务行只读，不打开编辑器、不切换筛选，也不承诺自动选中。恢复主窗口使用独立动作并回到进入驻留前的页面。若后续要求点击任务后选中它，需要再定义公开的导航选择申请，不能靠内部对象命中。

## 6. 设置设计

新增 `SettingsGroup.DesktopPresence`，显示名“驻留与边缘”。完整模块配置下，设置分区从四个变为五个。该分区的 17 项定义全部由 `desktop-presence-wpf` 的 `ISettingSource` 注册；不存在“Presence 注册任务设置”或“tasks-ui 注册但 Presence 未激活”的无主项。弹窗筛选、上限和颜色属于弹窗展示策略，Presence 将只读选项交给 tasks-ui 会话。

定义数据的意思是：

- 当前设置基线规划为 57 项，其中 8 项普通、49 项高级。
- 本功能计划新增 17 项，其中 3 项普通、14 项高级。
- 合并后的目标为 74 项，其中 11 项普通、63 项高级；“驻留与边缘”分区有 3 项普通定义，因此高级关闭时该分区仍显示，但只显示这 3 行。
- 当前源码已增加这 17 项定义；完整配置为 74 项、11 普通/63 高级。禁用 Presence 时不注册该来源，回到 57 项基线。

全部采用 `SettingPersistence.UserSettings` 和 `SettingApplyMode.Immediate`；不新增宿主配置文件字段。

| ID | 建议默认 | 范围/枚举 | 类别 | 归属与生效 |
| --- | --- | --- | --- | --- |
| `desktop.closeBehavior` | `Reside` | Reside / Exit | Normal | Presence；下一次关闭时 |
| `desktop.edge.activation` | `SingleClick` | Hover / SingleClick / DoubleClick | Normal | Presence；保存后立即更新事件接线 |
| `desktop.popup.filter` | `All` | All / Open / Completed | Normal | Presence；下一次查询/已打开弹窗刷新 |
| `desktop.edge.monitor` | `Primary` | Primary / LastMainWindow | Advanced | Presence；重新定位 |
| `desktop.edge.anchor` | `RightEdgeTop` | RightEdgeTop / TopEdgeRight | Advanced | Presence；重新定位；最终方向待确认 |
| `desktop.edge.tabSize` | `32` | 20–64 | Advanced | Presence；重新布局 |
| `desktop.edge.visibleDepth` | `12` | 6–64，且不大于 `tabSize` | Advanced | Presence；重新定位 |
| `desktop.edge.offset` | `24` | 0–工作区尺寸 | Advanced | Presence；保存时校验上限，布局时夹取 |
| `desktop.edge.topmost` | `true` | bool | Advanced | Presence；更新窗口层级 |
| `desktop.edge.hoverDelayMilliseconds` | `300` | 0–2000 | Advanced | Presence；取消旧计时后应用 |
| `desktop.popup.dismissDelayMilliseconds` | `250` | 0–2000 | Advanced | Presence；取消旧计时后应用 |
| `desktop.popup.autoHide` | `true` | bool | Advanced | Presence；下次离开事件 |
| `desktop.popup.width` | `380` | 280–640 | Advanced | Presence；更新弹窗 |
| `desktop.popup.maxHeight` | `560` | 240–1000 | Advanced | Presence；按工作区限制实际高度 |
| `desktop.popup.maxItems` | `12` | 1–100 | Advanced | Presence；下一次查询 |
| `desktop.popup.openColor` | `Theme` | Theme / `#RRGGBB` | Advanced | Presence；立即更新状态标记 |
| `desktop.popup.completedColor` | `Theme` | Theme / `#RRGGBB` | Advanced | Presence；立即更新状态标记 |

跨字段校验至少包括：

- `visibleDepth <= tabSize`。
- `offset` 不得超过目标工作区尺寸；实际运行时仍按显示器变化夹取。
- 弹窗最大高度不得让弹窗起始点落到工作区之外。
- 颜色只接受 `Theme` 或六位 `#RRGGBB`；高对比度主题可以覆盖。
- `desktop.closeBehavior=Exit` 显示警示，说明后续关闭会结束进程。

高级关闭时“驻留与边缘”分区仍显示，因为存在 3 个普通定义；14 个高级定义全部隐藏。若 Presence 模块未激活或模块图未满足依赖，则不注册这 17 个定义，整个分区消失；不会留下任何 tasks-ui 单独注册的弹窗设置。关闭高级开关时若修改过即将隐藏的高级项，继续复用现有未保存确认。

## 7. 装配顺序

建议模块依赖顺序：

```text
storage-sqlite
  → settings-sqlite
  → settings-core
  → tasks-sqlite
  → tasks-core
  → tasks-ui（提供 presence.tasks-view）
  → desktop-presence-core / desktop-presence-wpf
  → settings-ui
  → shell
```

Presence 的 manifest 需要：

- Provides：`desktop.presence@1`
- Requires：`settings.api@1`、`presence.tasks-view@1`

宿主窗口适配器由 Desktop 作为构造依赖显式注入，不伪装成模块图能力；当前 `ModuleGraph` 只解析模块提供者。`tasks-ui` 的 manifest 增加 `presence.tasks-view@1` 提供项，Presence 通过该能力获得装配顺序保证。Desktop 装配根取得 `tasks-ui` 暴露的公开工厂并传给 Presence，不跨模块读取 ViewModel。若设置模块未启用，模块图拒绝 Presence；若任务界面模块未启用，同样拒绝 Presence。此时关闭窗口沿用既有退出行为，并在诊断中说明驻留能力未装配。

单实例恢复、正常恢复和标题栏关闭都经同一个 Presence API；Shell 和 Tasks 页面不感知进程驻留细节。

## 8. 实施阶段

### P0：生命周期拆分

交付：关闭主窗口、驻留、恢复和退出四个申请；`ShutdownCoordinator` 增加驻留分流；系统会话结束与致命错误绕过驻留；单实例恢复接入统一入口。

验收：关闭后模块、设置和数据库保持存活；恢复无需重新打开数据库；取消未保存确认不会隐藏；真正退出仍等待写入与逆序释放；驻留创建失败时主窗口不消失。

### P1：契约与设置

交付：Presence Contracts/Core/Wpf 骨架；设置分组、17 项定义、跨字段校验和分区映射；普通/高级可见性、即时应用、恢复默认和本地化。

验收：高级关闭时只显示 3 项；高级开启后 17 项可到达；非法颜色、深度大于尺寸和越界偏移被拒绝；保存失败不应用；设置重开仍保留；Presence 未激活时不出现虚假分区。

### P2：边缘入口

交付：无边框方形入口、工作区定位、DPI 换算、置顶、恢复/退出菜单、键盘入口、最小可访问名称。

验收：单屏、多屏、负坐标、100/125/150/200% DPI、任务栏位置变化、显示器移除与重连；入口不会永久落到工作区外。

### P3：任务摘要贡献

交付：通用辅助视图贡献、`TaskPeekViewModel`、`TaskPeekView`、每次打开重新查询、空/错误/加载状态和完成色。

验收：只调用 `ITasksApi`；不引用仓储、SQLite、`TasksViewModel` 或 `TasksView`；并发查询只采用最新响应；关闭页面释放在途查询和订阅。

### P4：触发与弹窗

交付：Hover / SingleClick / DoubleClick 三种互斥模式、打开与收起计时、入口/弹窗连续悬停、Esc、外部点击、焦点与主题更新。

验收：双击不触发两次查询；离开后延时正确取消；主窗口恢复后无遗留弹窗和计时器；高对比度下状态不只依赖自定义颜色。

### P5：回归与发布

交付：测试、预览图、桌面烟雾流程和发布验证记录。

验收：关闭主窗→进程存活→入口可见→弹窗读取任务→恢复主窗→再次关闭→显式退出；单实例第二次启动恢复既有实例；退出后无窗口、计时器、事件和单实例锁残留；数据重开一致。

## 9. 测试矩阵

| 层 | 必测内容 |
| --- | --- |
| Presence.Core | 状态转换、关闭决策、三种触发、计时取消、重复关闭/恢复/退出竞争、无任务贡献回退 |
| Presence.Wpf | 边缘窗口定位、DPI 转换、多屏负坐标、工作区夹取、置顶、键盘与无障碍、弹窗包含关系 |
| Tasks.Wpf | 只读摘要、加载/空/错误、完成状态主题色、最大条目、最新响应覆盖旧响应、页面释放 |
| Settings | 新分区可见性、3 普通/14 高级、跨字段校验、即时应用、颜色格式、保存失败、恢复默认、重载 |
| Desktop 生命周期 | 关闭驻留、恢复、显式退出、系统会话结束、单实例恢复、创建失败、致命错误、排空与逆序释放 |
| 视觉/可访问性 | 100–200% DPI、主屏/副屏、高对比度、浅色/深色、键盘完整路径、颜色之外的完成状态表达 |
| 桌面烟雾 | 关闭窗口后进程仍存活且入口可见；弹出仅含任务；恢复后原草稿仍在；显式退出后进程结束；重启数据仍在 |

## 10. 验收条件

- 点击主窗口关闭按钮后，默认进入驻留；主窗口、页面和未保存编辑不销毁。
- 屏幕右上侧存在可配置的方形边缘入口；有任务能力时可见，没有任务能力时不创建空壳。
- 悬停、单击、双击三种模式可配置且互斥；弹窗每次打开读取最新任务，并清楚显示加载、空和错误状态。
- 弹出面板不包含任务编辑器，不复制业务规则；任务数据只来自 `ITasksApi`，完成状态只由 tasks-core 解释。
- 未完成和已完成任务同时用文字/图标和颜色表达；高对比度下仍可辨识。
- 主窗口恢复和第二次启动都收敛到同一恢复申请；恢复不重建页面或数据库。
- 设置新增“驻留与边缘”分组，高级关闭时仍只显示 3 项普通设置，高级开启后显示全部 17 项。
- 关闭驻留、恢复、退出、系统关闭与故障退出均有确定行为；显式退出仍执行未保存确认、在途写入等待和资源释放。
- 全部新增跨模块调用经公开契约；架构、公开 API、边界测试通过后才可视为交付。

## 11. 待确认的产品选择

以下选择不影响模块结构，但影响第一版默认值：

1. “右上侧”是“屏幕右边缘靠近顶部”，还是“屏幕上边缘靠近右侧”？建议默认前者。
2. 方形入口希望完整露出约 32×32 DIP，还是只露出约 12 DIP 的窄短舌？建议默认后者，低干扰更接近“隐藏的突起”。
3. 默认触发方式选单击还是悬停？建议默认单击，避免鼠标路过屏幕右上方时误弹出。
4. 弹窗中的任务行只读即可，还是需要单击后恢复主窗口并自动选中该任务？建议第一版只读，另外提供恢复动作；自动选中需要新增公开导航选择申请。
5. 弹窗是否允许保留一个很小的标题区，用于“打开主窗口”和“退出 Mosaic”两个图标？建议保留，以保证悬停模式下也能明确操作；任务正文仍只显示任务。

## 12. 实施记录（2026-11-16，0.3.0）

- `DesktopPresence.Contracts` 定义宿主申请、状态和退出结果；纯 .NET Core 串行化驻留、恢复、刷新和退出。Core 是实现库，WPF 激活入口以单个 `desktop-presence` 模块向依赖图提供能力。
- Desktop 的 `ShutdownCoordinator` 将退出接受与排空分开，避免 Presence 请求退出时等待自身释放。关窗默认保留 Shell/页面/草稿，显式退出沿用守卫和逆序清理。恢复时即使摘要清理失败也先恢复主窗口；系统会话结束/致命错误绕过驻留。
- WPF 模块独占边缘/弹窗窗口、鼠标与键盘事件、悬停/收起计时和目标显示器/DPI 换算。每次打开或修改弹窗参数都创建新摘要会话，先移除旧窗口再有界释放会话。
- Tasks UI 公开 `ICompanionViewFactory`；独立只读视图只调用 `ITasksApi.ListAsync`，含加载/空/错误/重试、状态图标和文字、超额提示、代次/取消与幂等释放。未复用 `TasksView` 或主页面 ViewModel。
- 17 项定义由 Presence 来源注册/撤销，中文/英文名称与枚举选项资源由 Presence 自己提供。Settings UI 按公开分组和类别投影出 3 普通/14 高级，跨字段检查深度、偏移和颜色。
- 默认新配置显式启用 Presence。旧配置只补已有 tasks-ui 新增的辅助视图能力绑定，不隐式启用驻留模块；用户禁用的模块不自动恢复。
- 自动化覆盖状态机竞争/异常、触发互斥、设置保存/非法组合/撤销、只读摘要及晚到响应、真实 WPF 窗口创建/刷新/释放。预览：`artifacts/ui-preview/presence-tasks-readonly.png`。
- 交互桌面确认：0.3.0 启动、保存任务、关闭后进程存活、同工作空间再次启动恢复原窗口与未保存备注、普通模式 3 项驻留设置、保存 Exit 后关闭进程正常结束。
- 桌面检查工具未枚举无任务栏的小型入口，尚未据此确认边缘点击、跨窗口悬停和菜单退出的交互式结果；这些路径已有源码与 STA 生命周期回归，但仍需完整交互验收。多屏负坐标、100–200% DPI 热插拔、全屏应用策略、系统注销、干净 Windows 安装/升级同样未验证。
