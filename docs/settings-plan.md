# Mosaic：设置功能规划（定稿）

0.9.0 实施更新（2026-11-17）：悬浮任务窗新增 `floating.dragBandHeight`、`floating.cardPadding`、`floating.contentGap` 三项高级设置，支持立即可逆预览，放弃时恢复运行布局且不改写窗口保存尺寸。完整配置96项（14普通/82高级，6个分区）；无配置或数据库迁移。实际验证和环境边界见 implementation-status.md 最新记录。

实施更新（2026-11-16）：0.6.0 已实现紧凑速览、自然高度、9 项视觉预览参数、独立 release-summary、Tasks 原子创建回执、发布清单和一次性配置迁移。完整配置 84 项（12 普通/72 高级），Presence 26 项。构建及自动化已验证；完整视觉/DPI/交互与安装升级矩阵仍待验收。实际文件和验证见 implementation-status.md 最新记录；下文原规划条目保留为验收基准。

状态：57 项基线的普通/高级投影、分区保存/放弃/恢复及固定右栏已实施。0.3.0 新增驻留分组 17 项；完整配置为 74 项、5 个分区、11 项普通/63 项高级。0.4.0 落实“跨分区持续预览、离开设置页统一确认”；0.5.0 完成宿主设置单一权威、ProviderMap/ModuleList 共享格式规则、跨存储提交日志、设置展示元数据下放模块和导航本地化键；0.5.1 增加生效配置层的一次性 Presence 迁移、迁移标记和原子验收。完整交互式桌面矩阵、重启 pending 数量、旧 0.1 配置完整迁移及结构化模块/provider 编辑仍待验收。

## 1. 已确认范围

- 前文清单中的所有可调行为参数全部纳入设置；本次再增加固定右栏所需的编辑器宽度与栏间距两项。
- 删除独立“普通设置”分区，但保留 `SettingCategory.Normal` 作为参数类别。原普通项按职责迁入“任务”和“外观与布局”。
- 高级设置默认关闭时：只显示含普通参数的分区及其普通项；一个普通参数都没有的分区，连分区栏都隐藏。
- 开启高级设置后：在相关模块已激活且存在注册定义的前提下显示各分区，并在当前分区展示普通项与高级项，不重复显示同一参数。
- 参数定义包含 `SettingsGroup` 与 `SettingCategory`。分区按“分组中是否存在当前可见参数”动态决定是否渲染，不硬编码某几个分区始终可见。
- 分区只表达展示归类，参数语义仍由拥有它的模块负责；不建议普通用户修改的项目归 `Advanced`。
- 普通项与高级项使用同一行模板、同一份值状态；模式只改变哪些分区和设置行可见。
- 设置页使用一个跨分区预览会话。更改设置或切换分区不弹保存确认；全部未保存值持续保留并尽可能即时预览，只在离开整个设置页时统一询问保存、放弃或取消。
- 任务页始终保持左右结构。任务列表在左，新建/编辑任务栏固定在整体内容区右侧；窗口缩小时只启用紧凑排版、换行和滚动，不把任务栏移到左侧，也不让它铺满任务页。
- 默认窗口尺寸使用 1504×1012 DIP，默认最小窗口尺寸使用 1100×720 DIP。启动时若工作区更小，只收缩实际显示尺寸并保证位于可见区域，不改写保存的启动尺寸。
- 唯一不设项的是模块间协议常量（错误码字符串、请求字段结构、单实例管道名、主题资源键）。它们不是运行参数；改动属于契约版本升级，由架构测试锁定。

## 2. 职责与新增模块

| 模块 | 提供 | 需要 | 唯一职责 |
| --- | --- | --- | --- |
| settings-sqlite | settings.repository@1 | sqlite.session@1 | 设置表迁移、参数化 SQL、值映射 |
| settings-core | settings.api@1 | settings.repository@1、各设置来源 | 定义注册、聚合、校验、持久化请求、恢复默认、即时/重启路由 |
| settings-ui | 设置导航与页面 | settings.api@1、通用界面服务 | 普通/高级可见性规则、按元数据生成分区导航、按类型渲染、警告与重启徽标 |
| host（基础设施，不是模块） | 宿主设置来源 | — | appsettings.json 的读取/校验/原子写回、日志、启动窗口与单实例参数 |

归属规则：

- 参数的语义仍归原模块：任务限额归 tasks-core，交互行为归 tasks-ui，主题与布局归 shell，存储参数归 storage-sqlite，宿主参数归 host。
- settings-core 只保存“定义 + 值 + 校验结果”，不实现任务规则、不执行 SQL、不决定布局。
- settings-ui 只调用 settings.api，不直接读写设置表，不直接修改 appsettings.json；它不解释任务、存储或布局语义，也不按 `ModuleId` 或参数前缀猜测归属，只使用各来源提交的分组元数据渲染导航与动态控件。
- 设置导航仍由 settings-ui 贡献 `IconKey = "settings"`。外壳只把这个通用图标键映射为矢量齿轮，不识别设置模块内部。

## 3. 参数总表（57 项）

### 3.1 原普通项迁移与可见性

原有 8 项日常参数在上一轮被改成 `Advanced`，导致所有 57 项都只能通过高级开关查看。本次把它们的 `SettingCategory` 恢复为 `Normal`，仍按职责归入对应分区：

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

- 不再提供“普通”分区；普通参数只作为所属分区中的一种可见性类别。
- 分区可见性由该分组在当前模式下的参数数量决定，规则如下：

| 分区 | 普通参数 | `ShowAdvanced = false` | `ShowAdvanced = true` |
| --- | --- | --- | --- |
| 任务 | 6 项 | 显示分区，只显示 6 项普通参数 | 显示分区，显示 20 项 |
| 外观与布局 | 2 项 | 显示分区，只显示 2 项普通参数 | 显示分区，显示 10 项 |
| 存储与系统 | 0 项 | 整个分区栏隐藏 | 显示分区，显示 16 项 |
| 文案 | 0 项 | 整个分区栏隐藏 | 显示分区，显示 11 项 |

- 默认选中“任务”。若任务模块的普通参数因模块未激活而全部缺失，则选中第一个仍含可见参数的分区；没有任何可见参数时只显示空状态。
- `ShowAdvanced = false` 时，高级参数行不参与渲染，也不作为分区存在的依据。
- `ShowAdvanced = true` 时，当前分区显示普通项与高级项的并集；每行只有一个权威实例，不能因切换模式重复添加。
- 普通项和高级项共用编辑会话。显式“保存全部修改”和离开时保存覆盖全页累计修改；单项恢复只改当前行，分区恢复只改当前可见定义，全部恢复只改当前已注册定义；以上恢复均先进入预览。
- 分区恢复默认按当前可见定义 ID 计算范围，不按 `ModuleId` 恢复。这样恢复“文案”分区时不会连带重置 tasks-ui 的任务行为参数。
- 高级开关不写入数据库，进入设置页时默认关闭。关闭高级开关只改变可见性，不触发保存确认，也不清空普通项或高级项的暂存值；重新开启后恢复同一行实例和输入。状态栏继续统计隐藏分区的未保存修改。

### 3.2 任务（20 项）

| ID | 默认值 | 范围/枚举 | 类别 | 生效 |
| --- | --- | --- | --- | --- |
| `tasks.confirmDelete` | true | bool | 普通 | 即时 |
| `tasks.confirmUnsaved` | true | bool | 普通 | 即时 |
| `tasks.defaultFilter` | All | All / Open / Completed | 普通 | 即时 |
| `tasks.showCharacterCount` | true | bool | 普通 | 即时 |
| `tasks.autoSwitchToAllOnHidden` | true | bool | 普通 | 即时 |
| `tasks.sortOrder` | CreatedDesc | CreatedDesc / CreatedAsc / TitleAsc / TitleDesc | 普通 | 即时 |
| `tasks.title.maxCharacters` | 200 | 1–1000 | 高级 | 重启 |
| `tasks.notes.maxCharacters` | 20,000 | 1,000–200,000 | 高级 | 重启 |
| `tasks.title.required` | true | bool | 高级 | 重启 |
| `tasks.defaultCompleted` | false | bool | 高级 | 重启 |
| `tasks.revisionStart` | 1 | 1–1,000,000 | 高级 | 重启 |
| `tasks.layout.compactBreakpoint` | 920 DIP | 600–1400 | 高级 | 即时 |
| `tasks.layout.editorPaneWidth` | 480 DIP | 360–720 | 高级 | 即时 |
| `tasks.layout.paneGap` | 24 DIP | 0–64 | 高级 | 即时 |
| `tasks.editor.compactActionsWidth` | 480 DIP | 360–900 | 高级 | 即时 |
| `tasks.editor.compactActionsHeight` | 680 DIP | 480–1000 | 高级 | 即时 |
| `tasks.list.minColumnWidth` | 300 DIP | 220–500 | 高级 | 即时 |
| `tasks.editor.notesMinHeight` | 128 DIP | 96–360 | 高级 | 即时 |
| `tasks.button.minWidth` | 96 DIP | 72–160 | 高级 | 即时 |
| `tasks.button.minHeight` | 40 DIP | 32–56 | 高级 | 即时 |

`tasks.layout.compactBreakpoint` 的含义改为“进入任务页紧凑排版”，只调整间距、页头动作排列与操作栏换行，不再切换列表/详情为单页。

### 3.3 外观与布局（10 项）

| ID | 默认值 | 范围 | 类别 | 生效 |
| --- | --- | --- | --- | --- |
| `appearance.theme` | FollowSystem | FollowSystem / Light / Dark / HighContrast | 普通 | 即时 |
| `ui.language` | FollowSystem | FollowSystem / Chinese / English | 普通 | 即时 |
| `shell.sidebar.collapseBelowWidth` | 1180 DIP | 900–1600 | 高级 | 即时 |
| `shell.sidebar.expandAtWidth` | 1200 DIP | collapse+20–1800 | 高级 | 即时 |
| `shell.sidebar.fullWidth` | 190 DIP | 160–260 | 高级 | 即时 |
| `shell.sidebar.railWidth` | 64 DIP | 48–88 | 高级 | 即时 |
| `shell.page.marginLeft` | 32 DIP | 8–64 | 高级 | 即时 |
| `shell.page.marginTop` | 30 DIP | 8–64 | 高级 | 即时 |
| `shell.page.marginRight` | 28 DIP | 8–64 | 高级 | 即时 |
| `shell.page.marginBottom` | 28 DIP | 8–64 | 高级 | 即时 |

### 3.4 存储与系统（16 项，全部为高级参数且重启生效）

| ID | 默认值 | 归属 | 范围/说明 |
| --- | --- | --- | --- |
| `shell.window.minWidth` | 1100 DIP | host | 1000–2400；保证左右栏同时存在 |
| `shell.window.minHeight` | 720 DIP | host | 640–1200 |
| `startup.width` | 1504 DIP | host | 1000–3840；不得小于 `shell.window.minWidth` |
| `startup.height` | 1012 DIP | host | 640–2160；不得小于 `shell.window.minHeight` |
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

### 3.5 文案（11 项，全部为高级参数且即时生效）

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
public enum SettingsGroup { Tasks, Appearance, StorageSystem, Text, DesktopPresence }

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
    string? Warning = null,
    SettingPreviewMode PreviewMode = SettingPreviewMode.None);

public interface ISettingsApi
{
    Task<OperationResult<IReadOnlyList<SettingDefinition>>> ListDefinitionsAsync(CancellationToken ct = default);
    Task<OperationResult<IReadOnlyDictionary<string, object>>> GetValuesAsync(CancellationToken ct = default);
    Task<OperationResult<IReadOnlyDictionary<string, OperationError>>> ProposeChangesAsync(
        IReadOnlyDictionary<string, object> changes, CancellationToken ct = default);
    Task<OperationResult> ResetAsync(string? moduleId = null, CancellationToken ct = default);
    Task<OperationResult> ResetAsync(IReadOnlyList<string> definitionIds, CancellationToken ct = default);
}

public interface ISettingSource
{
    IReadOnlyList<SettingDefinition> Definitions { get; }
    Task ApplyAsync(IReadOnlyDictionary<string, object> values, CancellationToken ct = default);
}

public interface IHostSettingSource : ISettingSource
{
    object? GetValue(string id);
    Task<OperationResult> ValidateAsync(
        IReadOnlyDictionary<string, object> effectiveValues, CancellationToken ct = default);
    Task<OperationResult> PersistAsync(
        IReadOnlyDictionary<string, object> values, CancellationToken ct = default);
    Task<OperationResult> RollbackAsync(CancellationToken ct = default);
}
```

settings-core 判定持久化位置：

- `UserSettings`：写入 settings 表；由 settings-sqlite 负责 SQL 与迁移。
- `HostConfig`：只调用 `IHostSettingSource.PersistAsync`，由宿主原子写回 appsettings.json；不在设置表复制一份。
- 重启参数保存后标记 pending，本次运行继续使用启动时快照；下次启动读取新值。
- 即时参数保存成功后，按 UI Dispatcher 调用对应来源的 `ApplyAsync`。
- `SettingCategory` 重新承担参数级别可见性：`Normal` 在两种模式下都可见，`Advanced` 仅在高级模式下可见。
- 每个参数来源在 `SettingDefinition` 中声明 `SettingsGroup`。settings-ui 只按公开元数据计算分区和行，不按 `ModuleId`、`tasks.*` 或 `ui.text.*` 前缀猜测归属。
- 分区导航只渲染当前模式下至少有一个可见定义的分组；参数所属模块未激活时不创建该模块的设置行，也不显示伪造占位项。
- 可见性使用一个只读投影，不改动 `_rows` 中的完整参数集合；切换高级开关只重新计算 `Sections` 和 `VisibleRows`，并从当前可见的分区集合中修正 `SelectedSection`。

可见性伪代码只表达界面投影规则，不引入新的跨模块调用：

```text
visibleDefinitions = ShowAdvanced
    ? registeredDefinitions
    : registeredDefinitions.Where(Category == Normal)

Sections = visibleDefinitions
    .GroupBy(Group)
    .Where(group => group.Any())
    .Select(group => group.Key)

VisibleRows = visibleDefinitions
    .Where(Group == SelectedSection)
    .Select(rowByDefinitionId)
```

阶段 10 起，恢复分区默认时界面把当前可见定义加入编辑会话的累计变更覆盖层；settings-core 按定义产生默认候选并预览，界面不自行计算第三方来源的默认值，预览阶段不持久化。最终保存时才提交。

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

- 分区元数据最多包含：任务、外观与布局、存储与系统、文案；不存在“普通”或单独的“系统与模块”入口。
- 顶部“高级设置”开关默认关闭。关闭时左侧只显示“任务”和“外观与布局”两个含普通参数的分区；“存储与系统”“文案”整个分区栏隐藏。
- 普通模式下，任务显示 6 项普通参数，外观与布局显示主题、语言 2 项；它们可以正常修改和保存。
- 开启高级设置后，所有存在注册定义的分组全部显示；完整模块配置下为四个分区，当前分区显示任务 20 项、外观与布局 10 项、存储与系统 16 项或文案 11 项。
- “保存全部修改”和“放弃全部修改”按全页累计修改启用，包含当前隐藏分区；“恢复当前分区默认”按当前可见定义进入暂存；“恢复全部默认”仅在高级模式可用并覆盖当前已注册定义。
- 开关状态不持久化。关闭高级开关只隐藏高级行，不询问保存、不撤销预览，也不清空普通项或高级项的输入；重新开启后恢复同一行实例。总修改数量包含当前不可见的行。
- 每行显示名称、说明、当前值、默认值、约束、恢复默认按钮、风险图标和“需重启”徽标。
- 控件按类型渲染：开关、数字框、下拉框、文本框、模块多选、provider 映射编辑。
- 保存前逐项校验；字段错误就地显示；跨字段错误显示在相关组顶部。
- 保存成功后：即时项刷新界面；重启项显示“下次启动生效（N 项）”。
- 恢复默认支持单项、分区、全部；它们先进入暂存和预览。全部恢复只覆盖当前已注册定义，不静默清除未注册/已禁用模块的持久化值。
- 切换设置分区或切换“高级设置”开关时不提示 `INavigationGuard`，直接保留全部暂存输入和预览。只有离开整个设置页、切换其他功能页面或退出应用时，才通过通用 `INavigationGuard` 统一提示保存、放弃或取消。

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

交付：四项分组元数据、按普通/高级类别计算的分区与行可见性、动态控件、警告、重启徽标、按定义 ID 恢复默认、未保存离开确认。

验收：STA 测试覆盖每类控件、字段错误、跨字段错误、危险确认取消、恢复默认、禁用模块空态；不存在“普通”分区；高级关闭时完整模块配置下只有任务与外观与布局两个分区，分别显示 6 项和 2 项普通参数，存储与系统和文案连分区栏都不存在；普通项可进入预览并显式保存全部；高级开启后四个分区和全部 57 项均可到达，普通项在两种模式下只有一份行实例；关闭开关时高级项暂存保留且不弹出确认；恢复文案分区不影响 tasks-ui 的任务参数；无业务模块时设置页仍可打开并从宿主来源读取系统项。

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
| 桌面 | 高级关闭时仅任务/外观两分区且只含普通项、高级开启后四分区、保存/恢复范围、右栏固定、1504×1012 启动、1100×720 最小尺寸、齿轮可访问名称、安装升级保留 |

## 11. 完成定义

设置功能完成需同时满足：

- 57 项参数归入四个分区，其中 8 项 `Normal` 参数在高级关闭时依然可见；其余 49 项仅在高级开启后可见。
- 不存在“普通”分区；没有普通参数的“存储与系统”“文案”分区在高级开启前整体隐藏。
- 普通项和高级项修改后都进入同一预览会话；关闭高级开关只隐藏行，不询问、不保存、不放弃；同一参数不会在两种模式下生成两份可编辑状态。
- 设置导航显示矢量齿轮；任务编辑栏在任何受支持窗口尺寸下都固定在任务页右侧，紧凑模式不铺满或覆盖列表。
- 默认窗口 1504×1012、最小窗口 1100×720；用户保存的启动尺寸继续可控，过小屏幕仅做本次可见区域修正。
- 即时项保存后生效；重启项保存后明确显示 pending，重启后生效。
- settings-core 不理解任务、数据库或布局；各模块仍独占自己的规则与数据。
- 所有危险项有明确警告、约束和测试；没有绕过公开接口直接读写设置表的路径。
- 0.1.0 配置与数据库可平滑加载；设置表迁移可备份、可重开；安装升级不丢设置。
- Release 构建、架构测试、业务/存储/界面测试与桌面验收通过后才算交付。

## 12. 后续增量：驻留与边缘任务面板

后台驻留功能将新增 `SettingsGroup.DesktopPresence`（显示名“驻留与边缘”）。17 项定义全部由新的 Presence 模块注册，避免 tasks-ui 单独留下无主的弹窗设置。该分区在高级设置关闭时保留，因为它有 3 个普通定义；另外 14 个高级定义仅在高级开启后显示。合并后完整模块配置的目标为 74 项参数、5 个分区，其中 11 项普通、63 项高级。Presence 未激活或模块依赖无效时，该分区整体不存在。

新增参数、归属、跨字段校验、即时应用、分区可见性、生命周期和测试要求见 [后台驻留与边缘任务面板规划](desktop-presence-plan.md)。上述 74 项已在 0.3.0 源码中定义，是否出现在页面取决于实际激活的模块。Presence 停止时撤销 17 项来源；其设置值仍由 settings-sqlite 保留。普通模式显示任务 6 项、外观 2 项、驻留 3 项；高级模式才显示其余 63 项。

### 0.3.0 跟进（2026-11-16）

新增设置来源不持有任务 API，也不修改宿主配置：它通过注入的驻留控制器和 WPF 表面应用参数；设置核心仍负责校验/持久化后才 Apply。来源注册和撤销、非法颜色/深度组合、即时关闭策略、普通 3 行/高级 17 行及 ComboBox 默认值都有 STA 回归。驻留枚举约束同选项一起本地化；主题和语言的原有预览语义保留。

仍未完成的设置基线事项：旧 0.1 配置完整迁移、重启 pending 计数、预览协议归位、禁用模块值的全部清除、ModuleList/ProviderMap 结构化编辑及干净 Windows 安装升级。新增驻留功能不代表这些验收自动通过。

## 13. 跨分区持续预览与离开时统一确认

状态：2026-11-16，0.4.0 已实现 PV0–PV3 的会话、预览路由、跨分区编辑和统一离开；PV4 自动化已执行，桌面完整场景与安装升级仍待验收。

### 13.1 交互目标

原有“切换分区时立即询问保存”改为“设置页内自由切换并持续预览，离开设置页时统一处理”：

```text
修改设置
  → 合法即时项立即预览
  → 非法项保留输入并显示错误
  → 切换到其他分区
  → 不保存、不放弃、不弹窗，暂存值和预览继续保留
  → 再修改其他分区
  → 所有分区累计为同一组未保存修改
  → 离开设置页
  → 保存并离开 / 放弃并离开 / 取消
```

保留显式“保存全部修改”和“放弃全部修改”按钮。它们是用户主动提交当前会话内全部未保存值的快捷方式；离开设置页时仍执行同一套保存/放弃/取消流程。若后续要求页面内完全不出现保存按钮，可只移除按钮，不改变预览会话和离开守卫。

“持续预览”的准确含义：

| 参数类型 | 编辑后 | 切换分区后 | 离开时选择保存 | 离开时选择放弃 |
| --- | --- | --- | --- | --- |
| `Immediate + Reversible` 可逆即时参数 | 先校验；成功则应用到当前运行中的消费模块，不写数据库 | 预览保持 | 统一持久化，运行值保持 | 恢复最后一次已提交值 |
| `Immediate + None` 不可逆即时参数 | 只保留编辑器值并校验，不提前应用 | 暂存值保持 | 持久化并应用 | 恢复最后已提交值 |
| `Restart` 重启参数 | 只保留编辑器值和“保存后需重启”状态，不提前应用 | 暂存值保持 | 持久化并标记待重启 | 恢复最后已提交值 |
| `HostConfig` 宿主参数 | 只保留编辑器值，不改 appsettings.json | 暂存值保持 | 原子写入 appsettings.json | 恢复最后已提交值 |

`SettingApplyMode.Immediate` 只表示“保存后本次运行即可应用”，不自动保证未保存值可以安全回滚。定义还应声明 `SettingPreviewMode`：当前主题、语言、侧栏、任务布局和驻留表面等可逆参数标记为 `Reversible`；未来任何可能执行不可逆操作或对外产生副作用的即时参数必须标记 `None`，只暂存到提交。

预览不能改写持久化设置。可逆即时参数可以在设置页中实际看到变化；数据库位置、日志模式、模块启用列表等重启参数只能预览表单值，不能假装已经生效。

### 13.2 职责与状态归属

| 所有者 | 职责 |
| --- | --- |
| settings-ui | 保存每行的暂存输入、分区切换、控件错误、总修改计数、输入合并/防抖，以及调用预览 API |
| settings-core | 创建预览会话、计算基线、执行类型/范围/跨字段/来源语义校验、决定哪些参数可即时应用、提交持久化与回滚 |
| 各 `ISettingSource` | 维护自己模块的最新有效运行快照，应用可逆即时值，并为之后的页面实例回放快照；仍不访问设置表或其他模块内部状态 |
| settings-sqlite | 仅在最终提交时写 `settings` 表；不参与预览 |
| host 设置来源 | 仅在最终提交时原子写 appsettings.json；预览不调用持久化方法 |

settings-ui 不能自行判断 `tasks.*`、`storage.*` 或 `appearance.*` 的含义，也不能直接调用其他模块的 `ApplyAsync`。它只向 `settings-core` 提交完整的当前暂存值。

来源模块停止的顺序必须是：申请 settings-core 暂停该来源的预览 → 取消在途 Apply → 用仍存活的来源回放最后已保存运行快照 → 从编辑会话中移除其定义和暂存项 → 注销来源 → 最后释放模块内部目标/订阅。设置注册生命周期因此需要可等待的注销或等价 `PrepareSourceStopAsync(moduleId)`；不能再只用同步 `IDisposable` 后立即销毁来源。正常退出时 settings-ui 应先停止；异常路径若设置页来不及停止，也必须按此顺序兜底。

### 13.3 建议预览契约

在 `Mosaic.Settings.Contracts` 增加编辑会话和预览能力声明。具体命名可在实施时调整：

```csharp
public enum SettingPreviewMode { None, Reversible }

// SettingDefinition adds SettingPreviewMode PreviewMode.

public sealed record SettingPreviewResult(
    long Revision,
    IReadOnlyDictionary<string, OperationError> Errors,
    IReadOnlyDictionary<string, OperationError> Warnings,
    IReadOnlyList<string> AppliedDefinitionIds,
    IReadOnlyList<string> SaveOnlyDefinitionIds);

public sealed record SettingsCommitResult(
    IReadOnlyDictionary<string, OperationError> ApplyWarnings);

public interface ISettingsEditSession : IAsyncDisposable
{
    IReadOnlyDictionary<string, object?> SavedValues { get; }

    Task<OperationResult<SettingPreviewResult>> PreviewAsync(
        IReadOnlyDictionary<string, object?> cumulativeChanges,
        long revision,
        CancellationToken cancellationToken = default);

    Task<OperationResult<SettingsCommitResult>> CommitAsync(
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<OperationResult> DiscardAsync(
        CancellationToken cancellationToken = default);
}

public interface ISettingsApi
{
    // Existing methods remain.
    Task<OperationResult<ISettingsEditSession>> BeginEditSessionAsync(
        CancellationToken cancellationToken = default);
}
```

会话在打开时读取一次已提交基线，并保存：

- 最后一次已提交值；`GetValuesAsync` 继续只表达已保存值，不表示当前运行值。
- 最后一次成功应用的全部即时参数快照。
- 当前完整累计变更覆盖层和最新编辑代次。
- 哪些定义只可保存、哪些定义实际进入运行预览。

`PreviewAsync` 接收相对已保存基线的**完整累计变更**，不是只传当前分区，也不是增量补丁。空覆盖层表示回到已保存基线。settings-ui 继续持有原始文本和本地解析状态；settings-core 持有权威基线、最后成功预览、序列化和最终提交。

同一 settings-core 同时只接受一个活动编辑会话。会话活动期间，其他外部 `ProposeChangesAsync`、`ResetAsync` 或第二个会话返回明确的“编辑会话冲突”；会话自身的 Commit/Discard 除外。这避免两者互相覆盖。会话关闭时如果既未提交也未丢弃，应执行安全回滚；该行为只作为异常清理保险，不替代正常离开确认。

显式“保存全部修改”成功后，会话继续活动：

- 用已提交值替换 `SavedValues` 和回滚基线。
- 清除本批 Dirty，revision 继续递增。
- 保留“最后有效运行快照”，用户可立即继续编辑下一批设置。
- 只有最终离开设置页、页面异常释放或设置模块停止时才结束单活动会话。

因此“本批已经提交”不等于“会话永久免回滚”。提交后新产生的草稿仍按下一批预览处理，异常释放时仍会回到最新 `SavedValues`。

### 13.4 预览计算与执行顺序

每次预览请求执行以下步骤：

1. 以最后一次已提交值作为基线，合并全页所有分区、包含隐藏高级行的完整累计覆盖层和已暂存的恢复默认操作。
2. 逐项规范化类型，执行范围、枚举、必填和格式校验。
3. 把完整候选值交给各来源执行跨字段校验。跨分区组合必须一起校验，例如休眠分区的侧栏宽度与其他分区的展开宽度。
4. 若存在任何错误，不调用任何 `ApplyAsync`；保留当前输入和错误，运行状态停留在“最后一次成功预览”，并明确提示“部分输入无效，当前显示上次有效预览”。
5. 若全部有效，只把 `Immediate + Reversible` 参数交给对应来源的 `ApplyAsync`。`Immediate + None`、`Restart` 和 `HostConfig` 参数只标记为暂存，不进入运行应用队列。
6. 即时应用成功后才更新“最后一次成功预览”快照和实际应用 ID；应用失败时对已经应用过的来源回放上一成功快照并报告错误。回滚失败必须单独暴露，不能显示为已经放弃。

即时预览沿用现有公开 `ISettingSource.ApplyAsync`，并明确要求：

- 接受完整运行快照且幂等。完整快照从当前有效运行值出发，只覆盖 `Immediate + Reversible` 的候选值；`Immediate + None` 保持原运行值，`Restart` 不进入参数。
- 可等待，调用返回时应用已经完成；类似 `ShellSettingsApplier` 当前 fire-and-forget 的 Dispatcher 调度必须改为可等待。
- 来源内部缓存最新运行快照；任务页面、驻留弹窗或外壳控件稍后创建/重新挂接时读取并回放该快照，不能因为当时没有活动 View 而丢失预览。
- 任一来源应用失败时，回滚集合必须包含该失败来源，因为异常前可能已经完成部分状态变更；只有所有来源都成功回放上一有效快照，才能把本次预览标记为已撤回。

settings-core 继续负责按 `ModuleId` 路由；settings-ui 不接触来源实例。

最终保存复用并加强现有宿主配置与 SQLite 的补偿提交顺序：完整校验 → 风险确认 → 写宿主配置 → 写设置表 → 应用即时参数。校验、确认或任一持久化步骤失败时留在设置页，不清除暂存输入；HostConfig 已写而 SQLite 返回失败、抛异常或被取消时，必须回滚 HostConfig，补偿失败要作为独立错误报告。

持久化全部成功、但即时 `ApplyAsync` 失败时属于 `ApplyWarnings`：新值已经保存并更新基线，界面清除 Dirty，可显示“已保存，部分运行效果未应用”后按离开流程继续。这个警告不能描述成“尚未保存”。

### 13.5 快速输入与并发

一次预览请求尚未结束时，新的输入不能与旧请求并发执行：

1. settings-ui 为设置页维护单调递增的编辑代次。
2. 用户输入触发合并机制：布尔、枚举和数字在控件提交值后立即申请；长文本使用约 120–150 ms 防抖。
3. 如果预览正在进行，只记录“还有更新的候选值”，当前请求完成后立即用最新完整暂存值再预览。
4. 只有最新代次的成功结果可以更新字段错误、跨字段错误和预览状态。
5. 旧代次晚到的响应不能覆盖新输入，也不能把运行值恢复成旧预览。

离开设置页前先建立离开屏障：停止接受新的编辑请求、等待当前预览链处理到最后一代，并用最后草稿的完整累计覆盖层完成一次同步预览。只有排空预览链并确认“运行状态已对应当前草稿”后才能判断 Dirty。若最新候选仍无效，用户选择保存时必须留在页面并显示错误；选择放弃则通过公开 Discard 恢复最后已提交值。取消离开时解除编辑冻结并允许继续输入。

### 13.6 跨分区未保存状态

- `HasChanges` 从“当前可见行是否脏”改为“设置页内所有普通/高级、可见/隐藏行是否与最后提交值不同”。
- 操作栏显示所有分区累计的修改数量，例如“3 项修改，正在预览”或“5 项修改尚未保存”。
- 每个 Dirty 行显示“预览中”或“保存后重启”状态；非法行显示“未预览：值无效”。
- 普通模式下隐藏的高级行仍计入总数。操作栏或状态提示应明确其中有隐藏分区修改，但不能因此弹出保存确认。
- `VisibleRows` 只作为渲染投影；任何切换分区、开启/关闭高级设置、刷新本地化文字或窗口重排都不得重建或丢弃 `_rows` 的暂存状态。
- 重复加载定义/值的刷新操作必须保留当前预览会话；只有在所有暂存修改清空并成功回滚后才能重新建立基线。

### 13.7 分区切换与高级开关

- `SettingsSectionCommand` 只更新 `SelectedSection` 并重建 `VisibleRows`，暂停、保存、放弃、AskUnsavedChanges 均不得被调用。
- 关闭高级开关只隐藏高级行；高级暂存值保留，重新开启后仍显示原输入、错误和预览状态。
- 高级关闭导致当前分区没有任何可见普通项时，可以选择第一个仍可见的分区，但这次自动改选不触发保存确认。
- 分区切换时如果最新预览仍在执行，界面可立即切换；在后台完成的预览只更新对应隐藏行的状态，不得把用户强制拉回旧分区。
- 设置导航项切换是纯页面内导航，不经过 Shell 的页面离开守卫。

### 13.8 恢复默认

恢复默认也属于可撤销设置修改：

- 单行恢复默认：把该行暂存值设为其默认值，参与预览，不立即持久化。
- 恢复当前分区默认：把当前可见定义写入累计变更覆盖层并更新对应行输入；其他分区暂存值不受影响。
- 恢复全部默认：仅高级模式可用；把当前已注册定义写入累计变更覆盖层并显示默认值。最终只在保存时执行全部恢复。
- 已禁用模块在当前会话中没有注册定义，不能假装已在界面中预览。恢复全部默认只影响当前已注册定义，不静默清除未注册模块持久化的值；该清理仍是独立、明确确认的维护动作。
- 用户随后修改某一行时，以该行新输入覆盖同一行的默认值；恢复默认的事实继续保留在该累计变更覆盖层中，不因分区切换重复弹出。

### 13.9 统一离开流程

`CanLeaveAsync` 在离开设置页、切换到其他功能页和真正退出应用时执行同一流程。默认的“关闭主窗口并驻留”只隐藏主窗口，设置页没有离开，因此不弹保存确认；驻留恢复后仍在原分区继续编辑。用户从驻留入口真正退出应用时，才进入统一离开流程。

```text
收到离开申请
  → 冻结新输入
  → 排空预览链并对最终累计草稿完成同步预览
  → 运行状态与草稿一致后重新计算 Dirty 及其它未完成工作

无未保存修改且无预览/回滚在途
  → 允许离开

有未保存修改
  → 询问：保存并离开 / 放弃并离开 / 取消

保存并离开
  → 校验全部暂存值
  → 列出全部危险项并一次性确认
  → 原子提交
  → 成功：清空 Dirty、结束编辑会话、允许离开
  → 保存成功但即时应用失败：保留“已保存、部分运行效果未应用”的诊断，再按既有应用警告语义离开
  → 失败：保持页面、输入和最后成功预览，拒绝离开

放弃并离开
  → 排空预览链
  → Discard 到最后一次已保存快照
  → 回滚成功：恢复所有行值、清空 Dirty、允许离开
  → 回滚失败：保持页面并报告错误，拒绝离开

取消
  → 解除输入冻结，保持当前分区、输入、错误和预览，拒绝离开
```

校验、持久化失败、非法字段、回滚失败和仍在处理中的写入都不能悄悄离开；持久化成功但即时应用给出 `ApplyWarnings` 时按已保存处理并允许离开。保存失败时应按错误所在分区定位；若错误只在隐藏的高级项中，先提示并展开高级设置，再允许用户继续修正。未能正确回放运行快照时，应把它视为“运行状态未确认”的失败并阻止离开，不能只检查 Dirty 是否已经为零。危险项确认只在显式保存或“保存并离开”时出现，不在编辑、切换分区或关闭高级设置时出现。

`CanLeaveAsync` 必须防重复进入，多个导航/退出请求共用同一个进行中的离开任务。设置页正常保存或放弃后，`DisposeAsync` 不再二次回滚；页面/设置模块因禁用或异常被直接释放时，`DisposeAsync` 异步取消预览并回滚最新 `SavedValues` 之后的未提交效果，不弹交互对话框，也不自动保存。

### 13.10 实施阶段

#### PV0：契约与边界

交付：编辑会话公开契约、`SettingPreviewMode` 元数据、可等待的来源注销/停止申请、架构白名单和公开 API 基线；确定性说明 `ApplyAsync` 为可等待、幂等、完整运行快照。

验收：settings-ui 只能调用 settings.api；settings-core 是唯一预览路由；重复活动会话和其他并发写入被明确拒绝；来源必须先完成预览撤销再注销；`Immediate` 与 `Reversible` 不被混为一谈。

#### PV1：核心预览会话

交付：基线快照、累计变更合并、跨字段校验、仅可逆即时项 Apply、最终原子 Commit、Discard、来源缓存、重置和异常清理。

验收：任意次数预览不写数据库或 appsettings；重启和不可逆即时项不提前应用；保存失败不部分落盘；放弃后主题、语言、外壳布局、任务 UI 与驻留参数恢复；来源页面稍后创建时仍能获得最新有效快照。

#### PV2：跨分区 UI

交付：删除分区切换和高级开关中的保存询问；全局 Dirty 计数；隐藏分区暂存；显式“保存全部/放弃全部”；保留同一行实例；移除 SettingRow 内主题/语言的特殊预览逻辑。

验收：在任意分区修改后切换多个分区，弹窗次数为零；返回原分区可见原输入；主题/语言等预览持续存在；关闭高级后重新打开仍可继续编辑。

#### PV3：快速输入与统一离开

交付：防抖、最新代次覆盖、单消费者预览、离开等待、统一 Save/Discard/Cancel、风险确认、错误定位和页面释放清理。

验收：快速连续输入只呈现最新值；旧响应不覆盖；用户改回原值后仍会排空旧预览再离开；离开只询问一次；取消留在原位置；校验/持久化失败或回滚失败均不离开，持久化成功但即时应用失败属于可离开的 ApplyWarning；显式保存后会话可继续编辑新草稿；驻留隐藏不弹窗且恢复后对话仍可继续。

#### PV4：回归与桌面验收

交付：单元、STA、桌面交互和预览记录。

测试矩阵：

| 层 | 必测内容 |
| --- | --- |
| settings-core | 基线+累计变更合并、跨分区跨字段校验、Immediate+Reversible/None/Restart/HostConfig 路由、单活动会话、Commit/Discard、并发写入拒绝、失败回滚 |
| settings-ui | 分区切换零弹窗、全局 Dirty、隐藏高级项、快速输入代次、保存/放弃全部、离开一次确认、错误分区定位 |
| 来源模块 | 主题、语言、外壳布局、任务 UI、驻留参数的应用、等待与回滚；延迟页面回放最新快照；重启参数运行值不变 |
| 生命周期 | 其他功能页导航、真正退出应用、驻留隐藏与恢复、设置页/设置模块释放、消费来源先停止时的预览撤销与注销顺序；取消/失败均留在页面 |
| 桌面 | 修改外观→切换任务分区→再改高级参数→返回外观；退出时分别选择保存、放弃、取消，重启后结果一致 |

### 13.11 完成定义

- 设置页内部任意切换分区、普通/高级模式或设置行都不出现保存确认；最新暂存输入和预览始终保留。
- 所有标记为可逆的即时参数在合法输入下持续预览；不可逆即时、重启和 HostConfig 参数只暂存，不伪装成已经生效。
- 全部分区共用一个修改总账、一个预览会话和一个最终提交点。
- 显式保存成功后会话继续可用，并以新提交值建立下一批基线；后续草稿仍可预览、放弃和异常回滚。
- 离开设置页先冻结输入并排空预览链；Dirty 为零但仍存在旧预览时不能直接离开。离页只询问一次，校验/持久化/回滚失败不会导致页面已离开但状态未确认。
- 重置默认也进入暂存和预览；全局恢复默认只在最终保存时生效。
- 预览不写数据库、不写宿主配置，不复制任务或业务规则，不绕过公开设置 API。
- 消费来源应先取消预览并向最后保存值回滚，再注销定义和释放内部目标。
- 架构、settings-core、settings-ui、消费模块和桌面验收全部通过后才算交付。

### 13.12 0.4.0 实施记录（2026-11-16）

- settings-core 独占 `ISettingsEditSession`，串行化 Begin/Preview/Commit/Discard 和来源准备停止；第二个会话、外部提交与恢复在编辑会话内被拒绝。
- `SettingPreviewMode` 默认 None。Shell、Tasks 可逆展示参数和 Presence 表面由拥有者显式声明 Reversible；重启与 HostConfig 值仅暂存。预览/回滚不调用仓储写入或宿主持久化。
- 全页累计覆盖层、revision、类型/范围/来源组合校验、失败来源在内的反向补偿、回滚失败阻止离开、提交后新基线与下一批草稿均已实现。仓储提交抛异常/取消后使用独立令牌补偿宿主配置。
- settings-ui 移除主题/语言 ID 特判。分区和高级模式只投影同一组行；总 Dirty 包含隐藏项。保存/放弃处理全页，恢复默认先暂存，危险确认只在提交时发生。
- 离开先冻结输入并排空预览链；并发离开申请复用同一任务。改回原值也等待旧预览回滚，取消保留输入与预览；异常页面释放取消查询并通过公开会话回滚。
- Shell Apply 改为可等待；Tasks UI 来源缓存最新运行快照，晚创建任务页不会重新用持久化值覆盖预览。来源停止前调用 PrepareSourceStopAsync，回放保存运行快照后撤销。
- 弹窗界面同步打磨：独立标题与收起按钮、任务数量摘要、完成状态卡片、固定打开/退出操作；低于 360 DIP 高度收起辅助文字，任务区域继续独立滚动。Tasks 仍独占只读内容，Presence 只渲染容器。
- 验证范围与测试计数见 implementation-status.md。重启 pending 数量、旧 0.1 完整迁移、模块/provider 结构化编辑，以及干净 Windows 安装升级仍未由本次功能补齐。

### 13.13 0.5.0 架构收口实施记录（2026-11-16）

- 删除 settings-core 中的全局 `SettingCatalog`；宿主独有定义迁入 Desktop 的 `HostSettingsCatalog`，设置页展示值按 canonical 默认、旧顶层配置基线、`settings` 覆盖的顺序取得，与下次启动实际有效值一致。
- `Mosaic.Settings.Contracts` 统一提供 `CapabilityBinding`、`SettingValueFormat`、`SettingValueComparer` 和显式格式错误；settings-core、设置行校验与宿主语义校验共用同一规则，ProviderMap 不再依赖键顺序判断修改。
- Host 与 SQLite 同批提交通过 `ISettingsCommitJournal` 协调：先持久化 pending，再写宿主配置与设置仓储，最后清除；settings-core 激活时幂等重放中断提交，恢复失败会阻止进入半启用界面并给出诊断。ResetWithResult 以附加契约保留明确的成功、失败和即时应用警告。
- `SettingDefinition` 自带显示名、描述与选项的资源键；Settings.Wpf 删除全局设置 ID 翻译表和选项目录，只按模块提交的元数据渲染。新增模块不需要修改设置 UI 源码。
- `NavigationItem.LabelKey` 由贡献模块提供，Shell 不再识别 `tasks.list`、`settings.home` 或中文业务名称；未知模块回退自身 Label，语言切换会重建导航与页面标题。
- 任务错误使用稳定键并经模块本地化器翻译；Shell 对话框、启动失败、退出协调和宿主诊断使用 Desktop/Shell 本地化资源。
- 紧凑任务页采用真实单栏：仅列表或仅编辑器加顶部返回条，宽屏继续双栏。任务数据、草稿、离开守卫和公开 API 边界不变。
- 未完成项：重启 pending 的可观测计数、旧 0.1 配置的完整交互式迁移矩阵、模块/provider 结构化编辑器、多屏/DPI/输入法验收及完整桌面 Smoke。以上项目不得因自动化全绿而标记完成。

### 13.14 0.5.1 旧配置迁移实施记录（2026-11-16）

- 根因位置：`AppConfiguration.Load` 在完整校验前直接修改并写回顶层配置，没有一次性迁移标记；Presence 被用户禁用后仍会重新补入。迁移前的完整依赖解析还会先拒绝尚无 `presence.tasks-view@1` 的旧 `tasks-ui` 配置。设置页已经保存 `settings.host.modules/providers` 时，旧逻辑可能只修改其中一层。
- 改动位置：`AppConfiguration.cs` 先校验顶层与生效配置，再选择实际覆盖层迁移；新增 `appliedMigrations` 和 `desktop-presence-v1`，成功后原子替换并保留 `.pre-migration`。`HostConfigurationValidator.cs` 成为宿主模块/provider 的唯一语义校验，`HostSettingSource.cs` 复用该入口。
- 验收位置：`DesktopTests.Configuration_presence_migration_is_validated_persisted_and_one_time` 覆盖顶层迁移、模块/provider 联合及单独覆盖、已迁移后主动禁用及非法配置不写盘。非桌面自动化 121/121，迁移黑盒 6/6。

## 14. 后续规划：速览视觉参数与更新任务

状态：2026-11-16，尚未实施。参数定义、归属和验收统一见 [任务速览 UI 与更新简要任务计划](peek-and-update-summary-plan.md) 第 8、9 节。

- 复用现有弹窗宽度、最大高度、任务上限、筛选与状态色；最大高度改为真正的高度上限，少量任务按内容收缩。
- 拟新增 9 项 Presence 高级视觉定义，交由通用辅助视图选项传递格式提示，Tasks UI 不再注册同一参数；无 Presence 时整组撤销。
- 拟新增普通项 `updates.summaryTask.enabled`，由独立更新模块注册到“任务”分区。设置保存只影响下次启动，不在预览或保存时创建任务，使用 `SettingPreviewMode.None` 和 `SettingApplyMode.Restart`。
- 上述 10 项尚未进入源码，当前 74 项、11 普通/63 高级的实施统计不变；实施同批更新定义数量、资源、截图、测试与发布说明。
- 普通/高级可见性及跨分区预览规则继续适用；分区与高级开关切换不弹保存确认，离开设置页统一处理全部修改。

## 15. 后续规划：悬浮任务窗

状态：2026-11-17，用户效果已确认，尚未实施。当前源码为 0.7.0，实际设置共 84 项；历史节的参数数量对应其当时基线。详见 [小窗实现计划](floating-task-windows-plan.md) 第 8 节。

- 拟新增“悬浮任务窗”分组，由新窗口模块唯一注册 9 项定义：自动恢复、默认置顶为普通项；默认尺寸、最小尺寸、窗口上限与写入合并延迟为高级项。高级关闭时分组保留并显示 2 项普通设置。
- 窗口位置、各窗大小/置顶/展开状态属于窗口运行状态，由专属持久化适配器保存，不复制成 Settings 定义或写入 appsettings。
- 任务完成操作只通过 Tasks 公开 API；窗口管理设置不解释完成状态。浮窗内容可切换状态，现有速览详情继续只读。
- 普通/高级投影及全页预览规则沿用现有实现；有副作用的创建/恢复参数不预览。可逆尺寸约束的临时预览不覆盖窗口的持久化偏好。
- 该组尚未注册，当前 84 项统计不提前变化；实施时同步枚举/资源/参数计数/迁移与验收。

## 16. 后续优化：顶部拖动命中与卡片密度

状态：0.9.0 已实施；当前共 96 项设置。`floating.dragBandHeight`（12，8–20 DIP）、`floating.cardPadding`（8，4–16 DIP）、`floating.contentGap`（4，0–12 DIP）由 FloatingSettings 唯一注册，支持可逆即时预览。

命中带复用已有顶部空间，不新增等高空白行；工具与任务内容共排。预览改变不覆盖用户窗口尺寸，放弃还原；旧用户缺少值时采用默认，无窗口表迁移。命中规则、布局、验证与位置见 [拖动及密度优化](floating-drag-region-plan.md)。

## 17. 后续规划：速览完成切换与筛选

状态：2026-11-17，0.10.0 已实施。当前完整配置 98 项（16 普通/82 高级），新增两项属于 Presence。

已新增 Presence 普通定义 `desktop.popup.completionToggle`（true）和 `desktop.popup.filterSwitch`（true），归“驻留与边缘”，均为 Immediate + Reversible 展示开关；总数增至 98 项（16 普通/82 高级）。`desktop.popup.filter` 保持为弹窗初始筛选，不是面板内临时筛选。

完成切换只通过 Tasks 公开 API 提交；筛选是速览会话内状态。选项通过扩展 `CompanionViewOptions` 传给 Tasks UI，禁用 Presence 后开关定义消失。行为、拖动覆盖、无障碍与验收见 [速览完成切换与筛选计划](peek-completion-filter-plan.md)。
