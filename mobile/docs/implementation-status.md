# 手机端 0.3.0 实施记录

日期：2026-11-26
分支：`claude/mobile-rebuild-8c92e7`
备份 tag：`backup/mobile-pre-refactor-20261126` → `8bc0acdee39fcb643e0461165ee0ba230c7e52a6`
版本：`0.3.0` / Android `versionCode=5` / 包名 `app.mosaic.tasks`
需求基线：`docs/mobile-refactor-plan.md`（未修改）

## 1. 根因位置

| 编号 | 根因位置 | 触发条件与出错原因 |
|---|---|---|
| R1 | `docs/mobile-refactor-plan.md:41`（旧版 `:10-16`） | 把“组件（卡片）”直接等同于 Android AppWidget，漏掉流体云。 |
| R2 | 旧 `mobile/src/Mosaic.Mobile.Presentation/ViewModels/TaskListViewModel.cs`、`mobile/src/Mosaic.Mobile.App/Controls/TaskCardView.xaml`（已随 `mobile/` 删除） | 把 App UI 当作同步主路径；无 UI 场景下的会话恢复与动作入口不可用。 |
| R3 | `8bc0acd` 中 `TaskWidgetActionReceiver.OnReceive`（已删除） | 动作集含取消归档、版本锁死 0.2.1、以保留 `mobile/` 为前提。 |
| R4 | 代码库检索对 `流体云\|Live Updates\|实况窗` 零命中 | 调研只覆盖仓库代码，缺少平台决策与权限前置。 |
| R5 | `docs/releases/v0.2.0-mobile.md`、`docs/releases/v0.2.1-mobile.md` | 承诺 App UI、覆盖升级与 v2 投影继承；`mobile/` 全量删除后全部失效，已标注“已作废”。 |
| R6 | 旧 `mobile/src/Mosaic.Mobile.App/Platforms/Android/Widgets/WidgetTaskRepository.cs`（已删除） | 依赖旧投影 schema 与 `ProjectionStoreFactory`；删除后必须重建数据访问边界。 |
| R7 | 本轮实现中新发现：旧快照校验用“响应字节 SHA256”对比 `contentSha256`，而服务端哈希算的是规范化条目内容（`server/src/Mosaic.Server/Backups/JsonCanonicalizer.cs`） | 旧客户端校验逻辑与服务器算法不一致；新实现改为客户端复算规范化摘要（`BackupCanonicalizer`），并把 manifest 与 snapshot 的 id/条目数/cursor 一起比对。 |

## 2. 改动位置

### 删除

- `mobile/` 全部 125 个文件（`git rm -r mobile`）。
- `docs/mobile-auth-sync-plan.md`、`docs/releases/v0.2.0-mobile.md`、`docs/releases/v0.2.1-mobile.md` 顶部加“已作废”说明（内容保留供历史追溯）。

### 新增

| 文件 | 类型 / 方法 | 职责 |
|---|---|---|
| `mobile/Directory.Build.props`、`Directory.Packages.props` | — | 手机端独立版本 0.3.0、独立包版本，阻断桌面 net10.0/0.18.x 设置 |
| `mobile/src/Mosaic.Mobile.Wire/WireJson.cs` | `WireJson.Options` | 全站 JSON 配置（camelCase、忽略未知成员） |
| `mobile/src/Mosaic.Mobile.Wire/WireError.cs` | `WireError.TryParse`、`WireErrorCodes` | 错误信封解析（`details` 优先、根级回退）、本地错误码 |
| `mobile/src/Mosaic.Mobile.Wire/AuthDtos.cs`、`SyncDtos.cs`、`BackupDtos.cs` | DTO | `/v1` 契约 |
| `mobile/src/Mosaic.Mobile.Core/Api/ApiResponse.cs`、`HttpMosaicApi.cs` | `HttpMosaicApi.SendAsync`、`GetBytesAsync`、`HttpMosaicApiFactory.Create` | HTTPS 传输、禁止重定向、响应体上限、传输失败/取消分类 |
| `mobile/src/Mosaic.Mobile.Core/Configuration/ServerAddress.cs`、`ServerConfiguration.cs` | `ServerAddress.TryNormalize`、`IServerConfigProvider` | 仅 HTTPS；仅回环允许 http；拒绝 credentials/query/fragment |
| `mobile/src/Mosaic.Mobile.Core/Auth/SecretStorage.cs`、`StoredSession.cs`、`TokenStore.cs` | `ISecretStorage`、`TokenStore.LoadSessionAsync/SaveSessionAsync/GetOrCreateDeviceIdAsync` | 令牌按 `sha256(origin)` 前缀隔离存放 |
| `mobile/src/Mosaic.Mobile.Core/Auth/SessionManager.cs` | `RestoreAsync`、`RefreshCoreAsync`、`VerifyIdentityCoreAsync`、`InvalidateAsync` | 无 UI 会话恢复、401 清令牌、`/v1/auth/me` 身份校验 |
| `mobile/src/Mosaic.Mobile.Core/Projection/ProjectionIdentity.cs` | `Create` | `sha256(normalizedOrigin + "\0" + userId)` |
| `mobile/src/Mosaic.Mobile.Core/Projection/SyncedTask.cs` | `TaskPayload.TryRead`、`SetComplete`、`SetArchived` | 保留未知字段的 payload 读改写 |
| `mobile/src/Mosaic.Mobile.Core/Projection/TaskProjection.cs` | `OpenAsync`、`ReplaceAsync`、`ApplyChangesAsync`、`ApplyServerTaskAsync`、`FindAsync`、`ListVisibleAsync`、`CountAsync` | 只读投影；owner/schema 校验；不匹配即隔离 |
| `mobile/src/Mosaic.Mobile.Core/Sync/BackupCanonicalizer.cs` | `Serialize`、`ComputeSha256Hex` | 复刻服务端规范化摘要 |
| `mobile/src/Mosaic.Mobile.Core/Sync/SnapshotBootstrapService.cs` | `RunAsync` | 备份引导；每任务 `serverRevision=0` |
| `mobile/src/Mosaic.Mobile.Core/Sync/IncrementalSyncService.cs` | `RunAsync` | 游标分页；只应用 task 变更 |
| `mobile/src/Mosaic.Mobile.Core/Commands/TaskCommandService.cs` | `ExecuteAsync` | 唯一写路径；串行化双击；409/401/离线语义；幂等重放 |
| `mobile/src/Mosaic.Mobile.Core/Cards/CardAction.cs`、`CardModel.cs`、`FluidCloud.cs` | `CardActionCodec`、`CardModelBuilder`、`IFluidCloudAdapter` | 三个动作白名单、卡片文本模型、流体云边界 |
| `mobile/src/Mosaic.Mobile.Core/MobileKernel.cs` | `ResolveAsync`、`RefreshAsync`、`ExecuteAsync` | 冷启动编排：配置 → 会话 → 投影 → 引导/拉取 → 卡片 |
| `mobile/src/Mosaic.Mobile.App/MosaicApplication.cs` | `OnCreate` | 唯一入口；无 Activity |
| `mobile/src/Mosaic.Mobile.App/MobileServices.cs` | `Initialize`、`Fallback` | 进程级组合根 |
| `mobile/src/Mosaic.Mobile.App/Platform/AndroidSecretStorage.cs` | `ReadAsync`、`WriteAsync`、`DeleteAsync`、`PurgeLegacyKey` | Keystore AES/GCM |
| `mobile/src/Mosaic.Mobile.App/Platform/AndroidServerConfigProvider.cs` | `GetAsync` | 可替换连接边界 |
| `mobile/src/Mosaic.Mobile.App/Platform/LegacyInstallCleanup.cs` | `Run` | 旧目录/旧偏好/旧密钥一次性退役，不迁移 |
| `mobile/src/Mosaic.Mobile.App/Widgets/TaskWidgetProvider.cs` | `OnUpdate`、`OnAppWidgetOptionsChanged` | `goAsync` + 8 秒预算 |
| `mobile/src/Mosaic.Mobile.App/Widgets/WidgetActionReceiver.cs` | `OnReceive` | 仅接受三个动作；`exported=false` |
| `mobile/src/Mosaic.Mobile.App/Widgets/WidgetRenderer.cs` | `Render`、`TaskIntent` | RemoteViews；request code `(taskId, action)`；FLAG_IMMUTABLE |
| `mobile/src/Mosaic.Mobile.App/FluidCloud/AndroidFluidCloudAdapter.cs` | `Status`、`PublishAsync` | 明确 Blocked，不发布 |
| `mobile/AndroidManifest.xml` + `Resources/**` | — | 无 Activity；两个非导出接收器；widget meta-data；禁明文；禁备份 |
| `mobile/tests/Mosaic.Mobile.Tests/**` | 37 个测试 | 见 §4 |

### 修改

- `docs/implementation-status.md`：新增本节摘要。
- 新增 `docs/releases/v0.3.0-mobile.md`。

## 3. 阶段 0 结论

见 `mobile/docs/platform-decision.md`：OPPO 流体云阻塞（无凭证、文档受限、无真机）；Android 16 Live Updates 与 HarmonyOS 实况窗不作为替代；`IFluidCloudAdapter` 边界保留，卡片显示“流体云不可用”，不用 AppWidget 冒充。

## 4. 验收矩阵

| ID | 结果 | 证据 / 说明 |
|---|---|---|
| P01 | 完成 | `mobile/docs/platform-decision.md` |
| P02 | 完成（判定不可实现） | 同上；适配器 `AndroidFluidCloudAdapter` 返回 Blocked |
| H01 | 代码完成，真机未验证 | 冷启动只经过 `MosaicApplication.OnCreate` → `MobileKernel`；无 Activity |
| H02 | 完成 | 合并清单无 `<activity>`；无 App 内列表/设置页 |
| H03 | 完成 | `IServerConfigProvider` / `AndroidServerConfigProvider`；卡片层不读服务器地址 |
| H04 | 完成（自动化测试） | `ProjectionTests`：owner 不匹配、损坏文件隔离、缺失显示 NeedsRestore |
| H05 | 部分自动化 | 401 清令牌（`KernelTests`、`TaskCommandTests`）；换号/卸载重装未在真机验证 |
| D01 | 完成（自动化测试） | `CardTests`、`ProjectionTests`：只列未归档、空状态、无备注/账号 |
| D02 | 完成（自动化测试） | `TaskCommandTests.Complete_*` |
| D03 | 完成（自动化测试） | `TaskCommandTests.Archive_*`；`deleted=false`，无 tombstone |
| D04 | 完成 | 动作枚举只有 Complete/Archive/Refresh |
| W01 | 未验证（无真机） | 清单中 provider 与 meta-data 已检查 |
| W02 | 未验证（无真机） | 布局只含数量/标题/状态；深浅色资源已提供 |
| W03 | 代码完成，未验证 | 接收器 `exported=false`、白名单、request code；`CardTests` 覆盖解析与 request code |
| F01 | 不可实现 | 平台阻塞 |
| F02 | 不可实现 | 同上 |
| F03 | 代码完成，未验证 | Blocked 状态与卡片文案；AppWidget 独立可用 |
| S01 | 完成（自动化测试） | `TaskCommandTests`：离线/401/409，失败不写本地 |
| S02 | 完成（自动化测试） | `TaskCommandTests.Replayed_*` |
| S03 | 完成（静态核查） | 无日志输出、无 WebView、仅 HTTPS、禁备份；真机抓包未做 |
| M01 | 完成 | `mobile/` 125 文件删除；旧文档标“已作废”；tag 存在 |
| M02 | 记录见 §5 | 覆盖安装/卸载重装/换号/撤销设备中仅代码路径覆盖，真机未验证 |
| B01 | 完成 | 见 §6；未设 `DOTNET_CLI_UI_LANGUAGE` |
| B02 | 完成 | 单次提交含源码、测试、发布说明、实施记录、根因与改动位置 |

## 5. 迁移验收

| 场景 | 实现行为 | 验证 |
|---|---|---|
| 覆盖安装（0.2.x → 0.3.0） | 包名相同、versionCode 5 > 4；首次启动 `LegacyInstallCleanup.Run` 把 `files/projections/` 改名为 `retired-projections-v2-<ts>`，删除 `mosaic_secure` 偏好与旧 Keystore 别名，然后标记完成 | 代码路径与单测覆盖退役标记；**未在真机验证** |
| 卸载重装 | 数据目录清空，需重新配置与重建投影 | 未验证 |
| 换号 | 投影按 owner hash 分开；旧文件不匹配新账号即隔离；旧 PendingIntent 按 taskId 在当前账号投影中查找，查不到即拒绝 | `ProjectionTests` 覆盖 owner 隔离；旧 PendingIntent 未做真机验证 |
| 撤销设备 | 401 → 清令牌 → 卡片“会话已失效” | `KernelTests` 覆盖刷新 401 |
| 无备份 | `SnapshotBootstrapService` 返回 NoSnapshot → 卡片 `NeedsRestore`，不展示旧缓存 | `KernelTests`/`SyncTests` 覆盖错误分支；真机未验证 |
| 回滚 | 代码回滚到 `backup/mobile-pre-refactor-20261126` 或 revert 本提交；旧 APK 需卸载后安装（versionCode 回退不允许覆盖安装），旧投影文件仍在 `retired-projections-v2-*` 下且新代码不读取 | 未验证 |

## 6. 构建与测试

```powershell
dotnet build mobile/src/Mosaic.Mobile.Core/Mosaic.Mobile.Core.csproj -c Release
dotnet build mobile/src/Mosaic.Mobile.App/Mosaic.Mobile.App.csproj -c Release -p:AndroidSdkDirectory="D:\Android\Sdk" -p:JavaSdkDirectory="C:\Program Files\Microsoft\jdk-21.0.13.102-hotspot"
dotnet test mobile/tests/Mosaic.Mobile.Tests/Mosaic.Mobile.Tests.csproj -c Release
```

结果：Core/Wire/测试 0 警告 0 错误；Android 宿主构建成功并产出 `app.mosaic.tasks-Signed.apk`；定向测试 37 通过 / 0 失败（Release，未设置 `DOTNET_CLI_UI_LANGUAGE`）。

未验证：真机/模拟器交互、真实服务端联调、覆盖安装与卸载重装、流体云降级观感、抓包确认仅 HTTPS。

## 7. 安全自查

- 令牌、deviceCode、userCode、邮箱不进入日志；项目中没有日志框架调用。
- `android:allowBackup="false"` + `dataExtractionRules` 排除全部文件/偏好。
- 无 WebView、无密码输入面。
- `network_security_config` 明文关闭（仅回环例外），`usesCleartextTraffic="false"`。
- 不读取桌面 `mosaic.sqlite`，不引用桌面程序集；手机端只消费 `/v1`。