# Mosaic

本地优先的 Windows 桌面任务工作台：任务、速览、浮窗、账户与备份、设置、维护集中在同一个模块化应用里。数据默认保存在本机 SQLite，账户与同步可选。

当前版本 **0.19.2**：本机存有凭据时打开软件自动登录；登录状态下按可配置间隔（默认 60 秒）自动备份任务与设置，登录即备份一次；0.19.1 的任务速览、差量刷新与整表恢复批量通知保持不变。

Windows 安装包：[下载 Mosaic 0.19.2](https://github.com/soehi/Mosaic/releases/download/v0.19.2/Mosaic-0.19.2-win-x64.msi)。安装包自带 .NET 10 运行时，无需安装 SDK。

---

## 功能导览

### 任务

- 新建、编辑、完成、恢复；标题与备注支持长度上限，未保存草稿有离开确认。
- 附件：添加、打开、删除，大小与数量上限可配置，归档后可按天数自动清理。
- 归档：手动归档、恢复、已确认的永久删除；主列表提供“已归档”筛选。
- 版本号、创建/更新时间、排序与筛选；列表与速览共享同一任务 API。
- 读屏与键盘：完整无障碍标签，附件按数量播报，按钮松开即结束按压反馈。

### 速览

- 侧边常驻的快速浏览区，支持按完成状态筛选，并记住上次选择。
- 点击圆圈切换完成状态；详情以只读卡片展示，备注可选中复制。
- 整条任务可拖出为悬浮窗；拖出成功后自动收起，可继续拖出其他任务。
- 浮窗放置位置有虚线预览，与最终创建位置共用同一套坐标与屏幕夹取规则。

### 悬浮窗

- 置顶、紧凑/展开、关闭后保留尺寸与模式。
- 标题栏状态、展开与工具按钮支持按住拖动窗口，阈值内短按仍执行原操作。
- 多显示器安全落点；关闭主窗口进入驻留，可从边缘入口恢复。
- 任务数据只通过 Tasks API 提交，浮窗不直接写数据库。

### 账户与备份

- 可选登录：邮箱验证码或密码、设备码（在浏览器中确认）；未配置服务器时完全离线使用。
- 账户页展示账户、同步与会话；登出或删除后清空会话与备份/恢复摘要。
- 备份与恢复摘要使用统一排版，空状态、刷新与按钮样式一致。

### 设置

八个分区：任务、外观与布局、存储与系统、文案、驻留与边缘、悬浮任务窗、账户与备份、维护。

- 每个设置显示生效状态（“预览中”“暂存，保存后生效”“保存后重启”），并给出影响说明。
- 主题支持浅色、深色、高对比；语言跟随系统或手动切换。
- 高级设置默认收起，切换分区时保持顶部说明与左侧导航位置不变。

### 维护

- 应用内卸载与修复安装入口；卸载默认保留 `%LOCALAPPDATA%\Mosaic` 用户数据，删除数据需显式勾选并二次确认。
- 数据库位置迁移：选择目标文件夹后先校验——必须是绝对路径、不能是磁盘根、目录必须已存在、不能与当前数据库目录相同、目录内不能已有同名 `mosaic.sqlite`、目标磁盘可用空间要够（目标文件夹本身可以不是空的）；请求写入配置目录，退出后在数据库连接打开前完成移动；默认保留源文件作为副本（`KeepSource = true`）。
- 升级前自动生成 `mosaic.sqlite.backup-*.sqlite`；日志与诊断保存在 `%LOCALAPPDATA%\Mosaic\Logs`。

### 更新简要

每个版本首次启动时生成一条“更新简要”任务，说明本版本新增、改进与修复；同一版本只生成一次。

---

## 安装与升级

系统要求：64 位 Windows 10 版本 2004（build 19041）或更高版本，或 Windows 11（安装包启动条件为 `VersionNT64 AND WINDOWSBUILD >= 19041`，`build/installer/Package.wxs:21`）。

1. 下载并运行 `Mosaic-0.19.2-win-x64.msi`，按向导完成安装。
2. 首次启动创建数据目录 `%LOCALAPPDATA%\Mosaic`，数据库文件为 `mosaic.sqlite`。
3. 新版本直接覆盖安装；升级保留设置、账户会话与任务数据，并在升级前自动备份数据库。

需要独立工作空间时：

```powershell
Mosaic.Desktop.exe --data-root D:\Some\Absolute\Folder
```

路径必须是绝对路径。每个数据根有独立的 `Config/appsettings.json`、数据库、日志与备份。

卸载默认保留用户数据；如需彻底清理，在维护页勾选“删除数据”并二次确认，或手动删除 `%LOCALAPPDATA%\Mosaic`。

---

## 快速上手

1. **建一个任务**：主页面右下角新建，填写标题与备注，需要时添加附件。
2. **打开速览**：在侧边查看今天要做的事；用筛选只看未完成，选择会被记住。
3. **拖出浮窗**：把速览里的任务整条拖到桌面，作为置顶小窗持续可见；关掉后再拖出，落点取本次释放的位置，尺寸、模式与置顶状态沿用该任务的旧记录（`FloatingWindowCoordinator.cs:32-37`）。
4. **整理**：完成后点击圆圈标记完成；不用的任务归档，需要时从“已归档”筛选恢复。
5. **配置**：进入设置调整自动归档、附件上限、主题、语言、驻留与浮窗行为；改动会标注何时生效。
6. **备份**：在“账户与备份”页查看备份与恢复摘要；迁移数据库位置前先确认目标磁盘有足够空间、目标文件夹里没有同名 `mosaic.sqlite`（文件夹里的其他文件不受影响）。

---

## 架构

Mosaic 是模块化单体：每个职责只有一个归属模块，模块之间只通过公开契约通信，宿主负责装配。

```text
Mosaic.Desktop（宿主）
  └─ Mosaic.Kernel（模块图、启停、执行门）
       └─ Mosaic.Contracts（清单与能力契约）
            ├─ Mosaic.Tasks.*        任务、附件、归档
            ├─ Mosaic.Settings.*     设置与设置页
            ├─ Mosaic.Storage.Sqlite SQLite 会话、备份、迁移
            ├─ Mosaic.FloatingWindows.* 浮窗与落点
            ├─ Mosaic.Auth.* / Sync.*   账户与同步
            ├─ Mosaic.Maintenance.*  卸载、迁移、诊断
            └─ Mosaic.Presentation.* 导航、页面、主题、本地化
```

核心约束：

- 跨模块只允许通过公开协议提出调用；禁止访问内部实现与数据表。
- 缺少能力时先判断职责归属，再扩展公开接口或新建模块。
- 导航与页面入口由已激活功能的贡献参数生成，Shell 不写死业务。
- 依赖方向由 `build/project-boundaries.json` 与架构测试强制。

---

## 插件开发

Mosaic 的“插件”是编译期模块：声明清单与能力，由宿主装配、内核排序、运行时启停。当前版本不支持运行时 DLL 加载或热插拔。

从零写一个插件：

1. 阅读 [插件制作教程](docs/plugins/README.md)，按步骤建契约、核心、持久化、界面四层工程。
2. 对照 [插件规范](docs/plugins/plugin-spec.md) 检查清单、能力与生命周期。
3. 按 [边界声明](docs/plugins/boundary-declaration.md) 更新引用矩阵与公开 API 清单。
4. 按 [框架规范](docs/plugins/framework-spec.md) 接入配置、设置与存储。
5. 按 [UI 规范](docs/plugins/ui-spec.md) 贡献导航、页面、状态栏与主题资源。

最小可运行示例（完整代码见教程）：

```csharp
public static ModuleManifest Definition { get; } = new(
    "notes-core",
    [new("notes.api", 1)],
    [new("sqlite.session", 1), new("settings.api", 1)],
    "Owns notes persistence and the notes API. Does not render UI.");
```

```json
{
  "modules": ["notes-core"],
  "providers": { "notes.api@1": "notes-core" }
}
```

`appsettings.json` 里的 `settings` 段保存设置页写入的权威值：`settings["host.modules"]`、`settings["host.providers"]` 存在时优先于上面两段，顶层两段只是模板默认与回退（见[框架规范](docs/plugins/framework-spec.md)）。

---

## 构建与测试

需要 .NET 10 SDK 与 Windows。

```powershell
dotnet build Mosaic.sln -c Release -m:1
build/test.ps1 -Configuration Release
```

只跑非桌面测试（更快）：

```powershell
build/test.ps1 -Configuration Release -SkipDesktop
```

生成发布目录与 MSI：

```powershell
build/publish.ps1
build/package.ps1
```

产物在 `artifacts/`：发布目录、安装包、`.wixpdb` 与 `SHA256SUMS`。发布内容指纹与版本说明由 `scripts/release_content_verify.py` 校验生成。

仓库约定：

- 警告即错误，构建必须 0 警告。
- 一次变更作为完整交付：源码、测试、用户可见行为、发布说明、实施记录同一次提交。
- 修复必须写明根因位置（文件:行号或类型.方法）与每个改动位置。
- 影响旧配置、数据目录或安装升级时，同时交付迁移逻辑与迁移验收项。

---

## 文档

**插件契约**

- [插件制作教程](docs/plugins/README.md)：从建工程到通过测试的完整步骤。
- [插件规范](docs/plugins/plugin-spec.md)：清单、能力、版本、生命周期、失败语义。
- [边界声明](docs/plugins/boundary-declaration.md)：允许引用矩阵、公开 API 清单、架构测试。
- [框架规范](docs/plugins/framework-spec.md)：启动链、模块图、配置、设置、存储、诊断。
- [UI 规范](docs/plugins/ui-spec.md)：导航、页面、设置页、状态栏、伴生视图、主题、本地化。

**架构与实施**

- [架构原则](docs/architecture-principles.md)：长期约束与交付规则。
- [架构方案](docs/architecture-plan.md)：模块划分与演进路线。
- [实施状态](docs/implementation-status.md)：每个版本的根因、改动位置与验收结果。
- [桌面驻留规划](docs/desktop-presence-plan.md)：关闭主窗口后的驻留与恢复。
- [浮窗规划](docs/floating-task-windows-plan.md)：浮窗行为与边界。
- [设置规划](docs/settings-plan.md)：设置分组、生效方式与持久化。

**版本**

- [0.19.2 发布说明](docs/releases/v0.19.2.md)
- [0.19.1 发布说明](docs/releases/v0.19.1.md)
- [0.19.0 发布说明](docs/releases/v0.19.0.md)
- [0.18.14 发布说明](docs/releases/v0.18.14.md)
- [0.18.13 发布说明](docs/releases/v0.18.13.md)
- [0.18.12 发布说明](docs/releases/v0.18.12.md)
- [0.18.11 发布说明](docs/releases/v0.18.11.md)
- [历史变更记录](docs/releases/changelog.md)
- [全部发布说明](docs/releases/)

---

## 数据与隐私

- 任务、设置、浮窗布局、账户会话均保存在本机数据目录。
- 未配置服务器时，应用不发起任何账户或同步网络请求。
- 服务器地址与账户凭据只保存在本机配置与凭据存储中。
- 卸载默认保留数据；删除数据必须显式确认。