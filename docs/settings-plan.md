# Mosaic：设置功能规划（定稿）

状态：规划已按最新界面与布局要求修订，待实施。建议作为 0.2.0 交付。本文只规划，不包含代码改动。

## 1. 已确认范围

- 前文清单中的所有可调行为参数全部纳入设置；本次再增加固定右栏所需的编辑器宽度与栏间距两项。
- 删除独立“普通设置”分区。原普通项分别迁入“任务”和“外观与布局”，不再存在无归属的通用参数。
- 高级设置开关默认关闭，并统一控制所有分区的设置行。关闭时任何分区都不显示可编辑参数；开启后才显示当前分区的全部行。
- 四个分区固定为：任务、外观与布局、存储与系统、文案。分区只表达展示归类，参数语义仍由拥有它的模块负责。
- 原标为“不建议”的项全部保留在高级门槛之后，并标记风险、范围和重启要求。
- 开启高级设置后，每行显示默认值、当前值、约束说明、恢复默认、风险提示和“下次启动生效”徽标。
- 任务页始终保持左右结构。任务列表在左，新建/编辑任务栏固定在整体内容区右侧；窗口缩小时只启用紧凑排版、换行和滚动，不把任务栏移到左侧，也不让它铺满任务页。
- 默认窗口尺寸使用 1504×1012 DIP，默认最小窗口尺寸使用 1100×720 DIP。启动时若工作区更小，只收缩实际显示尺寸并保证位于可见区域，不改写保存的启动尺寸。
- 唯一不设项的是模块间协议常量（错误码字符串、请求字段结构、单实例管道名、主题资源键）。它们不是运行参数；改动属于契约版本升级，由架构测试锁定。

## 2. 职责与新增模块

| 模块 | 提供 | 需要 | 唯一职责 |
| --- | --- | --- | --- |
| settings-sqlite | settings.repository@1 | sqlite.session@1 | 设置表迁移、参数化 SQL、值映射 |
| settings-core | settings.api@1 | settings.repository@1、各设置来源 | 定义注册、聚合、校验、持久化请求、恢复默认、即时/重启路由 |
| settings-ui | 设置导航与页面 | settings.api@1、通用界面服务 | 高级可见性门、四项分组导航、按类型渲染、警告与重启徽标 |
| host（基础设施，不是模块） | 宿主设置来源 | — | appsettings.json 的读取/校验/原子写回、日志、启动窗口与单实例参数 |

归属规则：

- 参数的语义仍归原模块：任务限额归 tasks-core，交互行为归 tasks-ui，主题与布局归 shell，存储参数归 storage-sqlite，宿主参数归 host。
- settings-core 只保存“定义 + 值 + 校验结果”，不实现任务规则、不执行 SQL、不决定布局。
- settings-ui 只调用 settings.api，不直接读写设置表，不直接修改 appsettings.json；它不解释任务、存储或布局语义，也不按 `ModuleId` 或参数前缀猜测归属，只使用各来源提交的分组元数据渲染导航与动态控件。
- 设置导航仍由 settings-ui 贡献 `IconKey = "settings"`。外壳只把这个通用图标键映射为矢量齿轮，不识别设置模块内部。

## 3. 参数总表（57 项）

### 3.1 原普通项迁移与可见性

现有 55 项中，8 项带有 `SettingCategory.Normal`。全部迁移后，不再有 `SettingsSection.Normal`，这 8 项统一改为高级门槛后的参数：

| 原普通项 | 新分区 | 归属 |
| --- | --- | --- |
| `appearance.theme` | 外观与布局 | shell |
| `ui.language` | 外观与布局 | shell |
| `tasks.confirmDelete` | 任务 | tasks-ui |
| `tasks.confirmUnsaved` | 任务 | tasks-ui |
| `tasks.defaultFilter` | 任务 | tasks-ui |
| `tasks.showCharacterCount` | 任务 | tasks-ui |
| `tasks.autoSwitchToAllOnHidden` | 任务 | tasks-ui |
| `tasks.sortOrder` | 任务 | tasks-core/仓储 |

规则：

- 不再提供“普通”分区，也不保留仅因“普通”而默认可见的行。
- `ShowAdvanced = false` 时，四个分区的设置行数都为 0；分区导航和开启说明仍可见。
- `ShowAdvanced = true` 时，只显示当前分区内的全部参数，不把其他分区参数混入。
- 关闭高级设置时禁用保存、放弃、分区恢复和全部恢复，避免通过隐藏控件间接修改不可见参数。
- 高级开关不写入数据库，进入设置页时默认关闭。若已有未保存修改，关闭开关前复用未保存确认；取消则保持开启。

### 3.2 任务（20 项）

| ID | 默认值 | 范围/枚举 | 生效 |
| --- | --- | --- | --- |
| `tasks.confirmDelete` | true | bool | 即时 |
| `tasks.confirmUnsaved` | true | bool | 即时 |
| `tasks.defaultFilter` | All | All / Open / Completed | 即时 |
| `tasks.showCharacterCount` | true | bool | 即时 |
| `tasks.autoSwitchToAllOnHidden` | true | bool | 即时 |
| `tasks.sortOrder` | CreatedDesc | CreatedDesc / CreatedAsc / TitleAsc / TitleDesc | 即时 |
| `tasks.title.maxCharacters` | 200 | 1–1000 | 重启 |
| `tasks.notes.maxCharacters` | 20,000 | 1,000–200,000 | 重启 |
| `tasks.title.required` | true | bool | 重启 |
| `tasks.defaultCompleted` | false | bool | 重启 |
| `tasks.revisionStart` | 1 | 1–1,000,000 | 重启 |
| `tasks.layout.compactBreakpoint` | 920 DIP | 600–1400 | 即时 |
| `tasks.layout.editorPaneWidth` | 480 DIP | 360–720 | 即时 |
| `tasks.layout.paneGap` | 24 DIP | 0–64 | 即时 |
| `tasks.editor.compactActionsWidth` | 480 DIP | 360–900 | 即时 |
| `tasks.editor.compactActionsHeight` | 680 DIP | 480–1000 | 即时 |
| `tasks.list.minColumnWidth` | 300 DIP | 220–500 | 即时 |
| `tasks.editor.notesMinHeight` | 128 DIP | 96–360 | 即时 |
| `tasks.button.minWidth` | 96 DIP | 72–160 | 即时 |
| `tasks.button.minHeight` | 40 DIP | 32–56 | 即时 |

`tasks.layout.compactBreakpoint` 的含义改为“进入任务页紧凑排版”，只调整间距、页头动作排列与操作栏换行，不再切换列表/详情为单页。

### 3.3 外观与布局（14 项）

| ID | 默认值 | 范围 | 生效 |
| --- | --- | --- | --- |
| `appearance.theme` | FollowSystem | FollowSystem / Light / Dark / HighContrast | 即时 |
| `ui.language` | FollowSystem | FollowSystem / Chinese / English | 即时 |
| `shell.sidebar.collapseBelowWidth` | 1180 DIP | 900–1600 | 即时 |
| `shell.sidebar.expandAtWidth` | 1200 DIP | collapse+20–1800 | 即时 |
| `shell.sidebar.fullWidth` | 190 DIP | 160–260 | 即时 |
| `shell.sidebar.railWidth` | 64 DIP | 48–88 | 即时 |
| `shell.page.marginLeft` | 32 DIP | 8–64 | 即时 |
| `shell.page.marginTop` | 30 DIP | 8–64 | 即时 |
| `shell.page.marginRight` | 28 DIP | 8–64 | 即时 |
| `shell.page.marginBottom` | 28 DIP | 8–64 | 即时 |
| `shell.window.minWidth` | 1100 DIP | 1000–2400；保证左右栏同时存在 | 重启 |
| `shell.window.minHeight` | 720 DIP | 640–1200 | 重启 |
| `startup.width` | 1504 DIP | 1000–3840；不得小于 `shell.window.minWidth` | 重启 |
| `startup.height` | 1012 DIP | 640–2160；不得小于 `shell.window.minHeight` | 重启 |

### 3.4 存储与系统（12 项，全部重启生效）

| ID | 默认值 | 归属 | 范围/说明 |
| --- | --- | --- | --- |
| `storage.filename` | mosaic.sqlite | host | 合法单个文件名；改名后新库为空，旧库保留 |
| `storage.location` | local-app-data | host | local-app-data 或绝对路径；换位置不自动搬迁 |
| `storage.queueCapacity` | 128 | storage-sqlite | 16–4096 |
| `storage.busyTimeoutSeconds` | 3 | storage-sqlite | 1–30 |
| `storage.backupRetention` | 5 | storage-sqlite | 0–50；0 表示不保留自动备份 |
| `storage.journalMode` | Delete | storage-sqlite | Delete / Wal；WAL 会产生 -wal/-shm，需完整备份与切换测试 |
| `storage.connectionMode` | ReusedSingle | storage-sqlite | ReusedSingle / PooledPerOperation；两者仍由单消费者队列串行执行 |
| `host.modules` | 七模块（storage-sqlite、settings-sqlite、settings-core、tasks-sqlite、tasks-core、tasks-ui、settings-ui） | host | 启用列表；复用 AppConfiguration 校验 |
| `host.providers` | 五绑定 | host | 能力绑定；禁用模块残留绑定仍拒绝 |
| `logging.level` | Information | host | Debug / Information / Warning / Error / None |
| `logging.retentionDays` | 0 | host | 0–365；0 表示不自动清理 |
| `diagnostics.singleInstanceRetryMilliseconds` | 250 | host | 50–2000 |

启动尺寸大于当前工作区时，只按工作区计算本次显示尺寸和位置，并把窗口放回可见区域；保存值保持不变。启动尺寸小于最小尺寸、或 `startup.*` 小于对应 `shell.window.min*` 时拒绝保存。

### 3.5 文案（11 项，即时生效）

| ID | 默认值 |
| --- | --- |
| `ui.text.emptyAllTitle` | 这里还没有任务 |
| `ui.text.emptyAllDescription` | 新建一项任务，给今天一个开始。 |
| `ui.text.emptyOpenTitle` | 没有未完成任务 |
| `ui.text.emptyOpenDescription` | 新建任务或恢复一项任务后，会显示在这里。 |
| `ui.text.emptyCompletedTitle` | 还没有已完成的任务 |
| `ui.text.emptyCompletedDescription` | 完成一项任务后，会显示在这里。 |
| `ui.text.untitledTask` | 未命名任务 |
| `ui.text.saveSuccess` | 已保存到此电脑 |
| `ui.text.deleteSuccess` | 任务已删除 |
| `ui.text.completedSuccess` | 已标记完成 |
| `ui.text.restoredSuccess` | 已恢复为未完成 |

文案为空时回退默认值；长度上限 200 码点。文案只改变显示，不参与业务判断。

## 4. 契约草案

```csharp
public enum SettingValueKind { Boolean, Integer, Double, Enum, String, ModuleList, ProviderMap }
public enum SettingCategory { Normal, Advanced }
public enum SettingApplyMode { Immediate, Restart }
public enum SettingPersistence { UserSettings, HostConfig }
public enum SettingsGroup { Tasks, Appearance, StorageSystem, Text }

public sealed record SettingDefinition(
    string Id,
    string ModuleId,
    string DisplayName,
    string Description,
    SettingValueKind Kind,
    object? DefaultValue,
    object? Min,
    object? Max,
    IReadOnlyList<string>? AllowedValues,
    SettingsGroup Group,
    SettingCategory Category,
    SettingApplyMode ApplyMode,
    SettingPersistence Persistence,
    string? Warning = null);

public interface ISettingsApi
{
    Task<OperationResult<IReadOnlyList<SettingDefinition>>> ListDefinitionsAsync(CancellationToken ct = default);
    Task<OperationResult<IReadOnlyDictionary<string, object>>> GetValuesAsync(CancellationToken ct = default);
    Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(
        IReadOnlyDictionary<string, object> changes, CancellationToken ct = default);
    Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken ct = default);
}

public interface ISettingSource
{
    IReadOnlyList<SettingDefinition> Definitions { get; }
    Task ApplyAsync(IReadOnlyDictionary<string, object> values, CancellationToken ct = default);
}

public interface IHostSettingSource : ISettingSource
{
    object? GetValue(string id);
    Task<OperationResult> PersistAsync(
        IReadOnlyDictionary<string, object> values, CancellationToken ct = default);
}
```

settings-core 判定持久化位置：

- `UserSettings`：写入 settings 表；由 settings-sqlite 负责 SQL 与迁移。
- `HostConfig`：只调用 `IHostSettingSource.PersistAsync`，由宿主原子写回 appsettings.json；不在设置表复制一份。
- 重启参数保存后标记 pending，本次运行继续使用启动时快照；下次启动读取新值。
- 即时参数保存成功后，按 UI Dispatcher 调用对应来源的 `ApplyAsync`。
- 为兼容已有定义结构保留 `SettingCategory`，但迁移后所有定义均为 `Advanced`；测试必须断言不存在 `Normal` 定义。设置页可见性由 `ShowAdvanced` 统一门控，不再按单行类型分别放行。
- 每个参数来源在 `SettingDefinition` 中声明 `SettingsGroup`。settings-ui 只按该公开元数据过滤，不再根据 `ModuleId`、`tasks.*` 或 `ui.text.*` 前缀判断参数属于哪个分区。
- 分区导航只渲染至少存在一个已注册定义的分组；参数所属模块未激活时不创建该模块的设置行，也不显示伪造占位项。

## 5. 装配顺序与数据流

```text
storage-sqlite
  → settings-sqlite（settings 表迁移）
  → settings-core（加载定义与值）
  → tasks-sqlite → tasks-core → tasks-ui
  → shell 设置来源注册
  → settings-ui（最后启动，看到全部定义）
```

- 模块激活时把自身 `ISettingSource` 注册进 settings-core；停止时撤销。
- 宿主在创建 ShellWindow 前注册 shell/主题/启动参数来源。
- settings-core 保留被禁用模块的持久化值；模块重新启用后恢复，不因暂时禁用而丢设置。
- 重复参数 ID、同一参数多个来源、未声明归属，均在注册时拒绝。

数据流：

```text
设置界面读取定义与当前值
  → 提交变更
  → settings-core 校验类型、范围、跨字段与危险确认
  → UserSettings 走 settings.repository / settings-sqlite
  → HostConfig 走宿主来源校验并原子写回
  → 即时项 Apply，重启项标记 pending
```

## 6. 设置界面

导航贡献：`设置`，ViewId `settings.home`，Order 900（任务为 100）。

布局：

- 左侧分区固定为：任务、外观与布局、存储与系统、文案；不存在“普通”或单独的“系统与模块”入口。
- 顶部“高级设置”开关默认关闭。关闭时四个分区都不显示设置行，只显示开启说明；开启后才显示当前分区全部参数。
- `ShowAdvanced = false` 时禁用保存、放弃、分区恢复和全部恢复；开关自身、分区导航和说明保持可用。
- 开关状态不持久化。存在未保存修改时关闭开关，必须先通过通用未保存确认；取消后保持开启并保留输入。
- 每行显示名称、说明、当前值、默认值、约束、恢复默认按钮、风险图标和“需重启”徽标。
- 控件按类型渲染：开关、数字框、下拉框、文本框、模块多选、provider 映射编辑。
- 保存前逐项校验；字段错误就地显示；跨字段错误显示在相关组顶部。
- 保存成功后：即时项刷新界面；重启项显示“下次启动生效（N 项）”。
- 恢复默认支持单项、分区、全部；全部恢复会提示同时清除已禁用模块保存的值。
- 关闭设置页或切换分区时提示未保存修改，复用通用 `INavigationGuard`。

导航图标：

- 设置仍由 settings-ui 贡献 `IconKey = "settings"` 和可读标签“设置”。
- 外壳按通用图标键映射到静态矢量 `Geometry` 齿轮，用 `Path` 绘制；不再使用 Unicode `⚙` 字符。
- 齿轮填充继承按钮前景色，因此在普通、悬停、选中、High Contrast 和完整/图标侧栏下保持一致；点击范围仍为完整导航按钮。
- `AutomationProperties.Name` 与 `ToolTip` 继续来自导航项的公开 `Label`，不把“设置”业务名称硬编码进外壳。

任务页右栏：

- 任务内容区使用固定三列：左侧列表 `*`、可调间距、右侧编辑栏。右栏宽度取 `tasks.layout.editorPaneWidth`，默认 480 DIP；间距取 `tasks.layout.paneGap`，默认 24 DIP。
- `TaskEditorPane` 始终位于第 2 列且始终可见。紧凑模式可以降低页边距、压缩页头动作并换行，但不能把编辑栏移到第 0 列、隐藏列表或让编辑栏跨满内容区。
- 宽度不足时优先保持两栏结构，再压缩允许的内部间距；列表和编辑内容分别在自身区域内滚动。窗口最小尺寸承担“两栏都可用”的最终保护。
- 页头右侧“新建任务”按钮继续保留；它打开或重置右侧编辑栏，不创建覆盖层或新的全屏编辑页。

危险项二次确认：

- 关闭标题必填、开启默认完成、修改 Revision 起点。
- 切换 WAL、切换 PooledPerOperation。
- 修改数据库文件名/位置、禁用模块。
- 恢复全部默认。
确认框列出影响范围与不可逆边界；用户取消则不提交。

## 7. 各模块如何消费参数

- tasks-core：激活时读取任务规则，构造 `TaskSettings` 快照；`TasksApi` 增加 `GetConstraintsAsync`，返回当前标题/备注限额与必填规则。任务 UI 从任务 API 读取显示值，不直接读设置。
- tasks-ui：行为开关、默认筛选、排序、文案覆盖在 ViewModel 初始化时读取；即时变更后刷新绑定。空标题任务显示 `ui.text.untitledTask`。
- tasks-ui：负责左右固定双栏布局、编辑栏宽度、栏间距、紧凑操作栏与列表最小宽度；不读取或修改 ShellWindow。
- tasks-core/仓储：排序扩展 `TaskQuery.SortOrder`；仓储契约拥有排序 SQL，UI 不自行排序。
- shell：主题来源调用 `MosaicThemeManager.Apply`；布局参数重算侧栏宽度、断点、页面边距、默认/最小窗口尺寸，并将 `IconKey = "settings"` 渲染为动态前景色矢量齿轮。
- storage-sqlite：队列容量、锁等待、备份保留、日志模式、连接模式在模块创建时读取 HostConfig；日志模式与连接模式在打开数据库前应用。
- host：模块启用/provider 绑定、日志级别/保留、启动窗口尺寸、单实例退避从 appsettings 读取；设置界面只提交申请。

## 8. 危险项防护

| 危险项 | 风险 | 防护 |
| --- | --- | --- |
| 标题必填关闭 | 空标题任务、列表不可辨识 | 列表显示“未命名任务”；保存时允许空标题；搜索/排序仍稳定 |
| 默认完成开启 | 新任务不在“未完成”筛选出现 | 保存后按当前筛选自动切换可见状态；默认筛选不受影响 |
| Revision 起点修改 | 与旧任务版本比较混淆 | 仅新任务使用新起点；更新仍 +1；冲突测试覆盖新旧混合 |
| WAL | 备份/关闭/文件集合变化 | 启动切换前 checkpoint；备份使用 SQLite 一致性备份；测试 WAL→DELETE 往返 |
| PooledPerOperation | 连接重置与事务边界 | 仍由单消费者队列串行执行；每次操作独立连接并重置；禁止并发事务 |
| 改数据库文件名/位置 | 新库为空、旧数据“消失” | 明确提示不搬迁；旧文件保留；重启后新库初始化；安装升级测试不覆盖旧库 |
| 禁用模块 | 入口与能力不一致 | 复用现有禁用矩阵；配置校验拒绝缺少依赖；重新启用恢复保存的设置 |
| 恢复全部默认 | 清掉禁用模块的设置 | 二次确认，列出受影响模块；取消不写入 |
| 文案覆盖 | 空文案或过长破坏布局 | 空值回退默认；200 码点上限；列表省略、空状态换行 |

## 9. 实施阶段与验收

### S0：契约与边界

交付：`Mosaic.Settings.Contracts`、`ISettingSource`、架构测试与引用白名单。

验收：跨模块不能直接访问设置表；settings-core/ui/sqlite 的引用边界与 public API 清单通过；重复参数 ID 和多重来源被拒绝。

### S1：settings-sqlite

交付：`settings` 表迁移、仓储适配器、值 JSON 映射。

验收：读写、事务、迁移摘要、备份与重开与任务仓储同一标准；禁用模块的值保留；表只由 settings-sqlite 访问。

### S2：settings-core

交付：定义注册、聚合、类型/范围/跨字段校验、UserSettings 持久化、HostConfig 转发、恢复默认、pending 状态。

验收：未知 ID、越界、非法枚举、跨字段冲突全拒绝；保存失败不部分应用；即时项只在成功后应用；重启项不提前生效。

### S3：宿主装配

交付：模块目录与依赖顺序、AppConfiguration 可选设置段、host 设置来源、Shell/主题来源、日志与启动参数。

验收：旧 0.1.0 配置可加载并取默认；新配置写回后重启生效；缺依赖、禁用模块残留绑定拒绝；配置文件原子替换失败保留原文件。

### S4：模块消费改造

交付：tasks-core 规则与约束 API、tasks-ui 固定右侧编辑栏/布局参数/行为/排序/文案、shell 布局/主题/矢量齿轮、storage 日志模式与连接模式。

验收：契约测试保证任务 UI 显示限额与业务校验一致；任务页在紧凑阈值前后都保持列表在左、编辑栏在右；主题切换即时；齿轮不再含 Unicode 图标；WAL/池化模式切换与回退通过。

### S5：settings-ui

交付：四项分组、统一高级可见性门、动态控件、警告、重启徽标、恢复默认、未保存离开确认。

验收：STA 测试覆盖每类控件、字段错误、跨字段错误、危险确认取消、恢复默认、禁用模块空态；“普通”分区和普通分类定义均不存在；高级关闭时所有分区 `VisibleRows` 为 0、相关命令禁用；高级开启后全部 57 项均可到达。无业务模块时设置页仍可打开并从宿主来源读取系统项。

### S6：集成验收

交付：0.2.0 安装包与验证记录。

验收：默认启动尺寸为 1504×1012、最小尺寸为 1100×720；安装/升级保留设置；改设置→重启→仍生效；改回默认；禁用任务模块后设置页仍可用；重新启用恢复值；无开发环境干净 Windows 启动通过。

## 10. 测试矩阵

| 检查层 | 重点 |
| --- | --- |
| 架构 | 引用白名单、public API、禁止直接访问 settings 表、协议常量未被运行时可改 |
| settings-core | 注册冲突、范围、枚举、跨字段、事务回滚、pending、恢复默认、来源撤销 |
| settings-sqlite | 迁移、重开、JSON 值映射、禁用模块值保留、备份 |
| tasks-core | 必填关闭、默认完成、Revision 起点、排序扩展、约束 API 一致性 |
| tasks-ui | 行为开关即时生效、文案覆盖/回退、排序、空标题显示、未保存确认；编辑器始终在右栏，紧凑阈值不改变左右结构 |
| shell | 主题、断点、宽度、边距、默认/最小窗口尺寸；矢量齿轮在完整/图标侧栏、选中和 High Contrast 下显示一致 |
| storage | 队列/锁/备份参数、WAL 往返、Pooled 模式串行保证、关闭排空 |
| host | appsettings 写回、原子替换、旧配置兼容、模块禁用矩阵、日志级别 |
| 桌面 | 高级关闭时无设置行、四分区导航、保存/恢复门控、右栏固定、1504×1012 启动、1100×720 最小尺寸、齿轮可访问名称、安装升级保留 |

## 11. 完成定义

设置功能完成需同时满足：

- 57 项参数均位于四个分区，并且只有开启高级设置后才能查看、修改、校验和恢复默认。
- 不存在“普通”分区或 `SettingCategory.Normal` 定义；高级关闭时所有设置行隐藏，避免同一设置出现两套可见性规则。
- 设置导航显示矢量齿轮；任务编辑栏在任何受支持窗口尺寸下都固定在任务页右侧，紧凑模式不铺满或覆盖列表。
- 默认窗口 1504×1012、最小窗口 1100×720；用户保存的启动尺寸继续可控，过小屏幕仅做本次可见区域修正。
- 即时项保存后生效；重启项保存后明确显示 pending，重启后生效。
- settings-core 不理解任务、数据库或布局；各模块仍独占自己的规则与数据。
- 所有危险项有明确警告、约束和测试；没有绕过公开接口直接读写设置表的路径。
- 0.1.0 配置与数据库可平滑加载；设置表迁移可备份、可重开；安装升级不丢设置。
- Release 构建、架构测试、业务/存储/界面测试与桌面验收通过后才算交付。
