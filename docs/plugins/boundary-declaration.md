# 边界声明

边界声明是 Mosaic “模块可以依赖谁”的唯一事实来源。它由三个文件和一个测试共同构成，任何插件新增或删除程序集都必须同步更新，否则架构测试失败。

| 文件 | 作用 |
| --- | --- |
| `build/project-boundaries.json` | 每个 `src` 工程允许引用的其他 `Mosaic.*` 程序集 |
| `build/public-api.txt` | 每个工程对外导出的公开类型清单 |
| `Directory.Packages.props`（仓库根目录） | 集中包版本；框架包使用受测试约束 |
| `tests/Mosaic.Architecture.Tests/BoundaryTests.cs` | 强制执行以上声明与额外硬规则 |

## 1. 允许引用矩阵

格式：键是工程名，值是允许引用的 `Mosaic.*` 程序集数组。

```json
{
  "[PLUGIN-NAME].Contracts": ["Mosaic.Contracts"],
  "[PLUGIN-NAME].Core": ["Mosaic.Contracts", "[PLUGIN-NAME].Contracts"],
  "[PLUGIN-NAME].Wpf": [
    "Mosaic.Contracts",
    "[PLUGIN-NAME].Contracts",
    "Mosaic.Presentation.Contracts",
    "Mosaic.Presentation.Wpf"
  ],
  "Mosaic.Desktop": ["...所有被宿主装配的工程..."]
}
```

规则（`BoundaryTests.cs:22-44`）：

1. **全覆盖**：`src` 下每个 `.csproj` 的工程名都必须作为键出现在 JSON 中，缺失即失败。
2. **只查 `Mosaic.*`**：第三方包不参与此矩阵，但受第 3 节的框架约束。
3. **精确匹配**：引用不在白名单内即失败；数组大小写敏感。
4. **闭合性**：被引用的工程本身也必须出现在 JSON 中。
5. **变更即声明**：新增引用就改 JSON，不允许“先引用后补声明”。

当前矩阵（节选，完整内容以文件为准）：

| 工程 | 允许引用 |
| --- | --- |
| `Mosaic.Contracts` | 无 |
| `Mosaic.Kernel` | `Mosaic.Contracts` |
| `Mosaic.Presentation.Contracts` | `Mosaic.Contracts`、`Mosaic.FloatingWindows.Contracts` |
| `Mosaic.Presentation.Wpf` | 无 |
| `Mosaic.Shell.Wpf` | `Mosaic.Contracts`、`Mosaic.Presentation.Contracts`、`Mosaic.Presentation.Wpf`、`Mosaic.Settings.Contracts` |
| `Mosaic.Tasks.Core` | `Mosaic.Contracts`、`Mosaic.Tasks.Contracts`、`Mosaic.Tasks.Persistence.Contracts`、`Mosaic.Settings.Contracts` |
| `Mosaic.Tasks.Wpf` | `Mosaic.Contracts`、`Mosaic.Tasks.Contracts`、`Mosaic.Presentation.Contracts`、`Mosaic.Presentation.Wpf`、`Mosaic.Settings.Contracts`、`Mosaic.FloatingWindows.Contracts` |
| `Mosaic.Desktop` | 所有内置模块与基础设施工程 |

## 2. 公开 API 清单

`build/public-api.txt` 是“可评审的公开面”：每行一个完全限定的公开类型，按字母序排列。

- 校验逻辑：反射读取 `src` 下每个工程 `bin/<Configuration>/<tfm>/<Assembly>.dll` 的公开类型（含嵌套公开类型），与清单逐项比较（`BoundaryTests.cs:59-84`）。
- 新增插件时，为每个公开类型追加一行：

```text
Mosaic.[PLUGIN].Contracts.I[PLUGIN]Api
Mosaic.[PLUGIN].Contracts.[PLUGIN]Item
Mosaic.[PLUGIN].Core.[PLUGIN]Module
```

- 删除或改名公开类型时，必须同时删除/替换清单行；旧类型保留 `[Obsolete]` 不改变清单。
- `XamlGeneratedNamespace.GeneratedInternalTypeHelper` 由 WPF 编译器生成，已在测试中豁免，不写入清单。
- 内部类型不要写成 `public`。测试需要访问内部类型时没有可见性豁免：`src` 下任一 `.cs` 出现 `InternalsVisibleTo` 即失败（`BoundaryTests.cs:88-91`）。正确做法是把被测类型改为 `public` 并登记 `build/public-api.txt`。

## 3. 框架与平台边界

### 3.1 Windows 工程白名单

只有下列工程可以使用 `net10.0-windows`（`BoundaryTests.cs:47-51`）；其中不使用 WPF 与 `CommunityToolkit.Mvvm` 的只有 `Mosaic.Auth.Windows`、`Mosaic.Maintenance.Windows` 两个——它们因 DPAPI/注册表等 Windows API 才留在 Windows TFM 上。WPF 程序集与 `CommunityToolkit.Mvvm` 的白名单更窄（`BoundaryTests.cs:55-56`），不包含这两个工程。

```text
Mosaic.Desktop
Mosaic.Shell.Wpf
Mosaic.Presentation.Contracts
Mosaic.Presentation.Wpf
Mosaic.Tasks.Wpf
Mosaic.Settings.Wpf
Mosaic.DesktopPresence.Wpf
Mosaic.FloatingWindows.Wpf
Mosaic.Auth.Windows
Mosaic.Auth.Wpf
Mosaic.Maintenance.Windows
Mosaic.Maintenance.Wpf
```

新增带界面的插件需要把工程名加入：

1. `BoundaryTests.cs` 的 `IsWindowsProject`（`BoundaryTests.cs:47-51`，决定测试加载哪个 TFM 的 DLL）。
2. 如果工程引用 `CommunityToolkit.Mvvm`，加入 `BoundaryTests.cs:55` 的包白名单；如果引用 WPF 程序集，加入 `:56` 的白名单；WPF 编译器生成的 `GeneratedInternalTypeHelper` 豁免清单在 `:76`。
3. `build/project-boundaries.json`，并提交该工程新生成的 `packages.lock.json`（`Directory.Build.props:9` 开启了 `RestorePackagesWithLockFile`）。

其余工程必须保持 `net10.0` 平台中立，方便在普通 CI 上单测。

### 3.2 包边界

| 包 | 唯一允许的工程 |
| --- | --- |
| `Microsoft.Data.Sqlite`、`SQLitePCLRaw.*` | `Mosaic.Storage.Sqlite` |
| `Microsoft.Extensions.*` | `Mosaic.Desktop` |
| `CommunityToolkit.Mvvm` | 第 3.1 节白名单中的界面工程 |

### 3.3 源码级硬规则

`BoundaryTests.cs:86-100`（`Source_has_no_service_locator_reflection_bypass_or_cross_module_sql`）扫描 `src` 下全部 `.cs`：

- 禁止 `InternalsVisibleTo`。
- 禁止 `Assembly.Load`、`Type.GetType`、`Activator.CreateInstance`、`ServiceLocator`。
- 非 `Mosaic.Desktop` 工程禁止 `IServiceProvider`、`IHost`、`GetRequiredService`、`GetService<`。
- 除白名单四个目录（`Mosaic.Storage.Sqlite`、`Mosaic.Tasks.Sqlite`、`Mosaic.Settings.Sqlite`、`Mosaic.FloatingWindows.Sqlite`）外，禁止 SQL 关键字模式（`SELECT ... FROM`、`INSERT INTO`、`UPDATE ... SET`、`CREATE TABLE`、`DELETE FROM`，`:94-95`）。新建 `Mosaic.[PLUGIN].Sqlite` 必须先把目录名加入这条白名单。
- 非 `Mosaic.Settings.Sqlite` 工程禁止直接操作 `settings` 表。
- `Mosaic.Tasks.Core`、`Mosaic.Kernel`、`Mosaic.DesktopPresence.Core` 禁止 `System.Windows`。

## 4. 设置归属声明

`BoundaryTests.cs:102-130`（`Setting_definitions_are_owned_by_the_module_that_declares_them`）要求每个 `SettingDefinition` 只能出现在拥有它的模块工程里。归属字典（`BoundaryTests.cs:104-115`）逐字如下：

| 设置命名空间 | 拥有工程（字典值） |
| --- | --- |
| `host` | `Mosaic.Settings.Core` |
| `tasks-core` | `Mosaic.Tasks.Core` |
| `tasks-ui` | `Mosaic.Tasks.Wpf` |
| `shell` | `Mosaic.Shell.Wpf` |
| `storage-sqlite` | `Mosaic.Storage.Sqlite` |
| `desktop-presence` | `Mosaic.DesktopPresence.Wpf` |
| `floating-windows` | `Mosaic.FloatingWindows.Wpf` |
| `auth-core` | `Mosaic.Auth.Core` |
| `sync-core` | `Mosaic.Sync.Core` |

检查方式（`BoundaryTests.cs:116-128`）：只扫描含 `SettingValueKind` 的行，用正则匹配**同一行**内的 `new("设置键", "模块 ID")` 或 `Definition("设置键", "模块 ID")`，且两个参数都必须是字符串**字面量**；命中后要求字典里有该模块 ID，并且声明文件所在工程名等于字典值（`:125-126`）。

由此产生三条已知盲区，改动时不要依赖测试兜底：

1. **常量/工厂传参的定义不被检查**。`HostSettingsCatalog.cs:27`、`:33`（第二参数是 `ModuleId` 常量）、`AuthSettingsSource.cs:15`、`ReleaseSummaryModule.cs:128`、`PresenceSettings.cs:46-47` 都逃过正则，这些设置的归属由人工维护。
2. **`host` 的字典值与实际声明工程不一致**：字典写 `Mosaic.Settings.Core`，`host.*` 设置实际声明在 `Mosaic.Desktop/HostSettingsCatalog.cs`。正因为第 1 条，这个矛盾目前不会让测试失败；一旦把 `ModuleId` 换成字面量，测试会以 `Setting '...' belongs to Mosaic.Settings.Core but is declared in ...` 报错，届时要同步把字典改成 `Mosaic.Desktop`。
3. **`release-summary` 不在字典里**：`ReleaseSummaryModule.cs:125`、`:128` 声明了该命名空间，但字典无此项；同样因为常量传参未被命中。

新增插件设置时，除了补归属表，还应把定义写成字面量参数（`new("设置键", "模块 ID")`），别让第 1 条的盲区落在自己身上：

1. 在归属表中加入 `"[PLUGIN-ID]" = "Mosaic.[PLUGIN].Core"`（或实际拥有该设置的工程）。
2. 设置键格式为 `[PLUGIN-ID].[setting-name]`，声明写在拥有工程内。
3. 禁止在 UI 工程声明 Core 语义的设置，除非该设置只影响界面。

## 5. 新增插件的完整边界步骤

```powershell
# 1. 建工程
dotnet new classlib -n Mosaic.[PLUGIN].Contracts -o src/Modules/[PLUGIN]/Mosaic.[PLUGIN].Contracts -f net10.0
dotnet new classlib -n Mosaic.[PLUGIN].Core -o src/Modules/[PLUGIN]/Mosaic.[PLUGIN].Core -f net10.0
dotnet sln Mosaic.sln add src/Modules/[PLUGIN]/Mosaic.[PLUGIN].Contracts/Mosaic.[PLUGIN].Contracts.csproj
dotnet sln Mosaic.sln add src/Modules/[PLUGIN]/Mosaic.[PLUGIN].Core/Mosaic.[PLUGIN].Core.csproj

# 2. 加引用（只按矩阵允许的方向）
dotnet add src/Modules/[PLUGIN]/Mosaic.[PLUGIN].Core reference src/Mosaic.Contracts/Mosaic.Contracts.csproj
dotnet add src/Modules/[PLUGIN]/Mosaic.[PLUGIN].Core reference src/Modules/[PLUGIN]/Mosaic.[PLUGIN].Contracts/Mosaic.[PLUGIN].Contracts.csproj

# 3. 登记边界与公开 API 后验证
build/test.ps1 -Configuration Release -SkipDesktop
```

`build/project-boundaries.json` 追加：

```json
"Mosaic.[PLUGIN].Contracts": ["Mosaic.Contracts"],
"Mosaic.[PLUGIN].Core": ["Mosaic.Contracts", "Mosaic.[PLUGIN].Contracts"]
```

`Mosaic.Desktop` 的数组追加：

```json
"Mosaic.[PLUGIN].Contracts",
"Mosaic.[PLUGIN].Core"
```

带持久化的插件还要建 `Mosaic.[PLUGIN].Sqlite`：在 `project-boundaries.json` 中声明它允许引用 `Mosaic.Contracts`、`Mosaic.Storage.Contracts`、`Mosaic.[PLUGIN].Contracts`，并把 `src/Modules/[PLUGIN]/Mosaic.[PLUGIN].Sqlite` 加入 `BoundaryTests.cs:94` 的 SQL 白名单（该名单是硬编码目录名，不按 `*.Sqlite` 通配）。

## 6. 失败信息对照

| 测试输出（逐字） | 含义 | 修复 |
| --- | --- | --- |
| `Undeclared project: {name}` | 新工程未在 `build/project-boundaries.json` 声明（`BoundaryTests.cs:29`） | 追加键 |
| `Assert.Contains() Failure ... Mosaic.[X]` | 引用了未授权的 Mosaic 程序集 | 删除引用或补白名单 |
| 测试名 `Exported_types_match_reviewable_api_inventory` 失败 | 公开类型与 `public-api.txt` 不一致（`BoundaryTests.cs:59-84`） | 按反射输出增删行 |
| `Unknown setting module '{id}' for '{key}' in {file}` | 设置归属表缺项（`BoundaryTests.cs:125`） | 在 `BoundaryTests.cs` 归属表补 ID |
| `Setting '{key}' belongs to {owner} but is declared in {file}` | 设置声明放错工程（`BoundaryTests.cs:126`） | 移到拥有工程 |
| 源码扫描规则失败（测试名含 `Source_has_no_...` 前缀） | 出现反射/容器/SQL/`System.Windows` 违规（`BoundaryTests.cs:88-98`） | 按规则重构 |

## 7. 验收标准

- `build/test.ps1 -SkipDesktop` 全绿。
- 边界文件、公开 API 清单、设置归属表与代码一致。
- 宿主 `Mosaic.Desktop` 只通过公开契约接触插件，不出现对插件内部类型的 `using`。

---

相关文档：[插件规范](plugin-spec.md) · [框架规范](framework-spec.md) · [UI 规范](ui-spec.md) · [插件制作教程](README.md)