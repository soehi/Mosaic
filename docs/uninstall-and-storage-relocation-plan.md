# Mosaic 维护页：应用卸载与存储位置迁移（实施计划）

> 目标版本：0.16.7（在 0.16.6 工作区变更提交/发布后单独实施）
>
> 范围：Windows 桌面端。服务端与移动端不在本计划内。

## 1. 目标

在“设置”中新增一个普通用户可用的“维护”页，提供两件事：

1. **卸载本软件**：检测当前是否为 MSI 安装；确认后退出应用，由独立助手进程调用 `msiexec /x` 卸载；默认保留用户数据，删除数据必须显式勾选并二次确认。
2. **简化存储位置修改**：不再让用户直接编辑“数据库位置”文本框，而是选择文件夹、校验目标、确认迁移、重启应用；重启时在数据库打开前完成真实的数据迁移，而不是只把路径指向一个空数据库。

同时补齐：

- “修复安装”入口（`msiexec /f`），与卸载成对出现，符合“安装包自带修复能力”的既有要求。
- 迁移/卸载的失败回滚、结果提示和日志。
- 旧安装的配置迁移，确保升级后模块列表与能力绑定完整。

## 2. 现状与根因位置

### 2.1 存储位置只是“改路径”，没有迁移

- 根因位置：
  - [HostSettingsCatalog.cs:23-26](../src/Mosaic.Desktop/HostSettingsCatalog.cs:23)：`storage.filename` / `storage.location` 被声明为普通 `Text` 设置，属于 `Advanced`、`Restart`，设置页只渲染一个文本框。
  - [AppConfiguration.cs:382-385](../src/Mosaic.Desktop/AppConfiguration.cs:382)：`AppPaths.Database` 只是把 `storage.Location` 和 `storage.Filename` 拼成新路径。
  - [Bootstrapper.cs:34-41](../src/Mosaic.Desktop/Bootstrapper.cs:34)：启动时直接按新路径构造 `StorageConfiguration` 并启动 `SqliteModule`。
- 触发条件与后果：用户在高级设置里把“数据库位置”改成另一个文件夹并重启，应用会在新位置创建一个空数据库；旧数据库仍留在原处，但应用不再读取它。任务、设置、浮窗布局看起来“消失”，且没有任何迁移提示或回滚。
- 数据库连接由 [DatabaseWorker.cs](../src/Modules/Storage/Mosaic.Storage.Sqlite/Internal/DatabaseWorker.cs) 在专用线程上长期持有，运行中不能安全地复制/移动数据库文件，因此迁移必须放在**数据库打开之前**的启动阶段，或先完整关闭数据库。

### 2.2 没有应用内卸载入口

- 根因位置：
  - [Package.wxs](../build/installer/Package.wxs) 只定义了 MSI 安装/卸载行为；应用本身没有读取卸载注册表、定位 ProductCode 或启动卸载助手的代码。
  - [PresentationContracts.cs:76](../src/Mosaic.Presentation.Contracts/PresentationContracts.cs:76) 的 `IUserDialogService` 只有确认、删除确认、未保存选择和错误提示；没有文件夹选择器，也没有“卸载 + 是否删除数据”的组合对话框。
  - [ShutdownCoordinator.cs:37](../src/Mosaic.Desktop/ShutdownCoordinator.cs:37) 的 `RequestExitAsync` 只负责接受退出和清理；没有“退出后执行外部动作”的钩子，也没有重启 API。
  - 当前进程的 exe 位于安装目录，运行中无法删除自身；必须由独立助手进程在进程退出后执行 `msiexec`。
- 既有边界：MSI 卸载默认保留 `%LOCALAPPDATA%\Mosaic` 用户数据（见 [release-content.json](../build/release-content.json) 的卸载说明）。本计划保留这个安全默认，删除数据改为显式选项。

### 2.3 设置页扩展点已具备，但缺少“维护”分区

- [SettingsPageRegistry.cs](../src/Mosaic.Presentation.Contracts/SettingsPageRegistry.cs) 已支持模块注册 `SettingsPageContribution(GroupId, Id, IViewFactory)`。
- [SettingsContracts.cs:8](../src/Modules/Settings/Mosaic.Settings.Contracts/SettingsContracts.cs:8) 的 `SettingsGroup` 没有 `Maintenance`；[SettingsViewModel.cs:638-647](../src/Modules/Settings/Mosaic.Settings.Wpf/Internal/SettingsViewModel.cs:638) 的 `SectionFor` 也没有对应分支。
- 现有 `SettingsView` 的 section 内容要么是通用设置行，要么是模块页面；新增维护页应走模块页面，不修改通用行编辑器。

### 2.4 新模块/新能力需要配置迁移

- [AppConfiguration.cs:108-137](../src/Mosaic.Desktop/AppConfiguration.cs:108) 的 `RepairProviderBindings` 只为**已启用模块**补齐能力绑定；它不会把新模块加入旧安装的 `modules` 列表。
- 旧安装的 `Config/appsettings.json` 保存了 `modules` 和 `providers`。如果只把新模块加进 `BuiltInModuleCatalog.DefaultModules`，旧安装升级后不会启用维护模块，`host.providers` 也缺少 `app.maintenance@1`，`ModuleGraph.Resolve` 会失败。
- 必须新增一次显式配置迁移（参考 `ApplyAuthSyncMigration`），并加入 `KnownMigrations` 和 `appsettings.json` 的 `appliedMigrations`。

## 3. 总体设计

### 3.1 新增/调整的项目与能力

| 项目 | 目标框架 | 职责 | 能力 |
| --- | --- | --- | --- |
| `Mosaic.Maintenance.Contracts`（新增） | `net10.0` | 维护动作契约、安装信息、结果模型、退出/重启生命周期契约 | 不提供 |
| `Mosaic.Maintenance.Windows`（新增） | `net10.0-windows` | MSI 安装检测、ProductCode 定位、助手脚本生成、数据删除守卫、维护模块 | `app.maintenance@1` |
| `Mosaic.Maintenance.Wpf`（新增） | `net10.0-windows` | “维护”设置页的 View/ViewModel，注册 `SettingsPageContribution` | 不提供 |
| `Mosaic.Storage.Contracts`（调整） | `net10.0` | 存储位置信息、迁移请求/计划、`IStorageRelocation`、启动迁移设置写入契约 | 声明 `storage.relocation@1` |
| `Mosaic.Storage.Sqlite`（调整） | `net10.0` | 迁移服务、启动迁移执行器、SQLite 一致性复制/校验 | 提供 `storage.relocation@1` |
| `Mosaic.Desktop`（调整） | `net10.0-windows` | 组合新模块、启动前执行迁移、HostSettingSource 适配、退出钩子、配置迁移 | 宿主 |
| `Mosaic.Settings.Contracts` / `Mosaic.Settings.Wpf`（调整） | `net10.0` / `net10.0-windows` | 新增 `SettingsGroup.Maintenance`、`SettingsSection.Maintenance`、导航文案 | 不提供 |

依赖方向：

```text
Maintenance.Wpf ──> Maintenance.Contracts
       │                    ^
       │                    │
       └──> Storage.Contracts
                            ^
Maintenance.Windows ────────┘（实现 app.maintenance@1）
       │
       └──> DesktopPresence.Contracts（退出协调的宿主抽象）

Storage.Sqlite ──> Storage.Contracts
Desktop ──> 所有模块 + 启动迁移 + HostSettingSource 适配
```

约束：

- `Mosaic.Maintenance.Wpf` 不引用 `Mosaic.Desktop`；维护动作全部通过契约调用。
- `Mosaic.Storage.Sqlite` 不引用 `Mosaic.Desktop`；启动迁移通过 `IStorageRelocationSettings` 回调写宿主配置。
- 不新增跨模块 SQL；迁移/校验代码只放在 `Mosaic.Storage.Sqlite`。
- 新项目加入 `build/project-boundaries.json`、`Mosaic.sln`、架构测试的 Windows 项目白名单；新公开类型加入 `build/public-api.txt`。

### 3.2 维护能力契约

`Mosaic.Maintenance.Contracts`：

```csharp
public enum MaintenanceAvailability { Installed, Portable, Unsupported }

public sealed record InstallationInfo(
    MaintenanceAvailability Availability,
    string? ProductCode,
    string? DisplayName,
    string? DisplayVersion,
    string? InstallLocation,
    string DataRoot,
    string ExecutablePath,
    bool CanUninstall,
    bool CanRepair,
    string? Reason);

public sealed record UninstallOptions(bool RemoveUserData);

public interface IApplicationMaintenance
{
    Task<OperationResult<InstallationInfo>> GetInstallationAsync(CancellationToken ct = default);
    Task<OperationResult> UninstallAsync(UninstallOptions options, CancellationToken ct = default);
    Task<OperationResult> RepairAsync(CancellationToken ct = default);
    Task<OperationResult> RestartAsync(CancellationToken ct = default);
}
```

`IApplicationMaintenance` 由 `MaintenanceModule` 实现并导出为 `app.maintenance@1`。

退出/重启钩子放在 `Mosaic.Maintenance.Contracts`，由 Desktop 实现：

```csharp
public interface IApplicationLifecycle
{
    // 在退出被接受后、清理开始前执行；返回失败则撤销退出并保持应用可继续使用。
    Task<OperationResult> RequestExitAsync(
        Func<CancellationToken, Task<OperationResult>>? beforeCleanup,
        CancellationToken ct = default);
}
```

`ShutdownCoordinator` 的调整：

- 新增带 `beforeCleanup` 的 `RequestExitAsync` 重载。
- 在 `_accepted = true` 之后、`BeginInvoke(FinishExitAsync)` 之前调用 `beforeCleanup`。
- 回调失败时：`_accepted = false`、恢复 `shell.IsEnabled = true`，返回失败，不退出。
- 这样卸载/重启助手脚本会在应用仍可取消退出时完成“启动助手”这一步，助手随后等待当前 PID 退出再执行动作，避免“进程已退出但助手没启动”的竞态。

### 3.3 存储迁移契约

`Mosaic.Storage.Contracts` 新增：

```csharp
public enum StorageRelocationMode { Move }

public sealed record StorageLocationInfo(
    string DataRoot,
    string DatabaseDirectory,
    string DatabasePath,
    string Filename,
    long DatabaseBytes,
    bool IsDefaultLocation,
    bool HasPendingRelocation);

public sealed record StorageRelocationPlan(
    string TargetDirectory,
    string TargetDatabasePath,
    string Filename,
    long SourceBytes,
    long RequiredFreeBytes,
    bool IsDefaultLocation,
    bool TargetHasExistingDatabase);

public sealed record StorageRelocationRequest(
    string TargetDirectory,
    StorageRelocationMode Mode = StorageRelocationMode.Move,
    bool RemoveSourceAfterSuccess = false);

public interface IStorageRelocation
{
    Task<OperationResult<StorageLocationInfo>> GetLocationAsync(CancellationToken ct = default);
    Task<OperationResult<StorageRelocationPlan>> ValidateTargetAsync(
        string targetDirectory, CancellationToken ct = default);
    Task<OperationResult> ScheduleAsync(
        StorageRelocationRequest request, CancellationToken ct = default);
    Task<OperationResult> GetPendingAsync(CancellationToken ct = default);
    Task<OperationResult> CancelPendingAsync(CancellationToken ct = default);
}

public interface IStorageRelocationSettings
{
    Task<OperationResult> WriteAsync(
        string location, string filename, CancellationToken ct = default);
}
```

约定：

- `Mode` 本轮只有 `Move`：把当前数据库迁移到目标位置。
- 目标位置已有同名数据库时**拒绝**，提示用户选择空文件夹；本轮不做“接管已有数据库”，避免把另一份数据误当当前数据。
- `RemoveSourceAfterSuccess` 默认 `false`：迁移成功后保留旧数据库、`-wal`/`-shm` 和旧备份，作为安全副本；用户显式勾选才删除。
- `IStorageRelocationSettings` 由 Desktop 用 `HostSettingSource` 实现，写入 `settings["storage.location"]` 和 `settings["storage.filename"]`，不直接改 `AppConfiguration.Storage` 基线。

### 3.4 启动迁移执行顺序

在 `Bootstrapper.StartAsync` 中：

```csharp
var configuration = AppConfiguration.Load(paths);
var hostSettings = new HostSettingSource(paths, configuration);
await StorageRelocationStartup.ApplyAsync(
    new StorageRelocationStartupOptions(paths.Root, paths.ConfigDirectory, paths.Database(currentStorage)),
    new HostStorageRelocationSettings(hostSettings),
    diagnostics);
var storage = new StorageConfiguration(
    "sqlite",
    hostSettings.GetValue("storage.location") as string ?? configuration.Storage.Location,
    hostSettings.GetValue("storage.filename") as string ?? configuration.Storage.Filename);
```

关键点：迁移在 `SqliteModule` 启动前执行，此时数据库没有打开的连接；迁移完成并写入宿主配置后，后续启动逻辑自然使用新路径。

### 3.5 迁移文件格式

`Config/storage-relocation.json`：

```json
{
  "version": 1,
  "requestId": "guid",
  "createdAt": "2026-11-20T00:00:00Z",
  "appVersion": "0.16.7",
  "sourceDatabase": "C:\\Users\\...\\Mosaic\\Data\\mosaic.sqlite",
  "targetDirectory": "D:\\MosaicData",
  "targetFilename": "mosaic.sqlite",
  "removeSourceAfterSuccess": false
}
```

`Config/storage-relocation-result.json`：

```json
{
  "version": 1,
  "requestId": "guid",
  "completedAt": "2026-11-20T00:00:00Z",
  "success": true,
  "message": "数据已迁移到 D:\\MosaicData。",
  "oldDataRetained": true
}
```

请求文件由维护页在用户确认后原子写入；结果文件由启动迁移写入，维护页打开时读取并显示一次性提示。

### 3.6 迁移算法（启动阶段）

1. 读取请求；校验 `version`、路径、`requestId`。
2. 规范化路径：`Path.GetFullPath`；拒绝目标等于源目录、目标位于源目录内、源位于目标内。
3. 目标目录不存在则创建；写临时文件验证可写；检查剩余空间 ≥ 源数据库大小 × 1.2 + 64 MB。
4. 目标数据库已存在则失败（本轮不接管）。
5. 用 `Microsoft.Data.Sqlite` 打开源数据库：
   - `PRAGMA wal_checkpoint(TRUNCATE)`；
   - `PRAGMA integrity_check`；
   - 用 SQLite Backup API 复制到目标临时文件 `mosaic.sqlite.relocating-{requestId}`。
6. 对目标临时文件执行 `PRAGMA integrity_check`，通过后原子移动到最终文件名（不覆盖）。
7. 通过 `IStorageRelocationSettings.WriteAsync` 写入：
   - 目标就是默认 `%LOCALAPPDATA%\Mosaic\Data` 时写 `local-app-data`；
   - 否则写绝对路径。
8. 写入成功结果；删除请求文件。
9. 若 `removeSourceAfterSuccess`：删除旧数据库、`-wal`、`-shm` 和旧备份文件；删除失败只记录警告，不回滚已成功的迁移。
10. 任一步失败：删除目标临时文件；保留源数据库和旧配置；写入失败结果；删除请求文件，避免每次启动重复失败。用户可在维护页看到失败原因并重试。

幂等/崩溃恢复：

- 如果配置里的当前数据库已经是请求目标，且请求仍存在：视为上次已切换成功，直接收尾（按选项删除旧文件、删除请求）。
- 如果源数据库已不存在、目标存在且配置已指向目标：清理请求。
- 如果目标存在但配置未指向目标：说明上次在复制后、写配置前崩溃；删除目标文件后重试，或提示用户手动确认。
- 单实例机制保证同一数据目录只有一个应用实例；外部 SQLite 工具仍可能占用数据库，失败时提示“请关闭其他正在使用该数据库的程序”。

### 3.7 卸载流程

1. 维护页调用 `GetInstallationAsync`：
   - 读取 `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\*`，按 `DisplayName == "Mosaic"` 且 `InstallLocation` 与当前 `AppContext.BaseDirectory` 匹配定位。
   - ProductCode 必须是合法 GUID；找不到时回退到 HKLM（仅用于诊断），仍找不到则判定 `Portable`。
   - 运行目录不在 MSI 安装目录、或注册表项不匹配时，`CanUninstall = false`，显示“当前不是通过安装包安装，无法在应用内卸载；可手动删除程序文件夹”。
2. 用户点击“卸载 Mosaic”：
   - 默认对话框说明：卸载程序与快捷方式，保留任务、设置、登录凭据和日志。
   - 可选勾选“同时删除本机数据”；勾选后二次确认，明确列出将删除：任务、设置、浮窗布局、登录凭据、日志、缓存。
3. 生成助手脚本到 `%TEMP%\Mosaic\maintenance-{requestId}.ps1`：
   - ASCII-only，避免 Windows PowerShell 5.1 无 BOM 中文编码问题；
   - 参数：父进程 PID、ProductCode、数据根目录、是否删除数据、日志路径；
   - 等待父进程退出；
   - `Start-Process msiexec.exe -ArgumentList @('/x', $ProductCode) -Wait -PassThru`；
   - 只有退出码为 `0` 或 `3010` 且勾选删除数据时，才删除数据根目录；
   - 删除前再次做守卫：不是盘符根、不是用户目录、不是 `%LOCALAPPDATA%` 本身、不是程序安装目录、路径确实等于应用数据根；
   - 结果写入 `%TEMP%\Mosaic\maintenance-{requestId}.log`。
4. 通过 `IApplicationLifecycle.RequestExitAsync` 的 `beforeCleanup` 启动助手：
   - 助手启动成功后才接受退出；
   - 助手启动失败则撤销退出，应用继续运行并显示错误。
5. 应用退出后，助手执行 `msiexec /x`。用户取消 MSI 卸载时退出码为 `1602`，助手不删除任何数据。

修复安装：

- 同一套助手机制，动作改为 `msiexec /f "{ProductCode}"`；
- 不删除数据，不重启应用（用户可手动重启）；
- 仅 MSI 安装可用。

### 3.8 维护页 UI

新增 `SettingsGroup.Maintenance` / `SettingsSection.Maintenance`，导航名“维护”。

页面结构：

1. **存储位置**
   - 当前数据库目录、数据库文件名、占用大小、默认/自定义标识。
   - “打开文件夹”（`explorer.exe`）。
   - “更改位置…”：`Microsoft.Win32.OpenFolderDialog` 选择文件夹；校验后显示目标路径、需要空间、是否默认位置。
   - 确认对话框：
     - 默认“迁移后保留旧数据”；
     - 可选“迁移后删除旧数据”；
     - 说明“迁移将在下次启动时执行，完成后应用会自动重启；迁移期间不要关闭应用”。
   - 已有待执行请求时显示横幅：“已安排在下次启动迁移到 X”，提供“取消迁移”“现在重启”。
2. **安装信息**
   - 当前版本、安装目录、安装方式（MSI/便携）。
   - “修复安装”按钮；非 MSI 时禁用并说明。
3. **卸载**
   - 危险按钮“卸载 Mosaic”；非 MSI 时禁用并说明。
   - 卸载确认中的“同时删除本机数据”默认不勾选。

高级设置中的 `storage.location` / `storage.filename` 保留，但在 `HostSettingsCatalog` 中补充 `Warning`：
“直接修改不会迁移现有数据；请使用‘维护 → 存储位置’完成迁移。”

### 3.9 模块装配与配置迁移

`BuiltInModuleCatalog`：

- 新增 `maintenance-core`（`MaintenanceModule.Definition`）。
- 新增 `maintenance-ui`（`MaintenanceUiModule.Definition`）。
- `DefaultModules` 自动包含它们；`DefaultProviders` 自动包含 `app.maintenance@1=maintenance-core`。
- `SqliteModule.Definition.Provides` 增加 `storage.relocation@1`。
- `MaintenanceUiModule` 构造依赖：`IApplicationMaintenance`、`IStorageRelocation`、`IUiDispatcher`、`IUserDialogService`、`IDiagnosticSink`、`SettingsPageRegistry`。
- `MaintenanceModule` 构造依赖：`MaintenanceOptions`（数据根、安装目录、进程路径、启动参数、版本）、`IApplicationLifecycle`、`IDiagnosticSink`。

`AppConfiguration`：

- 新增 `MaintenanceMigration = "maintenance-v1"`，加入 `KnownMigrations`。
- `ApplyMaintenanceMigration`：
  - 若 `settings-core`、`tasks-core`、`storage-sqlite` 已启用，则把 `maintenance-core`、`maintenance-ui` 加入 `modules`（同时处理 `host.modules` 覆盖层）；
  - `providers.TryAdd("app.maintenance@1", "maintenance-core")`；
  - `providers.TryAdd("storage.relocation@1", "storage-sqlite")`；
  - 写入 `host.modules` / `host.providers`（存在覆盖层时）；
  - 标记迁移。
- `src/Mosaic.Desktop/appsettings.json` 的 `modules`、`providers`、`appliedMigrations` 同步增加。
- `RepairProviderBindings` 保持“只补已启用模块的绑定、不覆盖用户显式绑定”的语义；新模块由迁移负责加入。

`SettingsGroup` / `SettingsSection`：

- `SettingsGroup` 末尾追加 `Maintenance`，不改变已有枚举值顺序。
- `SettingsViewModel` 的 `SettingsSection` 末尾追加 `Maintenance`；`SectionFor` 增加映射；`SettingsSectionNameConverter` 增加文案。
- `zh.xaml` / `en.xaml` 增加 `Settings.Section.Maintenance`、维护页文案、卸载/迁移确认文案、错误文案。
- `SettingsPageContribution(nameof(SettingsGroup.Maintenance), "maintenance.settings", factory)`。

## 4. 影响旧数据、配置、安装升级与模块装配

### 4.1 旧数据目录

- 现有默认数据目录 `%LOCALAPPDATA%\Mosaic`、自定义 `--data-root` 均不改名、不移动。
- 现有数据库文件、迁移表、`appliedMigrations` 不受影响；迁移只复制数据库文件，不改变数据库内部结构。
- 迁移默认保留旧数据库及备份；只有用户显式选择“删除旧数据”才删除。
- 卸载默认保留数据目录；只有显式勾选才删除。

### 4.2 配置文件

- `Config/appsettings.json` 增加 `maintenance-v1` 迁移标记；旧安装首次启动时自动补模块与能力绑定。
- 新增 `Config/storage-relocation.json`（待执行请求）和 `Config/storage-relocation-result.json`（结果）；两者都是运行期文件，不进入 `configVersion` 结构。
- 直接编辑高级设置里的 `storage.location` 仍然只改路径、不迁移；通过 `Warning` 明确提示。
- 迁移写入 `settings["storage.location"]`，保留 `storage` 基线为 `local-app-data`，不破坏 `AppConfiguration.ValidateCoreConfiguration` 的校验。

### 4.3 安装/升级

- MSI UpgradeCode 不变，ProductCode 仍由 WiX 按版本生成；MajorUpgrade 行为不变。
- 新程序集随 publish 输出进入 MSI payload，`build/package.ps1` 的 payload 收集不需要新增文件清单。
- 卸载注册表项仍是 `HKCU\...\Uninstall\{ProductCode}`；应用通过 DisplayName + InstallLocation 定位，兼容旧版本安装。
- 修复安装使用当前 ProductCode；旧版本安装升级到 0.16.7 后，维护页能看到新版本信息。

### 4.4 模块装配

- 旧配置缺少 `maintenance-core` / `maintenance-ui` 时，由 `maintenance-v1` 迁移补齐；否则 `ModuleGraph.Resolve` 会因 `app.maintenance@1` 无绑定而启动失败。
- 用户显式设置的 provider 绑定不被覆盖；只补缺失项。
- 便携/开发运行没有 MSI 注册项，维护页显示为 `Portable`，卸载/修复禁用，存储迁移仍可用。

## 5. 安全与边界

- 卸载只对检测到的 MSI 安装开放；不尝试删除任意目录。
- 删除数据前做多重守卫：
  - 必须是当前 `AppPaths.Root` 的规范化完整路径；
  - 不是盘符根、系统目录、用户目录、`%LOCALAPPDATA%` 本身；
  - 不包含当前运行的程序文件；
  - 路径中不经过重解析点（junction/symlink）到其他位置。
- 助手脚本只接受应用生成的 GUID、PID 和路径；ProductCode 用 `Guid.TryParse` 校验；脚本 ASCII-only，避免编码问题。
- 日志不记录登录令牌、验证码、密码；卸载/迁移日志只记录路径、退出码和错误消息。
- 迁移失败不回滚已成功的复制目标？实际策略：复制或校验失败时删除目标临时文件；配置写入失败时删除目标最终文件，源数据库保持不变。
- 迁移不删除 `%LOCALAPPDATA%\Mosaic\Auth\credentials.dat`；只有“删除本机数据”才删除整个数据根。

## 6. 测试与验收

### 6.1 单元测试

- `Mosaic.Maintenance.Tests`（新增）：
  - ProductCode 定位：匹配 InstallLocation、多个 Mosaic 项、无匹配、非法 GUID、HKLM 回退。
  - 助手脚本生成：路径含空格/中文/`&`/`%`、ProductCode 非法、PID 非法、删除数据开关。
  - 数据根守卫：盘符根、用户目录、`%LOCALAPPDATA%`、程序目录、正常数据根。
  - 退出钩子：助手启动失败时撤销退出；启动成功时接受退出。
- `Mosaic.Storage.Sqlite.Tests`（扩展）：
  - 迁移计划校验：目标等于源、目标在源内、目标不可写、空间不足、目标已有数据库。
  - 迁移成功：数据、迁移表、备份保留策略、旧数据保留/删除两种模式。
  - 迁移失败：源数据库损坏、目标锁定、配置写入失败，源数据不丢失。
  - 幂等：配置已指向目标、源已删除、请求残留、目标临时文件残留。
- `Mosaic.Architecture.Tests`：
  - 新项目依赖白名单、公开 API 清单、设置定义归属、无跨模块 SQL。
- `Mosaic.Desktop.SmokeTests`：
  - 旧配置（无 `maintenance-v1`）启动后自动补模块/绑定并通过 `ModuleGraph.Resolve`。
  - 临时数据根 + 临时目标目录完成一次迁移，重启后任务数据仍可读。

### 6.2 手工/黑盒验收

1. **存储迁移**
   - 新建任务、修改设置、打开浮窗，记录数据。
   - 维护页选择新文件夹，默认保留旧数据，确认并重启。
   - 重启后：任务、设置、浮窗布局完整；`Config/appsettings.json` 的 `storage.location` 指向新目录；旧目录数据库仍在；`-wal`/`-shm` 不残留于新目录。
   - 再迁移回默认位置；确认 `local-app-data` 写回。
   - 目标已有数据库：拒绝并提示。
   - 迁移中关闭应用/强制结束：下次启动能恢复或给出明确失败提示，源数据可读。
2. **卸载**
   - MSI 安装 0.16.7，维护页显示已安装、版本、安装目录。
   - 默认卸载：应用退出，MSI 卸载完成，快捷方式移除，`%LOCALAPPDATA%\Mosaic` 数据保留。
   - 勾选删除数据：二次确认后，卸载完成且数据目录删除；登录凭据、日志一并删除。
   - 用户取消 MSI：应用已退出但数据未被删除；重新安装后可继续使用。
   - 便携运行：卸载/修复禁用，显示手动删除说明。
3. **修复安装**
   - 删除安装目录中的某个 DLL，维护页点“修复安装”，MSI 修复后文件恢复，应用可启动。
4. **升级**
   - 从 0.16.6 升级到 0.16.7：旧配置自动补 `maintenance-v1`，维护页出现，原有数据位置不变。
   - 从更旧版本（缺 auth-sync/floating 迁移）升级：所有迁移按顺序执行，启动无 `Provider binding is missing` 错误。

### 6.3 构建与发布验收

- `dotnet build Mosaic.sln -c Debug`、`-c Release` 0 警告 0 错误。
- `dotnet test Mosaic.sln -c Release` 全绿（至少 Architecture、Storage.Sqlite、Settings.Core、Desktop.SmokeTests）。
- `build/package.ps1 -ProductVersion 0.16.7` 成功；MSI 管理安装提取校验通过。
- `build/test-install.ps1` 扩展：安装、升级、卸载保留数据、卸载删除数据（用测试数据根，不碰真实用户数据）。
- 更新 `build/public-api.txt`、`build/project-boundaries.json`、`build/release-content.json`、`build/published-release-index.json`、`docs/releases/v0.16.7.md`、`docs/implementation-status.md`。

## 7. 分阶段实施

虽然是一次完整交付，内部按以下顺序落地，便于每步验证：

1. **契约与存储迁移核心**
   - `Mosaic.Storage.Contracts` 新模型/接口；
   - `Mosaic.Storage.Sqlite` 迁移服务 + 启动执行器 + 测试；
   - Desktop 启动钩子与 `IStorageRelocationSettings` 适配；
   - 配置迁移 `maintenance-v1` 框架（先只补 storage.relocation）。
2. **维护模块与卸载**
   - `Mosaic.Maintenance.Contracts` / `.Windows`；
   - MSI 定位、助手脚本、守卫、退出钩子；
   - 单元测试。
3. **维护页 UI**
   - `Mosaic.Maintenance.Wpf`、`SettingsGroup/Section.Maintenance`、本地化、页面注册；
   - 存储位置卡片、卸载/修复卡片、待执行迁移横幅；
   - 手动 UI 验收。
4. **发布交付**
   - 文档、release 条目、public API、boundaries；
   - Debug/Release 构建、测试、MSI、test-install；
   - 版本 0.16.7，提交、打 tag、发布 Release。

## 8. 待确认的默认决策

以下按推荐值执行；如需调整，在实施前改这一节即可：

1. **卸载默认保留数据**，删除数据必须显式勾选并二次确认。
2. **包含“修复安装”入口**，与卸载放在同一维护页。
3. **存储迁移只迁移数据库**（任务、设置、浮窗布局等 SQLite 数据）；登录凭据、日志、配置仍留在数据目录。整个数据目录迁移列为后续独立需求。
4. **迁移默认保留旧数据**；删除旧数据是显式选项。
5. **目标位置已有数据库时拒绝**，本轮不做“接管已有数据库”。
6. **迁移完成后自动重启**；用户也可以选择“稍后手动重启”，请求保留到下次启动执行。
7. **便携/开发运行不支持应用内卸载**，只显示手动删除说明；存储迁移仍可用。

## 9. 实施前置条件

当前工作区已有 0.16.6 的未提交变更（账户页、浏览器授权页、打包等）。本需求应：

- 等 0.16.6 变更提交并发布后，从 `main`（或发布分支）新开 `feat/maintenance-and-storage-relocation`；
- 不与 0.16.6 的未提交文件混在同一提交；
- 按 AGENTS.md 作为**一个完整交付单元**提交：源码、测试、用户可见行为、发布说明、实施记录同一次提交；
- 最终答复与 `docs/implementation-status.md` 同时写明：
  - 根因位置：`HostSettingsCatalog.cs:23-26`、`AppConfiguration.cs:382-385`、`Bootstrapper.cs:34-41`、`ShutdownCoordinator.cs:37`、`Package.wxs`；
  - 改动位置：上表所有新增/修改文件及类型/方法/行号；
  - 迁移逻辑与迁移验收项；
  - 对已有安装的影响。
