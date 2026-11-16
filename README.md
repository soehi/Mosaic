# Mosaic

面向 Windows、数据保存在本机的模块化任务记录软件。

已确认技术方案：C# + WPF，采用 .NET 10、MVVM 和 SQLite。

当前本地开发版本：0.6.0。已实现模块化 WPF、SQLite 任务与设置、跨分区设置持续预览、宿主与用户设置提交日志，以及关闭主窗口后驻留、边缘任务速览和恢复草稿。0.6.0 为旧配置加入一次性 Presence 迁移，避免用户禁用后每次启动被重新启用。实际验证范围见实施记录。

Windows 安装包：[下载 Mosaic 0.6.0](https://github.com/soehi/Mosaic/releases/download/v0.6.0/Mosaic-0.6.0-win-x64.msi)。发布文件自带 .NET 10.0.15 运行时，无需安装 SDK。

目标为 Windows 10 2004（build 19041）及以上的 x64 系统；已验证 Windows 11 x64。其他系统、ARM64、干净虚拟机及不同 DPI/输入法组合仍需独立验收。

开发需要 .NET SDK 10.0.300（global.json 锁定），构建与验证：

```powershell
dotnet restore Mosaic.sln --locked-mode -m:1
dotnet build Mosaic.sln -c Release --no-restore -m:1
pwsh -File build/publish.ps1
pwsh -File build/test.ps1 -NoBuild
dotnet format Mosaic.sln --verify-no-changes --no-restore
dotnet run --project src/Mosaic.Desktop
```

`build/test.ps1` 使用 xUnit 可执行测试入口；桌面测试需要可交互的 Windows 桌面，并在 `artifacts/smoke-data` 中隔离数据。无桌面时使用 `-SkipDesktop`。也支持 .NET 10 的 `dotnet test --solution Mosaic.sln -c Release --no-build --no-restore`；受限宿主可能不允许其命名管道。

若官方 NuGet v3 签名元数据端点连接失败，可用 `pwsh -File build/publish.ps1 -UseV2Feed` 切换到官方 v2 源；仍保留锁文件与包签名验证。

构建安装包（WiX SDK 4.0.6 自动还原；PowerShell 7）：

```powershell
pwsh -File build/package.ps1 -NoPublish
```

本机仅使用一个完整发布目录 `artifacts/local-run`，安装包输出到 `artifacts/installer`；bin/obj 为开发构建缓存。

安装到 `%LOCALAPPDATA%\Programs\Mosaic`，开始菜单提供 Mosaic 入口。数据、配置、日志分别位于 `%LOCALAPPDATA%\Mosaic\Data`、`Config`、`Logs`。首次复制默认配置，后续无效配置保留原文件并显示诊断；模块配置修改后重启生效。`modules: []` 不打开数据库。卸载保留用户数据。

可使用 `Mosaic.Desktop.exe --data-root D:\Some\Absolute\Folder` 运行独立工作空间；参数路径必须为绝对路径。数据库备份为同目录中的 `mosaic.sqlite.backup-*.sqlite`，升级前自动生成；恢复时先完全退出程序，再保留当前数据库副本并用所选备份替换。

文档：

- [整体架构思想](docs/architecture-principles.md)：独立保存长期适用的架构原则。
- [细节实现计划](docs/implementation-plan.md)：接口、数据结构、目录、开发顺序与验收条件。
- [技术方案变更记录](docs/decisions/0001-csharp-wpf.md)：C# + WPF 的实施选择及其影响。
- [实施与验证记录](docs/implementation-status.md)：实际交付、测试覆盖和待验收事项。
- [设置功能规划](docs/settings-plan.md)：可参数化清单、普通/高级设置边界与模块化方案。
- [任务速览 UI 与更新简要任务计划](docs/peek-and-update-summary-plan.md)：待实施的速览改版、每版本一次的更新任务与同步交付验收。
- [Mosaic 0.1.0 发布说明](docs/releases/v0.1.0.md)：安装信息、校验值和验证结果。
- [Mosaic 0.5.0 发布说明](docs/releases/v0.5.0.md)：架构收口、验证范围和安装包信息。
- [Mosaic 0.6.0 发布说明](docs/releases/v0.6.0.md)：旧配置一次性迁移、迁移验收和安装包信息。

核心约束：每项职责只有一个归属模块；跨模块只允许通过公开协议提出调用申请，禁止访问内部实现和数据；缺少能力时先判断职责归属，再扩展公开接口或创建模块；宿主负责装配；导航和页面入口由已激活功能的贡献参数生成。

后台驻留与模块边界见 [驻留规划](docs/desktop-presence-plan.md)。新工作空间默认关闭主窗口后进入驻留；通过边缘入口可恢复或退出。0.6.0 首次启动会把旧版已启用 `tasks-ui` 的配置升级为同时启用 Presence，并记录一次性迁移标记；之后用户主动关闭 Presence 不会被自动恢复。
