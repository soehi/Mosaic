# 插件规范

适用范围：Mosaic 0.18.13，模块契约版本 `1`。

## 0. 结论先行

Mosaic 的“插件”就是**模块（Module）**：一组实现 `IModuleActivation` 的程序集，声明自己的 ID、提供的能力、依赖的能力和职责边界，由宿主在启动时装配、排序、启停。

0.18.13 的插件形态是**编译期插件**：插件源码与宿主一起编译，宿主通过 `BuiltInModuleCatalog` 显式登记每个模块的工厂。当前**没有**运行时 DLL 扫描、热插拔、插件市场或外部包安装能力（见 [架构原则](../architecture-principles.md) 与 [实施计划](../implementation-plan.md)）。本文同时作为将来外部加载器必须满足的契约基线。

| 能做什么 | 不能做什么（0.18.13） |
| --- | --- |
| 新增一个 `src/Modules/[PLUGIN-ID]/` 模块工程并编进解决方案 | 把任意第三方 DLL 丢进程序目录后被自动加载 |
| 声明并提供新能力，供其他模块通过构造函数依赖 | 通过反射、`Assembly.Load`、服务定位器拿到别的模块 |
| 贡献导航、页面、设置页、状态栏条目、伴生视图 | 直接读写其他模块的数据库表或内部类型 |
| 通过设置系统注册自己的设置项 | 绕过设置系统直接改 `appsettings.json` |
| 注册本地化资源与主题资源 | 在模块内部自建一套颜色/字体体系 |

## 1. 术语

| 术语 | 含义 | 契约位置 |
| --- | --- | --- |
| 模块 / 插件 | 一个可独立启停的职责单元 | `IModuleActivation` |
| 清单 | 模块的 ID、能力、职责、生命周期、入口版本 | `ModuleManifest` |
| 能力 | 模块对外暴露的协议，形如 `name@major` | `CapabilityId` |
| 提供者绑定 | 能力到唯一模块 ID 的映射 | `settings` 段的 `host.providers`（权威），顶层 `providers` 为回退 |
| 宿主 | 读取配置、装配模块、创建窗口的程序 | `src/Mosaic.Desktop` |
| 内核 | 排序、校验、启停、执行门 | `src/Mosaic.Kernel` |
| 贡献 | 模块交给界面层的导航/视图/状态等描述 | `Mosaic.Presentation.Contracts` |

## 2. 分层与依赖方向

```
┌───────────────────────────────────────────────┐
│ 宿主 Mosaic.Desktop                           │
│  配置读取 / BuiltInModuleCatalog / DI / 窗口    │
└───────────────┬───────────────────────────────┘
                │ 只依赖清单与公开契约
┌───────────────▼───────────────────────────────┐
│ 内核 Mosaic.Kernel                            │
│  ModuleGraph / ModuleRuntime / ExecutionGate  │
└───────────────┬───────────────────────────────┘
                │
┌───────────────▼───────────────────────────────┐
│ 契约 Mosaic.Contracts                          │
│  ModuleManifest / CapabilityId / IModule...    │
└───────────────────────────────────────────────┘

模块（插件）：Mosaic.[Plugin].Contracts ← Mosaic.[Plugin].Core ← Mosaic.[Plugin].Wpf
             依赖方向只能指向契约，不能指向宿主、Shell 或其他模块的实现。
```

强制规则：

1. 模块之间只通过**公开契约程序集**通信。`Mosaic.[A].Contracts` 可以被 `Mosaic.[B].Core` 引用，`Mosaic.[A].Core` 不可以被 `Mosaic.[B].*` 引用。
2. 模块不得引用 `Mosaic.Desktop`、`Mosaic.Shell.Wpf` 或任何 `Microsoft.Extensions.*`。唯一例外是宿主的组合根。
3. 模块不得使用 `IServiceProvider`、`GetRequiredService`、`IHost`（架构测试会扫描源码）。
4. 模块不得出现 `Assembly.Load`、`Type.GetType`、`Activator.CreateInstance`、`ServiceLocator`。
5. 执行 SQL 的代码只能出现在架构测试白名单里的 SQLite 工程内：`Mosaic.Storage.Sqlite`、`Mosaic.Tasks.Sqlite`、`Mosaic.Settings.Sqlite`、`Mosaic.FloatingWindows.Sqlite`（`BoundaryTests.cs:94-95` 按目录名硬编码）。新增 `Mosaic.[PLUGIN].Sqlite` 时必须把该目录加入这条白名单，否则 `CREATE TABLE` 等语句会被判违规。
6. `Mosaic.Tasks.Core`、`Mosaic.Kernel`、`Mosaic.DesktopPresence.Core` 不得引用 `System.Windows`，保持可跨平台单测。

## 3. 清单字段规范

定义位置：`src/Mosaic.Contracts/ModuleManifest.cs`。

```csharp
public sealed record ModuleManifest(
    string Id,
    IReadOnlyList<CapabilityId> Provides,
    IReadOnlyList<CapabilityId> Requires,
    string Responsibility = "unspecified",
    ModuleLifecycle Lifecycle = ModuleLifecycle.ActivateStopDispose,
    int EntryVersion = 1);
```

| 字段 | 约束 | 校验位置 |
| --- | --- | --- |
| `Id` | 命名约定：≤96 字符，首字符为小写 ASCII 字母，其余为 ASCII 字母（含大写 `A-Z`）、数字或 `-`（`SettingValueFormat.IsModuleId`，`SettingsContracts.cs:173-177`，用 `char.IsAsciiLetterOrDigit` 判定，作用于 `host.modules` 配置项）；`ModuleGraph.Resolve` 本身只拒绝空值与重复（`ModuleGraph.cs:13`） |
| `Provides` | 运行时只校验：非空、名称不含 `@`、主版本 ≥ 1（`ModuleGraph.cs:46-48`）。命名约定 `name@major`，名称 ≤160 字符、按 `.` 分段：每段首字符必须是小写 ASCII 字母，段内其余字符允许 ASCII 字母（含大写 `A-Z`）、数字和 `-`（`SettingValueFormat.IsCapabilityId`，`SettingsContracts.cs:179-192`），在配置键校验时生效 |
| `Requires` | 同上；按 `CapabilityId` 精确匹配名称与主版本，没有通配符和版本区间（`ModuleGraph.cs:34-38`） |
| `Responsibility` | 非空，一句中文或英文，写“拥有什么、不拥有什么” | `ModuleGraph.Resolve` |
| `Lifecycle` | 0.18.13 只允许 `ActivateStopDispose` | `ModuleGraph.Resolve` |
| `EntryVersion` | ≥ 1；模块自身行为/数据语义版本，不替代程序集版本 | `ModuleGraph.Resolve` |

命名建议：

- ID 用职责命名，不用公司前缀堆叠：`tasks-core`、`settings-ui`、`[PLUGIN-ID]`。
- 能力名从拥有者出发：`tasks.api`、`tasks.changes`、`[PLUGIN-ID].api`。
- 破坏性变更必须提升主版本：`[PLUGIN-ID].api@1` → `[PLUGIN-ID].api@2`，旧能力可在一个发布周期内并存，不能原地改语义。
- 只增字段、且新成员有默认值，视为兼容变更，不提升主版本。

## 4. 生命周期语义

`ModuleRuntime` 的规则（`src/Mosaic.Kernel/ModuleRuntime.cs`）：

1. **单次使用**。`StartAsync` 只能成功调用一次；停止后不可重启。
2. **清单必须一致**。工厂返回的模块实例的 `Manifest` 必须与排好序的清单逐字段一致（ID、职责、生命周期、入口版本、能力集合），否则抛错并释放该实例。
3. **顺序激活**。按依赖拓扑序调用 `ActivateAsync`；提供方永远先于依赖方（`Requires` 的声明方）启动完毕（`ModuleGraph.cs:34-41`）。
4. **失败回滚**。任一模块激活失败，已启动的模块按**逆序**执行 `StopAsync` + `DisposeAsync`。
5. **逆序停止**。`StopAsync` 先触发 `ModuleStopping`（界面贡献在此撤销），再 `StopAsync`，最后 `DisposeAsync`；所有异常聚合为 `AggregateException`，不中断其余模块的清理。
6. **`ActivateAsync` 必须可等待完成**。激活结束后模块应处于“可接受调用”状态；不要在这里启动无法取消的后台死循环。
7. **`StopAsync` 必须排空在途调用**。使用 `ModuleExecutionGate` 的做法见下节。
8. **`DisposeAsync` 必须幂等**。重复释放不得抛错。

### 执行门（`ModuleExecutionGate`）

对外 API 的每个入口都应取租约：

```csharp
public sealed class GuardedNotesApi(INotesApi inner, IModuleExecutionGate gate) : INotesApi
{
    public async Task<Result> DoAsync(CancellationToken ct = default)
    {
        using var lease = gate.TryEnter()
            ?? throw new InvalidOperationException("notes-core 已停止。");
        return await inner.DoAsync(ct);
    }
}
```

- `Activate()` 只能调用一次；停止后 `TryEnter()` 返回 `null`。
- 停止流程先 `StopAsync()` 拒绝新租约，再等待已有租约释放，最后释放资源。
- 内置模块的写法可参考 `src/Modules/Tasks/Mosaic.Tasks.Core/Internal/GuardedTasksApi.cs`。

## 5. 能力导出与绑定

### 5.1 宿主配置中的两条声明

配置文件是 `%LOCALAPPDATA%\Mosaic\Config\appsettings.json`（或用 `--data-root [PATH]` 指定的独立工作区）。模块列表与绑定有两层，启动时按下面的优先级取（`Bootstrapper.cs:44-45`）：

```json
{
  "modules": ["[PLUGIN-CORE-ID]", "[PLUGIN-UI-ID]"],
  "providers": { "[PLUGIN-ID].api@1": "[PLUGIN-CORE-ID]" },
  "settings": {
    "host.modules": ["[PLUGIN-CORE-ID]", "[PLUGIN-UI-ID]"],
    "host.providers": { "[PLUGIN-ID].api@1": "[PLUGIN-CORE-ID]" }
  }
}
```

- `settings["host.modules"]` / `settings["host.providers"]` 是**权威值**，设置页“启用模块”“能力绑定”两项写的就是它们（`HostSettingSource.PersistAsync` 写 `root["settings"]`，`HostSettingSource.cs:84-90`）。
- 顶层 `modules` / `providers` 是程序目录模板 `appsettings.json` 的默认值，也是 `host.*` 不存在时的兼容回退。
- 模板里没有 `settings` 段；首次启动按顶层值工作，用户保存过一次设置后以 `host.*` 为准。

规则（`ModuleGraph.Resolve`，`src/Mosaic.Kernel/ModuleGraph.cs:13-48`；宿主先用 `HostConfigurationValidator` 做同语义校验，失败消息前缀见 §8）：

1. `modules` 中每个 ID 必须唯一、非空，且在 `BuiltInModuleCatalog.Definitions` 中登记（`HostConfigurationValidator.cs:13-21`）。
2. 每个模块 `Provides` 的每个能力，**必须**在 `providers` 中出现，且绑定值等于该模块自己的 ID。**已启用**模块缺失的绑定每次启动由 `RepairProviderBindings` 自动补上（`AppConfiguration.cs:177-205`），所以给现有模块新增能力不需要写配置迁移；迁移**新启用**的模块不在此列，它的绑定必须随迁移写入（见 §7 第 2 条）。
3. 同一个能力只能有一个提供者；重复提供直接启动失败。
4. `Requires` 的能力必须由某个已启用模块精确提供，否则抛 `InvalidOperationException`，消息为 `Missing capability or incompatible version: {required}`（逐字串见 [框架规范 §2](framework-spec.md#2-模块图解析)）。
5. 依赖图不得有环；结果按拓扑序返回。
6. `providers` 中任何一项无效——键格式错误、值为空、指向未登记模块、指向未启用模块，或指向的模块并不 `Provides` 该能力——都会让启动失败，报 `能力绑定无效：{键列表}`（`HostConfigurationValidator.cs:23-38`）。反过来，一项绑定只要形式合法、指向已启用且确实提供该能力的模块，即使没有任何模块 `Requires` 它也能通过校验；那属于冗余配置，应清理。

### 5.2 能力解析的两种方式

- **内置宿主（当前）**：`BuiltInModuleCatalog` 在组合根里按依赖顺序构造具体对象，并把实例放进 `ModuleRuntimeContext`；模块工厂拿到的是具体接口，不经过反射。
- **内核绑定解析（契约能力）**：`ModuleRuntime.Resolve<T>(capability, bindings)` 可按绑定找到活动模块并调用 `ICapabilityProvider.GetCapability`。这条路径由内核测试使用，也是未来外部加载器的标准解析方式。

模块若希望支持第二种方式，需实现 `ICapabilityProvider` 并返回与清单一致的对象：

```csharp
public sealed class NotesModule : IModuleActivation, ICapabilityProvider
{
    private readonly INotesApi _api = new NotesApi();
    public object? GetCapability(CapabilityId capability) =>
        capability == new CapabilityId("notes.api", 1) ? _api : null;
}
```

`ModuleRuntime.Resolve<T>` 会校验三件事：绑定存在、模块正在运行、导出对象类型匹配；任一不满足都抛 `InvalidOperationException`，不做隐式降级。

## 6. 依赖注入规范

- 依赖通过**构造函数**显式传入，参数类型必须是公开契约或 BCL 类型。
- 依赖集合用 `sealed record [PLUGIN]Dependencies(...)` 聚合，可选依赖用可空参数并给出默认值。
- 不允许静态服务定位、不允许模块内部再建容器、不允许把 `IServiceProvider` 存进字段。
- 允许使用的宿主能力：`IUiDispatcher`、`IUserDialogService`、`IDiagnosticSink`、`TimeProvider`、`IIdGenerator`、`IDatabaseSession`（由 `Mosaic.Storage.Contracts` 定义）。
- 需要共享状态的模块，把状态放进自己的 Core，通过契约暴露操作，不暴露可变集合。

## 7. 配置、设置与迁移

- 插件若需要用户可配置项，必须通过 `ISettingsCore.Register(ISettingSource)` 注册（见 [框架规范 §7](framework-spec.md#7-设置系统)）。
- 插件不得直接写 `appsettings.json`。宿主配置只由宿主写回。
- 插件引入新的宿主级配置迁移时，必须：
  1. 在 `AppConfiguration` 增加幂等迁移，并加入 `KnownMigrations`（`AppConfiguration.cs:22`）；
  2. 迁移必须**同时**改有效模块列表与绑定，并写回原来那一层：存在 `settings["host.modules"]` / `settings["host.providers"]` 时改它们，否则改顶层 `modules` / `providers`（参照 `ApplyPresenceMigration`，`AppConfiguration.cs:207-249`）；绑定用 `Dictionary.TryAdd` 只补缺失键、不覆盖已有值（参照 `AppConfiguration.cs:228-229`、`:368-369`）。`RepairProviderBindings`（`AppConfiguration.cs:44`）在迁移链（`:48-60`）**之前**只执行一次，只能补当时已启用模块的绑定；迁移新启用的模块若不在迁移里写绑定，`:64` 的 `ValidateCoreConfiguration(…, validateDependencies: true)` 会抛 `模块依赖或能力绑定无效：Provider binding is missing or incompatible: …`，配置不写回，每次启动重复失败；
  3. 把迁移 ID 写入 `appliedMigrations`（`MarkMigration`，`:382-386`，按 `Distinct` 去重）；
  4. 在 `docs/implementation-status.md` 写明对已有安装的影响与验收项。
- 插件新增数据库表只能在自己的 Sqlite 工程中建表，并遵守“先建表后使用、可重复执行”的迁移约定。

## 8. 失败与诊断

| 场景 | 行为 |
| --- | --- |
| 清单非法 / 重复 ID / 能力重复 | 启动失败，显示配置错误，不进入主窗口 |
| 已启用模块的能力未绑定 | 下次启动由 `RepairProviderBindings`（迁移之前）自动补上，不报错；绑定指向错误模块则启动失败，错误信息含能力名。迁移**新启用**的模块不在此列：绑定必须随迁移写入，否则启动失败（见 §7） |
| 依赖缺失或主版本不匹配 | 启动失败，错误信息含能力名 |
| 激活抛异常 | 已启动模块逆序回滚，异常上抛 |
| 配置 JSON 损坏 | 启动失败；原文件保留，不被覆盖 |
| 运行期模块内部异常 | 记录到 `IDiagnosticSink` 与滚动日志；不得静默吞掉 |

诊断约定：

- `IDiagnosticSink.Report(correlationId, exception)` 用于启动失败与不可恢复错误。
- 日志目录 `%LOCALAPPDATA%\Mosaic\Logs`，保留天数由宿主设置控制。
- 用户可见错误必须可操作：说明哪个模块、哪个能力、下一步做什么。

## 9. 测试与验收规范

每个插件至少交付三类测试：

1. **单元测试**：`tests/Mosaic.[PLUGIN].Tests`，覆盖 Core 的状态机、边界条件、错误分支。
2. **契约测试**：能力对象的行为是否符合契约；错误码是否稳定。
3. **架构测试**：`tests/Mosaic.Architecture.Tests` 全绿，包括边界、公开 API 清单、设置归属。

验收命令：

```powershell
dotnet build Mosaic.sln -c Release -m:1
build/test.ps1 -Configuration Release
```

判定标准：

- 构建 0 警告（仓库开启 `TreatWarningsAsErrors`）。
- 全部测试通过；桌面冒烟测试必须串行（`tests/Mosaic.Desktop.SmokeTests/GlobalUsings.cs:3-5` 关掉了 xUnit 并行，因为这些用例会启动真实进程、抢前台窗口与鼠标）。
- 新模块出现在 `build/project-boundaries.json` 与 `build/public-api.txt`。
- 新增/修改的用户可见行为写入 `docs/implementation-status.md` 和对应版本发布说明。

## 10. 禁止事项清单

- 禁止模块引用宿主或 Shell 实现。
- 禁止在 `src` 下的 `.cs` 源码中出现 `InternalsVisibleTo`（`BoundaryTests.cs:88-91` 逐文件扫描）；测试需要访问内部类型时，把类型改为 `public` 并登记 `build/public-api.txt`，不要用可见性开关绕开约束。
- 禁止跨模块直接访问数据库、文件路径或缓存。
- 禁止在 Core 中使用 `System.Windows`。
- 禁止在非 Sqlite 工程中写 SQL。
- 禁止动态加载程序集或反射构造类型。
- 禁止绕过 `ModuleExecutionGate` 暴露可停止的 API。
- 禁止在清单里声明自己并不真正提供的能力。
- 禁止把未绑定到 `providers` 的能力留给“运行时再说”。

## 11. 提交前检查表

- [ ] `ModuleManifest` 六项字段齐全，ID/能力格式合法。
- [ ] `Provides` 每个能力都已写入 `providers` 并指向自己。
- [ ] `Requires` 的能力在默认模块集合中真实存在且主版本一致。
- [ ] 模块已加入 `BuiltInModuleCatalog.Entries`（`Definitions`/`DefaultModules`/`DefaultProviders` 自动派生），并出现在 `host.modules` 或顶层 `modules` 中。
- [ ] 边界声明与公开 API 清单已更新。
- [ ] 激活/停止/释放路径有测试，停止后入口返回明确错误。
- [ ] 用户可见行为、根因位置、改动位置写入实施记录。

---

相关文档：

- [插件制作教程](README.md)
- [边界声明](boundary-declaration.md)
- [框架规范](framework-spec.md)
- [UI 规范](ui-spec.md)
- [架构原则](../architecture-principles.md)