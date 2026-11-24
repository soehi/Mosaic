# 插件制作教程

本教程带你从零做一个能在 Mosaic 里运行的插件。读完你会得到：

- 一个包含契约、核心、持久化、界面四层工程的可编译插件；
- 一条可验证的启动路径（能力绑定 → 模块图 → 启停）；
- 一个导航页面和一组设置项；
- 一套通过架构测试与单元测试的提交。

> 术语约定：Mosaic 里“插件”等于“模块”。当前版本（0.18.13）是**编译期插件**，插件与宿主一起编译、由宿主显式登记。没有运行时 DLL 加载与热插拔。

---

## 1. 先看它能做什么

| 能力 | 入口 |
| --- | --- |
| 启动/停止时执行自己的逻辑 | `IModuleActivation` |
| 向其他模块提供 API | `Provides` + 公开契约接口 |
| 依赖其他模块的 API | `Requires` + 构造函数注入 |
| 在左侧导航加一个页面 | `IUiModule.Navigation` + `Views` |
| 在设置窗口加一页 | `SettingsPageRegistry.Register` |
| 在状态栏/侧栏显示状态 | `IStatusContribution` |
| 提供浮窗/速览内容 | `ICompanionViewFactory` |
| 注册自己的设置项 | `ISettingsCore.Register(ISettingSource)` |
| 本地化与主题 | `MosaicLocalizationManager` / `MosaicTheme` |

完整规则见 [插件规范](plugin-spec.md)。

## 2. 准备环境

```powershell
dotnet --version          # 需要 .NET 10 SDK
git clone <repo>          # 已有工作区可跳过
cd D:\Code\Mosaic
dotnet build Mosaic.sln -c Debug -m:1
build/test.ps1 -Configuration Debug -SkipDesktop
```

先确保基线全绿，再动代码。

## 3. 第 1 步：建四个工程

以插件 ID `notes` 为例。目录与命名必须一一对应，架构测试按工程名检查边界。SQL 只允许出现在 `BoundaryTests.cs:94` 白名单里的四个 SQLite 目录，所以 Core 不碰数据库，另建 `Mosaic.Notes.Sqlite`，**并把它的目录加进那条白名单**（名单按目录名硬编码，不会自动识别新工程）。

```powershell
dotnet new classlib -n Mosaic.Notes.Contracts -o src/Modules/Notes/Mosaic.Notes.Contracts -f net10.0
dotnet new classlib -n Mosaic.Notes.Core      -o src/Modules/Notes/Mosaic.Notes.Core      -f net10.0
dotnet new classlib -n Mosaic.Notes.Sqlite    -o src/Modules/Notes/Mosaic.Notes.Sqlite    -f net10.0
dotnet new classlib -n Mosaic.Notes.Wpf       -o src/Modules/Notes/Mosaic.Notes.Wpf

dotnet sln Mosaic.sln add src/Modules/Notes/Mosaic.Notes.Contracts/Mosaic.Notes.Contracts.csproj
dotnet sln Mosaic.sln add src/Modules/Notes/Mosaic.Notes.Core/Mosaic.Notes.Core.csproj
dotnet sln Mosaic.sln add src/Modules/Notes/Mosaic.Notes.Sqlite/Mosaic.Notes.Sqlite.csproj
dotnet sln Mosaic.sln add src/Modules/Notes/Mosaic.Notes.Wpf/Mosaic.Notes.Wpf.csproj
```

删除模板生成的 `Class1.cs`。三个非 WPF 工程还要删掉模板自带的 `<TargetFramework>` 行（`Directory.Build.props` 已统一设为 `net10.0`）；WPF 工程反过来要显式写 `net10.0-windows`，否则 `UseWPF` 无法生效。然后按下文设置引用。

`Mosaic.Notes.Contracts.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\..\Mosaic.Contracts\Mosaic.Contracts.csproj" />
  </ItemGroup>
</Project>
```

`Mosaic.Notes.Core.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\..\Mosaic.Contracts\Mosaic.Contracts.csproj" />
    <ProjectReference Include="..\Mosaic.Notes.Contracts\Mosaic.Notes.Contracts.csproj" />
    <ProjectReference Include="..\..\Settings\Mosaic.Settings.Contracts\Mosaic.Settings.Contracts.csproj" />
  </ItemGroup>
</Project>
```

`Mosaic.Notes.Sqlite.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\..\Mosaic.Contracts\Mosaic.Contracts.csproj" />
    <ProjectReference Include="..\Mosaic.Notes.Contracts\Mosaic.Notes.Contracts.csproj" />
    <ProjectReference Include="..\..\Storage\Mosaic.Storage.Contracts\Mosaic.Storage.Contracts.csproj" />
  </ItemGroup>
</Project>
```

`Mosaic.Notes.Wpf.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\Mosaic.Contracts\Mosaic.Contracts.csproj" />
    <ProjectReference Include="..\Mosaic.Notes.Contracts\Mosaic.Notes.Contracts.csproj" />
    <ProjectReference Include="..\..\..\Mosaic.Presentation.Contracts\Mosaic.Presentation.Contracts.csproj" />
    <ProjectReference Include="..\..\..\Mosaic.Presentation.Wpf\Mosaic.Presentation.Wpf.csproj" />
    <PackageReference Include="CommunityToolkit.Mvvm" />
  </ItemGroup>
</Project>
```

版本、可空、警告即错误由根目录 `Directory.Build.props` 统一提供，工程里不要重复写。

## 4. 第 2 步：定义公开契约

`src/Modules/Notes/Mosaic.Notes.Contracts/NotesContracts.cs`：

```csharp
using Mosaic.Contracts;

namespace Mosaic.Notes.Contracts;

public sealed record NoteItem(string Id, string Title, string Body, DateTimeOffset UpdatedAt);

public interface INotesApi
{
    Task<OperationResult<IReadOnlyList<NoteItem>>> ListAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<NoteItem>> CreateAsync(string title, string body, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteAsync(string id, CancellationToken cancellationToken = default);
}

// 仓储契约：由 Mosaic.Notes.Sqlite 实现，Core 只面向这个接口编程。
public interface INotesRepository
{
    Task<IReadOnlyList<NoteItem>> ListAsync(CancellationToken cancellationToken = default);
    Task AddAsync(NoteItem item, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default);
}
```

约定：

- 契约只放 DTO、接口、错误码；不放实现、不放数据库类型、不引用 WPF。
- 返回值优先 `OperationResult<T>`（定义在 `Mosaic.Contracts`），不要用异常表达业务失败。
- 方法都带 `CancellationToken`，默认值 `default`。
- 仓储契约与 API 同放一个契约工程是允许的：架构测试只检查「谁引用了谁」，不规定接口放在哪个工程。任务模块把持久化契约拆到 `Mosaic.Tasks.Persistence.Contracts`，是为了让 Core 与 Sqlite 共用接口而不让 UI 工程直接看到仓储；本篇为简洁只建一个契约工程，两种结构都合规。

## 5. 第 3 步：实现模块清单

`src/Modules/Notes/Mosaic.Notes.Core/NotesModule.cs`：

```csharp
using Mosaic.Contracts;
using Mosaic.Notes.Contracts;
using Mosaic.Settings.Contracts;

namespace Mosaic.Notes.Core;

public sealed record NotesDependencies(
    INotesRepository Repository,
    IDiagnosticSink Diagnostics,
    ISettingsCore Settings,
    IModuleExecutionGate Gate);

public sealed class NotesModule(NotesDependencies dependencies) : IModuleActivation, ICapabilityProvider
{
    public static ModuleManifest Definition { get; } = new(
        "notes-core",
        [new("notes.api", 1)],
        [new("notes.repository", 1), new("settings.api", 1)],
        "Owns notes rules and the notes API. Does not render UI or own persistence.");

    private INotesApi? _api;
    private IDisposable? _settingsRegistration;

    public ModuleManifest Manifest => Definition;
    public object? GetCapability(CapabilityId capability) =>
        capability == new CapabilityId("notes.api", 1) ? _api : null;

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = new NotesSettingsSource();
        _settingsRegistration ??= dependencies.Settings.Register(source);
        var values = await dependencies.Settings.GetValuesAsync(cancellationToken).ConfigureAwait(false);
        if (!values.IsSuccess)
            throw new InvalidOperationException(values.Error!.Message);
        await source.ApplyAsync(values.Value, cancellationToken).ConfigureAwait(false);
        _api = new GuardedNotesApi(new NotesApi(dependencies), dependencies.Gate, dependencies.Diagnostics);
        dependencies.Gate.Activate();
    }

    public async Task StopAsync()
    {
        await dependencies.Gate.StopAsync().ConfigureAwait(false);
        _api = null;
        _settingsRegistration?.Dispose();
        _settingsRegistration = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
```

三条硬约束：

- `ModuleManifest`、`ICapabilityProvider`、`IModuleExecutionGate` 都在 `Mosaic.Contracts`（`src/Mosaic.Contracts/ModuleManifest.cs:10,26,31`）。具体实现类 `ModuleExecutionGate` 在 `Mosaic.Kernel`，**Core 工程的边界不允许引用 `Mosaic.Kernel`**，门只能由宿主构造后经构造函数注入（对照 `src/Modules/Tasks/Mosaic.Tasks.Core/TasksModule.cs:8-15,32` 与 `src/Mosaic.Desktop/BuiltInModuleCatalog.cs:99-112`）。
- `Requires` 声明的是能力，不是工程：`notes.repository@1` 由 `Mosaic.Notes.Sqlite` 提供，`settings.api@1` 由 `settings-core` 提供。声明与绑定不一致时内核在启动前抛错。
- `Definition` 必须是**静态且只读**：`ModuleRuntime` 用 `Matches` 逐项比对工厂返回模块的 `Manifest` 与配置里的清单（ID、Responsibility、Lifecycle、EntryVersion、Provides/Requires 的集合相等），不一致直接拒绝启动（`src/Mosaic.Kernel/ModuleRuntime.cs:34-38,69-79`）。

## 6. 第 4 步：实现 API、仓储与迁移

### 6.1 核心层：API 与执行门

`src/Modules/Notes/Mosaic.Notes.Core/NotesApi.cs`：

```csharp
using Mosaic.Contracts;
using Mosaic.Notes.Contracts;

namespace Mosaic.Notes.Core;

internal sealed class NotesApi(NotesDependencies dependencies) : INotesApi
{
    private const string NoteNotFound = "NOTES_NOT_FOUND";

    public async Task<OperationResult<IReadOnlyList<NoteItem>>> ListAsync(CancellationToken cancellationToken = default)
    {
        var items = await dependencies.Repository.ListAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult<IReadOnlyList<NoteItem>>.Success(items);
    }

    public async Task<OperationResult<NoteItem>> CreateAsync(string title, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return OperationResult<NoteItem>.Failure(new(ErrorCodes.InvalidRequest, "标题不能为空。", Field: "title"));
        var item = new NoteItem(Guid.NewGuid().ToString("N"), title.Trim(), body, DateTimeOffset.UtcNow);
        await dependencies.Repository.AddAsync(item, cancellationToken).ConfigureAwait(false);
        return OperationResult<NoteItem>.Success(item);
    }

    public async Task<OperationResult> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var removed = await dependencies.Repository.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
        return removed
            ? OperationResult.Success()
            : OperationResult.Failure(new(NoteNotFound, "笔记不存在。", Field: "id"));
    }
}
```

`GuardedNotesApi.cs`——租约必须覆盖整段异步操作：

```csharp
internal sealed class GuardedNotesApi(INotesApi inner, IModuleExecutionGate gate, IDiagnosticSink diagnostics) : INotesApi
{
    private async Task<OperationResult<T>> Invoke<T>(Func<Task<OperationResult<T>>> operation)
    {
        using var lease = gate.TryEnter();
        if (lease is null)
            return OperationResult<T>.Failure(new(ErrorCodes.CapabilityUnavailable, "笔记功能尚未启用或正在关闭。"));
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            diagnostics.Report(correlationId, ex);
            return OperationResult<T>.Failure(new(ErrorCodes.InternalError, "操作未完成，请重试。", CorrelationId: correlationId));
        }
    }

    public Task<OperationResult<IReadOnlyList<NoteItem>>> ListAsync(CancellationToken ct = default) =>
        Invoke(() => inner.ListAsync(ct));

    public Task<OperationResult<NoteItem>> CreateAsync(string title, string body, CancellationToken ct = default) =>
        Invoke(() => inner.CreateAsync(title, body, ct));

    public async Task<OperationResult> DeleteAsync(string id, CancellationToken ct = default)
    {
        var result = await Invoke(async () =>
        {
            var deleted = await inner.DeleteAsync(id, ct).ConfigureAwait(false);
            return deleted.IsSuccess ? OperationResult<bool>.Success(true) : OperationResult<bool>.Failure(deleted.Error!);
        }).ConfigureAwait(false);
        return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(result.Error!);
    }
}
```

对照 `GuardedTasksApi`（`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/GuardedTasksApi.cs:10-24`）：`using var lease` 写在 `await` 外层。写成 `using var lease = gate.TryEnter() ?? throw …; return inner.ListAsync(ct);` 会在查询真正开始前释放租约，模块停机后旧页面仍能拿到写权限。异常必须由门统一转成 `OperationResult` 并上报 `IDiagnosticSink`，不要把异常抛给调用方。

错误码：`ErrorCodes` 只提供通用码，其中 `ErrorCodes.NotFound` 的字符串是 `TASK_NOT_FOUND`（`src/Mosaic.Contracts/OperationResult.cs:14`），属任务模块专用，不要复用；模块自有的失败码在自己的程序集里定义常量。

### 6.2 持久化层：仓储与迁移

SQL 只能出现在 `*.Sqlite` 工程——`BoundaryTests.cs:88-98` 只扫描 `src` 下的文件（`bin`/`obj` 除外），对路径不含白名单目录名的文件匹配 `SELECT … FROM`、`INSERT INTO`、`UPDATE … SET`、`CREATE TABLE`、`DELETE FROM`，命中即失败；测试工程不在扫描范围内，但 `IDatabaseSession.MigrateAsync` 的调用同样只应出现在 Sqlite 模块，Core 不碰会话接口。白名单按目录名硬编码为 `Mosaic.Storage.Sqlite`、`Mosaic.Tasks.Sqlite`、`Mosaic.Settings.Sqlite`、`Mosaic.FloatingWindows.Sqlite`，新增插件要把自己的 Sqlite 工程加进去。

`src/Modules/Notes/Mosaic.Notes.Sqlite/NotesSqliteModule.cs`：

```csharp
using Mosaic.Contracts;
using Mosaic.Notes.Contracts;
using Mosaic.Storage.Contracts;

namespace Mosaic.Notes.Sqlite;

public sealed class NotesSqliteModule(IDatabaseSession database) : IModuleActivation, ICapabilityProvider
{
    private static readonly MigrationDefinition[] Migrations =
    [
        new(1, """
            CREATE TABLE IF NOT EXISTS notes_items(
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                updated_at TEXT NOT NULL);
            """)
    ];

    private readonly SqliteNotesRepository _repository = new(database);
    private bool _active;

    public static ModuleManifest Definition { get; } = new(
        "notes-sqlite",
        [new("notes.repository", 1)],
        [new("sqlite.session", 1)],
        "Owns notes persistence mapping, SQL and database migrations.");

    public ModuleManifest Manifest => Definition;
    public object? GetCapability(CapabilityId capability) =>
        capability == new CapabilityId("notes.repository", 1) ? Repository : null;

    public INotesRepository Repository =>
        _active ? _repository : throw new InvalidOperationException("Notes repository is not active.");

    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        var result = await database.MigrateAsync("notes-sqlite", Migrations, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"笔记数据迁移失败：{result.Error!.Code} ({result.Error.CorrelationId})");
        _active = true;
    }

    public Task StopAsync() { _active = false; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { _active = false; return ValueTask.CompletedTask; }
}
```

`Internal/SqliteNotesRepository.cs`：

```csharp
using System.Globalization;
using Mosaic.Notes.Contracts;
using Mosaic.Storage.Contracts;

namespace Mosaic.Notes.Sqlite;

internal sealed class SqliteNotesRepository(IDatabaseSession database) : INotesRepository
{
    public async Task<IReadOnlyList<NoteItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await database.QueryAsync(new(
            "SELECT id, title, body, updated_at FROM notes_items ORDER BY updated_at DESC, id"),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
        return result.Value.Select(row => new NoteItem(
            row.String("id"),
            row.String("title"),
            row.String("body"),
            DateTimeOffset.Parse(row.String("updated_at"), CultureInfo.InvariantCulture))).ToArray();
    }

    public async Task AddAsync(NoteItem item, CancellationToken cancellationToken = default)
    {
        var result = await database.ExecuteAsync(new(
            "INSERT INTO notes_items(id,title,body,updated_at) VALUES($id,$title,$body,$updated)",
            new Dictionary<string, object?>
            {
                ["$id"] = item.Id,
                ["$title"] = item.Title,
                ["$body"] = item.Body,
                ["$updated"] = item.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)
            }), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        var result = await database.ExecuteAsync(new(
            "DELETE FROM notes_items WHERE id=$id",
            new Dictionary<string, object?> { ["$id"] = id }), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Error!.Message);
        return result.Value > 0;
    }
}
```

存储契约的硬约束（`src/Modules/Storage/Mosaic.Storage.Contracts/IDatabaseSession.cs`）：

- 参数只接受 `string`、`long`、`int`、`double`、`bool`（`:15-16`），所以 `DateTimeOffset` 按 `"O"` 格式化后存 `TEXT`，读回时用 `DateTimeOffset.Parse(text, CultureInfo.InvariantCulture)` 解析，保证时区不丢（`DateTimeOffset.Parse` 不接受 `DateTimeStyles.RoundtripKind`，传了会抛 `ArgumentException`）。
- 读结果只有按名访问器 `SqlRow.String(name)`、`SqlRow.Int64(name)`，以及按序号取原始值的 `Value(int)`（`IDatabaseSession.cs:45-47`），驱动类型不外泄。
- 迁移用 `MigrateAsync(moduleId, MigrationDefinition[], ct)`，返回 `OperationResult`（`IDatabaseSession.cs:50,71`，对照 `TasksSqliteModule.cs:33-35`）。版本必须从 1 连续编号，否则 `MigrationRunner.Apply` 抛 `Migrations must be contiguous from version one.`；已执行版本按 `(module_id, version, digest)` 记录在 `mosaic_migrations`，重复激活只校验摘要、不重放 SQL；改了已执行版本的 SQL 会抛 `Migration checksum or database version is incompatible.`（`MigrationRunner.cs:12-32`）。这两个异常在 `DatabaseWorker.Enqueue` 里被捕获并记入诊断，调用方拿到的是 `STORAGE_UNAVAILABLE` +「本机存储暂不可用，请重试。」（`DatabaseWorker.cs:117-124`），所以必须像示例那样检查 `IsSuccess` 并抛出。备份条件按代码原样：该模块已执行过迁移，或库里已有用户表（`:33-46`）——全新数据库首次建库不建备份；满足条件时按 `storage.backupRetention`（默认 5，0 关闭）先生成 `mosaic.sqlite.backup-<时间戳>.sqlite`，再进入升级事务。
- Sqlite 工程只引用 `Mosaic.Contracts`、`Mosaic.Notes.Contracts`、`Mosaic.Storage.Contracts`：`Microsoft.Data.Sqlite` 只允许出现在 `Mosaic.Storage.Sqlite`（`BoundaryTests.cs:54-57`），新工程不得自己引驱动；SQL 语句也只允许出现在四个白名单目录（`:94-95`；另在 `:96-97` 限制只有 `Mosaic.Settings.Sqlite` 能直接操作 `settings` 表），`Mosaic.Notes.Sqlite` 必须先加入白名单。
- 迁移 SQL 里仍建议写 `IF NOT EXISTS`：版本已记录时不会重放，手工删表不会自愈；但遇到从外部导入、`mosaic_migrations` 与表结构不一致的库，`IF NOT EXISTS` 能让升级走完而不是中断。

## 7. 第 5 步：登记到宿主

三处必须同步：模块目录、宿主配置（含升级迁移）、边界与公开 API。缺任何一处，启动或架构测试都会失败。

### 7.1 模块目录与运行时上下文

`src/Mosaic.Desktop/BuiltInModuleCatalog.cs` 两处改动。

**先给 `ModuleRuntimeContext` 加字段**（`:30-70`，与 `Repository` / `Tasks` 同级）：

```csharp
internal NotesSqliteModule? NotesRepository { get; set; }
internal NotesModule? Notes { get; set; }
```

**再在 `Entries` 里登记三个模块**（`:76-245` 的字典初始化器）。`Definitions`、`DefaultModules`、`DefaultProviders` 都由 `Entries` 自动推导（`:247-256`），不要再手改这三个：

```csharp
[NotesSqliteModule.Definition.Id] = new(NotesSqliteModule.Definition, context =>
{
    context.NotesRepository = new(context.Storage?.Session
        ?? throw new InvalidOperationException("Storage was not assembled before notes repository."));
    return context.NotesRepository;
}),
[NotesModule.Definition.Id] = new(NotesModule.Definition, context =>
{
    context.Notes = new(new(
        context.NotesRepository?.Repository
            ?? throw new InvalidOperationException("Notes repository was not assembled before notes core."),
        context.Diagnostics,
        context.Settings?.Api
            ?? throw new InvalidOperationException("Settings was not assembled before notes core."),
        new Mosaic.Kernel.ModuleExecutionGate()));
    return context.Notes;
}),
[NotesUiModule.Definition.Id] = new(NotesUiModule.Definition, context =>
    new NotesUiModule(new(
        context.Notes?.Api
            ?? throw new InvalidOperationException("Notes API was not assembled before notes UI."),
        context.Dispatcher,
        context.Dialogs,
        context.Diagnostics))),
```

- 工厂按 `ModuleGraph.Resolve` 排好的顺序调用（`src/Mosaic.Kernel/ModuleRuntime.cs:31-44`），每个模块在下一次工厂调用前已完成 `ActivateAsync`，所以 `context.Storage`、`context.Settings?.Api` 在依赖它的闭包里已可用。
- `Entries` 的书写顺序不影响启动顺序，依赖排序由 `Requires` 决定（`ModuleGraph.cs:26-44`）；但仍按 storage → repository → core → ui 排列，方便阅读。
- `context.Settings?.Api` 的类型是 `ISettingsCore`（`src/Modules/Settings/Mosaic.Settings.Core/SettingsModule.cs:38`），与 `NotesDependencies.Settings` 一致。
- 门一律在这里 `new Mosaic.Kernel.ModuleExecutionGate()`，与任务模块的写法相同（`:105`）。

### 7.2 宿主配置与升级迁移

`host.modules` 与 `host.providers` 是持久化的宿主设置；两个设置的默认值直接取自 `BuiltInModuleCatalog.DefaultModules` / `DefaultProviders`（`HostSettingsCatalog.cs:27-39`），缺值时的回退同样取这两个派生列表（`HostSettingSource.cs:60-65`）。

但**新安装的配置文件不是从这两个默认值生成的**：`AppConfiguration.Load` 在 `Config\appsettings.json` 不存在时直接拷贝程序目录模板（`AppConfiguration.cs:27`）。所以把三个新 ID 加进 `DefaultModules` 之后，还必须同步改 `src/Mosaic.Desktop/appsettings.json` 的顶层 `modules` / `providers`，否则全新安装看不到新功能。**已有安装保留自己的 `settings.host.modules`，不会自动追加**——这是 AGENTS.md 要求「影响模块装配的改动必须交付迁移逻辑与迁移验收项」的落点。

绑定**必须随迁移写入**，不能指望自动补齐：`RepairProviderBindings`（`AppConfiguration.cs:44`，实现 `:177-205`）在迁移链（`:48-60`）**之前**只运行一次，只能补「当时已启用模块」的缺失绑定，且从不覆盖用户已有条目。迁移新启用的模块对它来说不存在，绑定只能由迁移自己用 `TryAdd` 补（参照 `ApplyPresenceMigration` 的 `:228-229`）。漏写的结果是 `:64` 的 `ValidateCoreConfiguration(…, validateDependencies: true)` 抛 `模块依赖或能力绑定无效：Provider binding is missing or incompatible: notes.api@1`，配置不写回，每次启动重复失败。

在 `src/Mosaic.Desktop/AppConfiguration.cs` 里照 `PresenceMigration` 加一个迁移（`ApplyPresenceMigration`：`:207-249`；`MarkMigration`：`:382-386`）：

```csharp
private const string NotesMigration = "notes-v1";   // 与 :17-21 的常量并列

// KnownMigrations（:22）末尾追加 NotesMigration：迁移尚未执行时不做完整依赖校验
// Load 的迁移链（:48-60）末尾追加
if (!appliedMigrations.Contains(NotesMigration, StringComparer.Ordinal))
    configuration = ApplyNotesMigration(configuration);

private static AppConfiguration ApplyNotesMigration(AppConfiguration configuration)
{
    var settings = configuration.Settings is null
        ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        : new Dictionary<string, JsonElement>(configuration.Settings, StringComparer.Ordinal);
    var hasModuleOverride = settings.ContainsKey("host.modules");
    var hasProviderOverride = settings.ContainsKey("host.providers");
    var modules = ReadModuleOverride(configuration)?.ToList() ?? configuration.Modules.ToList();
    var providers = ReadProviderOverride(configuration) is { } providerOverride
        ? new Dictionary<string, string>(providerOverride, StringComparer.Ordinal)
        : new Dictionary<string, string>(configuration.Providers, StringComparer.Ordinal);
    var moduleChanged = false;
    var providerChanged = false;

    if (!modules.Contains("tasks-core", StringComparer.Ordinal))
        return configuration with { AppliedMigrations = MarkMigration(configuration, NotesMigration) };

    foreach (var id in new[] { "notes-sqlite", "notes-core", "notes-ui" })
    {
        if (modules.Contains(id, StringComparer.Ordinal)) continue;
        modules.Add(id);
        moduleChanged = true;
    }

    // 新启用模块的绑定必须在这里补齐：RepairProviderBindings 已在迁移之前跑过，看不到这些模块。
    providerChanged |= providers.TryAdd("notes.repository@1", "notes-sqlite");
    providerChanged |= providers.TryAdd("notes.api@1", "notes-core");

    if (moduleChanged && hasModuleOverride)
        settings["host.modules"] = JsonSerializer.SerializeToElement(modules.ToArray());
    if (providerChanged)
    {
        // 模块覆盖层可能启用了顶层基线下没有的模块，绑定要写回同一层，两份列表才自洽。
        if (hasModuleOverride || hasProviderOverride)
            settings["host.providers"] = JsonSerializer.SerializeToElement(providers);
    }

    return configuration with
    {
        Modules = hasModuleOverride ? configuration.Modules : modules.ToArray(),
        Providers = hasModuleOverride ? configuration.Providers : providers,
        Settings = settings,
        AppliedMigrations = MarkMigration(configuration, NotesMigration)
    };
}
```

五个要点：

1. **锚点条件**（示例用 `tasks-core` 已启用）——用户在设置里把整套功能关掉时，迁移不能把它重新打开。
2. **模块列表与绑定一起写**：`TryAdd` 只补缺失键，用户自定义绑定不被覆盖；漏写绑定会在写回前的完整校验里失败（见上一节）。
3. **覆盖层**：真正的模块列表可能来自 `settings["host.modules"]`，迁移必须改有效列表并写回同一层，否则改了顶层 `Modules` 也会被覆盖层盖住；`host.providers` 同理，`hasModuleOverride || hasProviderOverride` 任一成立就写 `settings["host.providers"]`（`AppConfiguration.cs:232-240`）。
4. **执行顺序**：启动时 `:44` 的 `RepairProviderBindings` → `:45-46` 首轮 `ValidateCoreConfiguration` → `:48-60` 迁移链 → `:62-66` 只在 `configuration != original` 时再以 `validateDependencies: true` 完整校验并写回。首轮校验是否带依赖校验取决于迁移是否全部应用（`:45-46`，见第 5 点）：老安装的第一轮只做轻量校验，真正拦住缺绑定的是 `:64`。
5. `KnownMigrations`（`:22`）是迁移全集的登记处，`:45-46` 用它决定迁移前要不要执行完整依赖校验（`KnownMigrations.All(…)`）。新迁移漏登记，`AppliedMigrations` 是否「走完全部迁移」的判断就永远不会包含它，校验会在迁移之前执行——新模块还没装进配置时就被当成最终状态检查。

### 7.3 边界、公开 API 与架构测试

下面五处必须同步，否则 `dotnet test tests/Mosaic.Architecture.Tests` 失败。

**`build/project-boundaries.json`**——四个新键（值只列直接引用），并把四个工程加进 `Mosaic.Desktop` 的列表：

```json
"Mosaic.Notes.Contracts": ["Mosaic.Contracts"],
"Mosaic.Notes.Core": ["Mosaic.Contracts", "Mosaic.Notes.Contracts", "Mosaic.Settings.Contracts"],
"Mosaic.Notes.Sqlite": ["Mosaic.Contracts", "Mosaic.Notes.Contracts", "Mosaic.Storage.Contracts"],
"Mosaic.Notes.Wpf": ["Mosaic.Contracts", "Mosaic.Notes.Contracts", "Mosaic.Presentation.Contracts", "Mosaic.Presentation.Wpf"]
```

`BoundaryTests.cs:22-44` 同时比对 `ProjectReference` 与编译产物里的程序集引用；未登记的工程直接报 `Undeclared project: {name}`（`:29`）。

**`build/public-api.txt`**——追加所有 `public` 类型的 `命名空间.类型名`（`:62,79-83` 精确比对，多一个少一个都失败）：

```text
Mosaic.Notes.Contracts.INotesApi
Mosaic.Notes.Contracts.INotesRepository
Mosaic.Notes.Contracts.NoteItem
Mosaic.Notes.Core.NotesDependencies
Mosaic.Notes.Core.NotesModule
Mosaic.Notes.Sqlite.NotesSqliteModule
Mosaic.Notes.Wpf.NotesUiDependencies
Mosaic.Notes.Wpf.NotesUiModule
```

`NotesApi`、`GuardedNotesApi`、`SqliteNotesRepository`、`NotesViewFactory`、`NotesViewModel`、`NotesPage` 都标 `internal`，不进清单。不要写嵌套 `public` 类型：比对只取类型名本身，不拼接外层类型。

**`tests/Mosaic.Architecture.Tests/BoundaryTests.cs`**——五处：

| 位置 | 改法 | 不做会怎样 |
| --- | --- | --- |
| `IsWindowsProject`（`:47-51`） | 加 `Mosaic.Notes.Wpf` | 去 `net10.0` 目录找 DLL，`File.OpenRead` 直接失败（`:34`、`:68`） |
| `CommunityToolkit.Mvvm` 白名单（`:55`） | 加 `Mosaic.Notes.Wpf` | `Assert.Contains` 失败 |
| `PresentationFramework` 白名单（`:56`） | 加 `Mosaic.Notes.Wpf` | `Assert.Contains` 失败 |
| SQL 白名单（`:94-95`） | 加 `Mosaic.Notes.Sqlite` | 命中 `CREATE TABLE`/`INSERT INTO`，断言失败 |
| 设置归属表（`:104-115`） | 加 `["notes-core"] = "Mosaic.Notes.Core"` | `Unknown setting module 'notes-core'` |

**设置归属的扫描规则**：`BoundaryTests.cs:116-128` 只检查**含 `SettingValueKind` 的行**，并要求同一行出现 `new("设置键", "模块 ID"`。所以 `SettingDefinition` 必须写成一整行（真实代码见 `src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskSettingsSource.cs:14-25`）；拆成多行会让这条检查静默跳过，等于自己绕过架构约束。反过来也要注意：这条正则只匹配两个**字符串字面量**参数，用常量传参（`AuthSettingsSource.cs:15`、`ReleaseSummaryModule.cs:128`、`HostSettingsCatalog.cs:27`）同样不会被检查——不要故意借这个盲区逃避归属检查。

详见 [边界声明](boundary-declaration.md)。

## 8. 第 6 步：加一个页面

`NotesUiModule.cs`：

```csharp
using Mosaic.Contracts;
using Mosaic.Notes.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;

namespace Mosaic.Notes.Wpf;

public sealed record NotesUiDependencies(
    INotesApi Api,
    IUiDispatcher Dispatcher,
    IUserDialogService Dialogs,
    IDiagnosticSink Diagnostics);

public sealed class NotesUiModule(NotesUiDependencies dependencies) : IModuleActivation, IUiModule
{
    public static ModuleManifest Definition { get; } = new(
        "notes-ui", [], [new("notes.api", 1)],
        "Owns the notes page and its navigation entry.");

    private bool _active;
    private IDisposable? _localizationRegistration;
    public ModuleManifest Manifest => Definition;

    public IReadOnlyList<NavigationItem> Navigation => _active
        ? [new("notes", "笔记", "folder", "notes-page", 200, "Notes.Navigation", IsDefault: false)]
        : [];
    public IReadOnlyList<ViewContribution> Views => _active
        ? [new("notes-page", new NotesViewFactory(dependencies))]
        : [];
    public IReadOnlyList<IStatusContribution> Status => [];

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _localizationRegistration ??= MosaicLocalizationManager.Register(
            "notes-ui", language => NotesLocalization.Dictionary(language == MosaicLanguage.English));
        _active = true;
        return Task.CompletedTask;
    }

    public Task StopAsync() { _active = false; return Task.CompletedTask; }
    public ValueTask DisposeAsync()
    {
        _localizationRegistration?.Dispose();
        _localizationRegistration = null;
        _active = false;
        return ValueTask.CompletedTask;
    }
}
```

`NotesViewFactory.cs` —— `IViewFactory.Create()` **没有参数**（`src/Mosaic.Presentation.Contracts/PresentationContracts.cs:16`），依赖全部走构造函数：

```csharp
using System.Windows.Controls;
using Mosaic.Presentation.Contracts;

namespace Mosaic.Notes.Wpf;

internal sealed class NotesViewFactory(NotesUiDependencies dependencies) : IViewFactory
{
    public ViewPage Create()
    {
        if (!dependencies.Dispatcher.CheckAccess()) throw new InvalidOperationException("Views must be created on the UI thread.");
        var model = new NotesViewModel(dependencies.Api, dependencies.Dialogs, dependencies.Diagnostics);
        return new(new NotesPage { DataContext = model }, model);   // 控制器是 ViewModel
    }
}

internal sealed partial class NotesPage : UserControl
{
    public NotesPage() => InitializeComponent();
}
```

`NotesPage.xaml` —— 视图层只用 `MosaicTheme` 键与资源键，不写死颜色和用户文本：

```xml
<UserControl x:Class="Mosaic.Notes.Wpf.NotesPage"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:theme="clr-namespace:Mosaic.Presentation.Wpf;assembly=Mosaic.Presentation.Wpf"
             Foreground="{DynamicResource {x:Static theme:MosaicTheme.TextPrimaryBrushKey}}"
             Background="{DynamicResource {x:Static theme:MosaicTheme.CanvasBrushKey}}">
  <DockPanel Margin="24">
    <Button DockPanel.Dock="Top" HorizontalAlignment="Left" Margin="0,0,0,12"
            Style="{DynamicResource {x:Static theme:MosaicTheme.AccentButtonKey}}"
            Command="{Binding AddCommand}" Content="{DynamicResource Notes.Add}"/>
    <ListBox ItemsSource="{Binding Items}" DisplayMemberPath="Title"
             Background="{DynamicResource {x:Static theme:MosaicTheme.SurfaceBrushKey}}"
             BorderBrush="{DynamicResource {x:Static theme:MosaicTheme.BorderBrushKey}}"/>
  </DockPanel>
</UserControl>
```

`NotesLocalization.cs` —— `MosaicLocalizationManager.Register` 需要 `Func<MosaicLanguage, ResourceDictionary>`，资源文件放在 `Localization/zh.xaml` 与 `Localization/en.xaml`：

```csharp
using System.Windows;

namespace Mosaic.Notes.Wpf;

internal static class NotesLocalization
{
    public static ResourceDictionary Dictionary(bool english) =>
        (ResourceDictionary)Application.LoadComponent(
            new Uri($"/Mosaic.Notes.Wpf;component/Localization/{(english ? "en" : "zh")}.xaml", UriKind.Relative));
}
```

`NotesViewModel.cs` —— `IPageController` 由 ViewModel 实现（与 `TasksViewModel` 相同），视图只负责布局：

```csharp
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Contracts;
using Mosaic.Notes.Contracts;
using Mosaic.Presentation.Contracts;

namespace Mosaic.Notes.Wpf;

internal sealed class NotesViewModel(INotesApi api, IUserDialogService dialogs, IDiagnosticSink diagnostics)
    : ObservableObject, IPageController
{
    public ObservableCollection<NoteItem> Items { get; } = [];
    public ICommand AddCommand => new AsyncRelayCommand(AddAsync);

    public async Task InitializeAsync()
    {
        var result = await api.ListAsync().ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            diagnostics.Report(result.Error!.CorrelationId ?? "", new InvalidOperationException(result.Error.Message));
            await dialogs.ShowErrorAsync(result.Error.Message).ConfigureAwait(true);
            return;
        }
        Items.Clear();
        foreach (var item in result.Value) Items.Add(item);
    }

    // 本页暂时没有未保存草稿；加入编辑后改成先问 dialogs.AskUnsavedChangesAsync()。
    public Task<bool> CanLeaveAsync() => Task.FromResult(true);

    public ValueTask DisposeAsync() { Items.Clear(); return ValueTask.CompletedTask; }

    private async Task AddAsync()
    {
        var result = await api.CreateAsync("新笔记", "", CancellationToken.None).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            diagnostics.Report(result.Error!.CorrelationId ?? "", new InvalidOperationException(result.Error.Message));
            await dialogs.ShowErrorAsync(result.Error.Message).ConfigureAwait(true);
            return;
        }
        Items.Insert(0, result.Value);
    }
}
```

- `Navigation` 与 `Views` 在 `ActivateAsync` 之后才返回非空：宿主在 `ModuleStarted` 时一次性采样并注册（`Bootstrapper.cs:124-131`），之后再变不会同步到界面。
- `IPageController` 的三个方法都有实际用途：`CanLeaveAsync` 在切换视图前调用（`false` 就留在原页），`InitializeAsync` 在工厂 `Create()` 之后调用，`DisposeAsync` 在释放旧页与退出时调用（`NavigationService.cs:9-31`）。
- 视图只能由 `IViewFactory` 在 UI 线程创建：真实视图工厂都用 `IUiDispatcher.CheckAccess()` 把关（`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskViewFactory.cs:10`），跨线程创建 `UserControl` 会抛 `InvalidOperationException`。
- `NotesUiDependencies` 里的 `Dispatcher` 与 `Diagnostics` 不是装饰：前者做线程断言，后者把 API 失败写进诊断管道；两者都由宿主在目录工厂里注入，不要自己 `new`。
- `LabelKey` 只有在本地化字典里注册过才生效，未注册时回退到 `Label`（`ShellLocalization.cs:13-14`）。停止模块时释放注册句柄，否则切换语言会留下失效字典。
- 样式只用 `MosaicTheme` 键，文本只用资源键。完整规则见 [UI 规范](ui-spec.md)。
- 导航项 `Order` 决定左侧顺序（升序，同值按 `Id` 排序）；内置项占用 100、110、900，插件导航建议 200–499。

启动页取 `Navigation` 中第一个 `IsDefault: true` 的项；没有则取第一个；两者都没有时显示“页面加载失败，请重新启动应用。”（`ShellViewModel.cs:86-89`）。`Navigation` 已按 `Order` 升序、同值按 `Id` 排序（`ContributionRegistry.cs:82`），所以「第一个」由这两个键决定，与模块注册顺序无关。运行时不做 `IsDefault` 唯一性校验，但请只标一个。

## 9. 第 7 步：加设置项

`src/Modules/Notes/Mosaic.Notes.Core/NotesSettingsSource.cs`：

```csharp
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;

namespace Mosaic.Notes.Core;

internal sealed class NotesSettingsSource : ISettingSource
{
    public string ModuleId => "notes-core";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new("notes.defaultTitle", "notes-core", "默认标题", "新建笔记时预填的标题", SettingValueKind.String, "未命名", null, null, null, SettingsGroup.Tasks, SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings, DisplayNameKey: "Settings.Notes.DefaultTitle", DescriptionKey: "Settings.Notes.DefaultTitle.Description")
    ];

    public Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
    {
        // 把 values["notes.defaultTitle"] 写入本模块运行时状态；读取与持久化由设置核心完成。
        return Task.CompletedTask;
    }
}
```

要求：

- `ModuleId`（`"notes-core"`）必须与边界测试归属表中的键一致。
- `SettingDefinition` 写成一整行：归属检查只看含 `SettingValueKind` 的那一行（`BoundaryTests.cs:121-123`），拆行等于不被检查。
- 设置键只允许小写字母、数字、点（`[a-z][a-z0-9.]*`），模块 ID 只允许小写字母、数字、连字符（`[a-z][a-z0-9-]*`）——这是 `BoundaryTests.cs:116` 的正则，运行时不做这项校验，写错只会让测试悄悄漏掉。
- 键名以 `notes.` 开头，避免与其他模块的 ID 空间冲突。
- 能立即生效的用 `Immediate`，必须重启的用 `Restart`，并在描述里写明重启原因。
- `ISettingSource.ApplyAsync` 返回 `Task`，不是 `Task<OperationResult>`（`src/Modules/Settings/Mosaic.Settings.Contracts/SettingsContracts.cs:285`）。应用失败就抛异常：设置核心在 `TryApplyAsync` 里捕获，把它变成“已保存，但即时应用失败，重启后生效。”的警告（`src/Modules/Settings/Mosaic.Settings.Core/Internal/SettingsService.cs:225-235`），不会回滚已保存的值；`ISettingsCore.Register` 返回的 `IDisposable` 要在停用时释放（`SettingsContracts.cs:329`）。
- 写入一律通过设置系统，不直接改 JSON。
- 本节的 `ApplyAsync` 只是最小骨架。要让设置真正生效，照 `TasksModule.ActivateAsync`（`TasksModule.cs:75-91`）的做法：`Register` 后 `GetValuesAsync` 取值、把值写进模块状态（`_rules.Apply`），再由 API 读取；示例里的 `notes.defaultTitle` 不接这一步就只是一条可编辑的配置。

## 10. 第 8 步：测试

`tests/Mosaic.Notes.Tests/Mosaic.Notes.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsTestProject>true</IsTestProject>
    <IsPackable>false</IsPackable>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Mosaic.Contracts\Mosaic.Contracts.csproj" />
    <ProjectReference Include="..\..\src\Mosaic.Kernel\Mosaic.Kernel.csproj" />
    <ProjectReference Include="..\..\src\Modules\Notes\Mosaic.Notes.Contracts\Mosaic.Notes.Contracts.csproj" />
    <ProjectReference Include="..\..\src\Modules\Notes\Mosaic.Notes.Core\Mosaic.Notes.Core.csproj" />
    <ProjectReference Include="..\..\src\Modules\Notes\Mosaic.Notes.Sqlite\Mosaic.Notes.Sqlite.csproj" />
    <ProjectReference Include="..\..\src\Modules\Storage\Mosaic.Storage.Sqlite\Mosaic.Storage.Sqlite.csproj" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <Using Include="Xunit" />
  </ItemGroup>
</Project>
```

`tests/Mosaic.Notes.Tests/NotesTests.cs`：

```csharp
using Mosaic.Contracts;
using Mosaic.Kernel;
using Mosaic.Notes.Contracts;
using Mosaic.Notes.Core;
using Mosaic.Notes.Sqlite;
using Mosaic.Storage.Sqlite;

namespace Mosaic.Notes.Tests;

public sealed class NotesTests
{
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string id, Exception ex) { } }

    [Fact]
    public void Manifest_declares_expected_capabilities()
    {
        Assert.Equal("notes-core", NotesModule.Definition.Id);
        Assert.Equal(new[] { new CapabilityId("notes.api", 1) }, NotesModule.Definition.Provides);
        Assert.Equal(
            new[] { new CapabilityId("notes.repository", 1), new CapabilityId("settings.api", 1) },
            NotesModule.Definition.Requires);
    }

    [Fact]
    public void Missing_provider_binding_is_rejected_before_start()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ModuleGraph.Resolve(
            [NotesSqliteModule.Definition, NotesModule.Definition],
            new Dictionary<string, string> { ["notes.repository@1"] = "notes-sqlite" }));
        Assert.Equal("Provider binding is missing or incompatible: notes.api@1", error.Message);
    }

    [Fact]
    public async Task Migration_is_idempotent_and_repository_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        try
        {
            await using var storage = new SqliteModule(new(path), new Diagnostics());
            await storage.ActivateAsync();

            await using (var first = new NotesSqliteModule(storage.Session)) await first.ActivateAsync();
            await using var second = new NotesSqliteModule(storage.Session);
            await second.ActivateAsync();

            var item = new NoteItem("n1", "标题", "正文", DateTimeOffset.UtcNow);
            await second.Repository.AddAsync(item);
            Assert.Equal("标题", Assert.Single(await second.Repository.ListAsync()).Title);
            Assert.True(await second.Repository.RemoveAsync("n1"));
            Assert.Empty(await second.Repository.ListAsync());
        }
        finally { File.Delete(path); }
    }
}
```

三个测试各守一条结论：清单与实际能力一致、绑定缺失在启动前失败、迁移幂等且仓储可读写。

测试只能通过 `public` 类型到达被测代码：架构测试禁止 `InternalsVisibleTo`（`BoundaryTests.cs:91`），也不允许反射或服务定位器（`:92`）。所以 `NotesModule`、`NotesDependencies`、`NotesSqliteModule.Repository` 必须 `public`；想让内部实现可测就把类型改 public 并在 `build/public-api.txt` 登记，不要用可见性开关绕开。

运行：

```powershell
dotnet sln Mosaic.sln add tests/Mosaic.Notes.Tests/Mosaic.Notes.Tests.csproj
dotnet restore Mosaic.sln
build/test.ps1 -Configuration Release -SkipDesktop
```

`build/test.ps1` 用 `dotnet build --no-restore`（`build/test.ps1:7`），`build/publish.ps1` 用 `--locked-mode`（`:8`）——所以新增工程后必须 `dotnet restore` 并提交生成的 `packages.lock.json`（仓库开了 `RestorePackagesWithLockFile`，`Directory.Build.props:9`），否则打包阶段直接失败。

## 11. 第 9 步：运行与验证

```powershell
dotnet run --project src/Mosaic.Desktop/Mosaic.Desktop.csproj -c Debug
```

验证顺序：

1. 窗口能打开，左侧出现“笔记”，点击后加载空列表。
2. 新建一条笔记，关掉应用再打开，数据还在——仓储、迁移、绑定三条链都通了。
3. 把 `host.providers` 里的 `notes.api@1` 改绑到 `notes-sqlite` 后重启，启动失败并报 `能力绑定无效：notes.api@1`（`HostConfigurationValidator.cs:36`；异常由 `AppConfiguration.Load` 的 `ValidateCoreConfiguration` 抛出，启动页原样显示 `ConfigurationException.Message`，`App.xaml.cs:88-90`）：`notes-sqlite` 不提供这个能力，而 `RepairProviderBindings` 只补缺失键、不覆盖已有键。只有校验被推迟到启动器（迁移未全部应用、加载期又没有写回配置）时才会多出 `模块或能力绑定配置无效：` 前缀（`Bootstrapper.cs:51`）。整条删掉不会报错——下次启动会把 `notes.api@1` 自动补回 `notes-core`（`AppConfiguration.cs:177-205`）。
4. 禁用 `notes-core`、并从 `host.providers` 删掉 `notes.api@1` 后重启，报 `模块依赖或能力绑定无效：Missing capability or incompatible version: notes.api@1`——`notes-ui` 还依赖它（`HostConfigurationValidator.cs:59-62` → `ModuleGraph.cs:37`）。只禁用模块、留着绑定则会先报 `能力绑定无效：notes.api@1`，因为绑定指向了未启用的模块（`HostConfigurationValidator.cs:28`）。同理，这两种消息在校验推迟到启动器时会再套一层 `模块或能力绑定配置无效：`。
5. 在设置里禁用 `notes-ui`，笔记页消失，其余功能不受影响。

### 升级迁移验收

用独立数据根验证，不动本机真实数据（`--data-root` 只接受绝对路径，`AppConfiguration.cs:496-497`）：

```powershell
dotnet run --project src/Mosaic.Desktop/Mosaic.Desktop.csproj -c Debug -- --data-root D:\Temp\MosaicNotesTest
```

验收项：

- [ ] 用**改动前**的构建在该 `--data-root` 启动，再进设置页把「启用模块」或「能力绑定」改动一次并保存。提交只把本次草稿里的项交给 `ProposeChangesCoreAsync`（`SettingsEditing.cs:271`），其中 `Persistence == HostConfig` 的项才经 `PersistAsync` 写进 `root["settings"]`（`SettingsService.cs:117-120`、`:147-149`；`HostSettingSource.cs:84-90`）。所以只有动过这两项，配置文件才会出现 `settings.host.modules` / `settings.host.providers`；只改别的设置或者不保存，则仍只有顶层 `modules` / `providers`，两条路径都要验。
- [ ] 换成新构建再启动：迁移追加到**当前生效的那一层**——有 `settings.host.modules` 时写这一层，否则写顶层 `modules`（`ApplyNotesMigration` 的 `hasModuleOverride` 分支）；`notes.repository@1`、`notes.api@1` 两条绑定由迁移自己 `TryAdd` 写进同一层——`RepairProviderBindings` 在 `:44` 只跑一次、且早于迁移链（`:48-60`），看不到迁移新启用的模块；`appliedMigrations` 出现 `notes-v1`。故意注释掉绑定那两行可验：`:64` 抛 `模块依赖或能力绑定无效：Provider binding is missing or incompatible: notes.repository@1`，配置不写回。
- [ ] 再启动一次：配置不再变化，`notes-v1` 不重复写入（`MarkMigration` 用 `Distinct` 去重，`AppConfiguration.cs:382-386`）。
- [ ] 先禁用 `tasks-core` 再升级：笔记模块不被启用（锚点条件生效）。
- [ ] 用户已经有自定义 `notes.api@1` 绑定时：迁移里的 `TryAdd` 不覆盖，启动按用户绑定走；绑到不提供该能力的模块，会在 `ValidateCoreConfiguration` 阶段报 `能力绑定无效：notes.api@1`（`HostConfigurationValidator.cs:23-38`）。

## 12. 常见错误对照

消息串逐字取自代码，可直接在日志里搜索。

| 消息 / 现象 | 抛出位置 | 原因 | 修复 |
| --- | --- | --- | --- |
| `模块或能力绑定配置无效：` + 设置校验消息 | `Bootstrapper.cs:51` | 只有校验推迟到启动器时才出现（迁移未全部应用且加载期未写回配置）；已完成迁移的安装由 `AppConfiguration.Load` 直接抛出内层消息，不带此前缀 | 看外层前缀后的内层消息，见下两行 |
| `模块依赖校验失败：` + 内核消息 | `Bootstrapper.cs:54` | 同上：设置校验通过，但 `ModuleGraph.Resolve` 在依赖/绑定上失败 | 看冒号后的内核逐字消息（`ModuleGraph.cs:13-48`） |
| `未知模块。` | `BuiltInModuleCatalog.cs:261` | 配置里有 ID，`Entries` 没登记 | 补 `Entries`，重启 |
| `模块列表包含未知或重复的模块。` | `HostConfigurationValidator.cs:19` | `host.modules` 含未登记 ID 或重复项 | 删掉多余项；新模块先改 `Entries` |
| `能力绑定无效：{键列表}` | `HostConfigurationValidator.cs:36` | `host.providers` 的键无效、绑到未启用模块，或被绑模块并不提供该能力 | 键写成 `名称@主版本`，值写成**提供该能力**的模块 ID |
| `模块依赖或能力绑定无效：` + 内核消息 | `HostConfigurationValidator.cs:59-62` | 模块图的依赖/绑定校验失败 | 看冒号后的逐字消息，见下两行 |
| `Provider binding is missing or incompatible: {capability}` | `ModuleGraph.cs:21` | 内核防御性消息：`Provides` 的能力没有对应绑定。已启用模块缺失的绑定，宿主每次启动都会用 `RepairProviderBindings`（`:44`，迁移链之前）补齐，正常配置下见不到；迁移**新启用**的模块不在补齐范围内，绑定漏写就会在 `AppConfiguration.cs:64` 的完整校验里以这条消息失败 | 直接调用 `ModuleGraph.Resolve` 的场景（如测试）自己补绑定；迁移新启用的模块在迁移里用 `TryAdd` 写入绑定 |
| `Missing capability or incompatible version: {required}` | `ModuleGraph.cs:37` | `Requires` 的主版本写错，或提供者没启用 | 对齐 `CapabilityId`，检查 `host.modules` |
| `Module dependency cycle.` | `ModuleGraph.cs:30` | 两个模块互相 `Requires` | 抽公共能力到第三个模块 |
| `Duplicate provider: {capability}` | `ModuleGraph.cs:19` | 两个模块声明同一个能力 | 只保留一个提供者 |
| `Capability '{key}' has no active bound provider.` | `ModuleRuntime.cs:15` | 内核防御性消息：`ModuleRuntime.Resolve` 找不到已启动的提供者。当前宿主用目录工厂注入依赖，不走这条路径 | 直接调用 `Resolve` 的场景才需要检查 `host.modules` |
| `Factory returned a module that does not match manifest '{id}'.` | `ModuleRuntime.cs:37` | 工厂返回的 `Manifest` 与 `Definition` 不一致 | 让模块的 `Manifest` 直接返回静态 `Definition` |
| 左侧没有导航 | — | `Navigation` 在激活前为空，或 `ViewId` 找不到视图 | 在 `ActivateAsync` 后返回；`ViewId` 对应已注册视图（可跨模块） |
| `Duplicate navigation or missing view contribution.` | `ContributionRegistry.cs:42` | 导航 ID 重复，或 `ViewId` 在当前合并视图表中不存在 | 先注册 `Views`，再让导航引用它 |
| 页面加载失败，请重新启动应用。 | `ShellViewModel.cs:107-110` | `InitializeAsync` 抛异常 | 在 `InitializeAsync` 内捕获并上报 `IDiagnosticSink`，返回可用的空状态 |
| 设置页打不开 | — | `GroupId` 不是合法分区名，或同组已注册页面 | 用 `SettingsSection` 的精确名称 `Tasks`/`Appearance`/`Storage`/`Text`/`DesktopPresence`/`FloatingWindows`/`AccountsAndBackup`/`Maintenance`（大小写敏感，`SettingsViewModel.cs:359`）；注意它是 `Storage`，**不是**设置定义里的 `SettingsGroup.StorageSystem`。每个分组只注册一页 |
| 架构测试失败 | — | 边界、公开 API、SQL 白名单、设置归属未同步 | 按失败消息补对应声明 |
| 深色模式文字看不见 | — | 写死了颜色 | 改用 `MosaicTheme` 画刷键 |

## 13. 提交清单

- 四个工程进 `Mosaic.sln`；`build/project-boundaries.json` 四键，`Mosaic.Desktop` 的引用清单同步。
- `build/public-api.txt` 与每个 `public` 类型一一对应；内部实现不进清单。
- `BoundaryTests` 五处同步：`IsWindowsProject`、`CommunityToolkit.Mvvm` 白名单、`PresentationFramework` 白名单、SQL 白名单、设置归属表。
- 模块在 `Entries` 登记；`Definition` 是静态只读属性，`Manifest` 直接返回它。
- `host.modules` 有升级迁移与锚点条件，迁移幂等；迁移新增模块的 `host.providers` 绑定必须在迁移里用 `TryAdd` 一并写入（`RepairProviderBindings` 早于迁移运行，补不到），见 §11 验收清单。
- 测试覆盖清单一致性、绑定缺失报错、迁移幂等与仓储往返；`build/test.ps1` 全绿。
- 四个工程的 `packages.lock.json` 一并提交（`build/publish.ps1:8` 用 `--locked-mode` 还原）。
- 源码、测试、发布说明、实施记录同一次提交。
- 实施记录写明根因位置（若修缺陷）与每个改动文件/类型/行号。
- 版本号由 `Directory.Build.props` 统一管理，不在插件工程里另写。

---

继续阅读：

- [插件规范](plugin-spec.md)：清单、能力、生命周期、失败语义。
- [边界声明](boundary-declaration.md)：允许引用矩阵、公开 API 清单、架构测试。
- [框架规范](framework-spec.md)：启动链、模块图、配置、设置、存储。
- [UI 规范](ui-spec.md)：导航、页面、设置页、状态栏、主题、本地化。
- [架构原则](../architecture-principles.md)：长期约束。