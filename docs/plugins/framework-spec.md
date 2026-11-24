# 框架规范

本文说明 Mosaic 运行时如何把模块装起来、按什么顺序启动、配置从哪里来、失败时发生什么。实现位置集中在 `src/Mosaic.Kernel` 与 `src/Mosaic.Desktop`。

## 1. 启动链

```text
Mosaic.Desktop.exe
  ├─ AppPaths.FromArguments(args)            解析数据根（默认 %LOCALAPPDATA%\Mosaic，或 --data-root；`App.xaml.cs:48`）
  ├─ AppConfiguration.Load(paths)           读取/修复/迁移 Config/appsettings.json
  ├─ SingleInstance                         单实例互斥；第二次启动转交给已运行实例
  ├─ Bootstrapper.StartAsync()
  │   ├─ ModuleGraph.Resolve(manifests, providers)        校验 + 拓扑排序（`Bootstrapper.cs:53`）
  │   ├─ ModuleRuntime.StartAsync(ordered, factory)       按拓扑序逐个调用工厂并激活；失败逆序回滚（`:79`）
  │   │   └─ factory = BuiltInModuleCatalog.Create(context, manifest)   按清单构造模块（`BuiltInModuleCatalog.cs:258`）
  │   │   └─ ModuleStarted 事件 → 注册 IUiModule 的导航/视图/状态
  │   ├─ ShellWindow                        承载导航与页面
  │   └─ DesktopApplicationLifecycle        主窗口显示/驻留/退出策略
  └─ ShutdownCoordinator                    停止模块、刷新设置、写日志
```

关键事实：

- 模块**不是**扫描发现的，`BuiltInModuleCatalog.Entries` 是唯一登记处；配置里的模块 ID 必须能在目录中找到，否则 `HostSettingSource.ValidateAsync` 先报「模块列表包含未知或重复的模块。」（`HostConfigurationValidator.cs:13-21`），宿主再包装成「模块或能力绑定配置无效：…」（`Bootstrapper.cs:46-51`）。
- 模块列表只决定“启用哪些”；顺序由依赖图计算，列表顺序只在**无依赖关系**的模块之间决定相对先后，有依赖时拓扑优先（`ModuleGraph.cs:26-43`）。
- 启动失败不会进入主窗口；配置错误与模块错误都会写入诊断和日志。

## 2. 模块图解析

`ModuleGraph.Resolve`（`src/Mosaic.Kernel/ModuleGraph.cs`）：

1. 按输入顺序建立 `ID → 清单` 字典；空 ID 或重复 ID 立即失败。
2. 建立 `能力 → 提供模块 ID` 字典；同一能力重复提供立即失败。
3. 校验每个模块的 `Provides` 都在 `providers` 中以**自身 ID** 出现。
4. 校验每个 `Requires` 都能在能力字典中找到，主版本精确相等。
5. 深度优先找环；发现环时抛固定消息 `Module dependency cycle.`，**不含模块 ID**（`ModuleGraph.cs:30`）。
6. 返回拓扑序（依赖在前，依赖方在后）。

失败信息是逐字的 `InvalidOperationException` 消息（`src/Mosaic.Kernel/ModuleGraph.cs:13-48`）：

```text
Duplicate or invalid module ID.
Module metadata is invalid: tasks-core
Duplicate provider: tasks.api@1
Provider binding is missing or incompatible: tasks.api@1
Module dependency cycle.
Missing capability or incompatible version: settings.api@1
Invalid capability ID.
```

`ModuleRuntime` 侧的错误串（`src/Mosaic.Kernel/ModuleRuntime.cs:15`、`:17`、`:28`、`:33`、`:37`，`{T}` 为 `Resolve<T>` 的 `T`）：

```text
Capability 'tasks.api@1' has no active bound provider.
Provider 'tasks-core' does not export capability 'tasks.api@1' as {T}.
Runtime is single use.
Factory returned no module for manifest 'tasks-core'.
Factory returned a module that does not match manifest 'tasks-core'.
```

停止阶段多个模块清理失败时聚合为 `AggregateException`（`ModuleRuntime.cs:65`）。

## 3. 运行时启停

`ModuleRuntime`（`src/Mosaic.Kernel/ModuleRuntime.cs`）：

```csharp
var runtime = new ModuleRuntime();
await runtime.StartAsync(orderedManifests, manifest => catalog.Create(context, manifest));
// ...
await runtime.StopAsync();
```

规则（行号均在 `src/Mosaic.Kernel/ModuleRuntime.cs`）：

| 阶段 | 行为 |
| --- | --- |
| `StartAsync` 前 | 单次使用：`_started` 非空或已停止时抛 `Runtime is single use.`（`:28`） |
| 构造 | 按 `ordered` 顺序逐个调用工厂；返回 `null` 抛 `Factory returned no module for manifest '{id}'.`（`:33`） |
| 实例校验 | `Matches` 比对 `Id`、`Responsibility`、`Lifecycle`、`EntryVersion` 与 `Provides`/`Requires` 集合；不一致先 `DisposeAsync` 再抛 `Factory returned a module that does not match manifest '{id}'.`（`:34-38`、`:69-79`） |
| 激活 | 先把模块加入 `_started` 以便回滚，再 `ActivateAsync`；成功后记入 `_activeModules` 并触发 `ModuleStarted`（`:39-43`） |
| 激活失败 | `catch` 里调用 `DisposeAsync()` → 对 `_started` 逆序 `StopAsync` + `DisposeAsync`，然后原样抛出首个激活异常（`:46-50`） |
| 运行中 | `Resolve<T>` 按绑定从 `_activeModules` 取能力（`:11-19`） |
| `StopAsync` | 幂等；逆序对每个模块触发 `ModuleStopping`、`StopAsync`、`DisposeAsync`，异常收集后抛 `AggregateException`（`:52-66`） |
| 停止后 | 不能再次 `StartAsync`（`:28`） |

`IModuleActivation` 契约：

```csharp
public interface IModuleActivation : IAsyncDisposable
{
    ModuleManifest Manifest { get; }
    Task ActivateAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}
```

## 4. 执行门

`ModuleExecutionGate` 解决“停止时仍有调用在跑”的问题：

```csharp
var gate = new ModuleExecutionGate();
await module.ActivateAsync();     // 模块内部 gate.Activate()
using var lease = gate.TryEnter(); // 停止后返回 null
await gate.StopAsync();            // 拒绝新租约，等待已有租约释放
```

实现约定（行号均在 `src/Mosaic.Kernel/ModuleExecutionGate.cs`）：

- `Activate()` 在激活阶段调用且只调用一次；重复调用或停止后调用抛 `Gate cannot be activated.`（`:16`）。
- `TryEnter()` 在未激活或已停止时返回 `null`（`:24`），调用方必须把它当作“能力不可用”处理；已取到的租约是 `IDisposable`，`Dispose` 后计数递减（`:43-47`）。
- `StopAsync()` 置 `_stopping`/清 `_active` 并返回 `_drained.Task`（`:29-38`）；租约未全部释放前该任务一直未完成，重复调用返回**同一个**任务，不会提前完成（`Release` 在 `:41` 递减计数并触发完成）。
- 所有对外方法先取租约；停止后返回稳定错误（`CapabilityUnavailable` 或模块自定义错误码）。
- 内部后台任务在 `StopAsync` 中取消并等待，不依赖 `DisposeAsync` 收尾。

## 5. 宿主组合根

`BuiltInModuleCatalog`（`src/Mosaic.Desktop/BuiltInModuleCatalog.cs`）只有一个手写登记处 `Entries`，其余成员全部由它派生：

| 成员 | 位置 | 来源 |
| --- | --- | --- |
| `Entries` | `:76-245` | 唯一手写：`ID → (Manifest, Func<ModuleRuntimeContext, IModuleActivation>)` |
| `Definitions` | `:247-248` | `Entries` 的 `Manifest` 投影，`ID → ModuleManifest` |
| `DefaultModules` | `:250-251` | `Entries` 按声明顺序取 `Manifest.Id` |
| `DefaultProviders` | `:253-256` | 每个 `Manifest.Provides` 映射到提供者自身 `Manifest.Id` |
| `Create` | `:258-261` | 按清单 ID 查 `Entries`；缺失时抛 `ConfigurationException("未知模块。")` |

所以新增模块只需在 `Entries` 追加一条（放在它依赖的模块之后，工厂里读 `context` 上已装配好的字段），`Definitions`、`DefaultModules`、`DefaultProviders` 自动跟着变，不要再手改这三个派生成员。示例：

```csharp
[NotesModule.Definition.Id] = new(NotesModule.Definition, context =>
    new NotesModule(new NotesDependencies(
        context.Storage?.Session ?? throw new InvalidOperationException("Storage was not assembled before notes."),
        context.Diagnostics,
        context.Dispatcher)));
```

`DefaultModules` 的数组顺序是字典插入顺序，也就是 `Entries` 的书写顺序；但模块**启动顺序**不取这个数组，而由 `ModuleGraph.Resolve` 按依赖拓扑排序决定。

`ModuleRuntimeContext`（`:30-70`）是宿主的构造上下文，成员按装配进度逐步填充：

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| `Paths` | `AppPaths` | 数据根、配置目录、日志、附件等路径 |
| `StorageConfiguration` | `StorageConfiguration` | 引擎、位置、数据库文件名 |
| `Clock` / `Ids` | `TimeProvider` / `IIdGenerator` | 时间与 ID 生成 |
| `Diagnostics` / `Dispatcher` / `Dialogs` | `IDiagnosticSink` / `IUiDispatcher` / `IUserDialogService` | 诊断、UI 线程调度、对话框 |
| `HostSettings` | `IHostSettingSource?` | `settings` 段宿主设置（`SettingPersistence.HostConfig`） |
| `SettingsPages` | `SettingsPageRegistry` | 设置页注册表 |
| `DeferredSettingSources` | `List<ISettingSource>` | 晚于设置核心创建的设置源，构造设置核心时一并注入 |
| `Storage` | `SqliteModule?` | SQLite 模块实例；`Storage?.Session` 才是 `IDatabaseSession`，模块不得自行开库 |
| `Settings` / `SettingsRepository` | `SettingsModule?` / `SettingsSqliteModule?` | 设置核心与仓储 |
| `Tasks` / `Repository` | `TasksModule?` / `TasksSqliteModule?` | 任务 API 与仓储 |
| `Presence` / `ReleaseSummary` | `IDesktopPresenceApi?` / `IReleaseSummaryApi?` | 驻留、更新简要 |
| `Floating` / `FloatingStorage` / `FloatingFactory` / `DetachSource` | 浮窗相关 | 由浮窗模块与任务 UI 模块提供 |
| `Auth*` / `Sync*` / `Maintenance` / `Lifecycle` | 对应模块类型 | 账户、同步、维护、生命周期 |
| `WindowHost` | `IDesktopWindowHost`（`required`） | 恢复主窗口等 |

可空成员由更早的 `Entries` 条目写入，工厂里读它们时必须显式判空并抛 `InvalidOperationException`，与现有条目写法一致（例如 `BuiltInModuleCatalog.cs:96`、`:102`）。

约束：

- 组合根是唯一允许使用 `Microsoft.Extensions.*`、`IServiceProvider`、`IHost` 的地方。
- 组合根只允许引用公开契约，不得触碰模块内部类型。
- 依赖在构造时一次性传入，模块内部不得延迟解析。

## 6. 配置文件

路径：`%LOCALAPPDATA%\Mosaic\Config\appsettings.json`。首次运行从程序目录复制模板。可用 `--data-root <绝对路径>` 指定独立工作空间。

结构（`AppConfiguration`）：

```json
{
  "configVersion": 1,
  "appliedMigrations": ["desktop-presence-v1", "..."],
  "modules": ["storage-sqlite", "settings-sqlite", "..."],
  "providers": { "sqlite.session@1": "storage-sqlite", "...": "..." },
  "storage": { "engine": "sqlite", "location": "local-app-data", "filename": "mosaic.sqlite" },
  "settings": { "host.modules": [...], "logging.level": "Information" }
}
```

上面的 `settings` 段是可选的：程序目录模板 `src/Mosaic.Desktop/appsettings.json` 只写 `configVersion`、`appliedMigrations`、`modules`、`providers`、`storage` 五项，`settings` 在设置页第一次保存后由 `HostSettingSource` 写入。

两层优先级（`Bootstrapper.cs:44-45`）：

- `settings["host.modules"]` / `settings["host.providers"]` 是**权威值**，设置页写入的就是这两个键。
- 顶层 `modules` / `providers` 只是新安装模板的默认值，也是 `host.*` 不存在时的兼容回退。

加载规则（`AppConfiguration.Load`）：

1. 文件不存在时从模板复制。
2. JSON 必须合法、不得有重复属性、不得有未知字段（`UnmappedMemberHandling.Disallow`）。
3. `providers` 缺项会先**修复**（按配置文件里**当时已启用**的模块清单补全绑定），再校验；已有键一律不覆盖（`RepairProviderBindings`，在迁移之前执行于 `AppConfiguration.cs:44`，实现 `:177-205`）。
4. 未应用的迁移按顺序执行，执行后校验并写回。
5. 任何无法修复的问题抛 `ConfigurationException`，主窗口不出现；只有 `configuration != original`（补过绑定或执行过迁移）时才做带依赖校验的完整校验并写回（`:62-66`），无需改写时不触盘。

数据根迁移：启动时 `storage.location` 只做格式校验（`local-app-data` 或绝对路径，`Bootstrapper.cs:40-41`），**不检查目录是否存在**；目标目录存在性、禁止磁盘根、源目标相同、同名库与磁盘空间由 `StorageRelocation.PlanAsync` 在维护页校验（`StorageRelocation.cs:57-73`）。`storage.filename` 必须是单个合法文件名（`AppConfiguration.IsValidDatabaseFilename`，`Bootstrapper.cs:42-43`）。

## 7. 设置系统

契约位置：`src/Modules/Settings/Mosaic.Settings.Contracts/SettingsContracts.cs`。

### 7.1 定义

```csharp
new("notes.defaultColor", "notes-core", "默认颜色", "新建笔记使用的颜色", SettingValueKind.String, "amber", null, null, null, SettingsGroup.Tasks, SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings, DisplayNameKey: "Settings.Notes.DefaultColor", DescriptionKey: "Settings.Notes.DefaultColor.Description")
```

参数名是 `Id`/`ModuleId` 等 PascalCase，且 `DisplayNameKey`/`DescriptionKey` 是可选的命名参数（`SettingsContracts.cs:14-36`）。**必须写成一整行**：架构测试只扫描含 `SettingValueKind` 的那一行（`BoundaryTests.cs:116-128`）。

要点：

- `moduleId` 必须与 `BoundaryTests` 的归属表一致，且声明文件位于拥有工程。
- `SettingsGroup` 是**追加式枚举**：新分组只能加在末尾，历史值按数字持久化。
- `SettingApplyMode.Restart` 的设置写入后需要重启才生效；`Immediate` 由模块自行订阅/轮询。
- `SettingPersistence.HostConfig` 写入 `appsettings.json`；`UserSettings` 写入用户设置库。
- `moduleId = "host"` 的宿主设置定义在 `HostSettingsCatalog`。

### 7.2 注册与读取

```csharp
public sealed class NotesModule(ISettingsCore settings) : IModuleActivation
{
    private IDisposable? _registration;
    public async Task ActivateAsync(CancellationToken ct = default)
    {
        _registration = settings.Register(new NotesSettingsSource());
        var values = await settings.GetValuesAsync(ct);
        if (values.IsSuccess) await ApplyAsync(values.Value, ct);
    }
    public Task StopAsync() { _registration?.Dispose(); _registration = null; return Task.CompletedTask; }
}
```

- `ISettingSource` 只有 `ModuleId`、`Definitions`、`ValidateAsync`（有默认实现，返回成功，可不写）和 `ApplyAsync`（**没有默认实现，必须实现**）（`SettingsContracts.cs:278-286`）；读取有效值用 `ISettingsCore.GetValuesAsync`，写回走 `ISettingsApi`。
- 注册时拒绝三种情况，都抛 `InvalidOperationException`：同一模块重复注册 → `A setting source is already registered for this module.`；设置 ID 在来源内或全局重复 → `A setting definition is already registered: {ids}`（不看元数据是否相同）；`Definition.ModuleId` 与 `ISettingSource.ModuleId` 不一致 → `A setting definition belongs to another module: {ids}`（`SettingsService.cs:11-34`）。
- `ApplyAsync` 收到的是保存成功后**全局**所有 `Immediate` 设置的有效值快照（不只本模块，需按 `ModuleId` 自行过滤，`SettingsService.cs:375-380`）；它抛出的异常被 `TryApplyAsync` 捕获，转为「已保存，但即时应用失败，重启后生效。」警告（`SettingsService.cs:225-235`），不回滚已保存的值。另一个调用路径是 `ApplyPreviewAsync`（`SettingsEditing.cs:146-172`）：只对 `Immediate` + `Reversible` + `UserSettings` 的来源调用，失败时逆序用旧值补偿。
- 设置键全局唯一；插件前缀使用 `[PLUGIN-ID].`。
- 修改设置必须走 `ISettingsApi`，不要直接写文件或数据库。

## 8. 数据与存储

- 所有持久化经由 `IDatabaseSession`（`Mosaic.Storage.Contracts`），底层为 SQLite。
- 建表、索引、迁移逻辑放在对应 `*.Sqlite` 工程；Core 只依赖仓储接口。
- 迁移必须幂等：`CREATE TABLE IF NOT EXISTS`、列存在性检查、按版本号顺序执行。
- 该模块已执行过迁移，或库中已有用户表时，升级前按 `storage.backupRetention`（默认 5，0 关闭；定义在 `SqliteModule.cs:28`，范围 0–50）生成 `mosaic.sqlite.backup-<yyyyMMddHHmmssfffffff>.sqlite`；全新数据库首次建库不生成。插件不得删除这些备份（`MigrationRunner.cs:33-46`、`:61-70`）。
- 迁移版本从 1 连续编号，按 `(module_id, version, digest)` 记账；已执行版本的 SQL 一旦改动，摘要不符即失败（`MigrationRunner.cs:12-32`）。失败会经 `DatabaseWorker.Enqueue` 归一成 `STORAGE_UNAVAILABLE` 失败结果（原始异常进诊断，`DatabaseWorker.cs:117-124`），模块若不检查返回值，就会带着未建表状态启动。
- 插件新增表使用自己的前缀，例如 `notes_items`，不得复用 `tasks_*`、`settings` 等既有表。

## 9. 关闭与驻留

- 关闭主窗口时按 `DesktopCloseBehavior` 分流（`src/Mosaic.Desktop/ShutdownCoordinator.cs:25-31`）：`Reside` 调 `presence.RequestParkAsync()` 驻留（失败弹 `MessageBox`），`Exit` 进入退出流程。当前值来自设置 `desktop.closeBehavior`，默认 `Reside`（`PresenceSettings.cs:12`）。
- 退出分两段。`ShutdownCoordinator.RequestExitAsync`（`:37-67`）先取接受：`shell.CanCloseAsync()`（`:45`）→ 浮窗 `PrepareExitAsync()` 失败时询问重试/丢弃/取消（`:47-59`）→ 接受后禁用窗口并在 `ContextIdle` 调度 `FinishExitAsync`（`:60-62`）。
- `FinishExitAsync` → `CleanupAsync`（`:103-113`）顺序固定：`presence.StopAsync()` → `shell.DisposeAsync()` → `Bootstrapper.DisposeAsync()`，三者异常聚合成 `AggregateException`（清理超过 10 秒会弹窗询问继续等待或强制退出，`:80-91`）。
- `Bootstrapper.DisposeAsync`（`Bootstrapper.cs:100-123`）内部顺序也固定：
  1. `ISettingsCore.PrepareSourceStopAsync("shell")`，失败则抛 `InvalidOperationException`（`:104-108`）；
  2. 释放 shell 的设置源注册与全部 UI 贡献注册（`:109-113`）；
  3. `ModuleRuntime.DisposeAsync()` → 逆序 `ModuleStopping` + `StopAsync` + `DisposeAsync`（`:114`）；
  4. `IHost.StopAsync()` 冲刷日志，随后 `Dispose`（`:117-121`）。
- 模块必须在 `StopAsync` 后不再持有 UI 引用；`ModuleStopping` 时宿主按模块撤销注册（`Bootstrapper.cs:128-131`）。

## 10. 诊断与日志

- 启动阶段失败：`App.xaml.cs:70-93` 捕获，`ReportFailure`（`:96-105`）写 `Logs` 文件日志；主窗口不出现，诊断窗口对 `ConfigurationException` 原样显示其消息并提示修正配置，其它异常显示通用文案；关闭诊断窗口后退出（`:83-93`）。
- 运行阶段：模块通过注入的 `IDiagnosticSink` 上报，不要直接写文件。
- 日志位置 `%LOCALAPPDATA%\Mosaic\Logs`。`logging.level` 的可选值是 `Debug`、`Information`、`Warning`、`Error`、`None`（默认 `Information`，`Mosaic.Desktop/HostSettingsCatalog.cs:40-41`）；`logging.retentionDays` 范围 0–365，0 表示不清理（`:47-48`，`FileLogging.cs:38-39` 只删除早于截止日的日志）。
- 用户可见文案必须本地化；诊断文案可以中文原文。

## 11. 新增模块装配清单

1. 在 `BuiltInModuleCatalog.Entries` 追加一条（`BuiltInModuleCatalog.cs:76-245`）。字典顺序不影响启动顺序：工厂按 `ModuleGraph.Resolve` 的拓扑序调用（`Bootstrapper.cs:79`），`ModuleRuntimeContext` 字段之间的先后由 `Requires` 保证——例如 `TasksModule` 声明 `tasks.repository@1`（`TasksModule.cs:61`），任务仓储的工厂才一定先执行并写入 `context.Repository`（`BuiltInModuleCatalog.cs:94-111`）。不要依赖 `Entries` 的书写顺序，也不要让工厂读取一个没有 `Requires` 声明的字段。
2. `Definitions`、`DefaultModules`、`DefaultProviders` 由 `Entries` 自动派生（`:247-256`），不要手写；新增的 `Provides` 能力会自动带上 `能力@版本=模块ID` 的默认绑定。
3. 让模块出现在实际启动列表里：`settings` 段的 `host.modules` / `host.providers` 是权威值，没有它们时才回退到 `appsettings.json` 顶层的 `modules` / `providers`（`Bootstrapper.cs:44-45`）。新安装由模板带出默认值；已有安装要写配置迁移（下一步），不能依赖自动追加。
4. `RepairProviderBindings`（`AppConfiguration.cs:44`，实现 `:177-205`）在迁移**之前**运行：只补当时已启用模块的缺失绑定、绝不覆盖用户已设的绑定，且在有 `host.*` 覆盖时写回 `settings["host.providers"]`。迁移新增的模块不在此列——它的绑定必须由迁移用 `TryAdd` 写入（参照 `:228-229`、`:368-369`）；只加模块不加绑定，会在 `:64` 抛 `Provider binding is missing or incompatible`。
5. 需要配置迁移时在 `AppConfiguration` 增加迁移并加入 `KnownMigrations`（`AppConfiguration.cs:22`），迁移后执行带依赖校验的完整校验（`:62-65`）。
6. 更新 `build/project-boundaries.json`、`build/public-api.txt`、`BoundaryTests` 白名单/归属表。
7. 运行 `build/test.ps1 -Configuration Release`，确认桌面冒烟测试通过。

## 12. 已知限制

- 无运行时插件加载；新增模块必须重新编译宿主。
- 无模块热卸载；`StopAsync` 之后进程内不能重启同一模块。
- 能力只有主版本匹配，没有次版本协商。
- 配置 `modules` 的精简（禁用模块）可能导致依赖缺失，启动会明确报错；设置页对此标注“禁用模块可能使依赖或绑定无效”。

---

相关文档：[插件规范](plugin-spec.md) · [边界声明](boundary-declaration.md) · [UI 规范](ui-spec.md) · [插件制作教程](README.md)