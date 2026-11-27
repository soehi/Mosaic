# 手机端 0.3.2 实施记录

日期：2026-11-26
分支：`claude/mobile-rebuild-8c92e7`
备份 tag：`backup/mobile-pre-refactor-20261126` → `8bc0acdee39fcb643e0461165ee0ba230c7e52a6`
版本：`0.3.2` / Android `versionCode=7` / 包名 `app.mosaic.tasks`
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
## 8. 0.3.1 修复（用户实测反馈）

用户实测 0.3.0 小组件显示“载入窗口小部件出现问题”，并要求改为 4×2 横向、提供可直接查看的测试任务条。本轮只修这几处，不重做手机端。

### 8.1 根因位置

| 编号 | 根因位置 | 触发条件与出错原因 |
|---|---|---|
| R8 | `mobile/src/Mosaic.Mobile.App/Resources/layout/widget_tasks.xml:49`（0.3.0） | 分隔线用了原生 `<View>`。`RemoteViews` 只接受白名单类型（`LinearLayout`、`FrameLayout`、`TextView`、`ImageView`、`Button` 等），`android.view.View` 不在其中，launcher 在 apply 时抛 `InflateException`，界面显示“载入窗口小部件出现问题”。 |
| R9 | `mobile/src/Mosaic.Mobile.App/Widgets/TaskWidgetProvider.cs`（0.3.0）`[BroadcastReceiver(Exported = false)]` | 系统 `AppWidgetService` 以不同 UID 发送 `ACTION_APPWIDGET_UPDATE`，`exported="false"` 使其无法投递，`onUpdate` 从不执行，小组件永远停在 `initialLayout`（“正在同步”）。预览与桌面渲染都走 `initialLayout`/`previewLayout`，所以“布局正常但内容不刷新”的表现与布局问题无关。 |
| R10 | `mobile/src/Mosaic.Mobile.App/Mosaic.Mobile.App.csproj`（0.3.0）`<PublishTrimmed>false</PublishTrimmed>` | 该 SDK（Microsoft.Android.Sdk 36.1.69）在关闭 trimming 且引用 `System.Text.Json` 时，托管类型注册失败：进程启动即 `java.lang.UnsatisfiedLinkError: No implementation found for void ...MosaicApplication.n_onCreate()`。已用最小工程复现并确认与本仓库代码无关。 |
| R11 | `mobile/src/Mosaic.Mobile.App/Widgets/WidgetRenderer.cs`（0.3.0）`ResolveRowCount` | `AppWidgetOptions` 的 min/max 高度是**像素**，旧实现直接当 dp 比较，密度 2.0 的机器上 4×2（110dp≈220px）被算成 5 行，底部留白且行数不可控。 |
| R12 | `mobile/src/Mosaic.Mobile.App/Resources/xml/widget_tasks_info.xml`（0.3.0） | 尺寸写成 2×4（`minWidth=110dp`、`minHeight=250dp`、`targetCell=2×4`），与用户要求的 4×2 横向相反。 |

### 8.2 改动位置

| 文件 | 改动 |
|---|---|
| `mobile/src/Mosaic.Mobile.App/Resources/layout/widget_tasks.xml` | 分隔线 `<View>` → `TextView`（`layout_height="1dp"`、`background="@color/widget_divider"`，id `widget_divider`）；根 padding 12dp→8dp；行高 27dp→26dp、行距 2dp→1dp；按钮文字改用 `action_complete_compact`/`action_archive_compact`（窄宽时单字标签）；文件头注释写明 RemoteViews 白名单 |
| `mobile/src/Mosaic.Mobile.App/Resources/layout/widget_tasks_safe.xml`（新增） | 只含 `LinearLayout`/`TextView` 的兜底布局：标题、说明、刷新 |
| `mobile/src/Mosaic.Mobile.App/Widgets/WidgetRenderer.cs` | `Render` 外包 try/catch，失败时 `RenderSafe` 用兜底布局重绘；`ResolveRowCount` 按 `DisplayMetrics.Density` 把像素换算成 dp 后分档（≥220→5、≥170→4、≥140→3、≥100→2、>0→1）；`UseCompactLabels` 按宽度 <180dp 切单字按钮；新增 `BuildViews`/`RenderSafe` |
| `mobile/src/Mosaic.Mobile.App/Widgets/TaskWidgetProvider.cs` | `Exported = true`（系统广播必需），`OnReceive` 只放行 `APPWIDGET_UPDATE/DELETED/OPTIONS_CHANGED/ENABLED/DISABLED/RESTORED`，其余直接丢弃 |
| `mobile/src/Mosaic.Mobile.App/Mosaic.Mobile.App.csproj` | `PublishTrimmed=true` + `TrimMode=partial`；`TrimmerRootAssembly` 保留 App/Core/Wire（`RootMode="All"`）；`System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=true`；新增 `MosaicWidgetTestSeed` 条件编译开关；版本 0.3.1 / versionCode 6 |
| `mobile/src/Mosaic.Mobile.App/Resources/xml/widget_tasks_info.xml` | `minWidth=250dp`、`minHeight=110dp`、`minResizeWidth/Height=110dp`、`targetCellWidth=4`、`targetCellHeight=2`，`updatePeriodMillis=0` 不变 |
| `mobile/src/Mosaic.Mobile.App/Widgets/TestSeedStore.cs`（新增，`#if MOSAIC_WIDGET_TEST_SEED`） | 6 条中文假任务（4 未完成、1 已完成、1 已归档，其中一条长标题用于测省略）；状态存独立偏好 `mosaic.mobile.v3.testseed`，**不碰投影目录、不伪造登录态**；`Model()` 直接产出 `CardModel`，详情行前缀“测试数据 · ” |
| `mobile/src/Mosaic.Mobile.App/Widgets/TestSeedReceiver.cs`（新增，同上条件编译） | 导出接收器，仅响应 `app.mosaic.mobile.action.SEED_TEST`，重置种子并重绘全部 widget |
| `mobile/src/Mosaic.Mobile.App/Widgets/WidgetRuntime.cs` | 种子启用时 `RefreshAsync`/`ExecuteAsync` 走 `TestSeedStore`，完成/归档在本地改状态并重渲染，toast 带“测试数据”；`RenderAll` 单实例渲染失败不再影响其他实例 |
| `mobile/src/Mosaic.Mobile.App/MosaicApplication.cs` | 测试构建在 `OnCreate` 调 `TestSeedStore.EnsureSeeded`；正式构建不编译该调用 |
| `mobile/Directory.Build.props` | `<Version>` 0.3.0 → 0.3.1 |

### 8.3 测试种子边界

- 只在 `-p:MosaicWidgetTestSeed=true` 时编译；正式 Release 包不包含 `TestSeedStore`、`TestSeedReceiver` 及 `SEED_TEST` 广播，可用 `aapt2 dump xmltree` 与 `dexdump`/字符串检索确认。
- 种子数据写在独立偏好文件，与 `mosaic.mobile.v3.secure`、`projections-v3/`、`mosaic.mobile.v3.config` 都无关；不写令牌、不写账号目录。
- 详情行固定显示“测试数据 · 共 N 项未归档”，完成/归档 toast 均以“测试数据”开头，避免与真实同步混淆。

### 8.4 模拟器实测（MuMu 12，Android 15 / x86_64，720×1280，density 2.0）

| 项 | 结果 |
|---|---|
| 布局 inflate | 通过：桌面不再出现“载入窗口小部件出现问题” |
| 2×4 预览 | 通过：微件面板显示预览与标题/按钮，无截断 |
| 4×2 配置 | 通过：`dumpsys appwidget` 显示 `min=(64001x28161)`，即 250dp×110dp |
| 系统 `APPWIDGET_UPDATE` | 通过：`exported=true` 后进程被拉起（`Start proc ... for broadcast`） |
| JNI 注册 | 通过：开启 trimming 后 `MosaicApplication.n_onCreate` 不再抛 `UnsatisfiedLinkError` |
| 测试种子渲染 | 通过：显示 4 项待办 / 共 5 项未归档 / 3 条可见行 / 长标题省略 |
| 完成按钮 | 通过：待办 4→3，首行变为 ✓，toast“测试数据：已标记完成” |
| 归档按钮 | 通过：共 5→4 项、待办 3→2，长标题那条消失，截图 `20-tap-archive.png` |
| 行数分档 | 已改为按可用高度算术计算：`(heightDp - 53) / 27`，夹在 1..5。实测 Launcher 上报 `minW=539 maxW=994 minH=134 maxH=337 density=2`（即 168.5dp）→ 4 行，与截图一致 |
| 4×2 桌面实例 | 通过（无头 adb 拖拽到 4 列宽）：4 行渲染、无底部裁切、完成/归档按钮可点，见 `headless/17-final-4x2.png`、`19-tap-complete.png`、`20-tap-archive.png` |

### 8.5 无头 adb 自动化验证（不依赖图形界面）

MuMu 以 `MuMuManager control -v 0 launch` 命令行启动，只用 `adb` 完成安装、广播、手势、截图与日志采集；证据在 `artifacts/mobile-0.3.1/headless/`。

| 步骤 | 命令 | 结果 |
|---|---|---|
| 启动/连接 | `MuMuManager.exe control -v 0 launch` → `adb connect 127.0.0.1:16384` | 成功，Android 15 / x86_64 |
| 安装 x64 测试包 | `adb install -r ...test-x86_64.apk` | Success |
| 生成种子 | `adb shell am broadcast -a app.mosaic.mobile.action.SEED_TEST -n app.mosaic.tasks/...TestSeedReceiver` | `dumpsys appwidget` 中 `id=8` 出现 `views=android.widget.RemoteViews@...`；截图 `01-seeded.png` 显示 4 项待办 / 测试数据 |
| 完成按钮 | `adb shell input tap 272 771` | 待办 4→3，首行 ✓，截图 `02-after-complete.png`，logcat 有 `Trying to notify widget update for package app.mosaic.tasks with widget id: 8` |
| 归档按钮 | `adb shell input tap 315 809` | 待办 3→2，截图 `03-after-archive.png`，同上广播记录 |
| 系统尺寸配置 | `adb shell dumpsys appwidget` | `min=(64001x28161)` = 250dp × 110dp，即 4×2；`minResize=(28161x28161)` = 110dp × 110dp |
| 4×2 实例外观 | `adb shell input draganddrop` 调整宽高 + `screencap` | 通过：拖到 4 列宽后实测上报 `minW=539 maxW=994 minH=134 maxH=337 density=2`（168.5dp），按算术规则渲染 4 行、无底部裁切、按钮同行，见 `headless/17-final-4x2.png`、`18-arithmetic.png`、`21-delivered-verify.png` |
| 日志 | `adb logcat -d` 过滤保存 | `headless/logcat.txt`（13 行，无令牌/邮箱/备注） |

自动化覆盖不到的只有：launcher 图形化微件面板选择“4×2 预览”的摆放过程、真机 arm64 设备、真实服务端联调。

### 8.6 未验证项

真机（arm64）安装与交互、4×2 新实例的实际行数与底部裁切、流体云（仍为阻塞）、真实服务端联调、覆盖安装与卸载重装。

## 9. 0.3.2：M2.5 小组件交互重做

用户否定了 0.3.1 的视觉与交互，要求按 M2.5 重做：单行紧凑列表、只有一个完成圆圈、原生可滑动。

### 9.1 根因位置

| 编号 | 根因位置 | 触发条件与出错原因 |
|---|---|---|
| R13 | `mobile/src/Mosaic.Mobile.App/Resources/layout/widget_tasks.xml`（0.3.1） | 一次性把 5 行静态布局塞进一个 4×2 卡片：每行都带“完成/归档”两个按钮和一个刷新按钮，高度 110dp 时第 3 行起被裁切；4×1 完全没有可用形态。 |
| R14 | `WidgetRenderer.BuildViews`（0.3.1） | 用 `RemoteViews` 直接摆行，没有 `RemoteViewsService`，所以无法承载超过可视区的任务；用户要求“下滑切换任务”，静态布局做不到。 |
| R15 | `WidgetRenderer.TaskIntent`（0.3.1） | `PendingIntent` 用 `FLAG_IMMUTABLE`。`setPendingIntentTemplate` + `setOnClickFillInIntent` 要求模板 **mutable**，否则 launcher 无法写入行 extras，点击静默失效。 |
| R16 | `Resources/drawable/widget_action.xml` + `values/colors.xml`（0.3.1） | 视觉是自定义的方角按钮配 `#F2FFFFFF` 半透明底，不是 M2.5 的圆角表面 + 柔和描边 + 单一主色。 |

### 9.2 改动位置

| 文件 | 改动 |
|---|---|
| `Resources/values/colors.xml`、`values-night/colors.xml` | M2.5 token：surface `#FFFFFF`/`#1F1F1F`、on-surface `#1F1F1F`/`#E8EAED`、secondary `#5F6368`/`#9AA0A6`、outline `#DADCE0`/`#5F6368`、primary `#1A73E8`/`#8AB4F8`、primary-container `#E8F0FE`/`#1A2B45` |
| `Resources/drawable/widget_background.xml` | 12dp 圆角 + 1dp `outline` 描边 |
| `Resources/drawable/widget_circle_outline.xml`、`widget_circle_checked.xml`、`ic_check_small.xml` | 24dp 完成圆圈：1.5dp 主色空心圆 / 实心主色带白勾 |
| `Resources/layout/widget_tasks.xml` | 整个卡片只剩一个 `ListView`（8dp 内边距、1dp 分隔线、无滚动条、透明选择器）；无标题栏、无刷新、无归档 |
| `Resources/layout/widget_task_row.xml`（新增） | 52dp 行：左侧 24dp 圆圈 + 12dp 间距 + 单行省略标题（`sans-serif-medium` 14sp） |
| `Resources/layout/widget_task_empty.xml`（新增） | 空列表“暂无任务”，无按钮 |
| `Resources/layout/widget_tasks_preview.xml`（新增） | 微件面板静态预览：ListView 内容在预览里无法渲染，用同样的 token 画两行示例 |
| `Resources/layout/widget_tasks_safe.xml` | 兜底布局改为纯 `TextView`，不再带刷新按钮 |
| `Resources/xml/widget_tasks_info.xml` | `minWidth=250dp`、`minHeight=40dp`、`targetCell 4×1`、`minResize 250×40dp`、`maxResizeWidth=250dp`、`maxResizeHeight=140dp`、`resizeMode="vertical"`；`previewLayout` 指向静态预览 |
| `Widgets/TaskWidgetService.cs`（新增） | `RemoteViewsService` + `IRemoteViewsFactory`：`OnDataSetChanged` 读快照，`GetViewAt` 绑定标题与圆圈，未完成行的圆圈带 `SetOnClickFillInIntent` |
| `Widgets/WidgetSnapshot.cs`（新增） | 行快照（id / 标题 / 完成位）存私有偏好 `mosaic.mobile.v3.widget`，只用于列表工厂重建；不含令牌、邮箱、备注 |
| `Widgets/WidgetRenderer.cs` | 改为 `SetRemoteAdapter` + `SetPendingIntentTemplate` + `NotifyAppWidgetViewDataChanged`；模板 PendingIntent 在 API 31+ 用 `FLAG_MUTABLE`（API 26–30 无该标志），显式组件 + 非导出接收器；渲染失败退回安全布局 |
| `Widgets/WidgetRuntime.cs` | 每次刷新/命令后写行快照并通知列表重载；`RefreshAsync`/`ExecuteAsync` 接收 `Context` |
| `Widgets/WidgetActionReceiver.cs` | 只接受 `ActionTask` + `complete`；移除 refresh 广播与归档入口（`TaskAction.Archive` 仍在内核里保留） |
| `Widgets/TestSeedStore.cs`、`TestSeedReceiver.cs` | 新增 `Rows()` 供列表工厂使用；种子广播后同时写快照并通知列表刷新 |
| `Mosaic.Mobile.App.csproj`、`mobile/Directory.Build.props` | 版本 0.3.2 / `versionCode=7` |
| `Resources/values/strings.xml` | 移除“完成/归档/刷新”按钮文案，新增 `widget_empty` 与两条预览标题 |

### 9.3 无头 adb 实测（MuMu 12 / Android 15 / x86_64，720×1280，density 2.0）

证据：`artifacts/mobile-0.3.2/shots/`

| 项 | 结果 |
|---|---|
| M2.5 视觉 | 通过：白色圆角卡片、1dp 描边、空心主色圆圈、单行标题、1dp 分隔线、无标题栏 |
| 深色模式 | 通过：`cmd uimode night yes` 后深色表面 + 浅色文字 + `#8AB4F8` 圆圈 |
| 原生滑动 | 通过：`input swipe` 上滑后列表整体位移，后续任务进入可视区 |
| 完成圆圈 | 通过：点圆圈 → 该行立即移除、列表重排、待办数下降；`notify widget update ... id: 10` |
| 空状态 | 通过：全部完成后显示“暂无任务”，无任何按钮 |
| 微件面板预览 | 通过：静态预览显示两行示例（ListView 内容无法在预览渲染） |
| 4 列宽 | **未达成**：模拟器屏宽 360dp，launcher 把 4 列封顶为 2 列；`targetCellWidth=4` 已是 API 31+ 的正确声明，真机宽屏才可能呈现 4 列 |
| 长按缩放 | 不可用：`ListView` 消费了长按手势，launcher 无法进入缩放模式（ListView 型小组件的固有行为） |

### 9.4 未验证

真机 arm64、宽屏设备上的 4×1/4×2 实际格数、流体云（仍阻塞）、真实服务端联调。

## 10. 0.3.3：长按切换任务组（未完成 / 已完成 / 已归档 / 全部）

追加需求与 M2.5 改版同一次交付，但 0.3.2 已提交，因此按规划会话的约定升版本：`0.3.3` / `versionCode=8`。

### 10.1 硬限制与选定路径

AppWidget 的长按由 launcher 持有，`RemoteViews` 没有 long-click API，应用无法拦截原始 long-press 事件。唯一受支持的等价路径是 `android:configure` + `android:widgetFeatures="reconfigurable|configuration_optional"`：用户长按卡片 → 系统菜单出现「编辑」铅笔 → 进入本应用的配置对话框。未使用透明遮罩、未伪造长按检测、未用 `setOnClickPendingIntent` 模拟长按。

### 10.2 根因位置

| 编号 | 根因位置 | 触发条件与出错原因 |
|---|---|---|
| R17 | `Resources/xml/widget_tasks_info.xml`（0.3.2） | 没有 `android:configure`，也没有 `widgetFeatures`，所以 launcher 的系统菜单里没有「编辑」，长按后无法进入任何配置入口。 |
| R18 | `Widgets/TaskWidgetService.cs:95`（0.3.2 的 `LoadRows`） | 只读 `WidgetSnapshotStore.Load(context)` 并按 `!Completed` 过滤，快照里根本没有归档位，`archived` 分组无从判断。 |
| R19 | `Widgets/WidgetSnapshot.cs`（0.3.2） | 行格式固定三字段（id / 标题 / 完成位），无版本号，无法承载完成位与归档位之外的排序时间。 |
| R20 | `Widgets/TaskWidgetService.cs`（0.3.3 首版） | 只读行不写 fill-in intent。`RemoteViewsAdapter` 回收子视图时，被复用的行继承了上一行的 fill-in extras，点「已完成」行的圆圈会把**另一条仍待办的任务**标记完成（实测复现：完成组点第一行，未完成组里「检查夜间备份任务」消失）。 |
| R21 | `Widgets/WidgetConfigActivity.cs`（0.3.3 首版） | `android:configure` 写的是 `app.mosaic.tasks.Widgets.WidgetConfigActivity`，而生成的 Java 包装类名是 `crc64…`，launcher 启动不到该 Activity。改为 `Activity(Name = "app.mosaic.tasks.WidgetConfigActivity")` 显式钉住 Java 名。 |

### 10.3 改动位置

| 文件 | 改动 |
|---|---|
| `Mosaic.Mobile.Core/Cards/WidgetTaskGroup.cs`（新增） | `WidgetTaskGroup` 四值枚举、`WidgetTaskGroups`（wire 名 `pending/completed/archived/all`、旧值 `open` 兼容、分组空态文案、`AllowsComplete`）、`WidgetTaskItem` 快照行、`WidgetTaskSelector.Select`（分组过滤 + 排序 + 上限 200 + id 兜底排序） |
| `Mosaic.Mobile.Core/Cards/WidgetSnapshotCodec.cs`（新增） | 快照线格式与升级路径：`CurrentVersion=4`，六字段/行；`version==0`（0.3.2 无版本键的三字段）按待办行迁移；其它版本一律读空；标题里的分隔符被替换，无法伪造字段 |
| `Mosaic.Mobile.Core/Projection/TaskProjection.cs` | 新增 `ListForWidgetAsync(limit, ct)`：读全部任务（含归档），`ORDER BY created_at_utc DESC, id ASC` 保证稳定；卡片自身的 `ListVisibleAsync` 不变 |
| `Mosaic.Mobile.Core/MobileKernel.cs` | 新增 `WidgetRefreshResult(Model, Items)` 与 `RefreshWidgetAsync`；`BuildAsync` 同时产出卡片模型与快照行；`CardActionResult` 增加可选 `WidgetItems`。`Items` 为 null 表示解析失败，调用方保留旧快照而不是清空 |
| `Mosaic.Mobile.App/Widgets/WidgetConfigActivity.cs`（新增） | 配置对话框：`android:configure` 入口、`Exported=true`、`ExcludeFromRecents`、无 ActionBar、无 launcher 图标、选完立即 `finish()`；四个选项按钮，当前组打勾；`RESULT_CANCELED` 不改动；`OwnsWidget` 校验 id 属于本 provider 才允许写入 |
| `Mosaic.Mobile.App/Widgets/WidgetGroupStore.cs`（新增） | 分组存储：`SharedPreferences("widget_prefs")`、键 `filter_<appWidgetId>`、值四选一，默认 `pending`；`OnDeleted` 时删除 |
| `Mosaic.Mobile.App/Widgets/TaskWidgetService.cs` | `OnDataSetChanged` 读分组并按组过滤；空态文案按组变化；**每行都写 fill-in intent**（三键齐全），只读行带 `ExtraReadOnly=true`，避免回收行串 id |
| `Mosaic.Mobile.App/Widgets/WidgetActionReceiver.cs` | 新增 `ExtraReadOnly`；命中该标记直接返回，绝不发起写 |
| `Mosaic.Mobile.App/Widgets/WidgetSnapshot.cs` | 行记录扩为 id/标题/完成/归档/更新时间；读写委托给 `WidgetSnapshotCodec`；新增 `WidgetRowFactory` |
| `Mosaic.Mobile.App/Widgets/WidgetRuntime.cs` | `RefreshAsync`/`ExecuteAsync` 改用 `RefreshWidgetAsync` 与 `WidgetItems` 落快照；新增 `RenderOne(context, id)` 只重绘被改的实例 |
| `Mosaic.Mobile.App/Widgets/TaskWidgetProvider.cs` | 新增 `OnDeleted`，删除该实例的分组偏好 |
| `Mosaic.Mobile.App/Resources/xml/widget_tasks_info.xml` | 加 `android:configure="app.mosaic.tasks.WidgetConfigActivity"`、`android:widgetFeatures="reconfigurable|configuration_optional"` |
| `Resources/layout/widget_config.xml`、`drawable/widget_config_option.xml`、`values/styles.xml`（新增） | 配置对话框布局（标题 + 四个选项容器）、选项圆角描边、`WidgetConfigTheme`（`Theme.Material.Dialog.Alert` 派生，无 Material Components 依赖） |
| `Resources/values/colors.xml`、`values-night/colors.xml` | 新增 `widget_on_primary`（浅色 `#FFFFFF` / 深色 `#1F1F1F`）；`ic_check_small` 改用它，深色模式下勾仍可辨 |
| `Resources/values/strings.xml` | 新增 `widget_config_label`、`widget_config_title`；`widget_description` 说明长按可配置 |
| `Mosaic.Mobile.App.csproj`、`mobile/Directory.Build.props` | 版本 0.3.3 / `versionCode=8` |
| `tests/Mosaic.Mobile.Tests/WidgetGroupTests.cs`、`WidgetSnapshotCodecTests.cs`（新增） | 四分组边界、互斥覆盖、`AllowsComplete`、wire 名往返、旧值 `open`、上限、空态文案；快照往返、0.3.2 迁移、未知版本读空、畸形行丢弃、标题内分隔符不能伪造字段 |

### 10.4 迁移

- 快照文件：0.3.2 的 `mosaic.mobile.v3.widget` 里 `rows` 无 `rows.version`。升级后首帧按 `version==0` 迁移为待办行（0.3.2 写快照时已过滤掉完成与归档），卡片不会空白；首次同步后重写为 v4 格式。
- 分组偏好：新文件 `widget_prefs`，旧安装没有该键，默认 `pending`，与 0.3.2 行为一致。
- 已有小组件实例：`appWidgetId` 不变，无需重新放置；`OnDeleted` 只清理被删除实例的键。
- 回滚：`git revert` 本次提交；设备侧 `versionCode` 不能从 8 回退到 7，需卸载后装 0.3.2。快照 v4 文件被 0.3.2 读作"无版本"会走三字段解析失败而丢弃，属可接受的降级（不崩、显示空态）。

### 10.5 无头 adb 实测（MuMu 12 / Android 15 / x86_64，720×1280，density 2.0）

证据：`artifacts/mobile-0.3.3/shots/`

| 项 | 结果 |
|---|---|
| 长按入口 | 通过：长按卡片后 launcher 菜单出现编辑铅笔与「点击即可更改微件设置」，点铅笔进入本应用配置对话框 |
| 配置对话框 | 通过：四个选项、当前组打勾、无 ActionBar、选完立即关闭 |
| 未完成组 | 通过：只列未完成未归档任务 |
| 已完成组 | 通过：只列已完成任务，圆圈为实心主色带勾 |
| 已归档组 | 通过：只列归档任务 |
| 全部组 | 通过：未完成 + 已完成，不含归档 |
| 只读行点击 | 通过（R20 修复后）：已完成组、全部组里点实心勾，列表无变化、logcat 无写入活动，未完成组任务不受影响 |
| 未完成组点圆圈 | 通过：该行消失、列表重排；切到已完成组能看到它 |
| 全部组点圆圈 | 通过：可写行变为实心勾并留在原位，只读行不受影响 |
| 分组空态 | 通过：四个组各自文案（截图 `77-pending-empty.png` 为「暂无未完成任务」） |
| 深色模式 | 通过：`cmd uimode night yes` 后深色表面、浅色文字、深色勾 |
| 快照升级 | 单元测试覆盖（0.3.2 三字段 → 待办行、未知版本读空、标题分隔符不能伪造字段）；设备上为全新安装路径，未做真机覆盖升级 |
| 重装后黑块 | 已定位：重装后 launcher 缓存 `app_not_installed`，重启 launcher 后恢复；不是布局缺陷 |
| 4 列宽 | 仍未达成：模拟器 360dp 宽，launcher 封顶 2 列 |
| 长按缩放 | 与 0.3.2 记录相反：实测长按能进入 launcher 的编辑菜单（`ListView` 未吞掉长按），但缩放把手仍不可用 |

### 10.6 未验证

真机 arm64、宽屏 4×1/4×2 实际格数、流体云（仍阻塞）、真实服务端联调下的分组切换。
