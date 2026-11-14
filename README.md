# Mosaic

面向 Windows、数据保存在本机的模块化任务记录软件。

已确认技术方案：C# + WPF，采用 .NET 10、MVVM 和 SQLite。

当前版本：0.1.0。已实现模块化 WPF 应用、SQLite 持久化、任务增删改、完成/恢复、筛选、冲突反馈及未保存编辑确认。

Windows 安装包：[下载 Mosaic 0.1.0](https://github.com/soehi/Mosaic/releases/download/v0.1.0/Mosaic-0.1.0-win-x64.msi)。发布文件自带 .NET 10.0.15 运行时，无需安装 SDK。

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

本机构建产物位于 `artifacts/win-x64` 和 `artifacts/installer`。

安装到 `%LOCALAPPDATA%\Programs\Mosaic`，开始菜单提供 Mosaic 入口。数据、配置、日志分别位于 `%LOCALAPPDATA%\Mosaic\Data`、`Config`、`Logs`。首次复制默认配置，后续无效配置保留原文件并显示诊断；模块配置修改后重启生效。`modules: []` 不打开数据库。卸载保留用户数据。

可使用 `Mosaic.Desktop.exe --data-root D:\Some\Absolute\Folder` 运行独立工作空间；参数路径必须为绝对路径。数据库备份为同目录中的 `mosaic.sqlite.backup-*.sqlite`，升级前自动生成；恢复时先完全退出程序，再保留当前数据库副本并用所选备份替换。

文档：

- [整体架构思想](docs/architecture-principles.md)：独立保存长期适用的架构原则。
- [细节实现计划](docs/implementation-plan.md)：接口、数据结构、目录、开发顺序与验收条件。
- [技术方案变更记录](docs/decisions/0001-csharp-wpf.md)：C# + WPF 的实施选择及其影响。
- [实施与验证记录](docs/implementation-status.md)：实际交付、测试覆盖和待验收事项。
- [设置功能规划](docs/settings-plan.md)：可参数化清单、普通/高级设置边界与模块化方案。
- [Mosaic 0.1.0 发布说明](docs/releases/v0.1.0.md)：安装信息、校验值和验证结果。

核心约束：每项职责只有一个归属模块；跨模块只允许通过公开协议提出调用申请，禁止访问内部实现和数据；缺少能力时先判断职责归属，再扩展公开接口或创建模块；宿主负责装配；导航和页面入口由已激活功能的贡献参数生成。
