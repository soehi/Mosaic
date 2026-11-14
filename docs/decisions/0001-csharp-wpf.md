# 0001：采用 C# + WPF 实施

日期：2026-11-13。状态：已确认；由用户指定。源码及本机验证已完成，详见 [实施与验证记录](../implementation-status.md)。

## 决策

Windows 桌面版本采用 C# + WPF，按 .NET 10 LTS、MVVM 和 SQLite 实施。详细安排见 [细节实现计划](../implementation-plan.md)。

## 计划调整

| 项目 | 原计划 | 当前计划 |
| --- | --- | --- |
| 界面 | Electron、React 和 TypeScript | WPF、XAML 和 C# MVVM |
| 项目边界 | workspace 包、包导出约束 | 独立 csproj/程序集、接口、internal 和引用白名单 |
| 模块请求 | 前后端代理和 IPC | 同进程带类型的受控接口与执行租约 |
| 数据库访问 | better-sqlite3 | Microsoft.Data.Sqlite 与存储模块后台执行队列 |
| 导航页面 | 前端组件与贡献描述 | 导航数据绑定、IViewFactory 与 ContentControl |
| 构建测试 | Node 工具链 | dotnet CLI、xUnit、程序集架构测试和 WPF STA 测试 |
| 分发 | Electron 安装包 | .NET 自包含发布及 Windows 每用户安装包 |

## 架构约束如何落实

每项职责唯一归属、跨模块只走公开申请、禁止访问内部实现、缺少独立能力时创建模块，以及导航由激活功能参数生成，继续作为实现的硬约束。

宿主为唯一装配根，不向模块暴露整个 DI 容器；UI 不引用仓储协议；任务业务不引用 WPF 或数据库驱动；数据库驱动对象不离开存储程序集。界面与数据库的线程和退出生命周期在实现计划中单独规定。

## 验证与待办

已检查本机有 .NET SDK 10.0.300 和 9.0.306。SDK 可用不代表项目已构建或 WPF 环境已通过验收。

已锁定 SDK 10.0.300 和 NuGet 版本，完成 WPF 构建、SQLite 原生依赖、自包含发布及本机每用户安装验证。目标 Windows 10 2004+ x64；WiX 4.0.6 生成 MSI。正式签名及干净系统验收尚未完成。
