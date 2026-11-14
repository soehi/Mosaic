# Mosaic：设置功能规划（定稿）

状态：规划定稿，待实施。建议作为 0.2.0 交付。本文只规划，不包含代码改动。

## 1. 已确认范围

- 前文清单中的所有可调行为参数全部纳入设置。
- 普通/高级分类按前文建议：用户日常项放普通设置；其余放高级设置。
- 前文标为“不建议”的项全部放入高级设置，并标记风险、范围和重启要求。
- 高级设置可展开，包含全部参数、默认值、当前值、约束说明、恢复默认和“下次启动生效”徽标。
- 唯一不设项的是模块间协议常量（错误码字符串、请求字段结构、单实例管道名、主题资源键）。它们不是运行参数；改动属于契约版本升级，由架构测试锁定。

## 2. 职责与新增模块

| 模块 | 提供 | 需要 | 唯一职责 |
| --- | --- | --- | --- |
| settings-sqlite | settings.repository@1 | sqlite.session@1 | 设置表迁移、参数化 SQL、值映射 |
| settings-core | settings.api@1 | settings.repository@1、各设置来源 | 定义注册、聚合、校验、持久化请求、恢复默认、即时/重启路由 |
| settings-ui | 设置导航与页面 | settings.api@1、通用界面服务 | 普通/高级设置界面、按类型渲染、警告与重启徽标 |
| host（基础设施，不是模块） | 宿主设置来源 | — | appsettings.json 的读取/校验/原子写回、日志、启动窗口与单实例参数 |

归属规则：

- 参数的语义仍归原模块：任务限额归 tasks-core，交互行为归 tasks-ui，主题与布局归 shell，存储参数归 storage-sqlite，宿主参数归 host。
- settings-core 只保存“定义 + 值 + 校验结果”，不实现任务规则、不执行 SQL、不决定布局。
- settings-ui 只调用 settings.api，不直接读写设置表，不直接修改 appsettings.json。

## 3. 参数总表（54 项）

### 3.1 普通设置（7 项，即时生效）

| ID | 默认值 | 归属 | 范围/枚举 |
| --- | --- | --- | --- |
| `appearance.theme` | FollowSystem | shell | FollowSystem / Light / Dark / HighContrast |
| `tasks.confirmDelete` | true | tasks-ui | bool |
| `tasks.confirmUnsaved` | true | tasks-ui | bool |
| `tasks.defaultFilter` | All | tasks-ui | All / Open / Completed |
| `tasks.showCharacterCount` | true | tasks-ui | bool |
| `tasks.autoSwitchToAllOnHidden` | true | tasks-ui | bool |
| `tasks.sortOrder` | CreatedDesc | tasks-core/仓储 | CreatedDesc / CreatedAsc / TitleAsc / TitleDesc |

### 3.2 高级：任务（5 项，重启生效）

| ID | 默认值 | 范围 | 风险 |
| --- | --- | --- | --- |
| `tasks.title.maxCharacters` | 200 | 1–1000 | 收紧后旧任务仍可读；编辑超限需截断 |
| `tasks.notes.maxCharacters` | 20,000 | 1,000–200,000 | 同上；过大会增加编辑与保存负担 |
| `tasks.title.required` | true | bool | 关闭后允许空标题，列表显示“未命名任务” |
| `tasks.defaultCompleted` | false | bool | 开启后新任务直接进入已完成 |
| `tasks.revisionStart` | 1 | 1–1,000,000 | 仅影响新任务初始版本；冲突语义不变 |

### 3.3 高级：外观与布局（19 项，即时生效）

| ID | 默认值 | 范围 |
| --- | --- | --- |
| `shell.sidebar.collapseBelowWidth` | 1040 DIP | 720–1600 |
| `shell.sidebar.expandAtWidth` | 1060 DIP | collapse+20–1800 |
| `shell.sidebar.fullWidth` | 190 DIP | 160–260 |
| `shell.sidebar.railWidth` | 64 DIP | 48–88 |
| `shell.window.minWidth` | 760 DIP | 640–1200，且 ≥ railWidth + 任务内容下限 |
| `shell.window.minHeight` | 620 DIP | 520–900 |
| `shell.page.marginLeft` | 32 DIP | 8–64 |
| `shell.page.marginTop` | 30 DIP | 8–64 |
| `shell.page.marginRight` | 28 DIP | 8–64 |
| `shell.page.marginBottom` | 28 DIP | 8–64 |
| `tasks.layout.compactBreakpoint` | 920 DIP | 600–1400 |
| `tasks.editor.compactActionsWidth` | 480 DIP | 360–900 |
| `tasks.editor.compactActionsHeight` | 680 DIP | 480–1000 |
| `tasks.list.minColumnWidth` | 300 DIP | 220–500 |
| `tasks.editor.notesMinHeight` | 128 DIP | 96–360 |
| `tasks.button.minWidth` | 96 DIP | 72–160 |
| `tasks.button.minHeight` | 40 DIP | 32–56 |
| `startup.width` | 580 DIP | 420–1200 |
| `startup.height` | 260 DIP | 240–800 |

### 3.4 高级：存储与系统（12 项，全部重启生效）

| ID | 默认值 | 归属 | 范围/说明 |
| --- | --- | --- | --- |
| `storage.filename` | mosaic.sqlite | host | 合法单个文件名；改名后新库为空，旧库保留 |
| `storage.location` | local-app-data | host | local-app-data 或绝对路径；换位置不自动搬迁 |
| `storage.queueCapacity` | 128 | storage-sqlite | 16–4096 |
| `storage.busyTimeoutSeconds` | 3 | storage-sqlite | 1–30 |
| `storage.backupRetention` | 5 | storage-sqlite | 0–50；0 表示不保留自动备份 |
| `storage.journalMode` | Delete | storage-sqlite | Delete / Wal；WAL 会产生 -wal/-shm，需完整备份与切换测试 |
| `storage.connectionMode` | ReusedSingle | storage-sqlite | ReusedSingle / PooledPerOperation；两者仍由单消费者队列串行执行 |
| `host.modules` | 四模块 | host | 启用列表；复用 AppConfiguration 校验 |
| `host.providers` | 三绑定 | host | 能力绑定；禁用模块残留绑定仍拒绝 |
| `logging.level` | Information | host | Debug / Information / Warning / Error / None |
| `logging.retentionDays` | 0 | host | 0–365；0 表示不自动清理 |
| `diagnostics.singleInstanceRetryMilliseconds` | 250 | host | 50–2000 |

### 3.5 高级：文案（11 项，即时生效）

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

- 左侧分区：普通、任务、外观与布局、存储、系统与模块、文案。
- 顶部“高级设置”开关；关闭时只显示普通设置。
- 每行显示名称、说明、当前值、默认值、约束、恢复默认按钮、风险图标和“需重启”徽标。
- 控件按类型渲染：开关、数字框、下拉框、文本框、模块多选、provider 映射编辑。
- 保存前逐项校验；字段错误就地显示；跨字段错误显示在相关组顶部。
- 保存成功后：即时项刷新界面；重启项显示“下次启动生效（N 项）”。
- 恢复默认支持单项、分区、全部；全部恢复会提示同时清除已禁用模块保存的值。
- 关闭设置页或切换分区时提示未保存修改，复用通用 `INavigationGuard`。

危险项二次确认：

- 关闭标题必填、开启默认完成、修改 Revision 起点。
- 切换 WAL、切换 PooledPerOperation。
- 修改数据库文件名/位置、禁用模块。
- 恢复全部默认。
确认框列出影响范围与不可逆边界；用户取消则不提交。

## 7. 各模块如何消费参数

- tasks-core：激活时读取任务规则，构造 `TaskSettings` 快照；`TasksApi` 增加 `GetConstraintsAsync`，返回当前标题/备注限额与必填规则。任务 UI 从任务 API 读取显示值，不直接读设置。
- tasks-ui：行为开关、默认筛选、排序、文案覆盖在 ViewModel 初始化时读取；即时变更后刷新绑定。空标题任务显示 `ui.text.untitledTask`。
- tasks-core/仓储：排序扩展 `TaskQuery.SortOrder`；仓储契约拥有排序 SQL，UI 不自行排序。
- shell：主题来源调用 `MosaicThemeManager.Apply`；布局参数重算侧栏宽度、断点、页面边距；窗口最小尺寸同步更新。
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

交付：tasks-core 规则与约束 API、tasks-ui 行为/排序/文案、shell 布局与主题、storage 日志模式与连接模式。

验收：契约测试保证任务 UI 显示限额与业务校验一致；布局边界矩阵通过；主题切换即时；WAL/池化模式切换与回退通过。

### S5：settings-ui

交付：普通页、高级分区、动态控件、警告、重启徽标、恢复默认、未保存离开确认。

验收：STA 测试覆盖每类控件、字段错误、跨字段错误、危险确认取消、恢复默认、禁用模块空态；无业务模块时设置页仍可打开并从宿主来源读取系统项。

### S6：集成验收

交付：0.2.0 安装包与验证记录。

验收：安装/升级保留设置；改设置→重启→仍生效；改回默认；禁用任务模块后设置页仍可用；重新启用恢复值；无开发环境干净 Windows 启动通过。

## 10. 测试矩阵

| 检查层 | 重点 |
| --- | --- |
| 架构 | 引用白名单、public API、禁止直接访问 settings 表、协议常量未被运行时可改 |
| settings-core | 注册冲突、范围、枚举、跨字段、事务回滚、pending、恢复默认、来源撤销 |
| settings-sqlite | 迁移、重开、JSON 值映射、禁用模块值保留、备份 |
| tasks-core | 必填关闭、默认完成、Revision 起点、排序扩展、约束 API 一致性 |
| tasks-ui | 行为开关即时生效、文案覆盖/回退、排序、空标题显示、未保存确认 |
| shell | 主题、断点、宽度、边距、最小尺寸；边界不造成不可操作 |
| storage | 队列/锁/备份参数、WAL 往返、Pooled 模式串行保证、关闭排空 |
| host | appsettings 写回、原子替换、旧配置兼容、模块禁用矩阵、日志级别 |
| 桌面 | 设置页导航、保存、重启徽标、危险确认、安装升级保留 |

## 11. 完成定义

设置功能完成需同时满足：

- 54 项参数均可在对应普通/高级区域查看、修改、校验和恢复默认。
- 普通项即时生效；重启项保存后明确显示 pending，重启后生效。
- settings-core 不理解任务、数据库或布局；各模块仍独占自己的规则与数据。
- 所有危险项有明确警告、约束和测试；没有绕过公开接口直接读写设置表的路径。
- 0.1.0 配置与数据库可平滑加载；设置表迁移可备份、可重开；安装升级不丢设置。
- Release 构建、架构测试、业务/存储/界面测试与桌面验收通过后才算交付。
