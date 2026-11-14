# Mosaic 0.1.0：实施与验证记录

日期：2026-11-13。实施依据：[细节实现计划](implementation-plan.md)、[架构原则](architecture-principles.md)。

## 交付内容

- 13 个独立生产程序集、7 个测试项目；C# 14、WPF、MVVM 和真实 SQLite 存储。
- 任务创建、查看、编辑、删除、完成/恢复及全部/未完成/已完成筛选。
- 加载、空列表、字段错误、保存失败、版本冲突、删除确认、未保存编辑和退出等待。
- 配置驱动的模块激活及导航贡献；空配置不打开数据库；禁用界面不创建任务页面。
- 有界单消费者数据库线程、原子版本写入、同步事务回调、迁移校验与升级前一致性备份。
- 数据目录独占锁文件及仅当前用户可访问的激活管道；异步关窗、在途请求租约与逆序资源回收。
- win-x64 自包含文件夹发布及 WiX 每用户 MSI；数据位于安装目录外。

实际项目布局见 `Mosaic.sln`。相关小协议集中在各自契约项目的源码文件中，未为每个类型机械拆分文件。

## 固定版本与实施选择

| 项目 | 版本/选择 |
| --- | --- |
| 开发 SDK | global.json：10.0.300，latestPatch |
| 自包含 .NET / Windows Desktop 运行时 | 10.0.15，显式固定，已核对发布 runtimeconfig |
| Microsoft.Data.Sqlite / Hosting | 10.0.9 |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5；锁文件解析原生 SQLite 3.53.4 |
| CommunityToolkit.Mvvm | 8.4.2 |
| xUnit v3 / runner / Test SDK | 4.0.3 / 4.0.1 / 18.12.0 |
| WiX SDK | 4.0.6，MS-RL；仅构建工具，不进入应用运行时 |
| 目标平台 | Windows 10 2004（19041）及以上，x64；本机验证为 Windows 11 26100 |
| 应用标识与范围 | Mosaic；版本 0.1.0；每用户安装；内置四格图标 |
| 字段规则 | 标题 Trim 后 1–200 个 Unicode 码点；备注最多 20,000 个码点 |
| SQLite 策略 | 单连接、不池化、DELETE 日志模式、默认 128 队列容量及 3 秒锁等待 |

自包含补丁依据 [官方 .NET 10 下载页](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)。旧 Microsoft.Data.Sqlite 传递的 SQLitePCLRaw 2.1.11 原生包被 NuGet 审计拒绝，故显式升级该依赖，保留驱动程序集边界。

WiX 7 的额外维护费协议没有被接受；采用已验证的 [WiX 4.0.6](https://www.nuget.org/packages/WixToolset.Sdk/4.0.6)。固定每用户安装不适用 ICE91；上游 e_sqlite3.dll 无语言资源导致 ICE60，因此安装项目窄范围排除这两项 ICE。其余安装器检查继续执行，未修改上游原生二进制资源。

## 已执行验证

Release 构建：0 警告、0 错误。格式检查通过。锁定依赖还原通过。

`dotnet test --solution Mosaic.sln -c Release --no-build --no-restore`：**47 成功、0 失败、0 跳过**（不含桌面自动化烟雾测试）。

| 测试项目 | 数量 | 主要覆盖 |
| --- | ---: | --- |
| Architecture | 3 | csproj 与实际程序集依赖、框架限制、公开类型清单、服务定位器/反射/SQL 越界 |
| Kernel | 3 | 依赖排序、重复提供者、缺失/版本/循环拒绝、调用租约、失败逆序回收 |
| Tasks.Core | 6 | CRUD、不可变字段、Unicode 限额、无效请求、取消、冲突、停止、存储失败 |
| Storage.Sqlite | 5 | 重开持久化、迁移摘要/版本拒绝/回滚/备份、取消后真实提交、停止排空、会话生命周期、锁超时 |
| Tasks.Contract | 2 | 同一仓储契约分别验证内存替身和真实 SQLite；排序、筛选、条件竞争写入、删除、重开 |
| Wpf | 21 | STA/Dispatcher、保存失败保留输入、冲突、删除取消、最新筛选、空贡献、保存中退出、重试覆盖、反馈分离、固定操作栏、筛选回退、主题调色板、导航契约与宽窄预览渲染 |
| Desktop.SmokeTests | 7 | 发布程序真实 UI Automation：中文/emoji CRUD、完成/恢复、单实例、重启读取，以及空/仅后端/缺依赖/坏 JSON/重复字段/路径穿越配置 |

数据库集成测试使用真实临时文件。桌面测试使用 `artifacts/smoke-data/<guid>`，不使用日常数据库。WPF 控件在独立 STA Dispatcher 中创建。

### 窄窗口渲染修复（2026-11-14）

任务页面在窄窗口下偶发越界文字浮层。根因是外壳把空工作空间提示放在内容页下层，内容层未使用不透明背景，导致“你的工作空间已就绪 / 当前没有启用的功能…”从任务页面透明区域透出并与界面文字混叠。修复为：有页面内容时隐藏空工作空间占位层，并给内容层增加不透明背景模板；任务列表、编辑器和滚动视口同时启用边界裁剪与像素对齐。

修复后的 Release 构建为 0 警告、0 错误，24 项非桌面自动化测试通过；WPF 测试新增断言，验证空工作空间占位层在有页面内容时切换为隐藏。当前受限会话中的交互式桌面自动化被物理 `Esc` 中止，因此桌面视觉复测仍需在正常交互式 Windows 会话执行。现有 2026-11-13 的 31 项测试结果保留为修复前基线。

发布文件夹已重新生成。MSI 也使用更新后的负载重建；由于本次受限会话无法访问 Windows Installer 服务，ICE 校验在此次重建中关闭，完整 ICE 结果仍以 2026-11-13 的安装验证为基线。

### 审查问题修复（2026-11-14）

本轮按代码审查清单完成了以下修复：

- 单实例从 `Local\` 会话内互斥量改为数据目录内独占锁文件；最终路径分析可归并尾部分隔符、短路径和联接点，激活管道名也由同一最终路径生成。监听失败增加 250 毫秒退避和诊断上报。
- 外壳在页面初始化成功后才显示；启动后半段失败会先释放 Shell，再恢复诊断窗口。关窗协调器会继续执行 Shell、模块和宿主的剩余清理步骤，观察清理异常，并避免从 `async void Closing` 二次泄漏异常。
- 事务入口在排队和调用回调前拒绝 `Task`、`ValueTask` 和 `ValueTask<T>` 回调。
- provider 绑定现在要求对应模块同时启用，禁用模块的陈旧绑定会在启动时拒绝。
- 任务排序只由 `ITaskRepository` 契约拥有，业务服务原样透传；任务模块自己启停执行门，避免模块状态与宿主门状态分离。
- UI 贡献注册返回可释放句柄，导航使用可观察只读集合；撤销贡献会同步移除导航并释放已显示页面。
- Kernel 在激活前比较 factory 返回的模块清单与装配清单；模块清单增加职责、生命周期和入口版本，内置入口集中由模块目录创建。
- SQLite 行同时保留列序和名称查找，重复列名不再因字典键冲突伪装成存储故障；迁移备份仅保留最近 5 份。
- 任务加载及后续页面操作的程序错误会带相关编号写入诊断；Host 不再重复读取已由强类型配置加载器解析的 JSON。

修复后 Release 构建为 0 警告、0 错误，31 项非桌面自动化测试通过。进程级验证确认主实例存活、使用尾部分隔符启动的第二实例以退出码 0 退出，数据库成功建立；禁用模块的陈旧 provider 配置被拒绝且未创建数据库。当前受限桌面的 UI Automation 仍只能在 8 项中完成 5 项，默认、空配置和仅后端 3 项在 15 秒内无法发现窗口，沿用此前的交互式桌面环境限制，不能据此判定视觉回归。联接点自动化因权限审批超时未执行；短路径在 NTFS 8.3 名称未生成的当前卷上不可用。

本次发布目录和 MSI 已使用修复后的负载重建。`dotnet format --verify-no-changes` 仍因受限会话不能连接 MSBuild 命名管道而无法执行；Roslyn 编译器与分析器在 Release 构建中报告 0 警告。MSI 因 Windows Installer 服务不可访问而使用 `SuppressValidation=true` 重建，完整 ICE 验收仍以 2026-11-13 的历史结果为准。

### 前端反馈与主题收尾（2026-11-14）

在上一轮模块边界收尾后，继续处理 UI 反馈、响应式与主题问题：

- 列表加载态先清除旧错误卡片，重试期间按钮不可重复触发；空态、错误、加载三层按 `ZIndex` 0/1/2 排列并新增断言。
- 字段校验错误只出现在字段下方，修改对应输入即清除；全局错误与成功消息互相替换，新建和删除后的消息不再残留。
- 编辑器备注改为 `MinHeight/MaxHeight` 自适应，底部保存与操作栏固定在滚动区域外，小窗口不再需要滚到底部才能保存。
- 冲突有独立提示卡与“重新加载最新版本”入口；所有保存、读取、状态切换、删除操作显示忙碌条并禁用可编辑控件。
- 在当前筛选下新建、保存或切换完成状态导致结果不可见时，自动切回“全部任务”，避免误判保存失败。
- 标题和备注提供必填/上限帮助文字与实时码点计数，超限时计数变红；对比度不足的 `TextMuted` 已调整并通过 4.5:1 断言。
- 共享 `Mosaic.Presentation.Wpf` 主题包含浅色、深色、高对比度三套调色板；启动时按系统设置自动选择，运行时跟随系统切换。
- 导航改为纯文本标签加 `IconKey` 契约，外壳统一渲染图标并高亮当前导航项；任务模块不再把“✓   任务”写进标签。
- 宿主不再特判 `tasks-ui`：`IUiModule` 是通用界面模块契约，`ModuleRuntime` 在模块启动后报告激活、停止前报告停止，宿主据此统一注册/撤销导航与页面贡献。
- `ShellWindow` 在 1040 DIP 以下把 190 DIP 完整侧栏收为 64 DIP 图标栏，并在 1060 DIP 才重新展开；图标栏保留 44×44 可访问导航按钮，只隐藏展示标签，不撤销导航贡献。
- `TasksView` 在自身可用宽度 920 DIP 以下把列表与详情改为单页切换；两个面板复用同一实例和 ViewModel，跨断点保留选择、输入与滚动状态，“返回任务列表”沿用未保存确认。
- 任务详情在窄栏或高度小于 680 DIP 时把“标记完成/重新加载”放在第一行、“删除”独占第二行；按钮最小高度为 40 DIP，底部操作区始终位于表单滚动区之外。

本轮更新后 Release 构建为 0 警告、0 错误，49 项非桌面自动化测试通过（架构 3、内核 5、存储 8、任务契约 2、任务核心 8、WPF 23）。响应式测试覆盖 760、839、920、1039、1040、1059、1060、1220 DIP 及 620、780 DIP 高度；`artifacts/ui-preview/` 包含 `shell-760-rail.png`、`tasks-detail-760.png`、`tasks-detail-920.png` 等计划要求的预览。

### 发布与安装

- 发布目录：`artifacts/win-x64`，含可执行文件、.NET/WPF 运行时及 x64 SQLite 原生库。
- 正式本机构建：`artifacts/installer/Mosaic-0.1.0-win-x64.msi`。
- `0.1.1` MSI 仅用于测试同一应用负载的安装器升级行为，不代表另一应用功能版本。
- `build/test-install.ps1` 最终复核已通过：0.1.0 每用户安装、0.1.1 升级、安装后与升级后各 7 项桌面烟雾测试、卸载、哨兵文件哈希保留和安装二进制清除。测试安装已卸载。
- 汇总日志：`artifacts/installation-verification.log`；详细日志：`artifacts/installer/install-test.log`、`upgrade-test.log`、`uninstall-test.log`。所有 Windows Installer 操作返回成功。
- 安装器日志位于 `artifacts/installer`；升级和卸载的数据保留检查使用独立哨兵文件，不宣称覆盖所有历史版本数据库迁移。

### 可复现命令

```powershell
dotnet restore Mosaic.sln --locked-mode -m:1
dotnet build Mosaic.sln -c Release --no-restore -m:1
pwsh -File build/publish.ps1
dotnet test --solution Mosaic.sln -c Release --no-build --no-restore
dotnet format Mosaic.sln --verify-no-changes --no-restore
pwsh -File build/package.ps1 -NoPublish
```

受限宿主中的 Roslyn/MTP 命名管道及桌面自动化可能被禁止，测试需要在允许命名管道的交互式 Windows 环境运行；`build/test.ps1` 可直接运行各 xUnit 可执行入口。`-SkipDesktop` 仅运行架构、内核、业务、数据库和 STA 测试。

本机遇到官方 NuGet v3 签名元数据端点 TLS 失败，采用 `build/publish.ps1 -UseV2Feed` 成功从官方 v2 端点还原；未关闭签名校验，也未改用第三方包源。

安装升级复核仅在没有既有 Mosaic 安装的测试用户中运行：

```powershell
pwsh -File build/package.ps1 -NoPublish -ProductVersion 0.1.1
pwsh -File build/test-install.ps1
```

## 后续验收边界

阶段 0–5 的首版功能、架构与本机测试已落实；阶段 6 的发布及本机 MSI 已交付，尚不能视为全部分发环境验收完成。

尚未执行：无 SDK 的干净 Windows 用户/虚拟机、Windows 10 实机、不同 DPI/中文输入法矩阵、ARM64、物理磁盘满/权限故障注入及迁移备份失败故障注入。本机未提供 Windows Sandbox。安装器数据保留检查和事务回滚测试不替代这些检查。

正式代码签名证书尚未提供，EXE/MSI 未签名。提醒、同步、分组、优先级、插件市场等仍不进入首版。
