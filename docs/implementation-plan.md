# Mosaic：细节实现计划（C# + WPF）

状态：已按本计划实施源码、测试、自包含发布和每用户 MSI。阶段 0–5 已完成本机验证；阶段 6 已完成本机安装验证，干净 Windows 环境及 DPI/输入法矩阵仍待独立验收。0.5.1 为旧配置补入可校验、原子写回、仅执行一次的 Presence 迁移；0.5.0 完成默认配置单一权威、跨存储提交日志、导航本地化键、设置展示元数据下放和任务紧凑单栏；0.4.0 的跨分区持续预览及 0.3.0 的后台驻留继续保留。本机发布统一输出 `artifacts/local-run`。详见 [实施与验证记录](implementation-status.md)。

用户已指定采用 C# + WPF。架构依据见 [整体架构思想](architecture-principles.md)，本次选型记录见 [技术方案变更](decisions/0001-csharp-wpf.md)。

## 1. 第一版目标与范围

交付 Windows 桌面任务记录软件，离线运行、本机存储，验证完整流程：

```text
配置选择模块 → 校验依赖并激活 → 提供导航和页面参数
→ ViewModel 提出业务请求 → 业务提出仓储请求
→ 数据库后台执行 → 返回结果 → UI 更新 → 重启后仍可读取
```

包括任务创建、查看、编辑、删除、标记完成、恢复未完成，以及全部/未完成/已完成筛选。需要加载、空列表、保存失败、未保存编辑和版本冲突的明确反馈。

截止时间、优先级、子任务、分组、回收站、提醒、云同步、第三方插件和运行中热卸载不进入第一版。后续先判断职责归属，再扩展公开接口或创建模块。

## 2. 技术实施基线

| 部分 | 实施选择 |
| --- | --- |
| 语言与运行时 | C#、.NET 10 LTS；SDK 在实施时锁定具体受支持补丁版本 |
| 桌面界面 | WPF + XAML，使用 MVVM |
| MVVM 基础能力 | CommunityToolkit.Mvvm，使用 ObservableObject、AsyncRelayCommand 等现成能力 |
| 装配与应用设施 | Microsoft.Extensions.Hosting、DependencyInjection、Configuration、Logging；容器仅由宿主访问 |
| 本机数据库 | SQLite，使用 Microsoft.Data.Sqlite；第一版使用显式 SQL 和迁移 |
| 配置 | System.Text.Json，强类型配置对象与启动校验 |
| 测试 | xUnit；独立架构约束测试；WPF 测试使用 STA/Dispatcher 环境 |
| 构建与分发 | dotnet CLI；先做 win-x64 自包含文件夹发布，再验证每用户安装包 |

本机已检测到 .NET SDK 10.0.300 和 9.0.306，这只说明已有 SDK，不代表 WPF 构建与发布已验证。实施阶段确认 Windows Desktop 引用包、运行包及选定 NuGet 版本可用，再建立 global.json、集中包版本和锁文件。

纯业务、契约和存储项目目标为 `net10.0`；WPF 宿主、外壳、界面契约和任务界面目标为 `net10.0-windows`，并开启 `UseWPF`。启用 nullable、代码分析和受控警告策略。

.NET 10 按 LTS 基线选择，维护中更新受支持补丁，不因机器存在较新主版本而自动切换。[官方支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy)

MVVM 辅助库和数据库访问依据：[MVVM Toolkit](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/)、[Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)。

## 3. 进程、线程与职责

第一版为单进程桌面应用，模块通过带类型的公开接口提交请求，不建立额外的跨进程通信桥。

| 执行位置 | 负责内容 |
| --- | --- |
| WPF UI 线程 | Window、UserControl、XAML 绑定、导航、对话框、界面状态更新 |
| 普通应用服务 | 无 WPF 依赖的任务规则、模块依赖和生命周期控制 |
| 存储模块后台执行队列 | 数据库打开、查询、写入、迁移、备份及关闭 |

WPF 对象及绑定集合在 UI 线程创建和更新。ViewModel 使用 await 获取结果；跨线程更新经 UI Dispatcher。禁止在 UI 线程使用 `.Wait()` 或 `.Result` 等待数据库请求。[WPF 线程模型](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model)

Microsoft.Data.Sqlite 的异步 ADO.NET 方法实际同步执行，因此不能只加 Async 后缀就认为不会阻塞界面。存储模块采用有容量上限的单消费者后台队列，串行执行实际数据库工作；连接只在该执行器内使用，请求任务把完成结果返回调用方。[官方异步限制](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)

队列、连接和事务属于存储模块内部，业务及界面不能直接排入任意工作。事务回调必须同步完成，不能跨 await 持有连接，也不能反向发起嵌套排队并等待自身队列。

## 4. 解决方案与项目布局

以下为项目职责布局；独立项目已创建，部分相关协议合并在同一个源码文件中。

```text
Mosaic.sln
Directory.Build.props                 # 统一编译和分析设置
Directory.Packages.props              # 集中 NuGet 版本
NuGet.config                          # 包源与还原规则
global.json                           # 已验证的 SDK 基线
src/
  Mosaic.Desktop/                     # WPF 应用宿主
    App.xaml / App.xaml.cs
    Bootstrapper.cs                   # 构建宿主并装配模块
    AppConfiguration.cs
    BuiltInModuleCatalog.cs
    HostConfigurationValidator.cs     # 顶层与生效模块/provider 的唯一语义校验
    HostSettingsCatalog.cs            # 宿主独有设置与 canonical 默认
    HostSettingSource.cs              # 有效值、校验和原子写回
    FileSettingsCommitJournal.cs      # 宿主与用户设置跨存储恢复日志
    DesktopLocalization.cs            # 宿主诊断与退出文案
    ShutdownCoordinator.cs
    Localization/                     # 宿主中英文资源
  Mosaic.Contracts/                   # 纯 .NET 通用模块与结果协议
    ModuleManifest.cs
    CapabilityId.cs
    ModuleActivation.cs
    OperationResult.cs
    IModuleExecutionGate.cs
    IIdGenerator.cs
  Mosaic.Kernel/                      # 依赖图、激活和逆序释放
    ModuleGraph.cs
    ModuleRuntime.cs
    CapabilityBindings.cs
    ModuleExecutionGate.cs
  Mosaic.Presentation.Contracts/      # 通用 WPF 界面扩展协议
    NavigationItem.cs
    ViewContribution.cs
    IViewFactory.cs
    INavigationGuard.cs
    IUiDispatcher.cs
    IUserDialogService.cs
  Mosaic.Shell.Wpf/                   # 外壳，不引用任务项目
    ShellWindow.xaml
    ShellViewModel.cs
    NavigationService.cs
    ContributionRegistry.cs
    UiDispatcher.cs
    UserDialogService.cs
  Mosaic.Presentation.Wpf/            # 共享 WPF 主题与控件样式
    MosaicTheme.cs
    MosaicThemeManager.cs
    Themes/Theme.xaml
    Themes/Palettes/Light.xaml
    Themes/Palettes/Dark.xaml
    Themes/Palettes/HighContrast.xaml
  Modules/
    Tasks/
      Mosaic.Tasks.Contracts/         # DTO、公开任务请求和结果
        TaskItemDto.cs
        TaskRequests.cs
        ITasksApi.cs
      Mosaic.Tasks.Persistence.Contracts/
        ITaskRepository.cs            # 只供业务和仓储适配器使用
      Mosaic.Tasks.Core/
        TasksModule.cs                # 唯一公开的模块装配入口
        Internal/TaskService.cs
        Internal/GuardedTasksApi.cs
      Mosaic.Tasks.Wpf/
        TasksUiModule.cs
        Views/TasksView.xaml
        Views/TaskEditorView.xaml
        Internal/TasksViewModel.cs
        Internal/TaskEditorViewModel.cs
        Internal/TaskViewFactory.cs
        Internal/TaskPeekViewFactory.cs # 独立只读任务摘要和会话
        Internal/TaskErrorLocalizer.cs  # 稳定任务错误码的界面翻译
    Storage/
      Mosaic.Storage.Contracts/       # 不暴露数据库驱动类型
        IDatabaseSession.cs
        SqlStatement.cs
        SqlRow.cs
        MigrationDefinition.cs
      Mosaic.Storage.Sqlite/
        SqliteModule.cs
        Internal/DatabaseWorker.cs
        Internal/SqliteSession.cs
        Internal/MigrationRunner.cs
      Mosaic.Tasks.Sqlite/
        TasksSqliteModule.cs
        Internal/SqliteTaskRepository.cs
        Migrations/001_CreateTasks.sql
    Settings/
      Mosaic.Settings.Contracts/
      Mosaic.Settings.Core/
        Internal/SettingsEditing.cs    # 编辑会话、预览、提交与回滚
      Mosaic.Settings.Sqlite/
      Mosaic.Settings.Wpf/
        Internal/SettingsViewModel.cs  # 通用元数据投影与设置行生命周期
    DesktopPresence/
      Mosaic.DesktopPresence.Contracts/ # 宿主申请、状态和结果（纯 .NET）
      Mosaic.DesktopPresence.Core/      # 串行状态机（纯 .NET）
      Mosaic.DesktopPresence.Wpf/       # 边缘/弹窗、计时、定位与设置来源
        PresencePopupView.xaml         # 通用弹窗容器，不解释任务数据
    # 后续功能按自己的职责增加模块，不预建空模块
tests/
  Mosaic.Architecture.Tests/
  Mosaic.Kernel.Tests/
  Mosaic.Tasks.Core.Tests/
  Mosaic.Storage.Sqlite.Tests/
  Mosaic.Tasks.Contract.Tests/
  Mosaic.Wpf.Tests/
  Mosaic.Desktop.SmokeTests/
  Mosaic.DesktopPresence.Core.Tests/
  Mosaic.Settings.Core.Tests/
  Mosaic.Settings.Sqlite.Tests/
build/                                # 发布、打包与边界检查脚本
docs/                                 # 原则、计划及决策记录
```

每个命名项目为独立 `.csproj`/程序集。业务 API 与仓储协议分开，任务界面根本不引用仓储协议程序集。不要把所有模块编译进同一个项目，再仅靠命名空间区别。

### 4.1 项目引用白名单

| 项目 | 允许的 Mosaic 直接依赖 |
| --- | --- |
| Contracts | 无 |
| Kernel | Contracts |
| Presentation.Contracts | Contracts；另可引用 WPF 框架类型 |
| Presentation.Wpf | 无（共享主题与控件样式，仅依赖 WPF 框架） |
| Shell.Wpf | Contracts、Presentation.Contracts、Presentation.Wpf、Settings.Contracts |
| Tasks.Contracts | Contracts |
| Tasks.Persistence.Contracts | Contracts、Tasks.Contracts |
| Tasks.Core | Contracts、Tasks.Contracts、Tasks.Persistence.Contracts、Settings.Contracts |
| Storage.Contracts | Contracts |
| Storage.Sqlite | Contracts、Storage.Contracts、Settings.Contracts |
| Tasks.Sqlite | Contracts、Tasks.Contracts、Tasks.Persistence.Contracts、Storage.Contracts |
| Tasks.Wpf | Contracts、Tasks.Contracts、Presentation.Contracts、Presentation.Wpf、Settings.Contracts |
| Settings.Contracts | Contracts |
| Settings.Core | Contracts、Settings.Contracts |
| Settings.Sqlite | Contracts、Settings.Contracts、Storage.Contracts |
| Settings.Wpf | Contracts、Settings.Contracts、Presentation.Contracts、Presentation.Wpf |
| DesktopPresence.Contracts | Contracts |
| DesktopPresence.Core | Contracts、DesktopPresence.Contracts；禁止 WPF/Tasks/Storage |
| DesktopPresence.Wpf | Contracts、DesktopPresence.Contracts、DesktopPresence.Core、Presentation.Contracts、Presentation.Wpf、Settings.Contracts；禁止 Tasks/Storage 实现 |
| Desktop | 通用基础设施及各模块公开入口，作为唯一装配根 |

框架依赖另行限制：Microsoft.Data.Sqlite 只出现在 Storage.Sqlite；SQL 文本只出现在存储基础设施、任务存储适配器及相应测试；WPF 和 MVVM 依赖只出现在界面项目；业务与内核不引用数据库或界面框架。

实现类型默认 internal、私有成员默认 private。仅契约、DTO、必要的模块入口和界面扩展类型公开。WPF XAML 生成类若因工具要求公开，应列入窄范围白名单，不允许跨模块直接 new 具体视图。通过 API 清单和程序集依赖测试检查 public 类型；不得用生产程序集间的 InternalsVisibleTo 绕过边界。

## 5. 模块清单、能力和装配

### 5.1 能力归属

| 提供者 | 提供的能力 | 需要的能力 |
| --- | --- | --- |
| storage-sqlite | sqlite.session@1 | 宿主传入的数据库位置和执行策略 |
| tasks-sqlite | tasks.repository@1 | sqlite.session@1 |
| tasks-core | tasks.api@1 | tasks.repository@1、时钟与 ID 生成器 |
| tasks-ui | 任务导航与页面贡献 | tasks.api@1、通用界面交互协议 |

宿主使用各模块公开入口创建实例，并传入具名依赖对象。内核校验清单并管理状态，不按任务类型写分支。DI 容器只在装配根使用，不把 IServiceProvider、IHost 或任意服务查找器注入功能模块。[.NET 依赖注入](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection)

界面模块实现 `IUiModule` 并提供导航与页面贡献；宿主在内核报告模块启动后统一注册这些贡献，在模块停止前撤销，外壳只做通用渲染。新增界面模块只需加入模块目录并实现同一契约，宿主不写 `tasks-ui` 特判。

提供给调用者的对象为公开接口的受控入口。例如 GuardedTasksApi 实现 ITasksApi，内部持有业务实现；调用者不获得 TaskService 的具体类型。

入口通过通用执行门取得调用租约：检查模块处于 Active 状态并计数在途请求，释放时减计数。进入 Stopping 后拒绝新租约，等已接受请求结束再释放资源。该机制避免“检查可用后立刻关闭”的竞态。

### 5.2 配置

```json
{
  "configVersion": 1,
  "modules": ["storage-sqlite", "tasks-sqlite", "tasks-core", "tasks-ui"],
  "providers": {
    "sqlite.session@1": "storage-sqlite",
    "tasks.repository@1": "tasks-sqlite",
    "tasks.api@1": "tasks-core"
  },
  "storage": {
    "engine": "sqlite",
    "location": "local-app-data",
    "filename": "mosaic.sqlite"
  }
}
```

数据库规划位于 `%LOCALAPPDATA%\Mosaic\Data\mosaic.sqlite`。配置和日志在应用自己的子目录。宿主解析绝对位置后注入存储模块，业务调用不传路径。文件名必须为合法单个文件名，不接受路径穿越。

首次使用打包默认配置，后续读取用户配置；无效配置显示诊断，不静默覆盖。修改模块配置后重启生效。`modules: []` 且 `providers: {}` 展示空外壳且不打开数据库；禁用了模块却仍保留其 provider 绑定视为配置漂移。缺依赖、重复提供者、版本不匹配、循环依赖拒绝启动功能。

## 6. C# 公开请求与业务契约

以下为已实施的公开任务协议。OperationResult/OperationResult<T> 为公共协议中的明确成功或失败结果；调用方不能把空结果当成功。

```csharp
public sealed record TaskItemDto(
    Guid Id,
    string Title,
    string Notes,
    bool IsCompleted,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Revision);

public enum TaskFilter { All, Open, Completed }
public sealed record TaskQuery(TaskFilter Filter = TaskFilter.All);
public sealed record CreateTaskRequest(string Title, string Notes);
public sealed record UpdateTaskRequest(
    Guid Id, string Title, string Notes, long ExpectedRevision);
public sealed record SetTaskCompletedRequest(
    Guid Id, bool IsCompleted, long ExpectedRevision);
public sealed record DeleteTaskRequest(Guid Id, long ExpectedRevision);

public interface ITasksApi
{
    Task<OperationResult<TaskItemDto>> CreateAsync(
        CreateTaskRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskItemDto>> GetAsync(
        Guid id, CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(
        TaskQuery query, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskItemDto>> UpdateAsync(
        UpdateTaskRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskItemDto>> SetCompletedAsync(
        SetTaskCompletedRequest request, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteAsync(
        DeleteTaskRequest request, CancellationToken cancellationToken = default);
}
```

建议业务规则：标题 Trim 后非空，最多 200 个 Unicode 码点；备注为纯文本，最多 20,000 个 Unicode 码点，C# 使用 EnumerateRunes 计数，不能直接用 UTF-16 Length 替代。新任务未完成、Revision 为 1；更新不改变 ID 和创建时间。限额实施前可调整。

任务业务只维护一份权威校验。ViewModel 展示业务返回的字段错误，不使用另一套属性注解重新决定标题是否合法。异步命令的 CanExecute 负责交互状态，例如正在提交时禁用保存。

时钟通过 TimeProvider 注入，ID 通过专用 IIdGenerator 注入；具体 UUID 生成留在宿主提供的实现。公开返回不可变 DTO 与只读快照，不暴露实体引用或可变内部集合。

列表按 UTC 创建时间倒序，Guid 的标准字符串作为稳定次级排序。第一版无分页，规模扩大时扩展查询协议。

### 6.1 仓储契约

```csharp
public interface ITaskRepository
{
    Task<OperationResult> InsertAsync(
        TaskItemDto task, CancellationToken cancellationToken = default);
    Task<OperationResult<TaskItemDto?>> FindAsync(
        Guid id, CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyList<TaskItemDto>>> QueryAsync(
        TaskQuery query, CancellationToken cancellationToken = default);
    Task<OperationResult> ReplaceAsync(
        TaskItemDto task, long expectedRevision,
        CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteAsync(
        Guid id, long expectedRevision,
        CancellationToken cancellationToken = default);
}
```

任务业务通过仓储读取并校验当前版本，构造 Revision 为旧值加一的新记录；适配器只写入该值，不重复增加。Replace/Delete 必须原子检查版本和写入，使用条件 SQL 或事务，不允许先查版本再无条件写。

读取不存在由任务 API 返回 TASK_NOT_FOUND；写入时受影响行数为零按 CONFLICT 处理，包括目标已被其他请求删除。界面保留编辑内容并允许重新加载。

### 6.2 结果、取消与错误

| 结果/错误 | 所属位置 | 预期行为 |
| --- | --- | --- |
| INVALID_REQUEST | 公开请求入口：null、无效枚举、格式或基础参数不符 | 不执行业务 |
| VALIDATION_FAILED | 任务业务：标题、备注规则 | 返回字段提示 |
| TASK_NOT_FOUND | 任务业务：读取不存在 | 提示并刷新 |
| CONFLICT | 仓储条件写入检测 | 保留编辑，提示重载 |
| STORAGE_UNAVAILABLE | 驱动错误经存储层转换 | 不宣告已保存 |
| CAPABILITY_UNAVAILABLE | 执行门：未启用或正在关闭 | 拒绝新调用 |
| CANCELED | 接受写入前取消，或可安全取消的读取 | 不显示为成功 |
| INTERNAL_ERROR | 公开入口未预期异常 | 通用提示，记录关联 ID |

错误含 Code、Message、可选 Field 和诊断关联 ID。SQL、路径和驱动堆栈只记录到内部日志。参数为 C# 强类型仍需检查 null 和无效枚举，不依赖 UI 正确使用。

非幂等写入不自动重试。写入一旦开始事务，取消请求不直接中断并宣告未保存；完成提交或明确回滚后返回真实结果。超时或强制退出导致结果未知时，重新读取确认。正常关闭必须等待已接受写入结束。

## 7. SQLite 存储与迁移

### 7.1 任务表

```sql
CREATE TABLE tasks (
  id TEXT PRIMARY KEY NOT NULL,
  title TEXT NOT NULL,
  notes TEXT NOT NULL,
  completed INTEGER NOT NULL CHECK (completed IN (0, 1)),
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  revision INTEGER NOT NULL CHECK (revision >= 1)
);
CREATE INDEX tasks_created_idx ON tasks(created_at DESC, id);
CREATE INDEX tasks_status_created_idx
  ON tasks(completed, created_at DESC, id);
```

Guid 用固定 D 格式，UTC 时间统一用不变文化的固定精度 ISO 8601 格式，确保排序和解析一致。SQL 及字段映射归 Tasks.Sqlite，任务业务不知道表名。

SQL 参数化；例如条件更新为 `WHERE id = $id AND revision = $expectedRevision`，通过受影响行数识别冲突。Microsoft.Data.Sqlite 的连接、命令、DataReader 都不跨出 Storage.Sqlite。

### 7.2 数据库公开协议

Storage.Contracts 定义公开的参数化 SQL 请求、结果行、事务会话和迁移描述。结果行只包含 BCL 数据值或专用不可变值类型，不包含驱动对象、DbDataReader 或延迟枚举。

任务适配器在数据库执行器外将结果快照转换为 TaskItemDto；数据库基础设施不认识任务实体。事务内部仅使用受限同步会话，禁止泄漏会话引用、开启嵌套事务或再次等待外层后台队列。

连接由后台单消费者独占，设置明确的忙等待和队列容量。第一版先采用单连接策略；日志模式、连接池策略和锁冲突行为在真实测试后固定，不把 WAL 当成界面不卡顿的替代方案。

### 7.3 迁移与恢复

任务迁移嵌入任务存储适配器程序集，通过数据库公开迁移入口执行。基础设施维护模块 ID、版本和校验摘要；迁移与版本记录在事务中提交。检测到发布脚本被修改或数据库版本高于当前支持范围时拒绝写入。

首次自动建库建表；升级既有数据库前由存储模块通过 SQLite 一致性备份机制生成副本，失败则停止升级。事务失败不记录成功。已提交的迁移不因后续 UI 激活失败自动撤销。

目录创建、连接失败、磁盘满、访问被拒、锁等待超时都有明确错误。不得静默改用内存或另建空库。宿主通过数据目录内的独占锁文件配合受限的本机激活通知处理第二个实例；锁文件按系统最终路径识别尾部分隔符、短路径和联接点等路径别名，并跨登录会话生效。该通知只用于窗口激活，不作为业务调用入口，也不额外打开数据库。

## 8. WPF 界面贡献和 MVVM

### 8.1 功能提供参数，外壳负责渲染

通用 Presentation.Contracts 定义 NavigationItem、ViewContribution、IViewFactory 和离开页面前的确认协议。数据示例：

```csharp
new NavigationItem(
    Id: "tasks.navigation",
    Label: "任务",
    IconKey: "check-square",
    ViewId: "tasks.list",
    Order: 100);
```

Tasks.Wpf 模块贡献 `tasks.list` 对应的 IViewFactory。工厂内部创建自己的 ViewModel 和 UserControl，给 View 设置 DataContext。外壳只调用已登记的通用工厂，不引用任务 ViewModel 或具体 View 类型。

ShellWindow 的 ItemsControl/ListBox 绑定导航只读集合，ContentControl 绑定当前 FrameworkElement。若没有贡献，则导航集合为空，内容区显示通用空状态。首次选择按 Order、ID 排序后的首个页面。重复 ID、导航指向未注册页面、未知工厂均拒绝激活；未知 ViewId 显示通用不可用提示。

通用外壳按约定提供主题资源；任务模块自己的 DataTemplate 和 ResourceDictionary 随模块加载，不把任务模板写进外壳。XAML 中禁止通过静态全局服务定位器解析业务对象。

### 8.2 任务界面

- TasksViewModel 只依赖 ITasksApi 和获准使用的通用界面服务，不依赖仓储或驱动。
- 任务页始终由左侧任务列表和右侧新建/编辑栏组成。右栏固定第 2 列，不因宽度变化移动到列表上方、左侧或铺满页面；宽度变化只触发紧凑排版。
- “新建任务”按钮仍位于页头右侧，点击后更新或重置右侧编辑栏，不创建全屏编辑页。
- 使用 ObservableCollection 承载列表显示，业务结果始终为独立快照；更新集合在 UI 线程执行。
- 使用 AsyncRelayCommand 处理加载、创建、保存、完成切换和删除；执行异常明确观察和转换。
- 表单保存失败保留输入，业务字段错误绑定到对应控件，不在 code-behind 中实现规则。
- 完成和删除成功后才改变列表；写入期间阻止重复提交；切换筛选只接受最新查询响应。
- 删除通过通用对话框确认；取消不发写入。
- 未保存编辑通过 INavigationGuard 参与离开确认；切换页面和退出使用相同协议。
- code-behind 仅允许焦点、控件事件转发和窗口生命周期等视图操作，不放 SQL 或业务用例。

### 8.3 禁用矩阵

| 配置/状态 | 预期 |
| --- | --- |
| modules 为空 | 空外壳，无任务菜单、页面或可请求的任务能力 |
| tasks-core 启用、tasks-ui 关闭 | 不创建任务 View 和 ViewModel，服务按声明存在 |
| tasks-ui 启用、tasks-core 缺失 | 配置失败并显示诊断，不出现半可用界面 |
| tasks-core 关闭且无依赖它的模块 | 不绑定 ITasksApi，旧入口或非法请求由执行门拒绝 |
| 存储启动失败 | 不显示任务入口，回收资源，不切换模拟存储 |

修改启用状态第一版需重启。编译进应用的内置程序集不等于它已启用，反射不能作为正常功能发现和调用手段。

### 8.4 窗口自适应：导航折叠与固定右侧编辑栏

目标：任务页始终保持“左列表、右新建/编辑栏”，窗口缩小时仍能访问全部字段和操作。窗口空间不足时由左侧导航折叠释放宽度，紧凑模式只调整间距、页头动作排列和操作栏换行。自适应不隐藏业务能力，也不改变模块契约。

职责划分：

- 导航折叠由 `Mosaic.Shell.Wpf` 负责。外壳只使用通用 `NavigationItem.Label` 与 `NavigationItem.IconKey`，不认识任务模块。
- 任务双栏布局由 `Mosaic.Tasks.Wpf` 负责。外壳不判断任务字段、编辑器宽度、栏间距或操作按钮。
- 两个模块各自响应所在控件的 `ActualWidth`，不通过全局服务或跨模块事件协调；窗口尺寸变化只触发纯视图重排，不重新创建 ViewModel。

导航模式：

| 模式 | 触发条件 | 外观 | 内容宽度 |
| --- | --- | --- | --- |
| 完整侧栏 | 默认窗口宽度 ≥ 1180 DIP | 190 DIP：Logo 与名称、导航图标与文字、底部状态文字 | 其余宽度 |
| 图标栏 | 默认窗口宽度 < 1180 DIP；恢复到 ≥ 1200 DIP 才展开 | 64 DIP：Logo 标记、44×44 图标按钮；状态卡折叠为单个状态圆点，不保留卡片外框 | 内容区增加约 126 DIP |

- 折叠与展开采用 20 DIP 滞回，避免在恰好 1180 DIP 附近抖动。
- 图标栏按钮以 `ToolTip` 和 `AutomationProperties.Name` 保留导航文字；图标由 `IconKey` 映射，缺失时回退为标签首字符。
- 保留键盘焦点环、当前项高亮和 High Contrast 主题下的对比度；可点击目标不小于 40×40 DIP。
- 图标栏只收起展示，不撤销导航注册；点击仍通过现有 `NavigateCommand` 进入同一页面。
- 默认最小窗口 1100 DIP 时使用图标栏，为固定宽度右编辑栏和左侧列表保留足够空间；该阈值必须与窗口最小尺寸一起测试。
- 设置导航继续贡献 `IconKey = "settings"`。外壳用动态前景色的矢量 `Geometry` 齿轮绘制，不使用 Unicode `⚙`；完整与图标侧栏共用同一图形和可访问名称。
- 第一版按窗口宽度自动切换，不持久化手动折叠偏好；手动固定侧栏作为后续扩展，不改变通用契约。

任务页面结构：

| 列/参数 | 默认值 | 行为 |
| --- | --- | --- |
| 列表列 | `*`，最小 300 DIP | 占满编辑栏和间距之外的空间；自身滚动 |
| 栏间距 | `tasks.layout.paneGap = 24 DIP` | 可调 0–64；只改变视觉间隔 |
| 编辑栏 | `tasks.layout.editorPaneWidth = 480 DIP` | 可调 360–720；始终位于第 2 列并可见 |
| 紧凑阈值 | `tasks.layout.compactBreakpoint = 920 DIP` | 降低任务页内边距、压缩页头动作并允许操作栏换行 |

- 删除 `ApplyCompactLayout` 中把编辑栏放到第 0 列、隐藏列表和让详情占满内容区的逻辑；不再存在“返回任务列表”的单页状态。
- 列表与编辑栏保持同一实例、始终同时可见；调整窗口不得丢失输入、滚动位置或已选任务。
- 详情内容在独立 `ScrollViewer` 中滚动，底部操作栏固定在可见区域，不随内容滚走。
- 窄宽或矮窗下，操作按钮改为可换行的两行布局，保留完整文字和至少 40 DIP 高度，不缩成省略号或仅剩图标。
- 详情必须可达：标题、备注、字数与字段错误、冲突提示与“重新加载最新版本”、保存、完成切换、重新加载、删除、忙碌反馈和状态标签。过滤、刷新与新建继续留在各自原区域。
- 低于紧凑阈值时外边距降为 16–18 DIP；高度小于 680 DIP 时压缩标题与段间距，但不改变列顺序，也不降低输入框和按钮的最小尺寸。
- 不引入横向滚动；文本换行；列表标题可省略，详情标题完整显示并换行。

响应式状态：

- `ShellWindow` 维护 `SidebarMode`（完整、图标栏），在 `SizeChanged` 中计算，重复应用同一模式不产生副作用。
- `TasksView` 维护 `IsCompactTasksLayout`，输入为自身 `ActualWidth`；两种状态都使用固定双栏，只改变间距和控件换行。
- 页头“新建任务”动作在紧凑时可在页头内换行，但编辑栏位置不变。
- 默认窗口尺寸为 1504×1012 DIP；默认 `MinWidth=1100`、`MinHeight=720`。启动尺寸大于当前工作区时只收缩本次显示并放回可见区域，不改写保存值。

测试与验收：

- 布局：在 STA/Dispatcher 环境渲染 1100、1179、1180、1199、1200、1366、1504 DIP 宽度与 720、1012 DIP 高度，断言侧栏宽度、编辑栏列号为 2、列表与编辑栏同时可见。
- 参数：将 `editorPaneWidth`、`paneGap`、`compactBreakpoint` 分别改到最小值、默认值和最大值，验证即时重排；无效或互相挤压的组合显示字段/跨字段错误。
- 功能：在 1100×720 和 1504×1012 下完成新建、保存、完成切换、重新加载、删除、冲突重载和未保存离开确认；筛选与刷新始终可用。
- 可访问性：图标栏每项保留可读名称与提示；设置齿轮在完整/图标侧栏、选中和 High Contrast 下可辨识；键盘可导航；100/125/150/200% DPI 下无裁剪或重叠。
- 保护：紧凑阈值往返不丢未保存输入，也不改变列表/编辑栏顺序；无任务模块时外壳的折叠仍正常；空配置和禁用任务界面下不产生任务控件或跨模块依赖。
- 预览：结果输出到 `artifacts/ui-preview/`，至少包含 `shell-1100-rail`、`tasks-fixed-right-1100`、`tasks-fixed-right-1504`、`settings-advanced-off`、`settings-advanced-on`。

非目标：第一版不实现移动端抽屉、侧栏折叠动画、折叠偏好持久化和新的响应式能力契约；这些扩展可在不改动业务模块的前提下后续加入。

### 8.5 过压缩防护：固定双栏与过小工作区

背景：窗口接近最小尺寸，或启动尺寸大于当前工作区时，任务页仍必须保持列表与右侧编辑栏。防护原则是保持列顺序、让内容在各自区域内滚动和换行；只有导航可以折叠，任务编辑栏不能变为单页覆盖。

两层最小尺寸：

| 层 | 最小尺寸 | 行为 |
| --- | --- | --- |
| 外壳窗口 | 默认 MinWidth=1100、MinHeight=720 | WPF 阻止继续缩小，保证两侧都有可操作空间 |
| 任务模块 | 列表 ≥ 300 DIP；编辑栏默认 480 DIP、最低 360 DIP | 固定左右双栏；内部滚动、文字换行、操作栏固定 |

过压缩时的具体规则：

- 顶栏右侧“MOSAIC / 工作空间”在窄窗折叠为“MOSAIC”或省略号，不与 `PageTitle` 抢占一行。
- 页头标题与副标题允许换行，超长时省略；新建任务按钮在紧凑页头中可以换行，但始终保留最小点击宽度。
- 列表行：完成图标列固定 30 DIP，标题与日期行使用省略，行高不随压缩继续降低。
- 详情表单：备注框保留 `MinHeight=128`；矮窗（高度 < 680 DIP）改用剩余空间自适应高度并保持滚动，冲突横幅、字段错误和状态标签必须留在可见滚动区内。
- 操作栏：窄宽时两行换行；删除按钮固定整行宽度，其余按钮最小宽度 96 DIP、高度 40 DIP；忙碌反馈和错误提示不覆盖按钮。
- 图标栏 64 DIP、按钮 44×44 固定，不随内容宽度变化继续压缩。
- 图标栏状态卡：文字折叠后只保留状态圆点（约 8–10 DIP）居中显示，移除卡片边框、背景和固定 44×44 外框；圆点以 `ToolTip` 和 `AutomationProperties.Name` 保留“本机工作空间 · 离线可用”完整语义。空文字的卡片边框禁止渲染，避免出现看起来可点击却无信息的占位方块。
- 保存的启动尺寸超出当前工作区时，计算本次 `ActualWidth/ActualHeight` 和位置以完全落入工作区，但不回写 `startup.width/height`；下一次在更大屏幕仍使用保存值。
- 若可用宽度低于任务模块声明的下限，应优先保持可滚动和可操作，不显示空白或“放不下”的替代状态。

极端尺寸验收矩阵：

| 渲染尺寸 | 覆盖状态 |
| --- | --- |
| 1100×720 | 图标栏、任务列表、右侧编辑栏、操作栏紧凑换行 |
| 1179×720 | 图标栏、固定右侧编辑栏；阈值前状态 |
| 1180×720 | 完整侧栏、固定右侧编辑栏；阈值边界状态 |
| 1366×768 | 常见笔记本工作区启动缩放，无右栏覆盖 |
| 1504×1012 | 默认完整布局 |

上述尺寸同时以 100/125/150/200% DPI 抽查。验证方式：渲染后使用 OCR 与边缘像素断言，检查文字是否裁出边界、控件是否重叠、操作按钮是否全部可达；该断言纳入自适应布局测试，不依赖人工目测。

## 9. 应用启动、失败回收与退出

启动：

1. App 在 UI 线程建立最小启动/诊断窗口，使用显式 ShutdownMode 控制退出时机。
2. 宿主申请单实例控制，读取配置与模块清单，校验依赖图和提供者。
3. 建立应用设施和范围受限的依赖；启动数据库执行器。
4. 存储适配器申请迁移，成功后提供仓储。
5. 任务模块提供受控 ITasksApi，界面模块在 UI 线程贡献工厂和导航。
6. 校验全部贡献后显示 ShellWindow；空配置直接展示空外壳。

失败：立即停止新的业务请求，撤销已登记入口，逆序释放已激活模块和资源，保留诊断窗口。启动回收只处理资源与注册，不伪装成已撤销提交的迁移。

正常退出：先通过页面守卫询问未保存编辑；用户选择保存时等待保存完成，失败则取消退出。确认离开后将执行门置为 Stopping，拒绝新调用，等待在途请求和数据库队列完成，再按相反顺序释放界面、业务、存储和宿主。

WPF Closing 事件先取消默认关窗，交给 ShutdownCoordinator 异步处理，完成后通过重入保护执行最终关闭；不能在 Application.Exit 中才开始无法等待的异步清理。用户选择继续等待或强制退出时，明确区分已确认保存与未知结果。

异常入口只做诊断和受控停止，不在 DispatcherUnhandledException 中无条件吞掉错误并继续宣称运行正常。Generic Host 的释放职责与模块资源释放职责明确划分，避免重复 Dispose。

## 10. 分阶段实施与验收

### 阶段 0：WPF 和数据库工具链

交付：解决方案、统一构建设置、空 WPF 窗口、SQLite 后台队列最小验证、自包含文件夹发布。

验收：开发与发布程序都能运行；通过存储公开协议写入并重开临时数据库；发布文件包含正确架构的 SQLite 原生依赖；无需目标机器安装开发 SDK。此阶段不建立任务功能绕过接口的快捷路径。

### 阶段 1：契约、内核和程序集边界

交付：通用协议、任务公开协议、仓储协议、存储协议、界面协议、依赖图、执行门、生命周期及架构测试。

验收：缺依赖、循环、版本不兼容、重复 ID 和提供者被拒绝；逆序回收及停止接收请求正确；禁止项目引用、越权 public 类型和禁止框架依赖被检查发现。

### 阶段 2：纯任务业务

交付：任务服务、受控公开入口、仅用于测试的仓储和时钟/ID 替身。

验收：任务操作、Unicode 限额、字段不可变、找不到目标、并发版本、存储失败与停止状态均正确；测试不加载 WPF 或数据库驱动。

### 阶段 3：SQLite 与任务仓储

交付：公开数据库会话、单消费者执行器、迁移和备份、任务 SQL 与映射。

验收：真实临时库通过仓储契约测试；关闭重开数据完整；竞争更新只有预期版本成功；迁移失败不留下部分状态；驱动对象不离开存储程序集；慢数据库操作不阻塞 UI。

### 阶段 4：宿主和模块装配

交付：强类型 JSON 配置、静态模块目录、提供者选择、DI 装配根、调用租约与应用关闭协调。

验收：公开调用贯通任务和存储；空配置不打开库；未激活能力无法请求；关窗拒绝新请求并等待在途写入；失败回收没有半启用入口。

### 阶段 5：参数导航与任务 MVVM

交付：ShellWindow、贡献注册、任务 Views/ViewModels、加载/错误/冲突状态、未保存离开确认。

验收：完整任务操作可用；无贡献则无业务菜单和页面；禁用任务界面不创建任务 View；所有绑定更新在 UI 线程；无旧查询覆盖；无重复提交或未保存内容被悄悄丢弃。

### 阶段 6：Windows 打包和恢复验证

交付：win-x64 自包含发布、每用户安装包、配置/日志/数据位置说明和验证记录。

先验证文件夹发布，再采用 WiX Toolset 制作每用户安装包并锁定所需版本。签名在有证书后接入。第一版不启用裁剪、Native AOT 或要求所有依赖合并为单文件的发布方式，减少 XAML、资源和原生 SQLite 装载的不确定性。发布及安装范围依据：[.NET 自包含发布](https://learn.microsoft.com/en-us/dotnet/core/deploying/)、[WiX 每用户安装范围](https://docs.firegiant.com/wix/schema/wxs/packagescopetype/)。

验收：无开发环境的干净 Windows 用户环境可以安装、运行、保存和重开；文件不写入安装目录；升级安装不覆盖用户数据；卸载默认保留数据，不隐式删除数据库；单实例和高 DPI/中文输入基础交互通过。

### 阶段 7：窗口自适应布局

交付：1504×1012 默认窗口、1100×720 最小窗口、过小工作区安全修正；外壳导航完整与图标栏两态、20 DIP 滞回；任务页固定左列表/右编辑栏；紧凑间距与操作栏换行；设置导航使用矢量齿轮；STA 布局、可访问性与预览。

验收：1100×720 和 1504×1012 下均可完成全部任务字段和操作，编辑栏始终位于第 2 列，窗口缩小时不铺满任务页；1179/1180/1199/1200 DIP 边界来回调整不抖动；保存启动尺寸大于工作区时仅缩放本次显示；图标栏与设置齿轮保留可读名称和主题对比度；跨紧凑阈值不丢未保存输入；空配置和禁用任务界面下无越界依赖。

### 阶段 8：设置功能（0.2.0）

交付：settings-sqlite、settings-core、settings-ui 三个独立模块；57 项参数归入“任务、外观与布局、存储与系统、文案”四个分区，其中 8 项普通参数在高级关闭时仍可见，另外 49 项高级参数仅在高级开启后可见；覆盖语言、任务规则、固定右栏布局、外观、存储系统与文案；宿主 appsettings 来源、即时/重启生效语义、危险项确认与恢复默认。

验收：不存在“普通”分区；高级设置默认关闭时完整模块配置下只有任务、外观与布局两个分区，分别显示 6 项和 2 项普通参数且可直接保存，存储与系统和文案分区整体隐藏；开启后四个分区和全部 57 项参数均可查看、修改、校验和恢复默认；普通项在两种模式下只有一份行实例，分区恢复按当前可见定义 ID 限定范围。阶段 8 实施时关闭开关会询问未保存修改；阶段 10 将改为无询问并保留全部暂存值与预览。新增 `tasks.layout.editorPaneWidth` 与 `tasks.layout.paneGap` 即时生效；默认启动尺寸为 1504×1012、最小尺寸为 1100×720；即时项保存后生效，重启项明确显示 pending 且下次启动生效；旧 0.1.0 配置与数据库可加载；禁用模块的设置值保留并在重新启用后恢复；危险项均有警告与测试；设置表只由 settings-sqlite 访问。完整计划见 [设置功能规划](settings-plan.md)。

### 阶段 9：后台驻留与边缘任务面板

实施状态（2026-11-16）：0.3.0 源码与本地 EXE 已实现 P0–P4；状态机、设置、只读摘要、实际 WPF 边缘/弹窗创建与释放已自动化验证。主窗口关闭驻留、二次启动恢复草稿及保存 Exit 后退出已在桌面确认。多屏/DPI/全屏/系统会话及完整烟雾、安装升级仍未验收，P5 不标完成。

交付：独立的 `desktop-presence` 模块；关闭主窗口与真正退出分流；右上侧方形边缘入口；悬停、单击、双击三种互斥触发；只读任务摘要；新增“驻留与边缘”设置分区；多屏、DPI、高对比度和键盘可访问性验收。

验收：主窗口关闭后进程、模块、页面与数据库继续存活，恢复主窗口不重建页面；真正退出仍执行未保存确认、等待在途写入、逆序释放和单实例释放；Presence 不读取 Tasks/Storage 内部对象，任务摘要只调用 `ITasksApi`，完整 `TasksView` 不被复制；没有任务摘要贡献时不创建无内容驻留入口；系统会话结束与致命错误不进入驻留；边缘入口和弹窗始终位于当前工作区，DPI 与副屏变化后重新定位；关闭后任务列表已更新，弹窗打开能看到最新快照。完整计划见 [后台驻留与边缘任务面板规划](desktop-presence-plan.md)。

### 阶段 10：设置跨分区持续预览

状态：0.4.0 已实现本地源码及自动化验证；完整交互式桌面、系统会话、DPI 和安装升级仍待验收。

交付：settings-core 增加单一活动编辑会话；设置定义增加 `SettingPreviewMode`；预览只应用可逆即时参数且不持久化；`SettingPreviewMode.None`、重启和 HostConfig 参数只暂存；settings-ui 在分区/高级开关切换时不再询问保存，而保留全页累计修改；离开设置页、切换功能页或真正退出应用时统一询问保存、放弃或取消。

验收：任意分区切换与高级开关切换均不调用未保存对话框；所有可见/隐藏分区共用一个 Dirty 总账；合法可逆即时项持续预览，非法候选保留最后一次合法预览；不可逆即时项和重启参数运行值不提前改变；快速输入由 revision 和单消费者队列保证最新值生效；预览、延迟页面回放、Discard 回滚均不写数据库或 appsettings；显式保存和离开时保存都一次提交全页累计变更并统一处理危险项；离开前冻结输入并排空旧预览，Dirty 为零也不能在预览在途时直接离开；校验/持久化/回滚失败留页，持久化成功但即时应用失败按 ApplyWarning 处理；显式保存成功后会话继续编辑并以新值重置回滚基线；关闭主窗口进入驻留不等于离开设置页，不弹确认，真正退出应用时才触发；消费来源先撤销预览再注销，页面异常释放执行异步取消/回滚。完整规则见 [设置功能规划](settings-plan.md) 第 13 节。

阶段按依赖顺序推进，每阶段验证后再继续，不提前构建提醒、同步或插件市场。

每个阶段交付时遵循 [架构原则](architecture-principles.md) 第 13 节的同步交付规则：源码、测试、用户可见行为、发布说明和实施记录必须属于同一个变更单元；影响旧数据目录、配置或安装升级的改动必须同时交付迁移逻辑与迁移验收项。发布说明必须写清对已有安装的影响和用户需要执行的动作。诊断与修复还必须写明错误位置（具体文件、行、契约或调用链，说明根因）和改动位置（实际修改的文件、行、测试与文档），二者随同一交付单元记录。

## 11. 测试与检查命令

| 检查层 | 重点 |
| --- | --- |
| 编译与分析 | nullable、分析警告、WPF XAML 编译、禁止引用和公开 API 清单 |
| 架构测试 | csproj 和实际程序集依赖、public 类型、禁止框架、生产 InternalsVisibleTo 和反射越界 |
| 内核测试 | 图排序、配置拒绝、能力绑定、执行租约、停止与失败回收 |
| 业务测试 | 规则、不可变字段、目标不存在、冲突、存储失败和取消语义 |
| 仓储契约 | 相同用例用于测试仓储和 SQLite，覆盖读取排序、条件写入和删除 |
| 数据库集成 | 重开、参数化查询、事务、迁移摘要、版本过高、失败备份和锁等待 |
| 配置迁移 | 顶层与 settings 覆盖层、先校验后写入、一次性标记、原配置备份、非法配置不改写 |
| MVVM/界面测试 | STA/Dispatcher、命令状态、线程更新、导航贡献、未保存守卫和慢查询 |
| 自适应布局 | 侧栏断点与滞回、任务固定右侧编辑栏、紧凑阈值往返、设置矢量齿轮、默认/最小窗口及工作区缩放 |
| 设置界面 | 高级关闭时仅两分区及 8 项普通参数、空普通分区整栏隐藏、高级开启后四分区及 57 项可达、普通项单实例；阶段 10 起分区与高级开关切换零确认，保存/放弃按全页累计范围 |
| 设置持续预览 | 分区切换零弹窗、跨分区累计暂存、可逆即时预览、不可逆/重启仅暂存、单活动会话、快速输入最新代次、统一离开一次确认、回滚与页面释放 |
| 后台驻留 | 关闭驻留与服务存活、恢复不重建、显式退出排空、三种触发互斥、任务摘要只走公开 API、多屏/DPI 定位、设置可见性与高对比度 |
| 桌面验收 | 干净 Windows 环境启动，创建编辑完成删除、禁用、关窗、安装升级重开 |

WPF 控件测试使用独立 STA 线程与 Dispatcher，不直接用普通并行测试线程创建 Window。纯 ViewModel 测试注入 IUiDispatcher/IUserDialogService 替身。桌面自动化先用 Windows UI Automation API 做关键烟雾流程，范围以可重复验证为准，不为每个控件建立脆弱测试。

可执行命令（xUnit v3 使用 .NET 10 Microsoft.Testing.Platform；发布后运行桌面测试）：

```powershell
dotnet restore Mosaic.sln --locked-mode -m:1
dotnet build Mosaic.sln -c Release --no-restore -m:1
pwsh -File build/publish.ps1
pwsh -File build/test.ps1 -NoBuild
dotnet test --solution Mosaic.sln -c Release --no-build --no-restore
dotnet format Mosaic.sln --verify-no-changes --no-restore
dotnet run --project src/Mosaic.Desktop
dotnet publish src/Mosaic.Desktop -c Release -r win-x64 --self-contained true -o artifacts/local-run
```

各项目已生成 packages.lock.json，锁定还原已验证。上述 test 命令应先按相同配置完成构建；测试使用独立数据库和 STA Dispatcher。每用户安装包由 `build/package.ps1` 构建。

## 12. 完成定义和实施前待定事项

第一版完成同时要求：可运行 WPF 桌面程序、真实持久化、公开接口调用闭环、参数驱动导航、有效程序集边界、UI 响应性、已执行测试和发布验证记录。

实施时补齐：应用标识和图标、是否采用默认字段限额、最低支持的 Windows 版本、目标处理器及安装范围。先验证 win-x64，ARM64 需要独立发布和原生依赖测试。正式签名证书和发布账号不阻塞本机开发。

实施选择：应用 ID Mosaic，默认字段限额不变；目标 Windows 10 2004+ x64，每用户安装；图标为内置四格 Mosaic 标记。正式签名、干净系统验收及 ARM64 另行推进，不将未执行的检查视为通过。
