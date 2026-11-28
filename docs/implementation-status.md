# Mosaic：实施与验证记录

## 桌面 0.19.5 候选的自动备份修复（2026-11-28）

**根因位置**

- `769f181:src/Modules/Sync/Mosaic.Sync.Core/SyncModule.cs` 的自动备份只在 `OnAuthChanged` 和固定间隔循环触发，没有订阅任务变更。登录时先上传一次当前快照；用户随后保存任务时不会立即上传，下一次上传要等完整间隔（默认 60 秒）。若用户在这个窗口下载备份，服务器最新快照只有登录时的设置，任务仍只在本机。
- `769f181:src/Modules/Sync/Mosaic.Sync.Core/Internal/SyncService.cs` 的自动上传没有空任务保护。新安装、换机或任务库尚未加载时，登录触发的自动上传会用“0 个任务 + 设置”的快照覆盖服务器上已有任务的备份，导致之后恢复只剩设置。
- 已核对不是根因的链路：`TaskTransferService.ExportAllAsync` 以 `TaskFilter.All` 和 `ArchiveScope.All` 导出全部任务，`TaskService` 在创建、修改、完成、归档、删除时发布 `TaskChange`，`SyncService.PrepareRestoreAsync` 与 `SqliteTaskTransferRepository.ReplaceAllAsync` 在同一事务恢复任务，账户页按恢复结果展示任务数。设置和任务共用 `AppConfiguration` 的同一个 `StorageConfiguration` 与 `mosaic.sqlite`，没有跨版本分库。

**改动位置**

| 文件 | 改动 |
|---|---|
| `src/Modules/Sync/Mosaic.Sync.Core/SyncModule.cs` | `SyncDependencies` 增加可选 `ITaskChangeSource`；激活时订阅、停止时释放；`OnTaskChanged` 在已登录时立即排队上传。登录和周期上传路径不变。 |
| `src/Mosaic.Desktop/BuiltInModuleCatalog.cs` | 组装 `SyncModule` 时传入 `context.Tasks?.Changes`。 |
| `src/Modules/Sync/Mosaic.Sync.Core/Internal/SyncService.cs` | 自动上传在导出任务数为 0 时先读取最新备份清单；若服务器 `taskCount > 0`，保留现有快照并只更新本地状态，不写入设置-only 快照。手动上传不启用该保护。 |
| `src/Modules/Sync/Mosaic.Sync.Contracts/SyncContracts.cs`、`src/Modules/Sync/Mosaic.Sync.Http/HttpSyncTransport.cs` | `BackupSnapshotDto` 增加可选 `TaskCount`；旧服务器不返回时为 `-1`，跳过保护。解析 `taskCount`。 |
| `server/src/Mosaic.Server/Backups/BackupEndpoints.cs` | 最新清单和完整快照响应增加 `taskCount`、`settingCount`，由清单条目计数。 |
| `tests/Mosaic.Sync.Core.Tests/AutomaticSyncTests.cs`、`Fakes.cs`、`SyncServiceTests.cs` | 覆盖任务保存立即上传、失败后重试、登出和关闭自动备份时不上传、空任务不覆盖服务器任务、手动上传仍可清空。 |
| `tests/Mosaic.Auth.Http.Tests/HttpSyncTransportTests.cs`、`server/tests/Mosaic.Server.Tests/BackupTests.cs` | 覆盖 `taskCount` 解析与服务端计数字段。 |

**验证**

- `Mosaic.Sync.Core.Tests` 35/35 通过，`Mosaic.Auth.Http.Tests` 36/36 通过，`Mosaic.Server.Tests` 99/99 通过，`Mosaic.Architecture.Tests` 4/4 通过，`Mosaic.Tasks.Core.Tests.TaskTransferTests` 5/5 通过。
- `dotnet build src/Mosaic.Desktop/Mosaic.Desktop.csproj -c Release` 0 警告 0 错误。
- 自动备份默认启用：`SyncOptions.AutoSyncEnabled = true`，`SyncSettingsSource` 在设置缺失时按 true 读取；默认间隔 60 秒，登录后立即上传，任务变更后立即排队上传。设置路径为「设置 → 账户与备份」，关闭自动备份或把间隔改大均保留“只上传、不下载”的语义。
- 未完成的验收：真实账户、正式服务器和真实任务库上的恢复往返；正式站仍返回配对 404 或连接超时，见上一节。Android 设备和正式 APK 签名仍未验收。

**迁移与回滚**

- 本轮没有数据库迁移，只增加可选的响应字段和自动上传保护。旧桌面客户端忽略 `taskCount`；旧服务端不返回时客户端按 `-1` 处理，行为回退到“任务变更也上传、但没有空任务保护”。
- 源码可用 `git revert` 回退本提交；回退后自动备份恢复为仅登录和周期触发。空任务保护只在自动上传路径生效，手动「立即备份」仍可显式上传空任务以清空服务器备份。

## 桌面 0.19.5 候选的配对诊断修复（2026-11-27）

**根因位置**

- 正式部署分支 `fix/peek-detach-0.18.8` 的已核对提交 `2018b9e` 在 `server/src/Mosaic.Server/ServerApplication.cs` 没有注册 `MapMobilePairing`。扫码下载和配对入口此前实测返回 404。2026-11-28 复查 `/health` 返回 200、版本 `0.1.2`；`/device/mobile/connect`、`/device/mobile.apk`、`/.well-known/assetlinks.json` 和配对开始接口仍返回 404。运行中容器的镜像提交及挂载待核对。
- `769f181:src/Modules/Auth/Mosaic.Auth.Wpf/Views/MobilePairingWindow.cs` 的 `Start`、`SendAsync` 将令牌失效、配对入口缺失和服务不可用映射为同一提示。`AuthChanged` 在首次刷新将会话置为 `SignedOut` 时关窗，用户看不到重新登录提示。
- `769f181:server/src/Mosaic.Server/Auth/MobilePairingEndpoints.cs:13` 将缺少有效发布清单与反代未使请求成为 HTTPS 都报告为 `mobile.unavailable`。两个仓库 Compose 文件只声明了 `/data` 卷，正式 APK 及清单需要单独的只读挂载。

**改动位置与验证**

| 文件 | 改动 |
|---|---|
| `src/Modules/Auth/Mosaic.Auth.Wpf/Views/MobilePairingWindow.cs` | `Start`、`SendAsync`、`FailureText` 区分重新登录、尚未上线、包或清单未就绪、HTTPS 配置和连接错误。`AuthChanged` 让首次刷新失效的窗口保留提示。 |
| `src/Modules/Auth/Mosaic.Auth.Wpf/Localization/en.xaml`、`Localization/zh.xaml` | 新增四类 `Auth.Mobile` 提示的英中资源。 |
| `server/src/Mosaic.Server/Auth/MobilePairingEndpoints.cs` | `MapMobilePairing` 分别返回 `mobile.unavailable` 和 `mobile.https_required`。 |
| `tests/Mosaic.Wpf.Tests/AccountViewModelTests.cs` | STA/Dispatcher 中打开实际配对窗，使用 HTTP 替身覆盖六类响应、刷新签出、二维码和六位码批准。账户类 43 项通过。 |
| `server/tests/Mosaic.Server.Tests/MobilePairingTests.cs` | 临时 Kestrel 验证缺清单、直接 HTTP、受信及未受信反代，保留同账户单次兑换并读取四条备份任务和一条增量的集成覆盖。服务端 99 项通过。 |
| `docs/releases/v0.3.5-mobile.md`、`docs/implementation-status.md` | 说明窗口提示、反代与只读发布目录的安装条件，记录此次隔离验收和回滚。 |

以真实 Android Debug 签名测试包验证隔离 Kestrel 的二维码 URL、HTTPS 落地页、APK 全量和 Range 下载，以及 `assetlinks.json` 的包名和签名。隔离 APK 与清单设置文件只读，下载件为 10,984,947 字节，SHA-256 `02cab71ba014ea890ba5d28c6acd2f7f29ceaaf874f14d5e8f9ba5d82a3d6c09`，重新验签及包名、版本核查通过。Android 设备关联、扫码安装、客户端首次同步与正式容器挂载待验证。两条 ADB 连接仍为 `offline`，现有 APK 的签名仅适用于隔离验收。

本轮沿用候选数据库迁移 4 和每用户 MSI 升级流程。候选源码用 `git revert` 回退本提交即可。若首次将候选部署到低版本服务端，升级前须对持久化 SQLite 做一致性备份并保留对应主密钥，验证可恢复性。退回旧服务端需停服并恢复升级前备份与对应配置，恢复会回退备份后的写入。正式验收需核对镜像对应提交、代理地址及 HTTPS、只读挂载和正式签名，并用 Android 实机完成首次同步。

## 桌面 0.19.5 候选与手机端 0.3.5 配对

以 `2018b9e` 为候选基线，该提交包含已发布的 `v0.19.4`，再纳入 `a00ccdb` 的 Android 客户端、服务端配对和 WPF 账户入口。

**根因位置**

- `v0.19.4:src/Modules/Auth/Mosaic.Auth.Wpf/Views/AccountView.xaml` 的账户操作区没有手机下载与配对入口。新 Android 安装无法通过桌面账户取得服务器地址和授权会话。
- `v0.19.4:src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthService.cs:352` 在获取 `_operations` 前读取服务器地址，在锁内读取当前刷新令牌。切换服务器发生在两次读取之间时，刷新请求会使用来自不同会话的状态。
- 手机分支与 `v0.19.4` 在桌面版本 0.18.14 后分叉。直接以手机分支打包会遗漏桌面 0.19.4 的改动，也会漏掉 `v0.19.4:server/src/Mosaic.Server/Data/DatabaseMigrator.cs:12` 注册的附件迁移 3。Android 行高、列表和旧样本的根因见下文手机 0.3.5 节。

**改动位置**

- `Views/AccountView.xaml`、`Views/AccountView.xaml.cs`、`Views/MobilePairingWindow.cs`、`Internal/AccountViewModel.cs`、`Internal/PairingQrCode.cs`、`Localization/en.xaml`、`Localization/zh.xaml`（`src/Modules/Auth/Mosaic.Auth.Wpf`）实现配对入口、渲染二维码和配对状态。`AuthService.GetAccessTokenAsync` 将服务器地址、刷新令牌和有效访问令牌置于同一次加锁读取。
- `Schema.Migration3` 和 `Schema.Migration4` 由 `DatabaseMigrator.ApplyAsync` 按序处理。`LoggingAndMigrationTests` 验证已有 v2、v3 的账户、会话和附件记录。配对路由、服务装配与 Android 文件详见下文手机 0.3.5 节。`docs/mobile-auth-sync-plan.md` 标记 0.2.x 手机端规划已作废，保留原文用于追溯。
- `Directory.Build.props` 将桌面候选版本设为 `0.19.5`。`build/release-content.json`、`build/published-release-index.json` 追加 0.19.5 内容及指纹，`build/release-manifest.json` 和 `docs/releases/v0.19.5.md` 由现有校验脚本生成。

**迁移与发布条件**

- 桌面使用现有每用户 MSI 升级流程。服务端以已有 v2 或带附件的 v3 数据库为升级输入，迁移后均包含附件迁移 3 与配对迁移 4。回滚服务端时须停服并恢复升级前的一致性 SQLite 备份和对应主密钥配置。备份后的写入随回滚丢失。
- 现有正式域名上，下载页、APK、`assetlinks.json` 和配对入口均返回 HTTP 404。两条 Android ADB 连接均为 `offline`。正式签名 APK、扫码安装、系统 App Link 关联、同账户首次同步和 launcher 行数仍待设备验收。桌面 0.19.5 目前仅作本地候选。
- 候选服务端 97 项、Android 逻辑 74 项通过。下文所列手机 APK、二维码和 41 项服务端测试是原 `a00ccdb` 的单独验收记录。
- 桌面以已生成的 Release 自包含布局执行 `build/test.ps1 -NoBuild`，16 个项目共 515 项通过，其中实际启动发布版的 smoke 为 55 项。`build/package.ps1` 通过版本清单、快捷方式、维护路由、对话框及 CAB 内 470 个文件的 SHA-256 核验。
- MSI 覆盖安装验收应在无现有 Mosaic 安装的测试用户中运行 `build/test-install.ps1 -BaseVersion 0.19.4 -UpgradeVersion 0.19.5`，验证升级位置、快捷方式和用户数据保留。本机当前用户已有安装位置登记与桌面快捷方式，本轮保留现有安装。

## 手机端 0.3.5：M3.5 小组件与桌面配对（2026-11-27）

**根因位置**

- `36d8267:mobile/src/Mosaic.Mobile.App/Widgets/TaskWidgetService.cs:59` 的 `OnDataSetChanged` 对整个分组应用 `.Take(_layout.Rows)`。同组第三条及之后的任务从适配器消失。`36d8267:mobile/src/Mosaic.Mobile.App/Resources/layout/widget_task_row.xml:5` 使用固定行高，程序算出的像素高度没有进入视图。
- `36d8267:mobile/src/Mosaic.Mobile.App/Platform/AndroidServerConfigProvider.cs:29` 只读预置地址，安装包没有同一账户的配置和令牌领取入口。`36d8267:src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthService.cs:352` 在桌面重启后只恢复刷新令牌时，原令牌获取逻辑提前拒绝首次兑换。
- `36d8267:mobile/src/Mosaic.Mobile.App/Widgets/TestSeedStore.cs:19`、`TestSeedReceiver` 和 `MosaicWidgetTestSeed` 是旧验收数据的运行时入口。关闭测试构建选项后，旧测试快照仍保存在 Android 私有偏好中。

**改动位置**

| 文件 | 实现 |
|---|---|
| `mobile/Directory.Build.props`、`mobile/src/Mosaic.Mobile.App/Mosaic.Mobile.App.csproj` | `0.3.5`、`versionCode=10`、`MosaicMobileAppLinkHost`，移除种子构建分支 |
| `mobile/src/Mosaic.Mobile.App/AndroidManifest.xml`、`MosaicApplication.cs` | 声明 HTTPS App Link；启动迁移清理旧夹具 |
| `mobile/src/Mosaic.Mobile.App/Platform/PairingActivity.cs`、`AndroidServerConfigProvider.cs` | `ConnectAsync` 核对 App Link 和当前签名；`SaveSessionAsync` 安装新会话；`Save` 写私有服务器地址 |
| `mobile/src/Mosaic.Mobile.App/Widgets/WidgetRuntime.cs`、`WidgetSnapshot.cs` | `ApplyPairingAsync` 与刷新、写入互斥；`WidgetSnapshotStore.RemoveRetiredFixture` 限定旧数据清理条件 |
| `mobile/src/Mosaic.Mobile.App/Widgets/TestSeedReceiver.cs`、`TestSeedStore.cs` | 删除运行时夹具实现 |
| `mobile/src/Mosaic.Mobile.App/Widgets/WidgetViewport.cs`、`WidgetRenderer.cs`、`TaskWidgetService.cs` | `ForHost`、`BuildViews`、`LoadRows` 分别处理宿主尺寸、视口尺寸和完整分组数据 |
| `mobile/src/Mosaic.Mobile.Core/Cards/WidgetRowGeometry.cs` | `ForHostSize` 用 dp 算视口行数，最后换算像素 |
| `mobile/src/Mosaic.Mobile.App/Resources/layout/widget_tasks.xml`、`widget_tasks_tall.xml`、`widget_task_row.xml`、`widget_task_empty.xml`、`widget_tasks_preview.xml` | 单行/双行视图、任务触区、空状态及静态预览 |
| `mobile/src/Mosaic.Mobile.App/Resources/drawable/widget_background.xml`、`widget_background_tall.xml`、`widget_mosaic_mark.xml`、`values/colors.xml`、`values-night/colors.xml`、`values/strings.xml`、`xml/widget_tasks_info.xml` | M3.5 渐变、圆角、拼片标记、浅深配色、预览文字和尺寸说明 |
| `server/src/Mosaic.Server/Auth/MobilePairingService.cs`、`MobilePairingEndpoints.cs`、`MobileRelease.cs` | 桌面会话族绑定、短时手机挑战、一次性兑换、HTTPS 下载与签名关联清单 |
| `server/src/Mosaic.Server/Auth/DeviceFlowService.cs`、`Data/Schema.cs`、`Data/DatabaseMigrator.cs`、`ServerApplication.cs`、`ServerOptions.cs` | `PollAsync` 隔离旧协议和新配对；`Migration4`；装配及发布清单配置 |
| `src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthService.cs` | `GetAccessTokenAsync` 在锁内按需兑换恢复的刷新令牌 |
| `src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs`、`Internal/PairingQrCode.cs`、`Views/AccountView.xaml`、`Views/AccountView.xaml.cs`、`Views/MobilePairingWindow.cs`、`Localization/zh.xaml`、`Localization/en.xaml` | 账户入口、二维码、原会话绑定、关闭窗口取消、配对进度和本地化 |
| `server/tests/Mosaic.Server.Tests/MobilePairingTests.cs`、`LoggingAndMigrationTests.cs`、`TestInfrastructure.cs` | 隔离 HTTPS 配对、会话和备份增量验收；v2/v3 数据库升级；受信测试证书 |
| `mobile/tests/Mosaic.Mobile.Tests/WidgetRowGeometryTests.cs`、`WidgetGroupTests.cs` | 单双行高度、像素舍入、无效尺寸和保留后续任务 |
| `docs/releases/v0.3.4-mobile.md`、`docs/releases/v0.3.5-mobile.md`、`mobile/docs/implementation-status.md` | 纠正旧版验证结论，记录本次迁移、验收和回滚 |

**迁移与验收**

- SQLite v2 和已有附件迁移标记的 v3 测试库都顺利补充 `mobile_pairings`，账户和会话仍可用，v3 附件记录保留。低版本服务端回滚使用升级前的一致性备份和对应主密钥。停服恢复会回退备份后的服务端写入。
- Android 保留现有数据库、服务地址、加密会话和分组偏好。旧夹具清理以专用偏好存在为条件，过滤历史六个夹具 ID。换号时先清空旧展示快照，投影继续按源地址与用户分区。Android 实际覆盖安装与旧夹具迁移仍需设备验收。
- 隔离 HTTPS 接口验证桌面会话族、配对码、过期、取消、旧设备协议、重复批准与并发兑换。手机会话取得桌面账户的四条备份任务和一条后续增量。此项覆盖服务端协议；Android 应用的首次同步和 launcher 截图待设备连接恢复。
- 使用独立 QR 解码器验证 WPF 渲染出的 85 和 271 字节 ASCII 链接，四种显示尺寸均成功解码，两组模块矩阵均与独立实现一致。二维码验收图使用占位域名与引用。
- 定向测试通过：服务端 41 项，手机端 74 项，Auth Core 26 项，WPF 179 项。Android Release APK 使用本地验收证书，已通过 v2/v3 验签。包名 `app.mosaic.tasks`，版本 `0.3.5`（10），ABI 为 `arm64-v8a` 和 `x86_64`。最终 APK SHA-256 为 `02cab71ba014ea890ba5d28c6acd2f7f29ceaaf874f14d5e8f9ba5d82a3d6c09`。
- 正式 App Link、APK 和下载清单的上线条件见 [0.3.5 发布说明](releases/v0.3.5-mobile.md)。两条 ADB 连接持续为 `offline`，设备扫码、首次同步、4×1/4×2 滚动、升级及浅深主题截图均待验收。

## 手机端 0.3.0：无界面客户端重建（2026-11-26）

`mobile/` 全量删除后按 `docs/mobile-refactor-plan.md` 重建：无 Activity 的 Android 客户端，只保留桌面小组件与流体云适配器边界；任务展示源为服务端备份 + 增量变更的只读投影，唯一写路径是单任务 `POST /v1/sync/changes`（`baseServerRevision`、`clientRevision`、`idempotencyKey`）。流体云判定为阻塞（无 OPPO 凭证与真机），保留 `IFluidCloudAdapter` 并显示“不可用”，未用 AppWidget 冒充。版本 0.3.0 / `versionCode=5`，不沿用 0.2.1 / 4。

**根因位置**

1. 需求术语未核对就把“组件（卡片）”等同于 AppWidget，并且以 App UI 为主路径：`docs/mobile-refactor-plan.md:41-43`（R1–R3）。
2. 旧发布说明承诺 App UI 与 v2 投影继承：`docs/releases/v0.2.0-mobile.md`、`docs/releases/v0.2.1-mobile.md`（R5，现已标注“已作废”）。
3. 旧 widget 数据访问依赖被删除的投影类型：旧 `WidgetTaskRepository.cs` 与 `ProjectionStoreFactory`（R6，已随 `mobile/` 删除）。
4. 旧快照校验用响应字节哈希对比服务端“规范化条目”哈希，算法不一致：`server/src/Mosaic.Server/Backups/JsonCanonicalizer.cs` 对照旧 `SnapshotBootstrapService`（R7，新实现改为客户端复算规范化摘要）。

**改动位置**

- 删除：`mobile/` 125 个文件（`git rm -r mobile`）；旧文档加“已作废”说明。
- 新增 `mobile/src/Mosaic.Mobile.Wire`（`WireJson`、`WireError`、DTO）、`Mosaic.Mobile.Core`（`MobileKernel`、`SessionManager`、`TaskProjection`、`SnapshotBootstrapService`、`IncrementalSyncService`、`TaskCommandService`、`CardModel`/`IFluidCloudAdapter`）、`Mosaic.Mobile.App`（`MosaicApplication`、`TaskWidgetProvider`、`WidgetActionReceiver`、`WidgetRenderer`、`AndroidSecretStorage`、`AndroidServerConfigProvider`、`LegacyInstallCleanup`、`AndroidFluidCloudAdapter`）、`mobile/tests/Mosaic.Mobile.Tests`（37 项）。
- 迁移：首次启动把旧 `files/projections/` 改名为 `retired-projections-v2-<ts>`，删除旧偏好 `mosaic_secure` 与旧 Keystore 别名；**不迁移**旧投影/令牌，升级后从服务器备份重建。
- 新增发布说明 `docs/releases/v0.3.0-mobile.md`；完整记录见 `mobile/docs/implementation-status.md` 与 `mobile/docs/platform-decision.md`。

**验证**：`Mosaic.Mobile.Core`、`Mosaic.Mobile.App` 构建 0 警告 0 错误；`Mosaic.Mobile.Tests` 37 项通过。真机交互、覆盖安装、流体云与联调未验证。


## 0.19.4：修复任务速览拖动闪屏并降低拖拽期内存占用（2026-11-25）

本轮解决用户报告的「拖动速览任务条时有闪屏」，同时按架构规范把拖拽热路径上的重复布局与重复分配去掉。闪屏不是单点问题：从任务速览的装饰层，到浮窗模块的幽灵预览窗，再到桌面驻留模块的宿主弹窗，三处都在鼠标移动或轮询时无条件重写窗口几何、无条件重做布局，而 `AllowsTransparency=true` 的分层窗口每次几何写入都会重建合成表面，视觉上即闪屏。本轮把三处的写入都改为「真正变化才写」，并把逐帧分配全部移出热路径。改动只发生在 WPF 渲染/宿主层，`Mosaic.Tasks.Wpf` 仍只经 `Mosaic.FloatingWindows.Contracts` 的 `BeginPlacementPreview/UpdatePlacementPreview/EndPlacementPreview` 与浮窗模块通信，`build/project-boundaries.json` 的依赖方向、`build/public-api.txt` 的公开契约均未变。本版本不改数据库 schema、不改设置键、不改模块装配，服务端保持 0.1.2 不动。

**根因位置**

1. `Mosaic.Tasks.Wpf.Internal.TaskPeekView.MoveGesture`（改动前 `TaskPeekView.xaml.cs:65-...`，原逐帧调用位于 `:78` 附近）：每次 `PreviewMouseMove` 都执行 `AdornerLayer.GetAdornerLayer(this)?.InvalidateArrange()`。触发条件：在速览面板内按住任务行拖动、指针每移动一个像素。出错原因：`AdornerLayer` 覆盖整个被装饰控件，整层失效会让全部装饰元素重新测量与重排，面板行数越多、耗时越长，帧率抖动直接表现为闪屏。
2. `Mosaic.Tasks.Wpf.Internal.DragHintAdorner.MeasureOverride/ArrangeOverride`（改动前 `DragHintAdorner.cs:69-99`）：`MeasureOverride` 每帧用 `PositiveInfinity` 重新 `_hint.Measure`，`ArrangeOverride` 每帧 `TransformToAncestor` 后重设 `Canvas.Left/Top` 并再次 `_canvas.Arrange`。触发条件：任意一次布局过程（含上述整层失效触发的那些）。出错原因：气泡尺寸在手势期间恒定，逐帧测量与重排纯属浪费；`TransformToAncestor` 还会在目标行离树时抛异常中断整轮布局。
3. `Mosaic.FloatingWindows.Wpf.FloatingSurface.UpdatePreview`（改动前 `FloatingSurface.cs:104-...`）：每次指针移动都 `CreateRecord(...)`（新建 `FloatingWindowRecord` 与字符串）并 `FloatingScreen.Resolve(record.MonitorId)`（`EnumDisplayMonitors` 全屏枚举 + 每屏 `GetMonitorInfo` 分配 `MonitorInfo`），随后无条件写 `Left/Top/Width/Height` 并把 `Opacity` 设成 1。触发条件：从速览拖出任务到桌面。出错原因：幽灵窗是 `AllowsTransparency=true` 的 Topmost 分层窗，同一几何重复写入也会让 WPF 丢弃并重建合成表面；同时逐帧的托管与原生分配在拖动期间持续抬高 GC 压力。
4. `Mosaic.DesktopPresence.Wpf.PresenceSurface.PositionWindows`（改动前 `PresenceSurface.cs` 内 `_popup.Left/Top` 写入段）：`_popup` 与 `_edge` 的 `Left/Top/Width/Height` 无条件写入，`fitContent` 分支每次都做无约束 `content.Measure`。触发条件：鼠标轮询、DPI 通知、显示器变化、任意设置提交都会走到这里，拖动期间高频命中。出错原因：几何未变时的写入同样触发分层窗口重建；无约束 measure 遍历整棵承载视图视觉树，是驻留期间的 CPU 与临时分配大头。
5. `Mosaic.DesktopPresence.Wpf.PresenceScreen.Place/WorkArea`（改动前 `PresenceScreen.cs:21-45`）：`Place` 每次无条件 `SetWindowPos`，`WorkArea` 每次三连 P/Invoke（`MonitorFromWindow` + `GetMonitorInfo` + `GetDpiForMonitor`）并分配 `MonitorInfo`。触发条件：同第 4 条。出错原因：几何已就位仍强制重定位，是驻留面板闪动与 `Win32Exception` 的另一个来源；工作区与 DPI 只在显示器配置变化时变，逐次重算是纯浪费。

**改动位置**

1. `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs`
   - `MoveGesture:65-83`：删除 `InvalidateArrange` 整层失效；`_dragging` 分支改为 `e.Handled = true; _preview?.Update(point); MovePreview(point);`（`:82`），提示层逐帧只改自身合成变换，对浮窗模块仍每帧转发一次位置，契约层 `Begin/Update/End` 保持 1:1。
   - `ActivateDrag:84-99`：改为 `_preview.Update(position); StartPreview(position);`（`:97-98`），加入提示层已触发一次完整布局，不再追加显式失效。
   - `RemoveDragHints:188-195`：倒序原地删除 `DragHintAdorner`，不再每次清理都 `ToArray()` 分配一个数组。
2. `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/DragHintAdorner.cs`（全文重构，113 行）
   - `MeasureOverride:69-74`：直接 `return constraint`，不再逐帧重测气泡。
   - `ArrangeOverride:76-100`：只在首次 `_hint.Measure(new Size(PositiveInfinity, PositiveInfinity))` 并缓存 `_hintSize`（`:_measured` 门控）；`TransformToAncestor` 失败改为 `catch (InvalidOperationException)` 吞掉并折叠虚线框；末尾 `_canvas.Arrange(new Rect(finalSize))` 后调 `Place(_offset)`。
   - `Update:58-63`：只写 `_hintOffset.X/Y`（`TranslateTransform`），不失效测量与重排。
   - `Place:103-111`：按 `AdornedElement.RenderSize` 做边界夹取，气泡越界时翻转到指针上方。
3. `src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/FloatingSurface.cs`
   - 字段 `:25-30`：`_values` 之外新增 `_previewWindow`/`_previewReference`/`_previewScreen` 与常量 `PreviewHeight=76`；`_previewWindow` 字段名被 `FloatingWindowsTests.cs:380-404` 反射读取，不可改名。
   - `Near:57`：新增 0.5 像素容差比较。
   - `UpdateSettings:58-84`：`Changed(values)` 返回 `null`（设置表完全没变，不复制不重排）/`false`（只有非浮窗设置变了，不重排）/`true`（`floating.*` 变了才 `Layout`），替代原来的 `new Dictionary` 无条件复制 + 无条件重排。
   - `BeginPreview:93-103`：`EndPreview()` 后若该任务已在小窗中则直接返回（已打开的窗不能被幽灵窗承诺新位置）；`(_previewWindow ??= CreatePreviewWindow()).Show()` 跨手势复用，只切换可见性。
   - `UpdatePreview:104-130`：直接算矩形，不再 `CreateRecord`；屏幕缓存 `_previewScreen` 只在释放点离开已缓存屏幕像素区时才 `FloatingScreen.Resolve`；`Math.Round` 对齐整物理像素；四处几何与 `Opacity` 都经 `Near` 门控。
   - `EndPreview`：改为 `Hide()`（测试断言 `Assert.False(preview.IsVisible)`，不能用 `Opacity=0`）；`Dispose` 里 `preview?.Close()`，`App.xaml` 的 `ShutdownMode="OnExplicitShutdown"` 保证隐藏窗不会拖住退出。
4. `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSurface.cs`
   - 字段 `:35-39`（原 `_sessionGeneration` 之后）：新增 `_measureVersion`/`_measuredVersion`/`_measuredWidth`/`_measuredHeight`/`_measureOperation`。
   - 新增 `Near(double,double)` 辅助（`:51`）与 `Set(Window, width, height, left, top)`（`:441-447`，紧随 `PositionWindows`）：几何只在变化时写入。
   - `QueueMeasure:290-297`：`++_measureVersion`（`:296`），承载视图报告 `LayoutInvalidated` 时标记自然高度可能已变。
   - `PositionWindows:395-439`（全文重写）：`_edge` 与 `_popup` 全部经 `Set(...)` 容差写入；`Topmost` 只在值变化时写；`fitContent` 分支的 `content.Measure` 由 `_measuredVersion != _measureVersion || !Near(_measuredWidth, width)`（`:422`）门控，缓存 `_measuredHeight`（`:426`）。
   - `ShowPopupAsync:243`：新会话时 `++_measureVersion; _measuredVersion = -1; _measuredWidth = double.NaN;`，上一次会话的自然高度不能复用。
   - `DisplayChanged:449-455` 与 `CreateWindow` 的 `DpiChanged`（`:389`）：`PresenceScreen.Reset()` + `++_measureVersion` 后异步 `PositionWindows`。
5. `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceScreen.cs`（全文重写，83 行）
   - 新增静态缓存 `Areas`/`Scales`（键 `(nint Monitor, bool LastWindow)`）与 `Reset()`。
   - `WorkArea:21-32`：命中缓存直接返回；未命中才做 `MonitorFromWindow` + `GetMonitorInfo` + `ScaleOf`。
   - `ScaleOf:34-43`：DPI 比例单独缓存，`GetDpiForMonitor` 缺失时回退 96。
   - `Place:46-61`：先 `GetWindowRect` 比对，已在目标像素矩形上就跳过 `SetWindowPos`；仅在几何确实需要变时才调用，失败才抛 `Win32Exception`。
6. 测试：未新增用例；`tests/Mosaic.Wpf.Tests/PresentationTests.cs` 的 `Leaving_during_save_waits_and_duplicate_submission_is_disabled`（`:158`）开头补一行 `MosaicLocalizationManager.SetLanguage(MosaicLanguage.Chinese)`，让 `:169` 的中文断言不再依赖进程默认 UI 文化（原实现只要 `DOTNET_CLI_UI_LANGUAGE=en` 就断言失败，属于用例自身的环境脆弱性，与本次改动无关）。既有 `tests/Mosaic.Wpf.Tests/PresenceTests.cs`（180 项）、`tests/Mosaic.Wpf.Tests/FloatingWindowsTests.cs` 与 `tests/Mosaic.FloatingWindows.Tests`（12 项）覆盖了本次改动的全部硬约束：`_previewWindow` 反射字段名、幽灵窗宽度区间 220..640、`EndPlacementPreview` 后 `IsVisible == false`、`PreviewBegins/Updates/Ends` 1:1 转发、`DragHintAdorner` 类名与 `Drop` 后装饰层为空、`Popup_measures_generic_content...` 在 `LayoutInvalidated` 后高度必须增长、旧会话通知不得改高度。
7. 发布链：`Directory.Build.props:12` 版本 `0.19.4`；`build/release-content.json` 追加 0.19.4 条目（improved 3 / fixed 1，`previousReleaseId = mosaic-0.19.3`，指纹 `53BCB2BAC1E60A9D36A79F8DA8D41495D70CC0E2604BC045BCA0AE379ECEB809`）；`build/published-release-index.json` 追加同指纹回执至 65 条；`build/release-manifest.json` 与 `docs/releases/v0.19.4.md` 由 `scripts/release_content_verify.py` 生成；`README.md` 当前版本、下载链接、MSI 文件名与发布说明入口更新为 0.19.4。

**迁移逻辑与验收项**

- 迁移逻辑：不改数据库 schema、不改设置键、不改模块装配与公开契约，无需数据迁移。
- 对已有安装的影响：升级覆盖安装即可，任务、设置、账户会话与浮窗状态不受影响。幽灵预览窗改为跨手势复用，进程退出时才关闭；`App.xaml` 的 `ShutdownMode="OnExplicitShutdown"` 已保证隐藏窗不阻塞退出。桌面驻留的屏幕几何缓存只存在于进程内，升级后首次启动自动重建。
- 验收项：
  1. `python -I scripts/release_content_verify.py` 输出 `Release content verified: 0.19.4, mosaic-0.19.4, 65 indexed releases.`
  2. `dotnet build src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf -c Release` 与 `dotnet build src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf -c Release` 均零警告零错误。
  3. `dotnet test tests/Mosaic.Wpf.Tests -c Release` 180/180 全绿（实测 21s 258ms；含 `Placement_preview_ghost_tracks_the_hint_and_ends_without_a_window`、`Popup_measures_generic_content_clamps_height_and_ignores_closed_session_notifications`、`Peek_drag_forwards_placement_preview_and_ends_on_cancel`、`Peek_repeated_drags_never_stack_drag_hint_adorners`）。注意**不要**给测试进程设置 `DOTNET_CLI_UI_LANGUAGE=en`：`PresentationTests` 依赖中文 UI 文化的断言会被改成英文文案（本轮已在该用例内固定为中文，但其他本地化用例仍按进程默认文化跑）。
  4. `dotnet test tests/Mosaic.FloatingWindows.Tests -c Release` 12/12 全绿（契约层 `Begin/Update/End` 1:1 转发）。
  5. 桌面 smoke（串行，`tests/Mosaic.Desktop.SmokeTests`）：`Floating_header_buttons_move_on_drag_and_still_click_on_short_press` 连续 7 次通过（12s 643ms 等）；`Floating_real_pointer_moves_resizes_clicks_and_restores_saved_bounds` 1/1 通过（15s 552ms）。更正记录：本轮早前有 4 次 header 用例失败（`FloatingDragTests.cs:150` 实测位移 21，期望 30..41），当时误判为 0.19.4 回归；后续在完全相同的代码上连跑 6 次全过，确认根因是本机残留 `Mosaic.Desktop.exe` 抢占前台，按下时首次点击仅激活窗口、越过系统拖动阈值的手势被推迟到轨迹后段才触发，`FloatingSurface.cs` 未再改动。运行 smoke 前先用 `Get-Process Mosaic.Desktop` 清掉残留实例。
  6. 发布前全量：`dotnet test Mosaic.sln -c Release` 515/515 全绿（含 `Mosaic.Desktop.SmokeTests` 1m 22s 与 `Mosaic.Architecture.Tests`，0 跳过）。
  7. 实机验收：打开任务速览，按住任一任务行在面板内快速来回拖动，气泡与虚线框全程跟随指针且无闪屏；拖出到桌面时幽灵预览窗平滑跟随、跨屏时位置正确；松手创建小窗、Esc 取消均正常；桌面驻留边缘条与弹窗在长时间驻留下无闪动。
- 发布核验：提交 `cd17e9b`（分支 `fix/peek-detach-0.18.8`，13 files changed，296 insertions / 56 deletions，新建 `docs/releases/v0.19.4.md`），tag `v0.19.4`（`Mosaic 0.19.4`）指向该提交；Release <https://github.com/soehi/Mosaic/releases/tag/v0.19.4> 非草稿、非预发布，正文与 `docs/releases/v0.19.4.md` 一致。资产 `Mosaic-0.19.4-win-x64.msi` 52,590,840 字节，SHA-256 `0A6FD313F0BA1292D0777FA2AB6FA18F2334FFFA279BC7729216B62566847D54`，与 `gh api` 读到的 GitHub 资产 digest `sha256:0a6fd313f0ba1292d0777fa2ab6fa18f2334fffa279bc7729216b62566847d54` 一致。`build/package.ps1` 全流程通过：publish 为 Release 自包含产物、`PASS: published payload is a release build with manifest mosaic-0.19.4.`、`PASS: MSI contains the Application-owned DesktopFolder Mosaic shortcut, INSTALLFOLDER working directory, and StartupCleanup registry removals.`、470 个 cabinet 成员逐一 SHA-256 比对、`PASS: MSI static structure, maintenance routing, dialogs, payload manifest and cabinet integrity verified for 0.19.4.`；本地产物在 `D:\Code\Mosaic\artifacts\installer\0.19.4\Mosaic-0.19.4-win-x64.msi`。打包环境说明：本机沙箱无法访问 `api.nuget.org` 的服务索引，`dotnet restore --locked-mode` 会因漏洞数据拉取失败报 `NU1900` 中止 `build/publish.ps1:9`；以环境变量 `NuGetAudit=false`（MSBuild 同名全局属性）关闭审计后依赖全部命中本地缓存，restore 与打包均通过。有网络的环境无需该变量。
- 回滚方式：`git revert` 本次提交即回到 0.19.3，行为回到旧实现（闪屏复现），数据无影响。

## 0.19.3：修复任务速览内未拖出面板时拖拽提示残留（2026-11-25）

本轮修复用户报告的问题：在任务速览（peek）面板内按住任务行拖动但松手时仍在面板内（或松键事件被吞掉），所有任务行会同时残留虚线拖拽框与「松手以创建小窗 · Esc取消」气泡，且每拖动一次叠加一层，直到面板关闭。改动后：每次激活拖拽前先按类型把本控件上残留的 `DragHintAdorner` 全量清扫，取消/结束手势时同样全量清扫；同时拖拽已激活后若收到左键已松开的鼠标移动（说明 MouseUp 被吞、`LostMouseCapture` 未路由），立即走 `CancelDrag` 收尾，不再依赖单一路径。本版本不改数据库、设置与模块装配，服务端保持 0.1.2 不动。

**根因位置**

1. `Mosaic.Tasks.Wpf.Internal.TaskPeekView.ActivateDrag`（改动前 `TaskPeekView.xaml.cs:82-96`，字段赋值 `_preview = new DragHintAdorner(...)` 位于原 `:88-90`）：激活拖拽时直接用新实例覆盖 `_preview` 字段，而移除旧提示层的唯一路径是 `ResetGesture`/`CancelDrag` 按字段引用移除。触发条件：前一次手势的 MouseUp 在行元素离树或捕获丢失时未路由到 `EndGesture`/`CaptureLost`（在面板内松手、或松键被其他元素吞掉）。出错原因：旧 `DragHintAdorner` 仍挂在 `AdornerLayer` 上，但字段被覆盖后已不是同一个引用，任何清理路径都再也找不到它，永久残留；下一次拖动再叠一层，用户截图即 4 行同时残留虚线框与气泡。
2. `Mosaic.Tasks.Wpf.Internal.TaskPeekView.MoveGesture`（改动前 `:65-...`）：`_candidate is null || e.LeftButton != Pressed` 分支直接 `return`，不处理「`_dragging` 已为 true 但 MouseUp 被吞」的状态。触发条件：拖拽激活后松键事件丢失，下一次鼠标移动到来。出错原因：此时没有其他事件能触发 `CancelDrag`，`_dragging`、提示层与预览小窗保持存活，视觉残留且拦截后续点击。

**改动位置**

1. `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs`
   - `MoveGesture:65-81`：保留「捕获瞬间伪 Move 必须直接返回」的原守卫形态（这是 0.18.8 起的既有约束，`CaptureMouse()` 成功瞬间会重发一个左键未按下的伪 Move），在 `_dragging` 分支内新增 `CancelDrag()`（`:72`），用于察觉被吞掉的 MouseUp。
   - `ActivateDrag:82-96`：`CaptureMouse` 成功并置 `_dragging` 后先调 `RemoveDragHints()`（`:87-88`），再创建新 `DragHintAdorner`，保证旧的泄漏 adorner 不会随字段覆盖永久残留。
   - `ResetGesture:165-176`：原按字段移除改为调用 `RemoveDragHints()`（`:168`），取消/结束/打开菜单所有收尾路径共用同一清扫逻辑。
   - 新增 `RemoveDragHints:185-191`：置空 `_preview` 后从 `AdornerLayer.GetAdornerLayer(this)` 取本控件全部 adorner，按 `OfType<DragHintAdorner>()` 全量 `Remove`，不依赖字段引用是否已漂移。
2. `tests/Mosaic.Wpf.Tests/PresenceTests.cs`：新增 `Peek_repeated_drags_never_stack_drag_hint_adorners`，覆盖三个场景——激活后伪 Move 后层数为 1、同一行连续两次激活不叠加、`CancelDrag` 后清零（本类 26/26 通过）。
3. 发布链：`Directory.Build.props:12` 版本 `0.19.3`；`build/release-content.json` 追加 0.19.3 条目（fixed 1，`previousReleaseId = mosaic-0.19.2`，指纹 `16D93C15F2706B9884886E35E7B8004A03618811CA67EAC48CB9CCC3C8299C50`）；`build/published-release-index.json` 追加同指纹回执至 64 条；`build/release-manifest.json` 与 `docs/releases/v0.19.3.md` 由 `scripts/release_content_verify.py` 生成；`README.md` 当前版本与下载链接更新为 0.19.3。

**迁移逻辑与验收项**

- 迁移逻辑：不改数据库 schema、不改设置键、不改模块装配与公开契约，无需数据迁移。
- 对已有安装的影响：升级覆盖安装即可，任务、设置、账户会话与浮窗状态不受影响；残留的提示层只存在于运行期视觉树，升级后重启即不存在。
- 验收项：
  1. `python -I scripts/release_content_verify.py` 输出 `Release content verified: 0.19.3, mosaic-0.19.3, 64 indexed releases.`
  2. `dotnet build src/Modules/Tasks/Mosaic.Tasks.Wpf -c Release` 零警告零错误。
  3. `dotnet test tests/Mosaic.Wpf.Tests --filter "DisplayName~Peek_repeated_drags_never_stack_drag_hint_adorners"` 全绿。
  4. `dotnet test tests/Mosaic.Desktop.SmokeTests` 55/55 全绿（含真实指针拖出任务的 `Peek_real_pointer_detaches_multiple_tasks_and_dismisses_after_each_drop`）。
  5. 实机验收：打开任务速览，按住任一任务行拖起后在面板内松手，再对其他行重复多次；所有行均不残留虚线框与气泡，随后拖出到桌面仍能正常创建小窗，Esc 仍可取消拖拽。
- 回滚方式：`git revert` 本次提交即回到 0.19.2，行为回到旧实现（残留问题复现），数据无影响。

## 0.19.2：重启自动登录与可配置间隔的自动备份（2026-11-25）

本轮解决两个用户可见断点：其一，重启软件后账号显示已登录，但任何需要服务器的操作都提示未登录，用户必须重新输入密码；其二，登录后没有任何周期性任务备份，只能手动点「立即备份」。改动后：启动时若本机存有与当前服务器同源的凭据，直接恢复登录状态，并在首次需要访问令牌时用保存的刷新令牌惰性换取访问令牌（启动过程仍然不联网）；`sync-core` 模块在激活后启动一个后台循环，登录状态下按用户设置的间隔（默认 60 秒，15–86400 秒可调，可单独关闭）上传任务与可移植设置，登录事件触发一次立即备份，改间隔/开关立即重定时，停止模块时先取消并等待循环退出。本版本不改数据库 schema、不改设置键名之外的数据、不改模块装配的依赖方向，服务端保持 0.1.2 不动。

**根因位置**

1. 自动登录断点：`Mosaic.Auth.Core.Internal.AuthService.GetAccessTokenAsync`（改动前的 `AuthService.cs:352-360`）以 `_session is null` 直接判定「尚未登录」并返回 `AuthErrorCodes.TokenInvalid`。而 `AuthService.ActivateAsync`（`AuthService.cs:49-73`，恢复分支 `:63-68`）只把 `_stored`（DPAPI 保存的刷新令牌）读回内存并写 `AuthStatus.SignedIn`，刻意不发网络请求、不建立 `_session`。触发条件：重启软件后首次触发任何需要访问令牌的调用（同步、任务上传/下载、账户页远程操作）。出错原因：状态显示已登录与访问层判定未登录来自两份不同的内存状态，刷新分支在没有 `_session` 时根本走不到 `transport.RefreshAsync`。
2. 自动备份缺失：`Mosaic.Sync.Core.SyncModule`（改动前 96 行）只有在宿主调用 `ISyncApi.UploadAsync` 时才备份，模块内没有定时器、没有登录事件驱动的备份；`Internal/SyncSettingsSource.cs`（改动前 3 个定义）只登记 `sync.enabled`/`sync.includeSettings`/`sync.includeAttachments`，没有间隔与自动开关。触发条件：用户登录后放着软件不动。出错原因：没有「时间」这个驱动源，备份完全依赖用户手动点击。
3. 设置读取时机陷阱（新实现必须避开）：`SettingsService.Register(source)` 只登记设置源，`ApplyAsync` 仅在设置提交或编辑会话时被调用，启动时不会自动应用。若后台循环直接用 `SyncOptions` 的字段默认值启动，用户上次保存的「关闭自动备份 / 自定义间隔」会被默认值覆盖。触发条件：激活即启动循环的朴素实现。出错原因：字段默认值与落盘值在激活时点不相等；`TasksModule` 的既有惯例是激活时先 `GetValuesAsync()` 再 `Apply`，本轮 Sync 对齐该惯例（`SyncModule.cs:62-67`）。

**改动位置**

1. `src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthService.cs`
   - `GetAccessTokenAsync:352-384`：判定条件从「`_session` 与 `_stored` 都必须存在」改为「`_stored` 与 `State.Server` 存在即可」，`_session` 为空或访问令牌过期时进入既有刷新分支，用 `_stored.RefreshToken` 调 `transport.RefreshAsync:367`，成功后走 `CompleteSignInAsync:386-403` 重建 `_session`、写回 DPAPI 并更新状态；刷新被服务器拒绝（`TokenInvalid`/`TokenExpired`）时清凭据并回到 `SignedOut`（`:371-377`，行为未变）。
2. `src/Modules/Sync/Mosaic.Sync.Core/Internal/SyncSettingsSource.cs`
   - `SyncOptions:7-40`：新增 `DefaultAutoIntervalSeconds = 60`、`MinAutoIntervalSeconds = 15`、`MaxAutoIntervalSeconds = 86400`、`AutoSyncEnabled`（缺省 `true`）、`AutoIntervalSeconds`（缺省 60）、`AutoInterval => TimeSpan.FromSeconds(AutoIntervalSeconds)`；`Apply:24-28` 用 `is not false` 语义读开关、`ParseAutoInterval:31-41` 对非数字/溢出回落默认值后再 `Math.Clamp`。
   - 设置定义 `:49-75`：新增 `sync.autoSyncEnabled`（Boolean，`StorageSystem`/`Normal`/`Immediate`/`UserSettings`）与 `sync.autoIntervalSeconds`（Integer，默认 60，范围 15–86400），均带 `Settings.Setting.SyncAutoEnabled(.Description)` / `Settings.Setting.SyncAutoIntervalSeconds(.Description)` 本地化键。
   - `ApplyAsync:78-86`：应用后再比对开关与间隔是否真的变化，只有变化才调用 `onTimingChanged`，避免每次设置提交都打断计时。
3. `src/Modules/Sync/Mosaic.Sync.Core/Internal/SyncService.cs`
   - `UploadAsync:107-108` 保持不变（手动备份按 `_options.IncludeAttachments` 决定是否镜像附件）；新增 `UploadTasksAsync:114-115` 内部入口，固定 `includeSettings: true, includeAttachments: false`；原实现主体改为 `UploadCoreAsync:117-…`，两条入口共用导出、校验、上传、状态更新与通知链路。
4. `src/Modules/Sync/Mosaic.Sync.Core/Internal/GuardedSyncApi.cs:41-42`：新增内部 `UploadTasksAsync`，与公开 `UploadAsync:34-35` 一样经 `IModuleExecutionGate` 与 `IDiagnosticSink`；不进 `ISyncApi` 公开契约，因此 `build/public-api.txt` 与蓝图边界不变。
5. `src/Modules/Sync/Mosaic.Sync.Core/SyncModule.cs`
   - `SyncDependencies:10-17` 新增可选 `TimeProvider? Clock`（默认 `TimeProvider.System`，`:43`），测试用手动时钟驱动。
   - `ActivateAsync:58-74`：`Register` 设置源 → `GetValuesAsync` 读落盘值（失败抛 `InvalidOperationException("同步设置读取失败。")`）→ `_options.Apply` → 订阅 `Auth.StateChanged` → `_gate.Activate()` → 建 `CancellationTokenSource` 并启动 `AutoSyncLoopAsync`；激活本身不上传。
   - `StopAsync:76-97`：先退订、取消并 `await` 后台循环（捕 `OperationCanceledException`）→ `PrepareSourceStopAsync("sync-core")` → 释放设置登记 → `_gate.StopAsync()`，符合框架规范「后台任务在 `StopAsync` 中取消并等待」。
   - `OnAuthChanged:101-106`：登录成功即 `ScheduleUpload()`（立即备份一次），登出不清状态、由循环下一轮自行跳过。
   - `RetimeAutoSync:109`/`ScheduleUpload:111-115`/`Wake:119-124`：用 `TaskCompletionSource` 唤醒信号打断 `Task.Delay`，重定时只重新排队、不额外上传。
   - `WaitForTickAsync:126-143`：`Task.Delay(_options.AutoInterval, _clock, ct)` 与唤醒信号 `Task.WhenAny`；信号胜出时放弃的 delay 挂 `ContinueWith(OnlyOnFaulted)` 观察异常，避免未观测任务异常。
   - `AutoSyncLoopAsync:145-163`：间隔到点无条件备份；仅被唤醒时只有 `_uploadDue == 1`（登录事件）才备份；非取消除外一律 `_diagnostics.Report` 后继续下一轮，单次失败不终止循环。
   - `AutoSyncOnceAsync:165-172`：未登录、`sync.enabled` 关闭或自动备份关闭时静默返回，否则 `_api.UploadTasksAsync`。
6. `src/Mosaic.Desktop/BuiltInModuleCatalog.cs:41`（`internal TimeProvider Clock { get; } = clock;`）与 `:209-220` 的 Sync 装配：把宿主时钟传入 `SyncDependencies`，与 Auth/Tasks 模块取时钟的方式一致。
7. `src/Modules/Settings/Mosaic.Settings.Wpf/Localization/zh.xaml:281-284`、`en.xaml:281-284`：新增 `Settings.Setting.SyncAutoEnabled(.Description)`、`Settings.Setting.SyncAutoIntervalSeconds(.Description)` 四个键。Integer 设置项在既有设置页已渲染为整数输入框（`SettingsViewModel.cs:72`、`:270`、`:296-297`），本轮零 UI 改动。
8. `tests/Mosaic.Auth.Core.Tests/AuthServiceTests.cs`：`WithStoredCredential()` 构造「只有刷新令牌、没有会话」的重启态，新增 3 个用例覆盖「首取令牌用刷新令牌重建会话」「访问令牌未过期不重复刷新」「刷新被拒清凭据回未登录」。
9. `tests/Mosaic.Sync.Core.Tests/Fakes.cs`：`FakeSettings` 后追加 `ManualTimeProvider`（`TimeProvider.CreateTimer` 返回可手动推进的 `ManualTimer`，实现 `IAsyncDisposable`），供后台循环确定性计时。
10. `tests/Mosaic.Sync.Core.Tests/AutomaticSyncTests.cs`（新增，8 个用例）：激活零上传、登录即备份一次（断言只上传 `Task`/`Setting`、无下载、无附件清单与附件上传）、每间隔重复、落盘间隔控制首次到点、登出跳过并重新登录恢复、关闭自动备份跳过并在重开后恢复、改小间隔即时重定时、`StopAsync` 后注销设置源且推进 600 秒无上传。
11. 发布链：`Directory.Build.props:12` 版本 `0.19.2`；`build/release-content.json` 追加 0.19.2 条目（added 2、improved 2、fixed 1，`previousReleaseId = mosaic-0.19.1`，指纹 `8E40D560…AA27D`）；`build/published-release-index.json` 追加同指纹回执至 63 条；`build/release-manifest.json` 与 `docs/releases/v0.19.2.md` 由 `scripts/release_content_verify.py` 生成。

**迁移逻辑与验收项**

- 迁移逻辑：不改数据库 schema、不改设置表结构、不改模块装配依赖方向，无需数据迁移。新增的两个设置键在首次读取时按缺省值生效（自动备份开启、间隔 60 秒），已有安装升级后行为即为「登录状态下每分钟备份一次」，可在设置页随时关闭或改间隔。手动备份行为与 0.19.1 完全一致（仍按「同步任务附件」开关决定是否镜像附件）。
- 对已有安装的影响：升级保留账号凭据、任务、附件与设置；升级后首次启动会用已保存的刷新令牌静默换取访问令牌，若该令牌已被服务器撤销则清凭据并显示未登录（需要重新输入密码），不会反复报错。
- 验收项：
  1. `python -I scripts/release_content_verify.py` 输出 `Release content verified: 0.19.2, mosaic-0.19.2, 63 indexed releases.`
  2. `dotnet build Mosaic.sln -c Release` 零警告零错误。
  3. `dotnet test tests/Mosaic.Auth.Core.Tests`（含 3 个重启自动登录用例）全绿。
  4. `dotnet test tests/Mosaic.Sync.Core.Tests`（含 8 个后台循环用例）全绿。
  5. `dotnet test tests/Mosaic.Architecture.Tests` 全绿（`build/public-api.txt` 未新增公开类型，蓝图依赖方向未变）。
  6. 实机验收：登录后关闭软件再启动，界面直接显示已登录且右上角/账户页不提示重新登录；保持登录放置 2 分钟，服务器侧备份记录每分钟新增一次；在设置里把间隔改成 15 秒后 15 秒内出现下一次备份。
- 回滚方式：`git revert` 本次提交即回到 0.19.1；回滚后新增的两个设置键留在设置库中不被读取，不影响启动。若只需关闭该行为而不回滚版本，把「自动备份」设为关闭即可。

## 0.19.1：合入 0.18.14 性能优化，保留任务附件同步（2026-11-24）

本轮把 `origin/main` 的 0.18.14 性能优化（`9e1d70c`）合入 0.19.0 附件同步线，解决两侧并行的 10 个文件冲突，并发布为客户端 **0.19.1**。合并后同时具备：任务查询分页投影与聚合计数、主列表按行差量更新、整表恢复单次批量通知（来自 main），以及账号备份的附件上传/下载/删除通道（来自 0.19.0）。数据目录、设置键、模块装配与数据库 schema 均未变；服务端保持 0.1.2 不动。

**根因位置**

1. 两条发布线从同一祖先分叉后各自演进。共同祖先为 `162c74dc78aa4406a70fc7373d90f755d98a48e6`；0.19.0 在其上发布（工作分支 `f58323a`），0.18.14 在 main 上发布（`9e1d70c`），后者的提交从未进入工作分支。触发条件：把 main 合入工作分支。出错原因：两侧修改了同一批文件——`TaskQuery`（`TaskRequests.cs:17-29`）、`ITaskRepository`（`ITaskRepository.cs`）、SQLite 仓储（`SqliteTaskRepository.cs:18-51`）、`TaskService`（`TaskService.cs:85-86`）、`GuardedTasksApi`（`GuardedTasksApi.cs:28-34`）、`AttachmentCountingTasksApi`（`AttachmentCountingTasksApi.cs:20-29`）、`TaskChangeKind`（`TaskChanges.cs:2-3`）、`TaskTransferService`、`TasksViewModel`、`TaskPeekViewFactory`、`TaskListItemViewModel`——于是产生 10 个冲突文件（9 个内容冲突 + 1 个 `docs/releases/v0.18.14.md` add/add 重复）。
2. 发布工具对历史长度上限的规则分叉。0.18.14 把三处 20000 上限一次性放宽到 40000（`build/release-content.ps1:160`、`scripts/release_content_verify.py:113-115`、`ReleaseManifest.cs:80-83`，对应上限用例 40001 字符）；0.19.0 把同一批位置改成「上限 `TaskConstraints.NotesMaxCharacters` = 20000 + 从最旧版本裁剪」。触发条件：合并时机械取一侧。出错原因：保留 40000 则 0.19.0 的裁剪实现与用例断言失效；只保留裁剪却沿用 main 的 40000 字面量则构建期与运行时（`TaskConstraints`）口径不一致。
3. `TaskTransferService` 的改动语义不重叠但位置重叠。0.19.0 在构造函数注入 `ITaskAttachmentRepository? attachments` / `ITaskAttachmentBlobStore? blobs` 并新增 6 个附件方法；0.18.14 把 `ReplaceAllAsync` 的逐条 `changed(new(...))` 改成一次 `changed(new(Guid.Empty, 0, TaskChangeKind.Replaced))`。触发条件：整份备份恢复。出错原因：取任一侧都会丢掉另一侧能力（附件无法备份，或恢复 10 万行时逐条重载列表与浮窗）。
4. 冻结回执链在两侧都追加到尾部。`build/published-release-index.json` 与 `build/release-content.json` 在 0.19.0 侧已冻结 0.18.14 回执（`f58323a`），main 侧自带同一 0.18.14 条目，数组尾部因此同时出现「相同条目、不同前驱链」的冲突块。

**改动位置（逐冲突文件）**

1. `Directory.Build.props:12`：版本取 `0.19.1`（两侧分别为 0.19.0 与 0.18.14），其余构建属性不变。
2. `src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskTransferService.cs`：文件头 `:1-5` 合并 `using System.Security.Cryptography;` 与 `using Mosaic.Contracts;`；构造函数 `:11-17` 保留附件依赖 `attachments`/`blobs`；`ReplaceAllAsync:33-36` 保留 main 的单次 `TaskChangeKind.Replaced` 通知；`:45-139` 的 `ExportAttachmentsAsync`/`OpenAttachmentAsync`/`ImportAttachmentAsync`/`DeleteAttachmentAsync`/`RemoveAttachmentsNotInAsync`/`SweepOrphanAttachmentsAsync` 原样保留。
3. `build/published-release-index.json:298-311`：保留 0.18.14（`41480A27…`）与 0.19.0（`977CF1C8…`）两条既有回执，追加 0.19.1（`5524C8D5…`），共 62 条；已提交条目的 id/version/hash 未改。
4. `build/release-content.json:1011-1026`：`currentReleaseId` 改为 `mosaic-0.19.1`；追加 0.19.1 条目（improved 3 条、fixed 1 条、`previousReleaseId = mosaic-0.19.0`），共 62 条；0.18.14 与 0.19.0 正文未改（指纹不变）。
5. `build/release-content.ps1:165-188`：保留 0.19.0 的裁剪循环（从最旧版本起裁直到 ≤ 20000 rune，再追加「更早版本…见 docs/releases/」提示），删除 main 的 40000 分支；`:138-151` 保留已提交索引不可变校验。
6. `scripts/release_content_verify.py:115-132`：同规则改为 `compose_range` 裁剪实现；`:98-103` 保留已提交索引条目不可变校验；`:136-139` 输出 `Release content verified: …`。
7. `src/Modules/ReleaseSummary/Mosaic.ReleaseSummary/ReleaseManifest.cs:81-83`：限额检查改用 `TaskConstraints.TitleMaxCharacters` / `TaskConstraints.NotesMaxCharacters`（20000），与脚本默认常量对齐。
8. `docs/releases/v0.18.14.md`：add/add 冲突去重，保留 3 条正文各一份（速览一页、主列表差量更新、恢复单次通知）。
9. `tests/Mosaic.ReleaseSummary.Tests/ReleaseSummaryTests.cs:1-4`：合并 `using System.Text;` 与 `using System.Text.Json;`；`:191-213` 的 `Every_published_release_receipt_is_recognized_by_the_shipped_manifest` 完整保留（0.18.14 回执不得因新版本替换链尾而丢失）。
10. `build/release-manifest.json`：由校验脚本按 0.19.1 重写，62 条，`currentReleaseId = mosaic-0.19.1`。

**auto-merged 后复核的文件（0.18.14 性能改动 + 0.19.0 附件能力并存）**

- 任务查询链路：`TaskRequests.cs`、`ITaskRepository.cs`、`SqliteTaskRepository.cs`、`TaskService.cs`、`GuardedTasksApi.cs`、`AttachmentCountingTasksApi.cs`（`CountAsync:28-29` 只透传，`ListAsync:20-26` 仍批量补附件数）、`TaskChanges.cs`。
- WPF 展示：`TasksViewModel.cs`、`TaskPeekViewFactory.cs`、`TaskListItemViewModel.cs`、`TaskFloatingContentFactory.cs`（订阅 `Replaced`）。
- 测试：`RepositoryContractTests.cs`、`AttachmentCleanupTests.cs`、`AutoArchiveTests.cs`、`TaskTransferTests.cs`、`FloatingWindowsTests.cs`、`PresenceTests.cs`、`PresentationTests.cs`、`PreviewCaptureTests.cs`、`ResponsiveLayoutTests.cs`、`tests/Shared/MemoryTaskRepository.cs`。
- `build/public-api.txt`：已含 `Mosaic.Tasks.Contracts.TaskCountsDto:216`、`TaskChangeKind:213`、`TaskAttachmentExportDto:210`、`Mosaic.Sync.Contracts.AttachmentManifestDto:178`、`RemoteAttachmentDto:185`，与合并后源码一致，无需再改。

**发布链调和说明**

0.18.14 记录中「上限从 20000 放宽到 40000」是当时的一次应急放宽；0.19.0 引入裁剪机制后上限回到 `TaskConstraints.NotesMaxCharacters = 20000`（裁剪保证任意起点都能收敛），40000 分支随之作废。历史章节保留原文不改（已发布的实施记录不回写），以本节为当前口径：构建期（ps1 与 py）与运行时（`ReleaseManifest.Validate`）三处一致。

**迁移逻辑与验收项**

- 迁移逻辑：不改数据库 schema、不改设置键、不改模块装配、不改安装布局，无需数据迁移。`TaskQuery` 默认值仍保持旧行为（`Limit = 0`、`IncludeNotes = true`）；发布链只追加，不改已冻结回执。
- 对已有安装的影响：0.18.14 与 0.19.0 用户升级到 0.19.1 后，两者的发布回执都能在 62 条随包清单中识别，都能收到 0.19.1 更新简要；任务、附件、设置与备份数据原样读取。
- 验收项：
  1. 发布校验脚本输出 `Release content verified: 0.19.1, mosaic-0.19.1, 62 indexed releases.`
  2. 已发布回执识别用例 `Every_published_release_receipt_is_recognized_by_the_shipped_manifest` 通过。
  3. `dotnet test tests/Mosaic.ReleaseSummary.Tests`、`tests/Mosaic.Sync.Core.Tests`、`tests/Mosaic.Tasks.Core.Tests`、`tests/Mosaic.Architecture.Tests` 全部通过。
  4. `dotnet build Mosaic.sln -c Release` 零警告零错误。
  5. 计数 62 的算术：合并前工作分支 HEAD（`f58323a`）61 条、main（`9e1d70c`）60 条，且 main 的 id 序列是 HEAD 的真前缀（仅缺 `mosaic-0.19.0`），两侧并集仍为 61 条；追加 0.19.1 后为 62。任务书中的 61 是合并前工作分支条数，不含本次新版本本身。
- 回滚方式：在 worktree 内 `git reset --hard f58323a` 即回到 0.19.0（合并提交未推送）；main 侧改动可在需要时从 `9e1d70c` 重新合入。无数据迁移需要撤销。

## 0.18.14：任务查询分页投影、主列表差量更新与整表恢复批量通知（2026-11-24）

本轮落实性能评审里优先级最高的三项：任务查询收窄为投影 + `LIMIT`/`COUNT`、主列表改为按 Id 差量更新行、整份备份恢复只发一次批量变更通知。附带把发布校验的历史长度上限从 20000 放宽到 40000 字符——0.18.13 之后整条发布历史已达 19959 字符，任何新条目都会在验证阶段被拒绝。数据目录、设置键与模块装配均未变。

**根因位置**

1. 列表查询没有上限且连备注一起物化。`SqliteTaskRepository.cs:18-26`（0.18.12）用 `SELECT *` 取回整行并逐条 `Map`；`TaskPeekViewFactory.cs:347`（0.18.12）每次加载都读取整张筛选结果、只在渲染时截断到 `MaxItems`；`TasksViewModel.cs:286`（0.18.12）同样取全字段。触发条件：打开速览、每次任务变更后的重载、主列表首屏与刷新。出错原因：行、DTO 与备注字符串的峰值和常驻量与任务总量同阶，备注上限 200000 字符时单条就可能占数百 KB；`AttachmentCountingTasksApi.cs:20-25` 还对整份结果再做一次 `with` 克隆。
2. 主列表每次刷新重建全部行。`TasksViewModel.cs:892-893`（0.18.12）`Items.Clear()` 后逐条 `new TaskListItemViewModel`，而行类型不可观察、每个 getter 都重新做本地化与格式化。触发条件：保存、完成、归档、切换筛选或排序。出错原因：O(N) 个新对象与 N+1 次集合通知，容器、选择和滚动位置全部丢失，保存一次就要重建整张表；语言切换走 `RebuildListTitles` 时同样整表重建。
3. 整表恢复逐条发变更。`TaskTransferService.cs:24-30`（0.18.12）对恢复的每一行调用 `changed(new(...))`，上限 10 万行。触发条件：从备份恢复。出错原因：速览、主列表与浮窗各自收到 N 次通知并各做一次全表查询，恢复大备份时界面持续被重载占满。
4. 发布历史长度上限已触顶。`scripts/release_content_verify.py:113`（0.18.13）、`build/release-content.ps1:160` 与运行时校验 `ReleaseManifest.cs:80` 都把整段历史的长度上限设为 20000 字符；59 个版本后最长调用已到 19959，新增任何说明都会超限——打包脚本与程序内清单校验会先后拒绝这份清单。

**改动位置**

1. `src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskRequests.cs:17-29`：`TaskQuery` 新增 `Limit`（0 表示不限）、`Offset`、`IncludeNotes`（默认 `true`，导出与详情行为不变），新增 `TaskCountsDto(Total, Open)`；`:44` 新增 `ITasksApi.CountAsync`。
2. `src/Modules/Tasks/Mosaic.Tasks.Persistence.Contracts/ITaskRepository.cs`：新增 `QueryCountsAsync`（同筛选与归档范围计数，忽略分页）。
3. `src/Modules/Storage/Mosaic.Tasks.Sqlite/Internal/SqliteTaskRepository.cs:18-51`：`QueryAsync` 按 `IncludeNotes` 选择列、按 `Limit` 追加 `LIMIT $limit OFFSET $offset`；新增 `QueryCountsAsync`（`COUNT(*)` 与未完成 `SUM`）；`:53-67` 抽出 `Where`；`:141-145` 的 `Map` 在跳过备注时填空串。
4. `src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskService.cs:85-86`、`GuardedTasksApi.cs:28-34`（`ValidQuery` 同时校验分页参数）、`AttachmentCountingTasksApi.cs:28-29`：计数路径贯通，附件装饰器只透传。
5. `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekViewFactory.cs:351-359`：速览取 `Limit = MaxItems`、`IncludeNotes = false` 的一页，再用一次 `CountAsync` 取总数与未完成数，计数失败时回退为当前页统计；`:379-380` 的摘要与「更多」提示改用总数。
6. `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TasksViewModel.cs:286-288`：主列表查询不再携带备注（编辑器与详情仍走 `GetAsync`）；`:893` 调用新的 `ApplyItems`（`:907-940`），按 Id 复用行、先删已消失的行、再按目标顺序 `Move`/`Insert`，移动超过 64 次时改为一次重建以避免 O(n²)；`:942-948` 的 `IndexOf` 用引用比较；`:1202-1206` 的语言切换改为原地 `RefreshLocalizedText`。
7. `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskListItemViewModel.cs:8-82`：改为可观察行，`Update`（`:38-46`）只在数据或未命名标题变化时通知，`RefreshLocalizedText`（`:48-54`）只刷新展示串，不再重建集合。
8. `src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskChanges.cs:2-3`：新增 `TaskChangeKind.Replaced`（`TaskId` 为空、`Revision` 为 0，表示整表重载）；`TaskTransferService.cs:33-36` 在替换成功后只发一次该通知；`TaskFloatingContentFactory.cs:68` 的订阅改为同时响应 `Replaced`。
9. 测试：`RepositoryContractTests.cs:16` 新增分页/投影/计数用例并对 SQLite 与内存仓储各跑一遍；`PresenceTests.cs:433` 断言速览只取一页、不带备注并读取聚合计数；`PresentationTests.cs:852` 断言刷新复用同一行对象、只更新变化字段且列表查询不带备注；`TaskTransferTests.cs:129` 改为断言单次 `Replaced`。各 `ITasksApi` 测试替身补齐 `CountAsync`。
10. 发布工具与元数据：`scripts/release_content_verify.py:113-115`、`build/release-content.ps1:160` 与 `ReleaseManifest.cs:80-83` 的上限统一放宽到 40000（对应上限用例改为 40001 字符）；`build/public-api.txt` 登记 `Mosaic.Tasks.Contracts.TaskCountsDto`；`Directory.Build.props:12` 升到 0.18.14；`build/release-content.json` 与 `build/published-release-index.json` 追加第 60 条；`build/release-manifest.json`、`docs/releases/v0.18.14.md` 由校验脚本生成。

**迁移逻辑与验收项**

- 迁移逻辑：不改数据库 schema、不改设置键、不改模块装配，无需数据迁移。`TaskQuery` 三个新属性都有保持旧行为的默认值（`Limit = 0`、`Offset = 0`、`IncludeNotes = true`），导出（`TaskTransferService.cs:16`）继续带备注；已发布历史与索引只追加，不改旧条目。
- 对已有安装的影响：升级后任务、附件、设置与 `task-peek-filter.txt` 均原样读取；主列表与速览的筛选、排序、空态与计数口径与 0.18.13 一致；列表 DTO 的 `Notes` 变为空串只影响列表视图，编辑器与详情仍通过 `GetAsync` 取得完整备注。
- 验收项：
  1. 速览一次加载只发 `Limit = MaxItems`、`IncludeNotes = false` 的列表查询，另发一次计数查询；摘要按计数显示（新增用例）。
  2. 主列表刷新后同一任务仍是同一个行对象、只收到变化字段的通知；查询不含备注（新增用例）。
  3. 整表恢复只发一次 `Replaced`（改后用例），浮窗按该通知重载。
  4. SQLite 与内存仓储在分页、投影、计数三处行为一致（新增契约用例双向执行）。
  5. 全量 492 项测试仅剩发布提交前的两项一致性检查失败（提交后通过）。
- 回滚方式：回退本次提交即可；无数据迁移需要撤销，旧版本读取的数据与设置不受影响。发布工具的上限放宽只影响构建期校验，不进入安装包。

## 0.19.0：任务附件同步（服务端 `/v1/attachments` + 客户端上传镜像与下载落盘）（2026-11-24）

本轮把「账号备份」从设置与任务扩展到**任务附件**：服务端新增 `attachments` 表（迁移 3）与 `/v1/attachments` 四个端点，客户端在既有「上传备份 / 下载恢复」两条显式路径上增加附件镜像与落盘，仍由用户点按钮触发，没有任何后台静默双向覆盖。设置键新增 `sync.includeAttachments`（默认开），账号页给出复选框与说明；关闭时备份只含任务本身，恢复会清空本地附件记录并保留磁盘文件（详见下方迁移节）。数据结构变化仅限服务端新增一张表；桌面端本地库 schema 未变，安装布局未变。

**根因位置（按问题顺序）**

1. 附件在传输层没有通道。`ITaskTransferApi` 原只有任务与设置两个方向的导出/替换（`src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskTransferContracts.cs:16`，HEAD），`TaskTransferService` 也只按 `ITaskRepository` 组装任务 DTO。触发条件：用户点「上传备份」。出错原因：备份体只能带 `TaskItemDto`，附件元数据与二进制既无处导出也无处导入；即便将来加上，也没有可靠的时间戳与哈希可用于去重和校验。
2. 同步服务只搬运设置与任务。`SyncService.UploadAsync` 在 `Raise()` 之后直接返回 `BackupUploadResult`（`src/Modules/Sync/Mosaic.Sync.Core/Internal/SyncService.cs:137`，HEAD 前），`DownloadAsync` 在 `ReplaceAllAsync` 之后就结束。触发条件：同一账号在两台机器间迁移。出错原因：任务行恢复了、附件行随 `DELETE FROM tasks` 的级联一起消失，磁盘上却留着旧文件，形成「有文件无记录」的孤儿；反向则是「有记录无文件」，打开附件直接失败。
3. 恢复时附件行的级联语义没被显式处理。`task_attachments` 对 `tasks` 声明 `ON DELETE CASCADE`，`ReplaceAllAsync` 在一个事务里清空 `tasks` 表。触发条件：下载恢复。出错原因：附件行随父行静默清空，若不先按备份清单预删、事后按备份逐件 `ImportAttachmentAsync` 回填并保留原 `Id`/`AddedAtUtc`，任务与附件的关系就会整体丢失；只补「新出现的」附件则会残留上一次备份里的旧附件。
4. 服务端没有附件存储。`server/src/Mosaic.Server/Data/Schema.cs` 原只有用户、设备、会话、备份三张表（迁移 1–2），`ServerApplication.cs` 没有附件端点。触发条件：客户端要上传附件。出错原因：没有表就没有权威清单，没有目录约定就没有落盘位置；配额与单件上限也无法在服务端约束，客户端单方面限制等于没有限制。
5. 服务端全局请求体上限 16 MiB 会挡住正常的附件。`MOSAIC_MAX_BODY_BYTES` 默认 16777216 施加在整条管道上。触发条件：上传大于 16 MiB 的附件。出错原因：`PUT /v1/attachments/{id}` 会在读 body 之前被框架以 413 拒绝，与「单件 50 MiB」的承诺矛盾。
6. 附件开关没有默认值与本地化说明。`SyncSettingsSource` 原只暴露 `sync.serverUrl`/`sync.deviceId`/`sync.lastUpload` 等键（`src/Modules/Sync/Mosaic.Sync.Core/Internal/SyncSettingsSource.cs:23`，HEAD 前）。触发条件：新安装用户首次上传。出错原因：没有键就没有可持久化的开关；缺说明文案则用户不知道关闭后会遗留孤儿文件。

**改动位置（逐文件、类型与方法）**

服务端（`server/`，独立版本 0.1.1 → 0.1.2）：

1. `server/src/Mosaic.Server/Attachments/AttachmentPaths.cs`（新增）：`class AttachmentPaths:9` 定义磁盘布局 `<数据目录>/attachments/<userId:N>/<attachmentId:N><ext>`；`MaxFileNameCharacters:11`、`Root:13`、`UserDirectory:16`、`FileFor:19`、`NormalizeUserId:23`、`NormalizeAttachmentId:34`、`Extension:48`（仅接受 2–17 位字母数字、白名单扩展，否则空串）、`TryDeleteUserDirectory:71`。
2. `server/src/Mosaic.Server/Attachments/AttachmentService.cs`（新增）：`class AttachmentService:31`（依赖 `Database`、`ServerOptions`、`TimeProvider`）；`ListAsync:39`、`SaveAsync:61`（校验 `taskId`/`fileName`/`sha256` → 已存在同哈希返回 duplicate → 复制到临时文件并同步计算 SHA-256 → 事务写行 → 提交后 `File.Move` 原子落盘 → 失败回滚行并删临时文件）、`OpenAsync:246`、`DeleteAsync:274`、`CopyAndHashAsync:313`、`MapItem:354`、`NormalizeAttachmentId:364`、`NormalizeTaskId:374`、`ValidateFileName:384`、`ValidateHash:409`、`TryDelete:425`。
3. `server/src/Mosaic.Server/Attachments/AttachmentEndpoints.cs`（新增）：`class AttachmentEndpoints:7`、`MapAttachmentEndpoints:9` 注册 `GET /v1/attachments`、`PUT /v1/attachments/{attachmentId}`、`GET /v1/attachments/{attachmentId}`、`DELETE /v1/attachments/{attachmentId}`；PUT 先提升 `IHttpMaxRequestBodySizeFeature` 到单件上限再读流，校验 `X-Mosaic-Sha256` 头。
4. `server/src/Mosaic.Server/Data/Schema.cs:168-182`（新增迁移 3）：`attachments(id,user_id,task_id,file_name,size_bytes,sha256,created_at,updated_at)` 与索引 `ix_attachments_user(user_id, task_id)`。
5. `server/src/Mosaic.Server/Data/DatabaseMigrator.cs:12`：迁移清单追加 `(3, "attachments", Schema.Migration3)`。
6. `server/src/Mosaic.Server/ServerApplication.cs:4`（using）、`:90`（`AddSingleton<AttachmentService>()`）、`:128`（`app.MapAttachmentEndpoints()`）。
7. `server/src/Mosaic.Server/ServerOptions.cs:44`（`AttachmentQuotaBytes`，1 GiB）、`:47`（`AttachmentMaxBytes`，50 MiB）、`:102-103`（`MOSAIC_ATTACHMENT_QUOTA_BYTES` / `MOSAIC_ATTACHMENT_MAX_BYTES`）、`:188`（配额不得小于单件上限的校验）。
8. `server/src/Mosaic.Server/Auth/SessionService.cs:2`（using）、`:431-433`：删除账户时行随用户级联删除，目录在事务提交后由 `AttachmentPaths.TryDeleteUserDirectory` 清理，失败只记日志不影响删除结果。
9. `server/tests/Mosaic.Server.Tests/AttachmentTests.cs`（新增，275 行）：覆盖清单/写序/重复上传/哈希不符/越界 ID/超限/配额/下载/删除/账户删除清理共 9 组；`server/tests/Mosaic.Server.Tests/LoggingAndMigrationTests.cs` 迁移断言改为 3。

桌面端：

10. `src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskTransferContracts.cs`：`ITaskTransferApi.ExportAttachmentsAsync:16`、`OpenAttachmentAsync:19`、`ImportAttachmentAsync:22`、`DeleteAttachmentAsync:25`、`RemoveAttachmentsNotInAsync:28`、`SweepOrphanAttachmentsAsync:31`；`record TaskAttachmentExportDto:35`（附件 DTO + 内容流 + `Sha256`）。`:8` 注释写明这些成员只服务于用户触发的备份上传/下载。
11. `src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskTransferService.cs`：构造函数追加 `ITaskAttachmentBlobStore? blobs:18`；`ExportAttachmentsAsync:45`、`OpenAttachmentAsync:52`、`ImportAttachmentAsync:77`（失败时回删已落盘 blob `:98`）、`DeleteAttachmentAsync:102`（先删行 `:109` 再删 blob `:111`）、`RemoveAttachmentsNotInAsync:115`、`SweepOrphanAttachmentsAsync:133-139`。
12. `src/Modules/Tasks/Mosaic.Tasks.Core/Internal/FileSystemTaskAttachmentBlobStore.cs:82` 起新增：`SweepAsync:116`（只清「Guid 目录 / Guid 文件名」布局与无主文件）、`DeleteAsync(taskId, attachmentId, fileName, storedPathHint):159`。
13. `src/Modules/Tasks/Mosaic.Tasks.Core/Internal/GuardedTaskTransferApi.cs:35-60`：六个附件方法加模块闸门，`ImportAttachmentAsync`/`DeleteAttachmentAsync` 成功后发任务变更通知。
14. `src/Modules/Tasks/Mosaic.Tasks.Persistence.Contracts/ITaskAttachmentBlobStore.cs`：`DeleteAsync:22`、`ImportAsync:28`、`SweepAsync:35`；`ITaskAttachmentRepository.cs:10` 增加 `ListAllAsync`；`src/Modules/Storage/Mosaic.Tasks.Sqlite/Internal/SqliteTaskAttachmentRepository.cs:23-31`（`ListAllAsync`，`SELECT ... ORDER BY added_at, id`）。
15. `src/Modules/Tasks/Mosaic.Tasks.Core/TasksModule.cs`：构造依赖追加 `ITaskAttachmentRepository? AttachmentRepository:16` 与 `string? DefaultAttachmentRoot:17`；`_attachmentBlobs:26`；`Transfer` 传入 blobs `:41`；能力清单补 `tasks.attachments@1:61`；`Attachments:69`；设置应用时同步 `tasks.attachments.storagePath:82-84`；清理钩子 `:91`、`:104`。
16. `src/Modules/Sync/Mosaic.Sync.Contracts/SyncContracts.cs`：`SyncErrorCodes.AttachmentHashMismatch:20`、`AttachmentsInvalid:21`；`BackupUploadResult` 增 `AttachmentCount`/`DeletedAttachmentCount:39`；`RestoreSummary` 增 `AttachmentCount:41`；`RemoteAttachmentDto:44`、`AttachmentManifestDto:47-48`；`ISyncTransport` 四个附件方法 `:69`、`:72`、`:76`、`:80`。
17. `src/Modules/Sync/Mosaic.Sync.Core/Internal/SyncService.cs`：`_lastRestore:46`；上传侧 `if (_options.IncludeAttachments)` 调用 `UploadAttachmentsAsync:137-139`；下载侧开关 `:164`、预删 `:173`、错误聚合 `:189`、临时目录 `Path.GetTempPath()/mosaic-sync:192`、逐件下载 `:200`、`ImportAttachmentAsync(new(item.Id, item.TaskId, item.FileName, item.SizeBytes, item.AddedAtUtc), temp, ...):206`、`SweepOrphanAttachmentsAsync:216`（注释明确只清 Guid 布局）、写 `_lastRestore` 后 `Raise():223-225`、聚合错误返回 `:227-231`；`UploadAttachmentsAsync:381`、同哈希跳过 `:396`、`SameAttachment:419`、`TagAttachmentError:424`、`TryDelete:428`。
18. `src/Modules/Sync/Mosaic.Sync.Core/Internal/SyncSettingsSource.cs`：默认 `IncludeAttachments = true:9`、读取 `:15`、常量 `IncludeAttachments = "sync.includeAttachments":23`、定义 `:37-40`（显示名 `Settings.Setting.SyncIncludeAttachments`，说明含「关闭后备份只包含任务本身，下载恢复会清空本地附件记录并保留磁盘文件」）。
19. `src/Modules/Sync/Mosaic.Sync.Http/HttpSyncTransport.cs`：`AttachmentsPath = "v1/attachments":20`、`AttachmentRequestTimeout = 30min:25`、`ListAttachmentsAsync:102`、`UploadAttachmentAsync:105`（`X-Mosaic-Sha256:122`、独立 deadline `:125`）、`DownloadAttachmentAsync:140`（校验失败在 `:178` 返回 `SYNC_ATTACHMENT_HASH_MISMATCH` 并丢弃临时文件）、`DeleteAttachmentAsync:197`、`ReadManifest:223`、`AttachmentEndpoint:248-250`。
20. `src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs`：`SyncIncludeAttachmentsId = "sync.includeAttachments":32`、`_includeAttachments = true:69`、属性 `IncludeAttachments:329-334`、保存 `:746`、读取 `:797`；`src/Modules/Auth/Mosaic.Auth.Wpf/Views/AccountView.xaml:242-244` 复选框（AutomationId `SyncIncludeAttachmentsToggle`）与说明文本。
21. 本地化：`src/Modules/Auth/Mosaic.Auth.Wpf/Localization/zh.xaml:59-60`、`en.xaml:59-60`（开关与说明）、`en.xaml:67-69`（LastRestore/Uploaded/Restored 三段带附件数）；`src/Modules/Settings/Mosaic.Settings.Wpf/Localization/zh.xaml:279-280`、`en.xaml:279-280`（`Settings.Setting.SyncIncludeAttachments`）。
22. 架构契约：`build/public-api.txt` 追加 `AttachmentManifestDto`、`RemoteAttachmentDto`、`TaskAttachmentExportDto`（架构测试的索引核对依赖此文件）。
23. 测试：`tests/Mosaic.Sync.Core.Tests/Fakes.cs`（假传输补四个附件方法 `:162-212`、`AddRemoteAttachment:128-133`；`FakeTaskTransferApi.ReplaceAllAsync:230-241` 按真实级联语义清空附件行与 blob `:238-239`）；`tests/Mosaic.Sync.Core.Tests/SyncServiceTests.cs` 新增 7 个用例（+168 行）；`tests/Mosaic.Tasks.Core.Tests/` 的 `AttachmentBlobStoreTests`、`AttachmentCleanupTests`、`AttachmentCountingApiTests`、`TaskDeleteAttachmentCleanupTests` 少量加法（新接口成员）。
24. 发布元数据：`Directory.Build.props:12` 升 `0.19.0`；`server/Directory.Build.props:12` 升 `0.1.2`；`build/release-content.json` 追加 `mosaic-0.19.0` 并改 `currentReleaseId`；`build/published-release-index.json` 追加第 60 条；`build/release-manifest.json` 与 `docs/releases/v0.19.0.md` 由 `scripts/release_content_verify.py` 生成；`server/README.md:1`、`:389`（测试计数 82 → 91）、`:397` 同步版本；`docs/server-implementation-status.md` 增补「任务附件」一节并校正 0.1.2 段状态；`docs/auth-sync-server-plan.md` §4.5 措辞校正（`ListAllAsync` → `ExportAttachmentsAsync` + 同哈希跳过）。

**迁移逻辑与对已有安装的影响**

1. 服务端迁移 3：`DatabaseMigrator` 启动时对既有库执行 `CREATE TABLE IF NOT EXISTS attachments(...)` 与索引创建，纯增量、无数据回填；旧库（迁移 1–2）升级后 `schema_version` 变为 3，备份表与用户表内容不变。验收：用迁移 2 的旧数据目录启动，日志出现 `applied migration 3 (attachments)`，`attachments` 表存在且 `SELECT COUNT(*)` 为 0，`/v1/backups` 仍可正常上传下载。
2. 附件目录：首次上传时按需创建 `<数据目录>/attachments/<userId:N>/`，旧安装不存在该目录不影响启动；`AttachmentPaths.Root` 使用既有 `ServerOptions.DataDirectory`，不需要新配置项。验收：上传一件附件后目录内出现 `<attachmentId:N><ext>`，删除后文件消失、目录可空。
3. 桌面端设置键迁移：`sync.includeAttachments` 走既有设置库，缺省读取为 `true`（`SyncSettingsSource:9`），旧安装无需迁移动作，首次打开账号页即显示为勾选。验收：升级后不点开关直接上传，`UploadAsync` 调用 `ListAttachmentsAsync`（`ListAttachmentRequests` 计数 +1）。
4. 关闭开关的后果（必须让用户知道）：备份体只含任务与设置；下载恢复时 `ReplaceAllAsync` 的级联清空会删掉本地全部附件记录，而磁盘文件不会被删除，也不会被 `SweepOrphanAttachmentsAsync` 自动回收（sweep 只清 Guid 布局且只在附件同步开启时执行）。本地化说明已写在 `Settings.Wpf/Localization/{zh,en}.xaml:280`，恢复摘要的附件数为 0。验收：关闭开关 → 下载恢复 → 任务恢复、`task_attachments` 为空、附件目录内旧文件仍在；重新打开开关再次上传，附件按哈希重新镜像。手工清理方式：删除 `<附件存储目录>` 下对应任务的文件，或调用任务模块的 `SweepOrphanAttachmentsAsync`。
5. 对已有安装的语言与布局：无新增窗口、无改名的设置键、无改动的任务数据目录；`tasks.attachments@1` 只是能力清单新增项，旧插件不受影响。验收：升级后打开任务页与账号页无异常，附件按钮行为与此前一致。

**验证与未做项**

- 已跑：`dotnet test tests/Mosaic.Sync.Core.Tests/Mosaic.Sync.Core.Tests.csproj` → 总计 21，失败 0，成功 21（含新增 7 个附件用例）；`dotnet test server/tests/Mosaic.Server.Tests/Mosaic.Server.Tests.csproj` → 91/91；`dotnet test tests/Mosaic.Tasks.Core.Tests/Mosaic.Tasks.Core.Tests.csproj` 定向通过；`dotnet test tests/Mosaic.Architecture.Tests` 通过（公共 API 索引与 SQL 关键字规则）；`dotnet test tests/Mosaic.ReleaseSummary.Tests/Mosaic.ReleaseSummary.Tests.csproj` → 总计 21，失败 0，成功 21（含新增 3 个裁剪用例与 1 个已发布回执用例）；`python scripts/release_content_verify.py` → `Release content verified: 0.19.0, mosaic-0.19.0, 61 indexed releases.`，并生成 `build/release-manifest.json` 与 `docs/releases/v0.19.0.md`。
- 未跑：全量 `dotnet test Mosaic.sln`（按用户要求不做每次全量；其中 `Mosaic.Desktop.SmokeTests.Floating_real_pointer_moves_resizes_clicks_and_restores_saved_bounds` 在 `FloatingDragTests.cs:38` 因真实指针/DPI 环境期望 56–64 实测 68，与本轮改动无关）；未做 MSI 安装后的真机附件往返（无现成测试账号与两台设备环境），由发布前手工验收项覆盖。
- 未做：附件断点续传与服务端去重（跨用户共享同一 blob）、附件按任务增量同步（当前为整清单镜像）、附件在速览/详情里的预览仍是原有能力，未接入同步状态提示。

**同轮修复：更新简要超过任务备注上限（发布链门禁阻塞）**

**根因位置（按问题顺序）**

1. 更新简要正文没有长度控制。`ReleaseSummaryComposer.Compose` 把升级范围内所有版本逐条拼接成一段正文（`src/Modules/ReleaseSummary/Mosaic.ReleaseSummary/ReleaseSummaryComposer.cs:29-43`，HEAD 前）。触发条件：用户回执停留在较早版本，或从 0.1.0 一路升级。出错原因：正文被运行时写成任务备注，而备注受 `TaskConstraints.NotesMaxCharacters = 20_000`（`src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskConstraints.cs:14`，按 rune 计数）约束，`TaskService.CreateAsync` 在写入前校验（`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskService.cs:27`）。实测 0.1.0 → 0.18.14 已占 20,145 个 rune，追加 0.19.0 后为 21,062（含本条修复自身的发布正文与 0.18.14 的三条条目），必然被拒绝；发布门禁要求「每个起点都能完整生成」，因此整条发布链被卡住。
2. 清单校验把上限写成魔数。`ReleaseManifest.Validate:81` 使用字面量 `200` / `20000`，与任务契约常量重复。触发条件：任务契约调整标题或备注上限。出错原因：契约变化后发布清单校验与运行时任务校验各说各话，门禁漂移。
3. 两个构建门禁按「整段拼接」只算一次。`build/release-content.ps1:157-163`（HEAD 前）与 `scripts/release_content_verify.py:110-114`（HEAD 前）对每个起点计算一次完整正文并与 20000 比较，没有与运行时一致的降级路径。触发条件：链上累计正文超限。出错原因：门禁与运行时必须是同一套规则，否则要么运行时拒绝而门禁放行（本次），要么门禁拒绝而运行时能生成。
4. 裁剪后的正文承诺「见 docs/releases/ 下对应版本的发布说明」，而 `v0.2.0.md` 与 `v0.4.0.md` 不存在：0.2.0 的内容写在 `v0.1.0.md`（标题为「Mosaic 0.1.0 / 0.2.0」）里，0.4.0 没有任何发布说明文件。触发条件：裁剪区间覆盖这两个版本（真实链 0.1.0 → 0.19.0 的裁剪区间是 0.1.0 至 0.3.0）。出错原因：指引行指向不存在的载体，等于把缺失隐蔽地推给用户。

**改动位置（逐文件、类型与方法）**

1. `src/Modules/ReleaseSummary/Mosaic.ReleaseSummary/ReleaseSummaryComposer.cs`：`Compose:47-58` 改为「逐起点尝试」循环——从最旧版本开始裁掉，直到正文不超过 `TaskConstraints.NotesMaxCharacters` 才返回；`适用范围` 起点取裁剪后区间的前一个版本（`previousVersion:52`），裁剪标记为 `trimmed:53`；只有当前版本自身就超限时返回 `Failure`（`:58`）。新增 `ComposeText:61-89` 承载原拼接逻辑，并在裁剪时于 `适用范围` 之后追加一行指引（`:70-73`，单版本无括号、多版本括号列区间）。
2. `src/Modules/ReleaseSummary/Mosaic.ReleaseSummary/ReleaseManifest.cs`：`Validate:81-82` 的 `200` / `20000` 换成 `TaskConstraints.TitleMaxCharacters` / `TaskConstraints.NotesMaxCharacters`；`:7` 新增 `using Mosaic.Tasks.Contracts;`。逐起点 `Compose` 的校验循环（`:74-84`）不变，裁剪逻辑使其自动通过。
3. `build/release-content.ps1`：`Get-SummaryText:41-67` 增加 `$Trimmed` 参数与指引行（`:45-52`）；`Get-RuneCount` 前的整段检查改为 `:167-188`——标题上限单独检查，再对每个 `$from` 用内层 `for ($start …)` 裁剪（空正文检查、`$rangePrevious`、`$trimmed`、`(Get-RuneCount $candidate) -le 20000` 即 `break`），全部起点都无解才 `throw`。
4. `scripts/release_content_verify.py`：`summary_text:35-53` 增加 `trimmed` 参数与指引行；新增 `compose_range:115-129` 镜像运行时裁剪，`for frm in range(len(chain))` 循环（`:131-132`）替换原整段检查。
5. `tests/Mosaic.ReleaseSummary.Tests/ReleaseSummaryTests.cs`（新增 3 个用例，`:91-138`）：`Overlong_range_drops_the_oldest_release_and_points_at_release_notes`（裁 1 个版本、指引行位置与内容）、`Overlong_range_with_multiple_trimmed_releases_lists_the_trimmed_span`（裁 2 个版本、区间措辞）、`Single_release_body_over_the_notes_limit_fails_instead_of_truncating_text`（单版本超限必须失败而不是截断）；`:1` 新增 `using System.Text;`。
6. `docs/releases/v0.2.0.md`、`docs/releases/v0.4.0.md`（新增）：按运行时生成格式（`# Mosaic <版本>` + `Mosaic <版本> · 更新日期` + `适用范围` + 分类编号列表）从 `build/release-content.json` 的冻结条目写出；`v0.2.0.md` 末尾指向 `v0.1.0.md` 以保留首个版本的完整说明。至此 61 个已发布版本均有 `docs/releases/v<版本>.md`。
7. `docs/releases/changelog.md`：新增 0.19.0 条目，含附件同步与本次裁剪机制。

**对已有安装的影响与验收项**

1. 无数据迁移、无设置键变化、无安装布局变化：裁剪只影响生成的更新简要文本，任务与附件数据不受影响。
2. 升级用户的可见变化：回执停留在 0.4.0 及以前时，最旧的一段明细被裁掉，正文改为「适用范围：0.4.0 → 0.19.0」+ 一行指引；更早的回执裁剪区间相应缩短。验收（真实链复算，61 个 release，含 0.18.14 与 0.19.0 的全部条目）：未裁剪全链 21,062 rune；`frm=0` → 裁掉 0.1.0–0.4.0 后 19,964 rune、`适用范围：0.4.0 → 0.19.0`、指引行「更早版本（0.1.0 至 0.4.0）的变更见 docs/releases/ 下对应版本的发布说明。」；`frm=1`/`frm=2`/`frm=3`（回执 0.2.0 / 0.3.0 / 0.4.0）→ 19,964 / 19,964 / 19,956 rune，指引区间分别为 0.2.0–0.4.0、0.3.0–0.4.0、单个 0.4.0；`frm=4`（回执 0.5.0）→ 19,912 rune、`适用范围：0.4.0 → 0.19.0`、不裁剪；`frm=59`（回执 0.18.13）→ 1,160 rune；`frm=60`（回执 0.18.14）→ 889 rune、首次使用 → 886 rune。
3. 单版本正文超过 20000 rune 时仍明确失败（错误消息含「备注上限」），不做静默截断；该分支与「空正文」失败分支一样保留原「待重试」语义。
4. 候选正文恰好落在上限内时不裁剪，正文与 0.18.13 完全一致——验收：`Mosaic.ReleaseSummary.Tests` 的 `Shipped_manifest_announces_every_version_after_the_last_receipt` 与 `Committed_release_source_matches_program_and_frozen_index` 用真实 61 条发布源跑通。
5. 发布说明完整性验收：`docs/releases/` 对 61 个 release 逐一存在 `v<版本>.md`（Python 核对输出 `missing notes files []`），指引行的承诺可核对为真。

- 回滚方式：`git revert <本次提交>` 并回退 `Directory.Build.props:12` 至 `0.18.13`、`server/Directory.Build.props:12` 至 `0.1.1`；已升级的服务端库可直接继续运行（迁移 3 只新增表，旧代码不读取它），如需彻底清理执行 `DROP TABLE attachments; DELETE FROM schema_migrations WHERE version = 3;`（先备份数据目录）。客户端回滚后 `sync.includeAttachments` 键留在设置库中不被读取，无副作用；附件目录与文件保留，可手工删除。

**同轮修复：已发布 0.18.14 的回执从发布链中丢失**

**根因位置（按问题顺序）**

1. 开发 0.19.0 时把链尾的 `mosaic-0.18.14` 整条替换成了 `mosaic-0.19.0`，并把 `mosaic-0.19.0.previousReleaseId` 指回 `mosaic-0.18.13`；`build/published-release-index.json` 里也没有 0.18.14 的索引条目。触发条件：用户装过正式发布的 0.18.14（用 `msiexec /a` 提取该 MSI 内的 `Programs/Mosaic/release-manifest.json`，实测 `current = mosaic-0.18.14`、共 60 条、`previousReleaseId = mosaic-0.18.13`），随后升级到 0.19.0。出错原因：`ReleaseSummaryComposer.Compose:20-23` 要求每个公告回执都在 `byId` 中命中，未命中即返回「无法识别已有公告回执 …，已保留待重试」；0.18.14 已从清单消失，用户永远收不到 0.19.0 的更新简要，回执卡在 0.18.14 且每次启动重试。
2. 该状态同时违反发布链的自洽规则：`build/published-release-index.json` 冻结的是「已经真实发布」的集合，而 0.18.14 在索引之外已经发布了正式 Release 与 MSI，链上却找不到它，裁剪指引与发布说明也随之缺了一环。

**改动位置（逐文件、类型与方法）**

1. `build/release-content.json`：在 `mosaic-0.18.13` 之后插回 `mosaic-0.18.14` 条目（`added` 0 条、`improved` 2 条、`fixed` 1 条，`releasedOn` 2026-11-24，内容取自该已发布 MSI 内清单，重算内容指纹 `41480A27905B5217AEBBA8B6DF4EE36B594C9D5B9E336DFA48D54EEADC6B79C5`）；`mosaic-0.19.0.previousReleaseId` 改为 `mosaic-0.18.14`，其 `contentSha256` 重算为 `977CF1C8405BCBA945B692083402A62F5D65DC70DB47F1680B35A525FE41620F`。
2. `build/published-release-index.json`：在 0.18.13 之后插入 `mosaic-0.18.14` 索引条目（60 → 61 条），0.19.0 条目的 `contentSha256` 同步为新指纹（该条目尚未提交，索引不可变规则不适用）。
3. `docs/releases/v0.18.14.md`（新增）：补齐这个已发布版本的发布说明，格式与运行时「首次使用」生成一致（`# Mosaic 0.18.14` + `Mosaic 0.18.14 · 更新日期 2026-11-24` + `适用范围：首次使用 → 0.18.14` + 三个分类的编号列表），与 GitHub Release 正文一致。
4. `tests/Mosaic.ReleaseSummary.Tests/ReleaseSummaryTests.cs:190-213`（新增用例 `Every_published_release_receipt_is_recognized_by_the_shipped_manifest`）：遍历 `build/published-release-index.json` 的每一个回执，断言 `Compose` 成功、非当前版本必须生成非空正文，并显式断言索引中存在 `mosaic-0.18.14`；任何已发布回执再次丢失都会在这里先失败。
5. `docs/releases/changelog.md:5`：0.19.0 条目结尾「包含 0.18.13 的全部内容。」改为「包含 0.18.14 的全部内容。」。

**对已有安装的影响与验收项**

1. 装过 0.18.14 的用户升级到 0.19.0 后回执被识别，收到只含 0.19.0 的更新简要（889 rune）；装过 0.18.13 的用户收到覆盖 0.18.14 + 0.19.0 的正文（1,160 rune）；更早的回执按上一节的裁剪规则生成。
2. 无数据迁移、无设置键变化、无安装布局变化；已有 `Data/`、`Config/`、任务、附件与回执全部原样保留，只是清单能重新解析。
3. 验收命令：`python scripts/release_content_verify.py` → `Release content verified: 0.19.0, mosaic-0.19.0, 61 indexed releases.`；`dotnet test tests/Mosaic.ReleaseSummary.Tests/Mosaic.ReleaseSummary.Tests.csproj` → 21/21（含新用例）。
4. 回滚方式：回退这两个 json 文件与测试到本提交前，0.18.14 的回执会重新变成「无法识别」，因此除非同时回退 0.18.14 的发布，否则不要单独回滚此项。

## 0.18.12：速览行复用、筛选同步与会话行本地化（2026-11-24）

本轮处理速览与账户页的八项审查结论：第 3、4、5、7、8 项落码修复，第 1 项（同一「分离到小窗」动作三种入口三种结果）与第 2 项（附件计数失败静默落空、任务夹徽标消失）按指示不改；第 6 项复核后确认已由 0.18.11 的伴随视图架构重构消除（`PresenceSurface.cs` 已无 `LoadFilter`/`filterPreferencePath`，构造函数为 4 参数），本轮无代码改动。数据结构、设置键与安装布局均未变。

**根因位置（按问题顺序）**

1. 速览每次刷新都重建整行，焦点恢复靠猜。原 `UpdateView` 无条件执行 `_view.Rows.ItemsSource = _items.Take(...).Select(item => new TaskPeekRow(...)).ToArray()`（`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekViewFactory.cs:312`，HEAD），随后在 `BeginInvoke` 里按 Id 从 `Items.Cast<TaskPeekRow>()` 找到行、再从容器里按按钮名找控件（`:328-336`，HEAD）。触发条件：切换筛选、收到任务变更通知、完成操作后的重新读取。出错原因：`ItemsSource` 换成新集合必然销毁全部容器，滚动位置与键盘焦点一起丢失；`BeginInvoke` 排队期间若又有一次刷新，旧行已不在 `Items` 里，查找落空后静默不回焦。行对象是 `record`（`:27`，HEAD），属性不可变，只能整行替换，无法只更新变化字段。
2. 筛选按钮的 `Checked` 同时承担用户点击与程序同步。`FilterClicked` 没有区分事件来源（`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs:21-27`，HEAD），而 `UpdateView` 直接写 `button.IsChecked = filter == _selectedFilter`（`TaskPeekViewFactory.cs:299`，HEAD）。触发条件：每次视图刷新同步选中态；键盘方向键在组内移动。出错原因：程序化赋值同样触发 `Checked`，被当成用户点击再走一遍 `SelectFilterAsync`（同值早退掩盖了大部分情况，但拖拽取消等副作用照常执行）；方向键切换只触发 `Checked` 不触发 `Click`，与鼠标路径行为不一致。
3. 关闭速览后，延迟回调仍会碰已释放的界面。关闭反馈的匿名 lambda 直接 `_feedbackCancellation?.Cancel()`（`TaskPeekViewFactory.cs:223`，HEAD），`ShowOperationError` 也先取消同一字段（`:388-390`，HEAD），而反馈计时器的 `finally` 已经 `Dispose()` 了它的 `CancellationTokenSource`；拖拽 lambda 每次无条件发 `InteractionChanged`（`:232`，HEAD），`CloseAsync` 也没有退订这些匿名处理器。触发条件：关闭速览后 6 秒反馈计时到点、完成操作返回、拖拽结束。出错原因：字段生命周期与 `Dispose` 未绑定，取消已释放的源抛 `ObjectDisposedException`；事件继续持有已关闭的 VM，关闭后仍可能触发界面操作。
4. 会话行硬编码中文（`src/Modules/Auth/Mosaic.Auth.Wpf/Views/AccountView.xaml:289-295`，HEAD）：`TargetNullValue=未知设备`、`TargetNullValue=未知平台`、`<Run Text=" · 最近活跃："/>`，语言切换处理只有 `OnPropertyChanged(string.Empty)`（`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs:1129`，HEAD）。触发条件：界面语言切到英文，或会话缺少设备名/平台名。出错原因：`TargetNullValue` 是编译期常量，切换语言不会重算；会话行绑定源是普通 `AuthSessionInfoDto`，`PropertyChanged(string.Empty)` 只刷新以 ViewModel 为源的绑定，行级 converter 不会再跑。
5. 会话列表的显示条件在三处重复推导：`ShowSessionList => HasSessions && !SessionsBusy`（`AccountViewModel.cs:347`，HEAD）、`SessionsBusy` 的 setter 通知（`:358`，HEAD）、`ReplaceSessions` 里的通知（`:589`，HEAD），而 XAML 只绑定 `ShowSessionsLoading` 与 `HasSessions`（`AccountView.xaml:274-277`），`ShowSessionList` 零引用。触发条件：刷新会话、空态与加载态切换。出错原因：同一状态由三处各自推导，新增状态时容易让列表与加载提示同时出现或同时消失；属性没有消费者，改动时容易被误当成有效契约。

**改动位置（逐文件、类型与方法）**

1. `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekViewFactory.cs`
   - `TaskPeekRow`（`:27-112`）：由不可变 `record` 改为可复用的 `sealed class`，字段改为可变私有属性；`Update(long, string, string, bool, long, Brush?)`（`:76-85`）只对变化的字段发通知，`RefreshText()`（`:87-93`）合并 `CompletionName`/`AccessibleName`/`AttachmentLabel` 三个本地化组合串；`IsBusy` setter 增加同值早退。
   - `_rowsById` 与 `_rows`（`:160-161`）：行身份缓存与当前列表快照。
   - `UpdateView()`（`:366-406`）：改为 `BuildRows()` + `SameRows()`，仅在行的数量或身份变化时重设 `ItemsSource` 并 `UpdateLayout()`，否则只对现有行调用 `RefreshText()`；焦点恢复改为同步执行，且仅在未关闭、未进入详情页时进行。
   - `BuildRows()`（`:408-436`）：加载中沿用旧行以保留容器；失败时清空缓存；按 Id 复用行并清理已消失的 Id。
   - `SameRows()`（`:438-444`）与 `RestoreFocus()`（`:446-456`）、`FocusFallback()`（`:458-466`）：按容器取行，优先恢复同名按钮，其次 `TaskCard`，都不可用时退回当前筛选按钮。
   - `CloseAsync()`（`:540-548`）：退订 `DismissFeedback` 与 `DragChanged`，并清空 `_view.CanDetach`。
   - `CancelFeedback()`（`:526-532`）只取消仍存活的通知源并立即置空；`ShowOperationError()`（`:513-523`）首行调用它；`DismissFeedback`（`:320-325`）与 `DragChanged`（`:313-318`）改为具名处理器，后者同值不再发 `InteractionChanged`。
   - `SubmitCompletionAsync()`（`:489-512`）的 `finally` 只移除 pending 与忙碌标记。
2. `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs`
   - `FilterClicked`（`:21-26`）增加 `_syncingFilters` 早退；新增 `SelectFilter(TaskFilter)`（`:28-37`）以程序方式同步三个单选按钮且不触发筛选请求；新字段 `_syncingFilters`（`:48`）。
3. `src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountConverters.cs`（已有文件，本轮新增两个转换器）
   - `LocalDateTimeConverter`（`:8-21`）：UTC 时间按本地时区输出 `yyyy-MM-dd HH:mm`（内容未变）。
   - `LocalizedFallbackConverter`（`:23-31`）：有文本用文本，否则按 `ResourceKey|默认文案` 取本地化值。
   - `LocalizedLabelConverter`（`:33-51`）：忽略绑定值，只渲染参数指向的本地化文案。
   - `CurrentSessionTagConverter`（`:53-62`）：仅当前会话渲染「本设备」标签，且随语言切换（内容未变）。
4. `src/Modules/Auth/Mosaic.Auth.Wpf/Views/AccountView.xaml`
   - `:11-14` 注册四个转换器；`:289-299` 的会话卡模板改为转换器组合，`未知设备`、`未知平台` 与「最近活跃」前缀全部来自资源键。
5. `src/Modules/Auth/Mosaic.Auth.Wpf/Localization/zh.xaml:104-106` 与 `en.xaml:75-77`
   - 新增 `Auth.Sessions.UnknownDevice`、`Auth.Sessions.UnknownPlatform`、`Auth.Sessions.LastSeen`。`LastSeen` 用 `xml:space="preserve"` 保留前导空格：XAML 会裁掉元素文本首尾空白，`&#32;` 同样被裁，不写该属性时渲染成「未知平台· 最近活跃：」。
6. `src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs`
   - 删除 `ShowSessionList` 及 `SessionsBusy` setter、`ReplaceSessions` 里的对应通知；`ReplaceSessions`（`:581-587`）只通知 `HasSessions` 与 `ShowSessionsLoading`。
   - `OnLanguageChanged`（`:1126-1130`）：在 `PropertyChanged(string.Empty)` 之后，会话非空时以 `ReplaceSessions([.. _sessions])` 重建行，使行级转换器重跑；服务地址输入框等用户草稿不受影响。
7. 测试
   - `tests/Mosaic.Wpf.Tests/PresenceTests.cs:389`：`Peek_reuses_row_containers_when_a_refresh_returns_the_same_items`，断言切换筛选后 `TaskCard` 实例与行对象不变、且查询序列恰为 `All → Open`。
   - `tests/Mosaic.Wpf.Tests/AccountViewModelTests.cs:549`：`Session_rows_follow_the_ui_language_for_missing_device_and_platform`，断言中文与英文下缺失设备/平台与「最近活跃」文案，并断言英文下不再出现「未知」。
   - `tests/Mosaic.Wpf.Tests/UiTestApplication.cs`（新增）：统一创建带 `Theme.xaml` 的进程级 `Application`，供在构造 `AccountView` 时解析 `StaticResource`。
   - `tests/Mosaic.Wpf.Tests/UiLocalizationCollection.cs`（新增）：语言状态是进程级全局量，把 `AccountViewModelTests`（`:21`）与 `TaskAttachmentLocalizationTests`（`:9`）放进同一个非并行集合；`AccountViewModelTests` 两处建视图前调用 `EnsureTheme()`（`:444`、`:564`）。
   - `tests/Mosaic.Wpf.Tests/PreviewCaptureTests.cs:28`：改用 `UiTestApplication.EnsureTheme()`，避免与其它测试重复创建 `Application`。
8. 发布元数据
   - `Directory.Build.props:12` 升到 0.18.12；`build/release-content.json` 新增 `mosaic-0.18.12` 并把 `currentReleaseId` 指向它；`build/published-release-index.json` 追加第 58 条；`build/release-manifest.json` 与 `docs/releases/v0.18.12.md` 由 `scripts/release_content_verify.py` 生成。

**迁移逻辑与验收项**

- 迁移逻辑：本次不改数据库 schema、不改设置键、不改模块装配、不改安装包布局，不需要数据迁移。已有安装升级后 `Data/`、`Config/` 与 `task-peek-filter.txt` 导入结果均保持不变；新版只改变视图层的行复用与文案来源。
- 验收项：
  1. 切换筛选后行的 `TaskCard` 容器与 `DataContext` 不变，查询只发一次（新增用例）。
  2. 完成操作或外部变更后列表按新数据增删行，键盘焦点回到同一行的同名按钮，找不到时退回 `TaskCard` 或当前筛选按钮。
  3. 关闭速览后 6 秒内、以及完成后返回时，不出现 `ObjectDisposedException`，诊断日志无新增记录。
  4. 英文界面下会话行显示 `Unknown device`、`Unknown platform · Last active:`；中文界面显示 `未知设备`、`未知平台 · 最近活跃：`（新增用例）。
  5. 已有安装升级后账户页会话、空态与加载提示的显隐与 0.18.11 一致（`ShowSessionsLoading` 仍为 `!HasSessions && SessionsBusy`）。
- 回滚方式：回退本次提交即可，没有需要撤销的数据迁移；`docs/releases/v0.18.12.md` 与发布索引同属该提交，回退后从 0.18.11 内容继续发布。

## 0.18.13：插件契约与教程按源码校准、README 教程化与版本变更记录（2026-11-24）

本轮交付文档体系：`docs/plugins/` 下的四份契约与插件制作教程入库，根 `README.md` 由版本列表改写为功能导览并加插件文档入口，新增 `docs/releases/changelog.md`。源码行为、配置结构、安装包布局与数据目录均未改动，产品行为与 0.18.12 一致。目标是让「插件规范 / 边界声明 / 框架规范 / UI 规范」四份契约与教程中的每条陈述都能在源码里查到对应行，读者照抄不会写出编译不过或行为错误的插件。

**根因位置（文档陈述与代码不一致，按读者会踩到的顺序）**

1. `docs/plugins/README.md` 原第 9 节把 `ISettingSource.ApplyAsync` 写成 `Task<OperationResult>`。真实声明是 `Task`（`src/Modules/Settings/Mosaic.Settings.Contracts/SettingsContracts.cs:285`）。触发条件：读者照教程实现自己的设置源。出错原因：签名不符编译失败；即使改成 `Task<OperationResult>` 也无法被设置核心调用，因为 `SettingsService` 只捕获异常并把失败转成「已保存，但即时应用失败，重启后生效。」的警告（`src/Modules/Settings/Mosaic.Settings.Core/Internal/SettingsService.cs:225-235`）。
2. 同节把迁移失败描述成直接抛 `Migrations must be contiguous from version one.` / `Migration checksum or database version is incompatible.`。这两个异常先被 `DatabaseWorker.Enqueue` 捕获（`src/Modules/Storage/Mosaic.Storage.Sqlite/Internal/DatabaseWorker.cs:117-124`），调用方拿到的是 `STORAGE_UNAVAILABLE` +「本机存储暂不可用，请重试。」。触发条件：读者的模块在 `ActivateAsync` 里调用 `MigrateAsync` 且不检查 `IsSuccess`。出错原因：文档描述的异常永远不会到达调用方，模块带着未建表状态启动，首次查询才失败且错误信息与迁移无关。
3. 同节把备份条件写成「首次建库（没有任何用户表）不建备份；已有表升级前建备份」。真实条件是 `applied.Count > 0 || hasUserTables`（`MigrationRunner.cs:33-46`，文件名 `mosaic.sqlite.backup-<yyyyMMddHHmmssfffffff>.sqlite` 见 `:40`）。触发条件：读者据此推断「全新库永远没有备份文件」，或据此判断备份存在。出错原因：只要该模块记录过迁移（`applied.Count > 0`）就会备份，即使当时没有任何用户表。
4. `docs/plugins/plugin-spec.md` §4 第 3 条把启动顺序写成「依赖方永远先于被依赖方」。真实实现先访问提供方再 `append` 依赖方（`src/Mosaic.Kernel/ModuleGraph.cs:34-41`），即提供方先启动。触发条件：读者据此排布 `ActivateAsync` 中对外部能力的取用。出错原因：结论与实现相反，任何依赖「被依赖方已启动」的推断都会失效。
5. 同文件 §7 第 2 条把模块迁移写成「只写 `settings["host.modules"]`」。真实逻辑用 `hasModuleOverride` 分支：存在覆盖层时改覆盖层，否则改顶层 `modules`（`src/Mosaic.Desktop/AppConfiguration.cs:207-249`）。触发条件：用户从未保存过设置（没有 `settings` 段）时执行迁移。出错原因：照文档写法会把改动写进一个不存在或从未生效的层，有效模块列表不变。
6. `docs/plugins/framework-spec.md` §6 让读者以为程序目录模板包含 `settings` 段。`src/Mosaic.Desktop/appsettings.json` 只有 `configVersion`、`appliedMigrations`、`modules`、`providers`、`storage`；`settings` 由 `HostSettingSource` 在设置页第一次保存时写入。触发条件：读者按模板排查「为什么找不到 settings」。出错原因：把可选段当成默认存在，会误判配置损坏。
7. `docs/plugins/ui-spec.md` §11 声称「导航、未保存离开、设置保存失败」在 `tests/Mosaic.Desktop.SmokeTests` 覆盖。实际这些用例在 `tests/Mosaic.Wpf.Tests`：`PresentationTests.cs:150`（保存中离开先等待）、`:168`（保存失败保留输入且守卫继续拦截）、`:181`（冲突保留编辑器），`EditingPresentationTests.cs:97`（并发离开共用提示、取消保留预览）、`:126`（回滚失败拒绝离开），即时应用失败但提交成功在 `tests/Mosaic.Settings.Core.Tests/EditingTests.cs:184`。冒烟测试覆盖的是启动、配置迁移、浮窗与速览往返（`tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs`）。触发条件：读者想复现某个交互失败。出错原因：去错工程找用例，或在冒烟测试里新增脆弱的 WPF 交互断言。
8. 多处行号漂移导致「按链接找不到代码」：`PresenceSurface.cs` 的过滤订阅与刷新（现为 `:220-237`、`:301-306`）、`SettingsPageRegistry.cs` 排序（`:10-13`）、`TaskSettingsSource.cs`（`:15-26`）、`TasksModule.cs`（`:75-91`）、`RepairProviderBindings`（`AppConfiguration.cs:177-205`）、`ModuleRuntime.StartAsync` 的工厂循环（`:31-44`）、`SqlRow` 访问器（`IDatabaseSession.cs:45-47`）、`MigrationRunner` 摘要校验（`:12-32`）、`ShellViewModel` 启动页选择（`:86-89`）。
9. 版本陈述停留在 0.18.10（`plugin-spec.md:3,9,11,82`、根 `README.md:5`），而 `Directory.Build.props:12` 与 `build/release-manifest.json` 的 `currentReleaseId` 已推进到 0.18.12，本轮再推进到 0.18.13。

**改动位置（逐文件）**

1. `docs/plugins/README.md`
   - `:10` 版本句改为 0.18.13。
   - `:298` §6.2 改为：扫描只覆盖 `src`（排除 `bin`/`obj`），命中五个 SQL 关键字即失败（`BoundaryTests.cs:88-98`）；测试工程不在扫描内，但 `MigrateAsync` 调用仍只应由 Sqlite 模块发起；白名单按目录名硬编码为四个现有 `*.Sqlite` 工程。
   - `:403` 读结果访问器引用改为 `IDatabaseSession.cs:45-47`。
   - `:404` 迁移段补齐：`MigrateAsync` 返回 `OperationResult`（`IDatabaseSession.cs:71`，迁移定义见 `:50`），连续编号与摘要异常（`MigrationRunner.cs:12-32`），异常经 `DatabaseWorker.Enqueue` 归一为 `STORAGE_UNAVAILABLE`（`DatabaseWorker.cs:117-124`），备份条件按 `applied.Count > 0 || hasUserTables`（`MigrationRunner.cs:33-46`）并给出备份文件名格式。
   - `:405` 保留 `Microsoft.Data.Sqlite` 只属于 `Mosaic.Storage.Sqlite`（`BoundaryTests.cs:54-57`）；SQL 目录白名单改指 `:94-95`（四个目录名写在 94 行），并补 `:96-97`「只有 `Mosaic.Settings.Sqlite` 能直接操作 `settings` 表」这第二条独立规则——原写法把两条规则合并成 `:94-97`，读者会以为 `settings` 表也适用于那四个目录。
   - `:452` 工厂调用顺序引用改为 `ModuleRuntime.cs:31-44`。
   - `:461` `RepairProviderBindings` 引用改为 `AppConfiguration.cs:177-205`。
   - `:570` 设置归属小节改指 `src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskSettingsSource.cs:14-25`（第一轮写的 `:15-26` 在第二轮按源码复核纠正）；`:730` 启动页改为 `ShellViewModel.cs:86-89` 并补「两者都没有时」的提示串。
   - `:766` `ApplyAsync` 返回 `Task`（`SettingsContracts.cs:285`）、失败抛异常由 `TryApplyAsync` 转警告（`SettingsService.cs:225-235`）、`ISettingsCore.Register` 句柄需释放（`SettingsContracts.cs:329`）；`:768` `TasksModule.cs:75-91` 并补 `_rules.Apply`。
   - §11「升级迁移验收」：保存后写入 `settings` 层（`HostSettingSource.cs:84-90`）、迁移写回生效层、`MarkMigration` 去重（`AppConfiguration.cs:382-386`）、锚点条件、用户自定义绑定不被覆盖。第一轮把「保存后必写 `settings` 段」与「绑定由 `RepairProviderBindings` 补齐」写成无条件成立，第二轮改为「必须先动过启用模块或能力绑定」「绑定由迁移 `TryAdd` 写入」（见根因 11、12 与改动位置 9）。
2. `docs/plugins/plugin-spec.md`
   - `:3,9,11,82` 版本改为 0.18.13。
   - §4 第 3 条改为「顺序激活，提供方先于依赖方」（`ModuleGraph.cs:34-41`）。
   - §7 第 2 条改为「改有效模块列表并写回原来那一层」（`AppConfiguration.cs:207-249`）；第一轮仍写「绑定交给 `RepairProviderBindings`」，第二轮推翻并改为「绑定必须随迁移用 `TryAdd` 写入」（见下文根因 10 与改动位置 7）。
   - `:227` 冒烟测试必须串行，并指向 `tests/Mosaic.Desktop.SmokeTests/GlobalUsings.cs:3-5` 的并行化关闭声明与原因。
3. `docs/plugins/framework-spec.md`
   - §6 补：模板无 `settings` 段，该段由 `HostSettingSource` 在首次保存时写入。
   - §7.2 三条改为：`ISettingSource` 只有 `ModuleId`/`Definitions`/可选 `ValidateAsync`/`ApplyAsync`（`SettingsContracts.cs:278-285`），读取用 `ISettingsCore.GetValuesAsync`、写回走 `ISettingsApi`；注册拒绝三种情况及其逐字消息（`SettingsService.cs:11-34`）；`ApplyAsync` 收到全量 `Immediate` 快照（`:377-380`），异常被 `TryApplyAsync` 捕获（`:225-235`），预览路径只对 `Immediate`+`Reversible`+`UserSettings` 调用并在失败时逆序补偿（`SettingsEditing.cs:146-172`）。
   - `:249` 备份条件与 `storage.backupRetention`（默认 5，0 关闭；`SqliteModule.cs:28`，范围 0–50）；`:250` 摘要校验范围改为 `MigrationRunner.cs:12-32`，并补 `DatabaseWorker.cs:117-124` 的失败归一。
4. `docs/plugins/ui-spec.md`
   - §4 设置页排序引用 `SettingsPageRegistry.cs:10-13`，并说明 `Registration.Dispose` 幂等（`:30-33`）。
   - §5 补可选成员默认实现（`PresentationContracts.cs:54-73`、`ICompanionFilterSelection :38-43`）。
   - §6 `PresenceSurface.cs:220-237`、`:301-306`，并写明仅在 `desktop.popup.filterSwitch` 打开时订阅 `FilterChanged`。
   - §8 主题来源改为 `MosaicThemeManager`（`:79-95`、`:41-51`，合并 `Themes/Palettes/{Light,Dark,HighContrast}.xaml`）。
   - §11 测试位置改正（见根因 7），并说明 `Sta.Run` 实现在 `tests/Mosaic.Wpf.Tests/Sta.cs:5`、`build/test.ps1` 逐工程串行。
   - §12 `IsDefault` 多标时取「按 `Order`、`Id` 排序后的第一个」（`ContributionRegistry.cs:82`）。
5. `docs/plugins/boundary-declaration.md`：矩阵说明（`BoundaryTests.cs:22-44`）、Windows TFM 白名单（`:47-51`）、包约束（`:54-57`）、公开 API 比对（`:59-84`）、设置归属（`:102-130`）与当前测试一致；§3.3 的扫描范围引用由 `:86-99` 改为 `:86-100` 并补测试方法名 `Source_has_no_service_locator_reflection_bypass_or_cross_module_sql`（该方法从 86 行 `[Fact]` 起、到 100 行结束，断言块为 88–98），避免与 §6 失败对照表里的 `:88-98` 读起来像两个不同范围。
6. 根 `README.md`：由逐版本变更列表改写为「功能导览」式定位说明——`功能导览`（任务、速览、悬浮窗、账户与备份、设置、维护分节）、`插件`（编译期模块说明、五步阅读顺序、最小清单与 `modules`/`providers` 示例、`settings` 段优先级）、`构建与测试`（`build/test.ps1`、`-SkipDesktop`、`build/publish.ps1`、`build/package.ps1`、产物位置）、`文档`（四份插件契约与架构文档入口）；`:124` 由「三层工程」改为教程实际使用的契约、核心、持久化、界面四层；`:5` 版本、`:7` 下载链接、`:65` MSI 文件名与 `:205-209` 版本导航指向 0.18.13。

**根因位置（第二轮：对抗式审计确认的偏差）**

第一轮逐节人工对照后，再用 14 个独立 agent 对 6 份文档做对抗式审计：每个 agent 只拿一份文档与源码，逐条要求给出 `文件:行号` 依据，找不到或结论相反即判为偏差；共核对 605 条陈述，确认 30 条偏差，按读者会踩到的顺序归并为 7 类（编号接上文）：

10. 教程 §7.2 与 `plugin-spec.md` §5.1 第 2 条把依赖绑定整体写成「交给 `RepairProviderBindings` 自动补齐」。该函数在启动链上只跑一次、且位于迁移链**之前**（调用在 `src/Mosaic.Desktop/AppConfiguration.cs:44`，迁移链 `:48-60`），看不到迁移新启用的模块。触发条件：读者照教程在迁移里只 `modules.Add("[PLUGIN-CORE-ID]")` 而不同时写 `providers`。出错原因：迁移执行后 `:64` 的 `ValidateCoreConfiguration(configuration, validateDependencies: true)` 抛 `模块依赖或能力绑定无效：Provider binding is missing or incompatible: …`，配置不写回，每次启动重复失败，且报错不指向迁移。
11. 教程 §11 验收第 1 条把「保存设置后 `settings.host.modules` 必然出现」当成保存即成立。实际提交只含本次草稿里的键（`src/Modules/Settings/Mosaic.Settings.Core/Internal/SettingsEditing.cs:271`），其中只有 `Persistence == SettingPersistence.HostConfig` 的项才经 `PersistAsync` 落盘（`src/Modules/Settings/Mosaic.Settings.Core/Internal/SettingsService.cs:117-120`、`:147-149` → `src/Mosaic.Desktop/HostSettingSource.cs:84-90`）。触发条件：验收时只改主题、语言这类普通设置再保存。出错原因：配置里不会出现 `settings` 段，后续「迁移写回覆盖层」的验收步骤全部失去前提。
12. 教程 §11 验收第 2 条声称 `RepairProviderBindings` 会把迁移所需的绑定补出来。同根因 10：它早于迁移链运行，只按当时已启用的模块补全（`AppConfiguration.cs:177-205` 的实现与 `:44` 的调用点）。触发条件与出错原因同第 10 条——照此验收会得出「漏写绑定也不报错」的错误结论，把必然启动失败的迁移判为通过。
13. 教程 §7.2 的迁移示例只写 `settings["host.modules"]`，漏了 provider 方向的写回条件。`ApplyPresenceMigration` 只在 `providerChanged` 且 `hasModuleOverride || hasProviderOverride` 任一成立时写 `settings["host.providers"]`（`AppConfiguration.cs:232-240`）。触发条件：已有安装的配置文件里存在 `settings.host.modules` 覆盖层（用户保存过「启用模块」）。出错原因：只改 modules 会让新模块跑在旧 provider 映射上，绑定缺失被覆盖层掩盖，直到 `:64` 才失败。
14. `boundary-declaration.md` §4 原计划按「字典值应写 `Mosaic.Desktop`、应补 `release-summary` 行」修正。核对 `tests/Mosaic.Architecture.Tests/BoundaryTests.cs:104-115` 后确认字典逐字为 `host → Mosaic.Settings.Core`、且没有 `release-summary`；而 `host.*` 定义实际在 `src/Mosaic.Desktop/HostSettingsCatalog.cs`，`release-summary` 声明在 `ReleaseSummaryModule.cs:125`、`:128`。触发条件：读者以为架构测试会兜住新增设置的归属。出错原因：检查正则（`BoundaryTests.cs:116`）要求两个参数都是同一行内的字符串字面量，且只扫含 `SettingValueKind` 的行（`:121`），常量传参、工厂传参与跨行定义都不会被命中——文档按「应然」写就会与测试实际行为不符。改为逐字描述字典内容并列出三条盲区。
15. 教程设置归属小节引用 `TaskSettingsSource.cs:15-26`，真实定义块为 `src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskSettingsSource.cs:14-25`。触发条件：读者按行号找不到定义。出错原因：引用偏移一行，落到定义块之外的类声明行。
16. 根 `README.md` 三处功能陈述与实现不符：
    - 数据库位置迁移的前置条件（`README.md:52`、`:88`）写成「校验可写与空间」「目标文件夹为空」。真实校验顺序在 `src/Modules/Storage/Mosaic.Storage.Sqlite/StorageRelocation.cs:42-79`：非空、绝对路径、路径有效、非磁盘根、目标目录已存在、与源目录不同、源库存在、目标目录无同名库、目标磁盘空间足够；**不要求目标文件夹为空**，也不检查「可写」。
    - 系统要求（`README.md:63`）漏掉 build 门槛。`build/installer/Package.wxs:21` 的 Launch 条件为 `Installed OR (VersionNT64 AND WINDOWSBUILD >= 19041)`，即 64 位 Windows 10 2004 及以上。
    - 浮窗落点（`README.md:85`）写成「回到上次的位置与模式」。`src/Modules/FloatingWindows/Mosaic.FloatingWindows.Core/FloatingWindowCoordinator.cs:32-37` 的实际优先级是 detach hint 覆盖保存的 `MonitorId`/`X`/`Y`，尺寸、模式与置顶仍取旧记录。

**改动位置（第二轮，逐文件）**

7. `docs/plugins/plugin-spec.md`：`:149` 把 §5.1 第 2 条拆成「已启用模块缺失绑定 → 每次启动自动补齐」与「迁移新启用模块 → 绑定必须随迁移写入」两段；`:187` 在 §7 第 2 条补时序（`AppConfiguration.cs:44` 早于 `:48-60`）、`:64` 的失败消息、`TryAdd` 只补缺失键；`:197` 在 §8「已启用模块的能力未绑定」行加同一限定。
8. `docs/plugins/framework-spec.md`：`:196` 改为 providers 缺项先修复、按当时已启用模块补全、执行于 `:44`（迁移之前）；`:277` 启动链第 4 步写明迁移新增模块的绑定必须由迁移 `TryAdd` 写入（参照 `AppConfiguration.cs:228-229`、`:368-369`），只加模块不加绑定会在 `:64` 失败。
9. `docs/plugins/README.md`：`:522` §7.2「五个要点」改写——第 2 条反转绑定归属、第 3 条补 `hasModuleOverride || hasProviderOverride` 的 provider 写回规则（`AppConfiguration.cs:232-240`）、第 4 条补执行顺序；`:901` §11「升级迁移验收」三条限定（第 1 条必须先动过启用模块或能力绑定、第 2 条绑定由迁移 `TryAdd` 写入、第 5 条 `TryAdd` 不覆盖用户绑定）；`:929` 失败对照表补 `AppConfiguration.cs:64` 的触发条件与两种修复路径；`:942` §13 提交清单写明迁移新增模块的绑定必须随迁移写入；`:570` 设置归属小节改指 `TaskSettingsSource.cs:14-25`，并写明正则只扫含 `SettingValueKind` 的行、常量传参不被检查。
10. `docs/plugins/boundary-declaration.md`：§4 `:116-146` 改为逐字描述 `BoundaryTests.cs:104-115` 的归属字典、`:116-128` 的检查方式与三条已知盲区（`:136-138`），并把「新增插件设置时」的引导句合并到盲区之后。
11. 根 `README.md`：`:52` 改写迁移校验清单并补 `KeepSource = true`；`:63` 补 `WINDOWSBUILD >= 19041` 门槛与 `Package.wxs:21` 出处；`:85` 改写浮窗落点优先级并引 `FloatingWindowCoordinator.cs:32-37`；`:88` 改为「确认目标磁盘空间、目标目录无同名 `mosaic.sqlite`」。
12. 发布元数据与变更记录（新增）：`Directory.Build.props:12` 升到 0.18.13；`build/release-content.json` 追加 `mosaic-0.18.13`（`previousReleaseId = mosaic-0.18.12`，三条索引句）并把 `currentReleaseId` 指向它；`build/published-release-index.json` 追加第 59 条；`build/release-manifest.json` 与 `docs/releases/v0.18.13.md` 由 `scripts/release_content_verify.py` 生成，指纹 `8B92129E17522E61D95EBFA5C90BE0072DD7EE78DD68FE6EEAE5F5E93B66F32C`；新增 `docs/releases/changelog.md`（0.18.13 至 0.11.0 逐版本条目）并从根 `README.md:208` 链入。

**迁移逻辑与对已有安装的影响**

- 本轮无源码、配置结构、数据目录、安装升级或模块装配改动，因此没有数据迁移，也没有回退脚本需求。已有安装升级到 0.18.13 后，`Data/`、`Config/`、日志与备份均保持原样；版本号变化只影响安装包的 ProductVersion、内嵌的 `release-manifest.json` 与「更新简要」的适用区间（0.18.13 相对 0.18.12 无产品行为变化，升级后生成的更新简要只包含本轮三条索引句）。
- 回退方式：回退本次提交（`git revert <commit>` 或把分支重置到 `59efd74`）即恢复 0.18.12 的文档与版本号；`docs/plugins/`、`docs/releases/changelog.md` 与 `docs/releases/v0.18.13.md` 同属该提交，回退后一并移除；发布侧只需保留 `v0.18.12` 为 Latest（旧安装不受影响，因为没有 schema 或配置迁移）。
- 教程新增的迁移要求只作用于「读者按教程新增自己的模块」这一场景，并已写明验收项（见 `docs/plugins/README.md` §11「升级迁移验收」五条）：迁移改的是有效模块层（有 `settings["host.modules"]` 改它，否则改顶层 `modules`），**迁移新启用模块的绑定必须随迁移用 `Dictionary.TryAdd` 一并写入同一层**——`RepairProviderBindings` 在 `AppConfiguration.cs:44` 只跑一次且早于迁移链（`:48-60`），补不到迁移新启用的模块，漏写会在 `:64` 抛 `模块依赖或能力绑定无效：Provider binding is missing or incompatible: …` 且配置不写回，每次启动重复失败；已有绑定用 `TryAdd` 只补缺失键、不覆盖用户值。已有安装的 `settings.host.modules` 不会被自动追加，必须靠迁移。文档同时要求交付迁移验收项，与 AGENTS.md 的模块装配改动规则对齐。

**验证与未做项**

- 用一次性脚本（位于系统临时目录，未入库）把四份契约文档、插件制作教程与根 `README.md` 里全部 190 处 `文件:行号` 引用解析到仓库实际文件并校验行数范围，0 处越界或缺失；同一脚本遍历根 `README.md` 与 `docs/**/*.md` 共 182 条本地相对链接，本轮改动的 5 份插件文档 0 处失效；`docs/uninstall-and-storage-relocation-plan.md` 另有 8 条 `path:line` 形式链接指向不存在的路径，属本轮范围外的历史文档，未改动。文中已引用的异常消息、枚举值、接口签名、主题键、导航 `Order`（100/110/900）、设置归属正则（`[a-z][a-z0-9.]*` / `[a-z][a-z0-9-]*`）逐条对照源码。
- 核对方式为两轮：第一轮逐节人工对照源码；第二轮用 14 个独立 agent 做对抗式审计（一人一份文档，逐条要求给出 `文件:行号` 依据，找不到或结论相反即判偏差），共核对 605 条陈述、确认 30 条偏差，即上文根因 10–16，已全部修正；其中 2 条审计意见本身有误（`plugin-spec.md` 例子里 `Tasks.Api`/`tasks.API` 实为两个都不通过、`ui-spec.md` 引用的 `pack://` + `UriKind.Absolute` 与源码 `UriKind.Relative` 不符），复核后未采纳并按源码写法改正。
- 版本面同步：根 `README.md:5`（当前版本与一句话定位）、`:7`（下载链接 `v0.18.13`）、`:65`（MSI 文件名）、`:205-209`（版本导航列出 0.18.13/0.18.12/0.18.11 与变更记录）；`docs/plugins/` 四份契约与教程的版本句升到 0.18.13；`docs/releases/changelog.md` 补 0.18.13、0.18.12、0.18.11 三条并保留既有历史条目。
- 构建与测试：`dotnet build Mosaic.sln -c Release -m:1` 0 警告 0 错误；`build/test.ps1 -Configuration Release` 16 个测试工程共 488 项全部通过（含 `Mosaic.Desktop.SmokeTests` 55 项真实进程冒烟与 `Mosaic.Wpf.Tests` 177 项），0 失败、0 跳过，日志在 `artifacts/verify-0.18.13/test.log`。本轮无 C# 改动，构建通过验证的是文档改动没有带入编译期副作用（例如 `.md` 被误纳入编译项）。
- 发布验收见 0.18.13 发布记录（`docs/releases/v0.18.13.md` 只保留三条索引句，完整根因与改动位置以本节为准——全链「更新简要」正文上限 20000 字符是产品约束：`src/Modules/ReleaseSummary/Mosaic.ReleaseSummary/ReleaseManifest.cs:80` 与 `src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskConstraints.cs:6` 的 `NotesMaxCharacters = 20_000`，0.1.0 → 0.18.12 的合并正文已占 19839 字符，0.18.13 只剩 161 字符余量，无法容纳本节全文）。

## 0.18.11 源码更新 · 速览筛选归属设置库与设置页布局（2026-11-24）

**根因位置**

1. 速览最近筛选是设置模块之外的私有文件。0.18.10 的 `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSurface.cs` 构造函数接收 `filterPreferencePath`：`LoadFilter` 读取 `<数据根>/Config/task-peek-filter.txt`，`SessionFilterChanged` 用临时文件加 `File.Move` 写同一路径。`DesktopPresenceModule.cs` 的 `DesktopPresenceDependencies.FilterPreferencePath` 提供路径，`src/Mosaic.Desktop/BuiltInModuleCatalog.cs:147` 从 `src/Mosaic.Desktop/AppConfiguration.cs:485` 的 `AppPaths.PeekFilterPreference` 取得。触发条件：用户在速览中切换筛选。后果：该值不在设置快照里，备份、恢复、重置与设置编辑会话都看不到它；文件缺失时只能回退默认筛选，无法表达「尚未选择」；文件系统失败只落诊断。
2. 设置应用不可等待、范围过宽。`src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSettings.cs` 的 `ApplyAsync` 直接返回 `dispatcher.InvokeAsync(() => apply(values))`，而应用委托是同步 `void`：`DesktopPresenceModule` 的 `_settings` 只调用 `_surface.UpdateSettings(values)`，弹窗重建与 `RefreshAsync` 均为 fire-and-forget。`PresenceSurface.UpdateSettings` 每次都 `PositionWindows()` 并刷新弹窗，即使改的是关闭行为或悬停延迟。刷新失败经 `ShowResult` 在 `SettingsService` 的操作锁内弹 `MessageBox`。
3. 驻留设置文案存在两套键。`src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceLocalization.cs` 的前 18 项用 `Settings.Setting.desktop.*` 小写点分键注册，`PresenceSettings.DisplayNames` 全部按 `Settings.Setting.Desktop.<PascalCase>` 查找，后 10 项才用正确前缀。触发条件：打开设置 → 驻留与边缘，前 18 行显示原始键名并缺少说明；新增的 `desktop.popup.lastFilter` 也没有对应文案。
4. 模块贡献页仍显示通用「高级设置」栏。`src/Modules/Settings/Mosaic.Settings.Wpf/Internal/SettingsViewModel.cs:374` 只有 `HasSettingsSectionContent => SelectedPage is null`，`src/Modules/Settings/Mosaic.Settings.Wpf/Views/SettingsView.xaml` 的 `ModeBar` 没有可见性绑定；0.18.9 为保证坐标稳定把该栏固定在顶部。触发条件：切换到「账户与备份」「维护」。
5. 筛选变化通知没有显式可选契约。`src/Mosaic.Presentation.Contracts/PresentationContracts.cs` 的 `ICompanionViewSession.FilterChanged` 是默认实现事件，宿主无法区分实现是否支持筛选；给基础接口加成员会破坏所有伴随视图实现，也无法在不动 `CompanionViewOptions` 的前提下扩展。

**改动位置**

1. `src/Mosaic.Presentation.Contracts/PresentationContracts.cs:40` 新增可选接口 `ICompanionFilterSelection`（`event EventHandler<string>? FilterChanged`）；`CompanionViewOptions` 的 10 个构造参数与 `ICompanionViewSession` 未改动。
2. `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekViewFactory.cs:51` 的 `TaskPeekViewModel` 实现 `ICompanionFilterSelection`，复用第 63 行已有的 `FilterChanged` 与 `SelectFilterAsync` 触发点。
3. `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSurface.cs`：
   - 第 15-18 行构造函数改为 `(IDesktopWindowHost, ICompanionViewFactory, ISettingsCore, IDiagnosticSink)`，删除 `filterPreferencePath`、`LoadFilter` 与临时文件写入。
   - 第 52 行 `PopupSessionSettingIds` 与第 62 行 `LayoutSettingIds` 区分「需要重建会话」与「只需重新测量」的设置。
   - 第 69 行 `UpdateSettingsAsync` 从 `desktop.popup.lastFilter` 读取最近筛选，`Unset` 或非法值视为无最近选择；只在首帧或相关设置变化时 `PositionWindows()` 与 `RequestRefreshAsync()`。
   - 第 87 行 `SessionFilterChanged` 串行化偏好写入；第 110 行 `SaveFilterAsync` 经 `ISettingsCore.SaveModulePreferenceAsync("desktop-presence", "desktop.popup.lastFilter", filter)`，失败抛错并上报诊断。
   - 第 98 行 `AttachFilterChanged` 优先订阅 `ICompanionFilterSelection`，未实现时回退基础接口事件；`ShowPopupAsync` 与 `ClosePopupAsync` 成对订阅/退订。
   - 第 342 行 `StopAsync` 在停止阶段等待在途偏好写入；`ClosePopupAsync` 不等待，避免与设置操作锁互等。
   - 第 435 行 `ReportResult` 把刷新失败写入诊断，设置提交路径不再弹对话框。
4. `PresenceSettings.cs:19` 新增隐藏偏好 `desktop.popup.lastFilter`（`ShowInSettings=false`、`Advanced` 分组、`Immediate`、`UserSettings`，取值 `Unset`/`All`/`Open`/`Completed`）；第 55 行新增 `DefinePreference`；第 62 行 `DisplayNames` 补充 `desktop.popup.lastFilter` 的显示名键；第 123 行 `ApplyAsync` 先在 UI 线程启动应用并等待其完成。
5. `DesktopPresenceModule.cs:11-13` 依赖改为 `Func<string?>? ReadLegacyFilter`；第 32-38 行应用委托改为 async 并等待 `UpdateSettingsAsync`；第 58-76 行启动时读取旧值并按迁移 ID 导入。
6. `PresenceLocalization.cs:9` 改为单一 `SettingNames` 表（29 项），循环生成显示名与说明；第 71 行增加 `Unset` 值文案。
7. `src/Mosaic.Desktop/BuiltInModuleCatalog.cs:147-148` 传入读取旧文件的委托；`AppConfiguration.cs:485` 的 `PeekFilterPreference` 仅保留为旧文件位置。
8. `src/Modules/Settings/Mosaic.Settings.Contracts/SettingsContracts.cs:331` 与第 334 行为 `ISettingsCore` 增加带默认实现的 `SaveModulePreferenceAsync` 与 `InitializeSettingAsync`，未实现时返回 `CapabilityUnavailable`。
9. `src/Modules/Settings/Mosaic.Settings.Core/Internal/SettingsEditing.cs:56` 的 `SaveModulePreferenceAsync` 校验定义属于该模块、`ShowInSettings=false`、`Persistence=UserSettings`，经 `ProposeChangesCoreAsync` 保存并应用全部来源，再调用 `EditSession.UpdateSavedPreference` 同步 `_saved` 并移除同键草稿；第 79 行 `InitializeSettingAsync` 归一化、校验后调用 `ISettingRepository.InitializeAsync`；第 210 行 `UpdateSavedPreference`。
10. `src/Modules/Settings/Mosaic.Settings.Core/Internal/GuardedSettingsApi.cs:50` 与第 61 行把两个新入口纳入模块执行租约与异常映射。
11. `SettingsViewModel.cs:374` 与第 377 行新增 `HasSettingsSectionContent` 与 `ModeBarVisibility`，第 680 行在页面切换后通知；`SettingsView.xaml:109` 绑定 `ModeBarVisibility`，用 `Hidden` 而非 `Collapsed` 保留占位高度。
12. `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/Mosaic.DesktopPresence.Wpf.csproj:3` 对 `Mosaic.Wpf.Tests` 开放内部类型，供本地化一致性测试直接检查内部表。
13. `build/public-api.txt:83` 登记 `Mosaic.Presentation.Contracts.ICompanionFilterSelection`。

**迁移逻辑**

- 迁移 ID `desktop.popup.lastFilter.v1`，目标 `desktop.popup.lastFilter`。启动时 `DesktopPresenceModule.ActivateAsync` 读取旧文件（委托由 `BuiltInModuleCatalog` 提供），只接受 `All`、`Open`、`Completed`，其他内容不触发初始化。`SettingsService.InitializeSettingAsync` 拒绝非 `UserSettings` 定义，归一化并校验后调用 `ISettingRepository.InitializeAsync`；SQLite 实现在事务内 `INSERT OR IGNORE` 并记录迁移 ID，因此设置库已有值时保留已有值，同一迁移 ID 只生效一次。失败（设置库不可用）只写诊断，不阻断启动，本次使用 `Unset`（回退默认筛选），下次启动以同一 ID 重试。旧文件不删除、不修改。
- 全新安装：定义为 `Unset`，运行时 `_lastFilter == null`，显示筛选条时使用 `desktop.popup.filter` 的默认值。
- 已有安装：升级后第一次启动导入旧选择，随后可删除 `Config/task-peek-filter.txt`，删除后不影响设置库中的值。回退到 0.18.10 时，设置库中多出的 `desktop.popup.lastFilter` 行被现行定义列表过滤掉（`SettingsService.GetValuesAsync` 按注册定义返回），旧文件仍在，0.18.10 继续按原路径读取。

**迁移验收项**

1. 升级前把 `task-peek-filter.txt` 写为 `Open`，启动一次后 `GetValuesAsync()` 返回 `desktop.popup.lastFilter=Open`。
2. 再次启动不重复初始化（仓储初始化计数不增、值不变）；删除旧文件后值仍为 `Open`。
3. 设置库已有 `Completed` 而旧文件为 `Open` 时保留 `Completed`。
4. 旧文件为非法内容时不执行初始化，值为 `Unset`，显示筛选条时使用 `All`。
5. 注入一次初始化失败后应用仍启动、值为 `Unset`；下次启动以同一迁移 ID 重试成功。
6. 隐藏筛选条时使用 `desktop.popup.filter`（`All`），保留的 `Open` 不被改写。

**测试与验证**

- `tests/Mosaic.Wpf.Tests/PresenceTests.cs:599` 的 `Peek_remembers_user_filter_across_reopen_restart_and_legacy_migration` 覆盖上式 1–6：真实驻留宿主与速览单选按钮、重开、宿主重建、独立保存与编辑会话不互相覆盖、隐藏/显示筛选条、迁移幂等、已有值优先、非法值、失败重试与旧文件保留。
- `tests/Mosaic.Wpf.Tests/PresenceTests.cs:460` 的 `Settings_register_28_rows_project_6_normal_validate_save_and_revoke` 断言 29 个定义、28 个可见行，隐藏偏好 `ShowInSettings=false`、`PreviewMode.None`、默认 `Unset` 与四项取值，以及独立通道的正/反向写入。
- `tests/Mosaic.Wpf.Tests/PresenceTests.cs:521` 的 `Presence_localization_has_a_single_authoritative_key_set_for_every_definition` 逐一断言每个定义与选项的中英文显示名、说明存在，且 `Settings.Setting.` 前缀键数恰为定义数的两倍。
- `tests/Mosaic.Wpf.Tests/PresenceTests.cs:554` 的 `Surface_creates_refreshes_and_releases_edge_popup_and_sessions` 验证隐藏偏好保存不重建弹窗、值进入设置快照，停止后释放弹窗。
- `tests/Mosaic.Wpf.Tests/PresentationTests.cs:1401` 的 `Settings_header_and_sections_stay_aligned_when_switching_to_contributed_pages` 断言贡献页模式栏为 `Hidden`、仍有实际高度，导航与内容容器顶部坐标与常规分区一致。
- `Mosaic.Architecture.Tests` 4 项通过，含公开 API 清单与项目边界校验。
- `build/test.ps1 -Configuration Release`：0 警告、0 错误。16 个测试工程共 486 项，485 项通过、1 项失败：`Mosaic.Desktop.SmokeTests.DesktopTests.Floating_configuration_migration_updates_effective_layer_once`（`tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs:530`）在整项目并行运行时读到另一进程写成缩进加 CRLF 的 `appsettings.json`，与紧凑 JSON 的期望不符；该用例用 `-method` 单独复跑时 6 个场景全部通过。该用例与本次改动的文件无交集（配置写回未改动），按既有并行冒烟不稳定处理。日志位于 `artifacts/verify-0.18.11-tests.log`。

**发布文件**

`Directory.Build.props`、`build/release-content.json`、`build/published-release-index.json`、`build/release-manifest.json` 与 `docs/releases/v0.18.11.md` 更新至 0.18.11，`contentSha256` 为 `6F0E556A7E24940AA8D2E7704B7D8C1D2B8721C87477E5D0DD5F97C4ED7BD4BA`。`README.md` 在工作区存在未提交的重写，本次未纳入提交，其版本引用已同步到 0.18.11，待随该重写一并提交。

安装包：`powershell -NoProfile -ExecutionPolicy Bypass -File build/package.ps1 -ProductVersion 0.18.11` 通过（0 警告、0 错误），产物 `artifacts/installer/0.18.11/Mosaic-0.18.11-win-x64.msi`，大小 52,558,074 字节，SHA-256 为 `BAAF20C8D04C3580E7540EF911C84A6D6D3F07177CB40A18406C508F9A0391DB`。脚本校验通过：开始菜单与桌面快捷方式、`INSTALLFOLDER` 工作目录、启动清理注册项、维护与升级路由、对话框、负载清单与 470 个 cabinet 成员的 SHA-256 逐一比对。该 MSI 基于最终 `build/release-manifest.json`（含本节指纹）重建；日志位于 `artifacts/verify-0.18.11-package.log`。

## 0.18.10 源码更新 · 账户与备份排版及速览筛选记忆

- 账户页根因位于 `src/Modules/Auth/Mosaic.Auth.Wpf/Views/AccountView.xaml` 的会话卡片。无结果时，`ShowSessionsLoading` 与 `ShowSessionList` 都为 `false`，标题下缺少状态反馈；刷新时 `AccountViewModel.ShowSessionList` 隐藏已有行。会话和云端备份按钮混用带不同尺寸与外边距的样式。云端刷新在独立账户视图中还会受链接样式的主题资源解析影响，删除区说明与操作的字号和间距也不一致。
- `AccountView.xaml:62`、`AccountView.xaml:251`、`AccountView.xaml:269` 和 `AccountView.xaml:321` 分别增加空状态、对齐备份操作、调整会话卡片和删除卡片。会话刷新时保留现有行，备份和会话操作按钮等高并在窄宽度下左对齐换行。删除说明使用正常字号，入口和确认按钮使用主题危险操作样式。`src/Modules/Auth/Mosaic.Auth.Wpf/Localization/zh.xaml:102` 与 `Localization/en.xaml:73` 补齐会话空状态文案。
- 速览根因位于 `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSurface.cs` 的 `ShowPopupAsync`。每次展开都用 `desktop.popup.filter` 创建新会话，`TaskPeekViewModel.SelectFilterAsync` 之前只改变当前会话的筛选，收起后所选值随会话销毁。
- `src/Mosaic.Presentation.Contracts/PresentationContracts.cs` 的 `ICompanionViewSession.FilterChanged` 提供筛选变化通知，`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekViewFactory.cs` 的 `TaskPeekViewModel.SelectFilterAsync` 在用户更改选项时触发。`PresenceSurface.ShowPopupAsync`、`SessionFilterChanged`、`LoadFilter` 和 `ClosePopupAsync` 分别负责恢复、保存、校验和解绑。`DesktopPresenceModule.cs` 的 `DesktopPresenceDependencies` 接收偏好文件路径，`src/Mosaic.Desktop/AppConfiguration.cs` 的 `AppPaths.PeekFilterPreference` 和 `BuiltInModuleCatalog.cs` 的驻留模块装配提供路径。
- `PresenceSettings.cs` 的 `desktop.popup.filter` 与 `desktop.popup.filterSwitch`、`PresenceLocalization.cs` 的对应资源，以及 `src/Modules/Settings/Mosaic.Settings.Wpf/Localization/zh.xaml:223` 与 `Localization/en.xaml:223` 同步显示默认筛选和恢复规则。
- 已有安装继续读取 `desktop.popup.filter` 的键和值，并把它用作默认筛选。新增本机偏好文件位于 `<数据根目录>/Config/task-peek-filter.txt`，只保存 `All`、`Open` 或 `Completed`。旧安装没有文件或内容无效时自动沿用现有默认值，无需手动迁移。关闭筛选条时用默认值，重新显示后恢复保存的值。回退原版本时可保留偏好文件，恢复新版后仍可读取。需要重置偏好时，退出应用后移走该文件。
- `tests/Mosaic.Wpf.Tests/PresenceTests.cs` 的 `Peek_remembers_user_filter_across_reopen_and_restart_and_preserves_the_configured_fallback` 走驻留宿主和真实速览单选按钮，覆盖三个筛选、关闭重开、宿主重建、缺失及非法文件、隐藏与重新显示筛选条。`AccountViewModelTests.cs` 的 `Account_cards_show_session_states_and_fit_the_available_width` 覆盖空状态、加载、保留已有会话、按钮对齐与换行和删除确认入口。`PreviewCaptureTests.cs` 的 `Render` 和 `FindRepositoryRoot` 供账户页预览复用。`tests/Mosaic.Desktop.SmokeTests/PeekCompletionTests.cs` 的 `Peek_completion_and_filter_use_real_persisted_task_state` 用 UI Automation 验证 Release 桌面进程关闭面板重开及退出重启后恢复所选筛选。

- Release 方案构建为 0 警告、0 错误。16 个测试工程共 485 项通过，含 174 项 WPF 测试与 55 项实际桌面进程冒烟。默认并行冒烟中，浮窗拖动用例 `FloatingDragTests.cs:38` 两次读到 0 像素水平位移，期望区间为 56–64 像素。定向复跑和 `-parallel none` 串行全跑通过。核对了中文浅色 600 DIP、英文深色 320 DIP 的账户页渲染。预览位于 `artifacts/ui-preview/account-sessions-light.png` 与 `artifacts/ui-preview/account-sessions-dark.png`，构建和测试日志位于 `artifacts/verify-account-peek-release/`。
- 发布文件：`Directory.Build.props`、`README.md`、`build/release-content.json`、`build/published-release-index.json`、`build/release-manifest.json` 与 `docs/releases/v0.18.10.md` 更新至 0.18.10，0.18.10 汇总包含 0.18.9 之后的任务列表与速览状态、附件显示更新。`build/package.ps1` 已通过发布载荷、快捷方式、维护路由、对话框、清单与 CAB 内容校验；发布载荷内嵌版本为 `0.18.10+f42e3e9`。`Mosaic-0.18.10-win-x64.msi` 为 52,558,074 字节，SHA-256 `7C7687AF8299F7DCCE9CAA9E75D7E8B23A617F9C5AD6BA2DCED55D2FB8818DA0`，校验和位于 `artifacts/installer/0.18.10/SHA256SUMS`。本次构建与测试日志位于 `artifacts/verify-0.18.10-tests.log` 与 `artifacts/verify-0.18.10-package.log`。

## 0.18.10 源码更新 · 任务列表与速览

- 主任务列表使用圆角状态徽标，深色模式下提高未完成任务标题的可读性。
- 主任务列表与任务速览共用矢量回形针。有附件的任务在行右侧显示回形针，悬停可查看数量。
- 任务速览用图标区分完成状态，顶部筛选和屏幕阅读器的状态提示仍可使用。旧安装保存的「显示状态文字」设置值会被忽略，其余设置照常读取。

### 移除速览行状态文字

- 原有行为来自 `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml` 的 `TaskCard` 模板。状态文字占用独立列，由 `TaskPeekRow.StatusVisibility` 读取默认开启的 `desktop.popup.showStatusText`，与左侧状态图标重复显示。
- `TaskPeekView.xaml:140` 删除状态列，回形针靠右。`Internal/TaskPeekViewFactory.cs` 的 `TaskPeekRow` 移除文字可见性属性，`AccessibleName` 仍提供本地化的完成状态。`src/Mosaic.Presentation.Contracts/PresentationContracts.cs` 的 `CompanionViewOptions` 移除该开关。`src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSurface.cs:163` 不再读取旧设置，`PresenceSettings.cs` 移除定义与显示名索引，`PresenceLocalization.cs` 移除对应说明。
- 旧安装兼容处理由设置核心 `SettingsService.GetValuesAsync` 按现行定义筛选：旧 `desktop.popup.showStatusText` 即使保存为 `true` 也不再生效，持久值保留以支持回退。`tests/Mosaic.Wpf.Tests/PresenceTests.cs` 的 `Settings_register_28_rows_project_6_normal_validate_save_and_revoke` 验证旧键从运行时和值列表消失，且已保存的标题字号仍生效。该文件的速览用例验证状态图标、无障碍名称、回形针右对齐和整行拖动。
- 本次源码更新的 Release 方案构建为 0 警告、0 错误。16 个测试工程共 482 项全部通过，其中 171 项 WPF 测试验证界面行为，55 项桌面冒烟测试使用本次构建的桌面程序。

### 任务状态与附件图标

- 原有实现位于 `Views/TasksView.xaml` 的任务行模板，状态使用 `○` 和 `✓` 字符，旧回形针嵌在创建时间行。`Internal/TaskPeekViewFactory.cs` 的 `TaskPeekViewModel.UpdateView` 已能获得 `TaskItemDto.AttachmentCount`，但投影为 `TaskPeekRow` 时没有传入该字段，速览模板也没有附件徽标。
- `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml:187` 使用圆角底板区分未完成圆点和已完成对勾，并将附件徽标放在独立右列。新增 `Themes/TaskRowIcons.xaml` 为主列表与速览提供共用的斜向矢量回形针和显示规则。`Internal/TaskPeekView.xaml:140` 在速览卡片右侧使用同一徽标。`Internal/TaskPeekViewFactory.cs` 的 `TaskPeekRow` 与 `TaskPeekViewModel.UpdateView` 传递附件数量，并将其加入有附件任务的无障碍标签。`Internal/TaskListItemViewModel.cs` 的 `AttachmentLabel` 与速览共用 `Internal/TaskLocalization.cs` 的 `AttachmentCountLabel`。
- `tests/Mosaic.Wpf.Tests/PresentationTests.cs` 的 `Task_list_renders_status_marks_and_shows_paperclip_only_with_attachments` 验证浅色、深色及高对比度主题。`tests/Mosaic.Wpf.Tests/PresenceTests.cs` 的附件徽标回归验证窄布局与刷新后的附件显隐；速览预览示例加入有附件行，原有状态图标断言改用 `CompletionMark`。`tests/Mosaic.Wpf.Tests/PreviewCaptureTests.cs` 的 `Write_required_responsive_previews_to_artifacts` 增加含附件的深色列表预览。
- 已查看主列表与速览的浅色和深色截图。此次改动复用现有附件计数，无数据、配置或安装迁移。

### 深色模式任务标题

- 根因位于 `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml:227` 的主任务列表标题模板。原样式仅在 `IsCompleted=True` 时指定主题字色。未完成项继承列表项的默认前景色，WPF 用例实测为 `#000000`，与深色普通行背景的对比度为 1.27:1。
- `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml:232` 为标题样式添加 `TextPrimaryBrushKey` 动态资源。完成项的触发器继续使用 `TextSecondaryBrushKey` 和删除线。`tests/Mosaic.Wpf.Tests/PresentationTests.cs` 的 `Task_list_titles_use_readable_theme_colors_in_dark_mode` 检查实际生成的列表行，覆盖普通和选中背景下两种完成状态的字色、至少 4.5:1 的对比度及完成项删除线。本节记录本次改动及验证结果。
- 专项用例在旧样式下复现黑色标题，修复后通过。深色模式下，未完成项与普通、选中背景的对比度分别为 14.15:1、10.00:1。Release 构建 0 警告、0 错误，WPF 测试 166 项全部通过。这次改动仅涉及标题显示，无数据或配置迁移。

## 0.18.9：设置分区切换布局（2026-11-23）

- 根因：`src/Modules/Settings/Mosaic.Settings.Wpf/Views/SettingsView.xaml:105` 的 `SubtitleText` 和 `ModeBar` 原先把可见性绑定到 `HasSelectedPage` 的反值。`SettingsViewModel.SelectPageAsync` 在模块页初始化完成后更新该状态，WPF 随即折叠这两处控件并缩短顶部 `Auto` 行，使下方导航与内容整体上移。回归用例在 860 × 620 的布局中复现导航顶部从 155.33 移到 53.33，相差 102 个设备无关单位。
- 改动位置：`src/Modules/Settings/Mosaic.Settings.Wpf/Views/SettingsView.xaml:105` 去掉两处顶部可见性绑定，分区标题和模块内容仍由原有绑定切换。`src/Modules/Settings/Mosaic.Settings.Wpf/Internal/SettingsViewModel.cs` 的 `NotifyPageContentChanged` 删除描述旧顶部绑定的注释。`tests/Mosaic.Wpf.Tests/PresentationTests.cs` 的 `Settings_header_and_sections_stay_aligned_when_switching_to_contributed_pages` 通过实际导航按钮命令依次切换外观、账户与备份、维护及外观，检查标题说明和高级设置栏持续可见，并比较导航与内容容器的顶部坐标。`Directory.Build.props`、`README.md`、`build/release-content.json`、`build/published-release-index.json`、`build/release-manifest.json` 及 `docs/releases/v0.18.9.md` 随包更新版本和发布说明。
- 升级影响：沿用已有 MSI 升级路径，布局修复无需迁移设置、数据库或数据目录。

## 0.18.8：任务速览拖出与自动收起（2026-11-23）

- 根因：`TaskPeekView.EndGesture` 和 `CaptureLost` 在调用 `FinishDrag` 前把 `_candidate` 清空，清理时拿不到捕获鼠标的任务行。松手后捕获可能残留，影响下一次拖动。成功打开小窗时 `TaskPeekViewModel.DetachAsync` 没有把结果传回速览宿主，而 `PresenceSurface.Inside` 把速览内的键盘焦点视为保留窗口的条件，成功拖出后速览仍可停留。
- 改动位置：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs` 的 `ActivateDrag`、`EndGesture`、`CaptureLost`、`FinishDrag`、`CancelDrag` 和 `ResetGesture` 统一清理捕获与预览，在等待小窗打开前释放捕获；落点按速览视图换算屏幕坐标。`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekViewFactory.cs` 的 `TaskPeekViewModel.DetachAsync` 回传成功状态，`DragDetached` 请求宿主收起；打开失败在原速览反馈。
- 改动位置：`src/Mosaic.Presentation.Contracts/PresentationContracts.cs` 为 `ICompanionViewSession` 添加可选 `DismissRequested` 事件。`src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSurface.cs` 在会话打开/关闭时订阅和退订，收到成功通知后按 `desktop.popup.autoHide` 收起当前速览。任务浮窗继续由 FloatingWindows 模块持有。
- 测试：`tests/Mosaic.Wpf.Tests/PresenceTests.cs` 覆盖仍在等待打开时就释放捕获、失败后不发收起通知、面板内放下取消和后续任务拖出成功。`tests/Mosaic.Desktop.SmokeTests/PeekDragTests.cs` 验收真实鼠标依次拖出两条任务、速览每次收起、两个浮窗同时存在。
- 发布文件：`Directory.Build.props` 更新版本，`README.md` 更新当前版本与下载链接，`build/release-content.json`、`build/published-release-index.json`、`build/release-manifest.json` 与 `docs/releases/v0.18.8.md` 记录本次发布。本条记录位于 `docs/implementation-status.md`。
- 升级沿用原安装位置和数据目录。此版本无数据库或设置迁移。关闭「离开自动收起」时，拖出成功后保留速览。
- 回归验收：`build/test.ps1 -Configuration Release` 构建 0 警告、0 错误，16 个工程 476 项全部通过，其中 Desktop Smoke 55 项。独立真实鼠标用例还确认关闭第一个浮窗后，第二个浮窗继续保留。
- 打包验收：`build/package.ps1` 已通过发布载荷、快捷方式、维护路由、对话框、清单和 CAB 内容校验。`Mosaic-0.18.8-win-x64.msi` 为 52,566,264 字节，SHA-256 `DCC42B5E0710E93654B4AB53968383FFC56440043489C4AB7402A4C3CB91D5E5`。

## 0.18.7：评审问题收口——启动页契约、账户/同步状态、主题与窗口、配置键名迁移（2026-11-23）

- 根因位置：
  1. 启动路由由隐藏设置项与跨模块事件驱动：0.18.7 前 `src/Mosaic.Desktop/Bootstrapper.cs` 的 `StartAsync` 在 Auth 模块激活钩子里调用 `AuthModule.InitialAccountChoice` 并 `ISettingRepository.InitializeAsync`；`src/Mosaic.Shell.Wpf/ShellViewModel.cs` 订阅 `SettingsPageRegistry.StartPageRequested` / `InitialChoiceCompleted`，只有该设置提交成功并回调后才导航；`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs` 的 `MarkAccountChoiceMadeAsync` 在离线/设备码登录路径上写设置。触发条件：全新安装、设置库不可写或事件时序变化时，启动页依赖一次设置写入，失败后工作区为空且没有错误提示；`auth.accountChoiceMade` 还永久占用了一个用户设置键。
  2. 已登录账户内容被写死隐藏：0.18.7 前 `src/Modules/Auth/Mosaic.Auth.Wpf/Views/AccountView.xaml` 的账户卡片、同步开关与会话列表外层是 `Visibility="Collapsed"`，`ShowAccountSettings` 未参与可见性；页头另有一个只显示图标的 Logout 按钮，与账户卡片里的按钮重复。触发条件：已登录用户进入账户设置，看不到账户信息、同步与设备会话，只能看到无文字说明的登出图标。
  3. 登出/删除后残留上一账户状态：`AccountViewModel.NotifyAuth` 只在登录分支刷新，未清空 `Sessions`、`SnapshotSummary`、`RestoreSummary`；`src/Modules/Sync/Mosaic.Sync.Core/Internal/SyncService.cs` 的 `RaiseForAuthChange` 只触发事件，`State` 在未登录时仍返回 `_latest` / `_lastRestore` / `_messageCode`。触发条件：登出或删除账户后再次进入账户页或登录页，仍显示上一个账户的最近备份与上次恢复。
  4. 设备码倒计时跨线程读改写：0.18.7 前 `AccountViewModel.PollDeviceAsync` 每秒在后台线程对 UI 属性 `DeviceSecondsLeft` 做自减，再以它为时钟判断过期与下次轮询；`Task.Delay(1s)` 与真实经过时间不等，UI 卡顿或服务端 `slow_down` 会让倒计时漂移甚至提前过期。
  5. 同步选项提交未串行化：`AccountViewModel.CommitSyncOptionsAsync` 每次切换都独立走 `BeginEditSessionAsync` / `PreviewAsync` / `CommitAsync`；快速连续切换时两个编辑会话基于同一快照提交，后一次可能覆盖前一次。
  6. 语言切换与页面重建丢状态：语言变更处理器重建服务器地址输入而没有保留用户正在输入的值；`AuthViewFactory.Create` 每次访问都新建页面 VM，`_offlineChosen` 只存在 VM 实例里，离开再回来即丢失。
  7. 未配置服务器时设备码登录静默走官方服务器：`AccountViewModel.StartDeviceSignIn` 在 `ServerUrl` 为空时直接开始设备码流程，用户无法判断登录目标。
  8. 跨模块错误码映射：`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AuthLocalization.cs` 的 `AuthLocalization.Error` 映射 `SyncErrorCodes`。依赖面只有 `Mosaic.Sync.Contracts`（错误码枚举），账户页同时承载账户与同步设置，属于有意的组合；0.18.7 在代码中写明该理由，未拆成新模块。
  9. 任务列表无障碍信息绑错：0.18.7 前 `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml` 的列表项 `AutomationProperties.Name` 绑 `Title`、`HelpText` 绑 `AccessibilityLabel`，读屏只报标题；附件指示器的 `ToolTip` 与辅助名使用静态资源键 `Tasks.Attachments.Indicator`，不含附件数量。
  10. 主题词典自带浅色副本且模板部件缺失：0.18.7 前 `src/Mosaic.Presentation.Wpf/Themes/Theme.xaml` 顶部定义 23 个浅色画刷，运行时 `MosaicThemeManager` 又会合并 `Palettes/Light|Dark|HighContrast`，单独加载该词典的宿主（测试、预览）与运行时不等于同一组合；ComboBox 没有 `PART_EditableTextBox`，可编辑态仍显示只读内容；ProgressBar 的轨道没有 `PART_Track`；菜单项没有勾选列与快捷键列；ToolTip 不换行且下拉高度写死 300；禁用态复选框/单选按钮的字形与圆点不随禁用色变化。
  11. 顶层窗口 chrome 放在表现层程序集：0.18.7 前 `src/Mosaic.Presentation.Wpf/MosaicWindowChrome.cs` 位于可复用表现层，却直接 P/Invoke 顶层 HWND；`ThemeChanged` 在非窗口线程触发时直接取句柄抛 `InvalidOperationException`；`ObjectDisposedException` 与 `InvalidOperationException` 的捕获顺序、窗口关闭后的清理与高对比度跳过都不完整。触发条件：主题切换、窗口关闭瞬间、测试宿主或多 UI 线程。
  12. 窗口位置只按主显示器工作区钳制：0.18.7 前 `src/Mosaic.Shell.Wpf/ShellWindow.xaml.cs` 的 `OnLoaded` 用 `SystemParameters.WorkArea` 缩小窗口并居中。触发条件：上次位置保存在副显示器或已拔掉的显示器，合法位置被移回主屏并缩小。
  13. 配置写回键名大小写不一致：0.18.7 前 `src/Mosaic.Desktop/AppConfiguration.cs` 的 `WriteConfiguration` 用 `["storage"] = configuration.Storage` 直接序列化 `StorageConfiguration` 记录，产出 `Engine` / `Location` / `Filename`；读取端与外部工具按小写 `engine` / `location` / `filename` 读取会得到 null。触发条件：任何写回配置的启动（首次修复、迁移、设置变更）。
  14. 默认配置缺少能力绑定：0.18.7 前 `src/Mosaic.Desktop/appsettings.json` 没有 `tasks.attachments@1` 的 provider 映射，而 `TasksModule` 声明需要该能力；启动修复把健康配置判为不完整并重写，与 13 叠加后暴露键名问题。触发条件：每次启动。
  15. 测试基线漂移：`tests/Mosaic.Wpf.Tests/PresentationTests.cs` 的 `LoadTheme` 与 `tests/Mosaic.Desktop.SmokeTests/ButtonFeedbackTests.cs` 只加载 `Theme.xaml` 不合并调色板；`tests/Mosaic.Wpf.Tests/AccountViewModelTests.cs` 的 `FakeAuth.PollDeviceAuthorizationAsync` 在 approved 时不更新登录态；登出用例在 `StartAsync` 之前设置替身状态会被重置；smoke 测试并行时抢前台窗口与鼠标；拖动断言未给系统拖动阈值留量。

- 改动位置：
  1. 启动页契约：`src/Mosaic.Presentation.Contracts/PresentationContracts.cs` 的 `NavigationItem` 新增 `IsDefault`；`src/Modules/Tasks/Mosaic.Tasks.Wpf/TasksUiModule.cs` 为任务导航声明 `IsDefault: true`；`src/Mosaic.Shell.Wpf/ShellViewModel.cs` 的 `InitializeAsync`、`OnContributionsChanged` 改为按 `IsDefault` 选择启动页，新增 `PageError` / `HasPageError`，加载失败时不再只弹对话框；`src/Mosaic.Shell.Wpf/ShellWindow.xaml` 的空工作区按 `HasPageError` 切换图标、标题并隐藏描述；`src/Mosaic.Presentation.Contracts/SettingsPageRegistry.cs` 删除 `IsInitialChoiceActive`、`InitialChoiceCompleted`、`StartPageRequested`、`RequestStartPage`、`CompleteInitialChoice`；`src/Mosaic.Desktop/Bootstrapper.cs` 删除 Auth 激活钩子；`src/Modules/Auth/Mosaic.Auth.Core/AuthModule.cs` 删除 `InitialAccountChoice`；`src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthSettingsSource.cs` 删除 `AccountChoiceMade` 定义；`src/Mosaic.Shell.Wpf/ShellWindow.xaml.cs` 构造 `ShellViewModel` 时去掉 `settingsPages` 参数。
  2. 账户页可见性：`src/Modules/Auth/Mosaic.Auth.Wpf/Views/AccountView.xaml` 的登录态容器改为绑定 `ShowAccountSettings`，删除页头重复 Logout，账户卡片 Logout 增加 `AutomationProperties.AutomationId="AccountLogoutButton"`。
  3. 账户与同步状态：`AccountViewModel.NotifyAuth` 在未登录分支清空 `Sessions`、`SnapshotSummary`、`RestoreSummary` 并通知相关属性/命令；新增 `RefreshAccountContextAsync`，登录状态由外部事件触发时刷新备份摘要与会话；`SyncService.RaiseForAuthChange` 在未登录时清空 `_latest` / `_lastRestore` / `_messageCode`，`SyncService.State` 在未登录时屏蔽这三项。
  4. 设备码时钟：`AccountViewModel.StartDevicePolling` 与 `PollDeviceAsync` 改用 `Stopwatch.GetTimestamp()` 的单调截止时间，新增 `RemainingSeconds`，UI 属性只作为展示副本。
  5. 同步选项串行化：`AccountViewModel.CommitSyncOptionsAsync` 用 `SemaphoreSlim _syncPersistGate` 包裹整个编辑会话，`finally` 释放。
  6. 页面状态保持：`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AuthViewFactory.cs` 持有 `_offlineChosen`，通过 `offlineChosen` / `offlineChosenChanged` 构造参数交给页面 VM；`AccountViewModel` 新增 `SetOfflineChosen`，`UseOffline` / `Connect` / `NotifyAuth` 统一走它；语言变更不再重置服务器输入。
  7. 无服务器拒登：`AccountViewModel.StartDeviceSignIn` 在服务器地址为空时打开服务器面板并返回 `Auth.Error.ServerRequired`，不再静默使用官方服务器。
  8. 跨模块映射：`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AuthLocalization.cs` 在 `SyncErrorCodes` 映射前写明依赖只到 `Mosaic.Sync.Contracts` 的理由。
  9. 任务列表无障碍：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml` 的列表项 `AutomationProperties.Name` 改绑 `AccessibilityLabel`，移除重复的 `HelpText`；附件指示器 `ToolTip` 与 `AutomationProperties.Name` 改绑新属性 `AttachmentLabel`；`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskListItemViewModel.cs` 新增 `AttachmentLabel`（`Tasks.Attachments.Accessibility`，`{0} 个附件`）。
  10. 主题词典与模板：`src/Mosaic.Presentation.Wpf/Themes/Theme.xaml` 删除内嵌浅色画刷副本，只由 `Palettes/*` 提供颜色；补齐 ComboBox `PART_EditableTextBox` 与 `IsEditable` 触发器、ProgressBar `PART_Track`、禁用态复选框字形/单选圆点颜色、ToolTip 文本换行、菜单项勾选列与快捷键列、下拉 `MaxHeight` 绑定 `MaxDropDownHeight`。
  11. 窗口 chrome 归位：删除 `src/Mosaic.Presentation.Wpf/MosaicWindowChrome.cs`，新增 `src/Mosaic.Shell.Wpf/MosaicWindowChrome.cs`（`Mosaic.Shell.Wpf` 命名空间）：`Attach` / `Detach` 用弱引用登记窗口，主题变化时切回窗口调度器再应用，关闭/已销毁句柄直接返回，高对比度跳过 DWM 着色，先试属性 20 再回退 19；`src/Mosaic.Shell.Wpf/ShellWindow.xaml.cs` 调用同名类型；`build/public-api.txt` 基线从 `Mosaic.Presentation.Wpf.MosaicWindowChrome` 改为 `Mosaic.Shell.Wpf.MosaicWindowChrome`。
  12. 多显示器钳制：`src/Mosaic.Shell.Wpf/ShellWindow.xaml.cs` 新增 `ClampToVirtualScreen`，按 `VirtualScreenLeft/Top/Width/Height` 组成的虚拟屏幕判断窗口与标题栏是否可见，只在越界时移动，不再按主屏工作区缩小。
  13. 配置键名：`src/Mosaic.Desktop/AppConfiguration.cs` 的 `WriteConfiguration` 显式写出小写 `engine` / `location` / `filename`。
  14. 能力绑定：`src/Mosaic.Desktop/appsettings.json` 新增 `"tasks.attachments@1": "tasks-core"`。
  15. 迁移与测试：新增 `src/Modules/Settings/Mosaic.Settings.Sqlite/Migrations/002_RemoveAccountChoice.sql`，`SettingsSqliteModule.ActivateAsync` 注册版本 2；`tests/Mosaic.Settings.Sqlite.Tests/SettingsRepositoryTests.cs` 新增 `Removed_account_choice_setting_is_deleted_and_other_settings_survive`，通用初始化用例改用中性键；`tests/Mosaic.Wpf.Tests/PresentationTests.cs` 的 `LoadTheme`、`tests/Mosaic.Desktop.SmokeTests/ButtonFeedbackTests.cs` 的测试窗口合并 `Palettes/Light.xaml`；`tests/Mosaic.Desktop.SmokeTests/GlobalUsings.cs` 关闭 smoke 并行；`tests/Mosaic.Desktop.SmokeTests/FloatingDragTests.cs` 拖动位移改为方向与量级区间、右边框缩放失败时最多重试 3 次，60×40 指针位移在非 100% DPI 上按设备无关像素取整，断言留 ±4 像素容差（实测 63px）；`tests/Mosaic.Desktop.SmokeTests/ButtonFeedbackTests.cs` 在指针释放后重新定位到按钮并等待悬停态再校验 0.88 亮度；`tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs` 新增 `TaskRow`，按 `TaskList` 下的 `ListItem` 控件类型与完整无障碍名称定位任务行（原按名称搜索会命中行内 TextBlock，没有 `SelectionItemPattern`），并把 `Until`/`UntilCondition` 的等待上限由 15 秒放宽到 30 秒（冷启动叠加本机同时运行其他实例时，任务行出现可能晚于 15 秒）；`tests/Mosaic.Wpf.Tests/AccountViewModelTests.cs` 修正 FakeAuth approved 登录态、登出用例顺序与摘要断言，删除已失效的「无服务器静默登录」用例；`tests/Mosaic.Wpf.Tests/ResponsiveLayoutTests.cs` 删除对已移除 `IsInitialChoiceActive` 的断言。

- 迁移逻辑与验收项：
  - 迁移：`settings` 表删除 `auth.accountChoiceMade` 行。该键在 0.18.7 起没有任何定义与读取方；旧安装升级后第一次启动即执行版本 2 迁移，其他设置行原样保留。
  - 配置：`appsettings.json` 新增 `tasks.attachments@1` → `tasks-core`，并对已存在的安装配置生效（配置由程序目录读取，升级替换程序文件后立即使用）。
  - 验收项：
    1. 用含 `auth.accountChoiceMade` 的旧 `mosaic.sqlite` 启动 0.18.7：启动进入任务页；设置页正常打开；`SELECT setting_id FROM settings WHERE setting_id='auth.accountChoiceMade'` 返回空；`auth.serverUrl` 等其余设置值不变。
    2. 全新安装启动：直接进入任务页，不再出现首次账户选择页；工作区无空白页。
    3. 已登录进入账户设置：账户卡片、同步开关、会话列表可见，只有一个 Logout 按钮。
    4. 登出/删除账户后再进入账户页：会话列表为空，最近备份与上次恢复不显示上一账户内容。
    5. 未填服务器时点「使用浏览器登录」：停在服务器面板并提示需要服务器地址。
    6. 双显示器保存窗口位置后拔掉副屏再启动：窗口出现在虚拟屏幕内且标题栏可抓取；副屏仍在时位置不被重置。
    7. 修改任一设置触发配置写回后，`appsettings.json` 的 `storage` 下仍是 `engine` / `location` / `filename` 小写键。
  - 对已有安装的影响：程序文件替换；SQLite `settings` 表执行一次删除行的迁移（有 `.pre-migration` 备份机制）；用户数据、账户、任务、附件、日志与备份目录不变。

- 验证：
  - `dotnet build Mosaic.sln -v:minimal -m:1`：0 警告 0 错误。
  - `build/test.ps1 -Configuration Release`：Release 构建 0 警告 0 错误，16 个测试工程 475 项全部通过（exit 0）——Architecture 4、Auth.Core 26、Auth.Http 36、Desktop.SmokeTests 54/54、DesktopPresence 17、FloatingWindows 12、Kernel 7、Maintenance 16、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 6、Storage.Sqlite 29、Sync.Core 14、Tasks.Contract 8、Tasks.Core 39、Wpf 165。smoke 在 30 秒等待上限与串行模式下 54 项全绿（71s）。
  - 主题用例同时校验浅色与深色解析，smoke 按钮禁用态取色通过；浮动窗口真实鼠标拖动/缩放/点击用例在串行模式下通过。
  - 发布核验：提交 `c802be8`（`main`）；tag `v0.18.7` 指向 `c802be8`；Release <https://github.com/soehi/Mosaic/releases/tag/v0.18.7> 非草稿、非预发布，资产 `Mosaic-0.18.7-win-x64.msi` 52,566,264 字节，SHA-256 `B5001F6AEB66E5082002C6E00677F8FE7634E8FC0E4C31034F12B6C5D24AC015`，与 GitHub 资产 digest `sha256:b5001f6aeb66e5082002c6e00677f8fe7634e8fc0e4c31034f12b6c5d24ac015` 一致；Release 正文与 `docs/releases/v0.18.7.md` 逐字一致（964 字符）；`build/verify-published.ps1 -PublishDirectory artifacts/local-run -ProductVersion 0.18.7` PASS（`published payload is a release build with manifest mosaic-0.18.7`）；`scripts/release_content_verify.py` PASS（53 条索引）。

- 未验证项：未在真实双显示器热插拔过程中重复验证窗口钳制；未在 Windows 10 1809–1903 实机上验证 DWM 属性 19 回退；未对 0.18.6 之前更老版本的 `settings` 表结构做额外迁移矩阵（表结构自 001 起未变）。

## 0.18.6：附件徽标矢量化、深色模式补全、启动固定任务页、安装完成自动启动、升级保留设置（2026-11-23）

- 根因位置：
  1. 回形针是字体字形且辅助名绑错：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml:212-220`（0.18.6 前）用 `TextBlock Text="&#xE723;" FontFamily="Segoe MDL2 Assets"` 渲染附件提示，着色 `TextMutedBrush`。触发条件：深色主题下列表行含附件任务；Segoe MDL2 Assets 缺失或被字体回退替换时字形显示为方框，且 `TextMuted` 在深色卡片上对比不足。同一 `TextBlock` 的 `AutomationProperties.Name` 绑定行模型的 `AccessibilityLabel`，读屏聚焦附件图标时朗读整条任务描述（标题、时间、完成状态），图标本身「含附件」的语义丢失。
  2. 深色模式只换调色板，控件模板仍是系统默认：`src/Mosaic.Presentation.Wpf/Themes/Theme.xaml` 在 0.18.6 前只定义 Button/TextBox/ComboBox/ListBox/ScrollBar 等隐式样式，`ToolTip`、`CheckBox`、`RadioButton`、`PasswordBox`、`ProgressBar`、`ContextMenu`、`MenuItem`、`Separator` 全部走 WPF Aero2 默认模板（系统浅色底、黑字、系统蓝高亮）。触发条件：深色主题下打开设置页复选项、账户页密码框、备份/迁移进度条、任意提示框或右键菜单，出现浅色块与黑字。窗口标题栏由 DWM 独立绘制，`MosaicThemeManager` 只切换资源字典，从不设置 `DWMWA_USE_IMMERSIVE_DARK_MODE`，所以深色主题下标题栏保持系统浅色。`src/Mosaic.Presentation.Wpf/Themes/Palettes/Dark.xaml` 的 Canvas `#0A0F0C`、Surface `#151D18`、Border `#33443A` 与卡片底色间距过小，深色下卡片与背景难分辨。
  3. 启动路径被首次账户选择劫持：`src/Mosaic.Shell.Wpf/ShellViewModel.cs` 的 `InitializeAsync`（0.18.6 前约 81-90 行）在 `SettingsPageRegistry.IsInitialChoiceActive` 为真时把 `InitialGroupId` 设为 `settings.home` 并导航设置页，覆盖 `Navigation` 第一项（`tasks.navigation`）。触发条件：全新安装或未完成账户选择时启动。与此同时 `src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs` 的 `ShowStartPage` 只在构造时按首次流程状态赋值、并在 `LogoutAsync` 时置真；若只删除启动劫持，未登录用户在非首次流程下主动进入账户页会得到 `ShowStartPage=false` 且 `ShowAccountSettings=false` 的空白页。
  4. 安装器没有启动动作：`build/installer/Package.wxs` 的 `InstallUISequence` 原本只有 1301 `Exit` 完成页，安装/升级/修复结束后不拉起应用。需求为安装结束默认打开软件。
  5. 升级保留设置缺少明确保证：per-user 安装把程序放在 `%LOCALAPPDATA%\Programs\Mosaic`（`Package.wxs:24` 的 `INSTALLFOLDER`），数据、配置、日志与备份在 `%LOCALAPPDATA%\Mosaic`（`AppPaths`/`Bootstrapper`），`MajorUpgrade Schedule="afterInstallInitialize"` 先移除旧版本安装目录内容。MSI 中没有任何组件删除数据根目录，但发布说明从未写明，用户无法据此判断升级是否清数据；同时升级会让正在运行的旧版本与新版本争用同一 SQLite 库。
- 改动位置：
  - 矢量回形针：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml:212-234` 改为 18px 高胶囊 `Border`（`AccentSoftBrush` 底、`BorderBrush` 1px 边、圆角 9）内嵌 10×12 `Path`，`Data` 为手写回形针轮廓（`M 16.5,6 L 16.5,17.5 A 4.5,4.5 0 0 1 7.5,17.5 L 7.5,5 A 3,3 0 0 1 13.5,5 L 13.5,15.5 A 1.5,1.5 0 0 1 10.5,15.5 L 10.5,6`），`Stroke` 取 `AccentTextBrush`，圆头线帽、`Stretch="Uniform"`、`SnapsToDevicePixels`；不再依赖任何字体。`ToolTip` 与 `AutomationProperties.Name` 改为本地化键 `Tasks.Attachments.Indicator`（0.18.4 已存在），读屏只报「含附件」，可见性仍由 `HasAttachments` 经 `BoolVisibility` 控制。
  - 深色控件模板：`src/Mosaic.Presentation.Wpf/Themes/Theme.xaml:423-793` 新增 9 个隐式样式，全部使用 `DynamicResource` 取调色板画笔，主题切换即时生效：
    - `ToolTip`（423-450）：Surface 背景、1px Border、圆角 8、阴影，`ContentPresenter` 显式继承模板 `Foreground`，不再黑字。
    - `CheckBox`（451-529）：18×18 圆角 5 方框 + 矢量对勾（`M 1,5 L 4.2,8 L 10,1.6`），选中时 `AccentButtonBackground` 填充、`AccentButtonText` 对勾，支持三态空值横线，悬停/键盘焦点用 `FocusBrush` 描边，禁用态用 `DisabledSurface`/`DisabledText`。
    - `RadioButton`（530-590）：18×18 圆 + 8px 圆点，同一套焦点与禁用配色。
    - `PasswordBox`（591-635）：自定义模板（`PART_ContentHost` ScrollViewer），背景/边框/前景均动态画笔，焦点 2px `FocusBrush` 边框。
    - `ProgressBar`（636-675）：`DisabledSurface` 轨道 + `AccentBrush` 指示条；`IsIndeterminate` 时经 EnterActions/ExitActions 启动/停止 0.25↔1 的透明度呼吸动画。
    - `ContextMenu`（676-701）：Surface 背景、圆角 9、阴影、`StackPanel IsItemsHost="True"`。
    - `MenuItem`（702-783）：完整模板含图标、标题、快捷键文字、子菜单箭头与 `PART_Popup`；`IsHighlighted` 用 SurfaceMuted，`Role="SubmenuHeader"` 显示箭头，禁用态完整。
    - `Separator`（784-793）：以 `{x:Static MenuItem.SeparatorStyleKey}` 注册，1px `BorderBrush` 分隔线。
  - 深色标题栏：`src/Mosaic.Presentation.Wpf/MosaicWindowChrome.cs`（新增）：`Attach(Window)` 用 `WeakReference<Window>` 登记窗口并在首次附加时订阅 `MosaicThemeManager.ThemeChanged`；`Apply` 取 `WindowInteropHelper.Handle`，按 `MosaicThemeManager.Current == MosaicThemeVariant.Dark` 调 `DwmSetWindowAttribute(hwnd, 20, 1/0, 4)`，返回非 0 时回退旧版本属性 19；捕获 `DllNotFoundException`/`EntryPointNotFoundException`（精简系统无 dwmapi 时保持系统标题栏）。`src/Mosaic.Shell.Wpf/ShellWindow.xaml.cs:45` 在构造期调用 `MosaicWindowChrome.Attach(this)`（配 `using Mosaic.Presentation.Wpf;`）。窗口销毁后由弱引用自动清理，主题切换时对仍存活的窗口逐一重放；`Apply` 先检查 `Dispatcher.CheckAccess()`，跨线程触发（测试宿主、多 UI 线程）时经 `Dispatcher.BeginInvoke` 回到窗口自己的线程再取 HWND，避免把 `InvalidOperationException` 抛进 `MosaicThemeManager.ApplyCore` 打断主题切换，并在窗口关闭后跳过着色。
  - 深色调色板层次：`src/Mosaic.Presentation.Wpf/Themes/Palettes/Dark.xaml` 调整 9 个值——Canvas `#0B110D`（4 行）、Surface `#18211B`（5）、SurfaceMuted `#212C24`（6）、Sidebar `#0F1612`（7）、Border `#3A4C41`（8）、TextSecondary `#C0D0C4`（9）、TextMuted `#A0B3A5`（10）、ControlHoverBorder `#62806D`（21）、DisabledSurface `#2A352E`（23）。
  - 启动固定任务页：`src/Mosaic.Shell.Wpf/ShellViewModel.cs:70-84` 删除首次流程分支（原设置 `_firstChoiceActive`/`InitialGroupId` 并导航 `settings.home`），`InitializeAsync` 现在只在读取并应用设置后导航 `Navigation.FirstOrDefault()`（即 `tasks.navigation`，`NavigationItem` 序号 100 的第一项）；删除不再使用的 `IsAccountChoiceMade`。
  - 账户页不再空白：`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs:46-47` 把 `ShowStartPage`/`ShowAccountSettings` 改为由登录态派生（`ShowStartPage => IsSignedOut`、`ShowAccountSettings => IsSignedIn`），删除 `_showStartPage` 字段与构造/`LogoutAsync` 赋值；`NotifyAuth`（1045-1050）补两个属性的变更通知，登录/离线的切换都会重算两页的可见性。
  - 安装结束自动启动：`build/installer/Package.wxs:67-75` 新增 `CustomAction Id="LaunchMosaic"`（`Directory="INSTALLFOLDER"`、`ExeCommand="[INSTALLFOLDER]Mosaic.Desktop.exe"`、`Execute="immediate"`、`Impersonate="yes"`、`Return="asyncNoWait"`，Type 34 拉起后立即返回，不阻塞完成页）；`InstallUISequence` 在 `VerifyRepairedPayload`（1301）与 `Exit`（1303）之间插入 `LaunchMosaic`（112 行），条件 `NOT REMOVE AND NOT (MOSAICSTATE = "Maintenance" AND MOSAICSCANSTATUS = "Healthy")`：卸载不启动、健康维护（同版本无损扫描）不重复拉起，静默安装不进入 UI 序列因此也不会启动。文案同步：`build/installer/Dialogs.wxs:67`（安装确认页）与 `:110`（升级提示页）写明安装/升级完成后自动打开并保留用户数据与设置。
  - 升级保留设置：不改安装目录布局（`Package.wxs:24` per-user `%LOCALAPPDATA%\Programs\Mosaic`），数据根 `%LOCALAPPDATA%\Mosaic` 不进入任何 MSI 组件，`MajorUpgrade` 只替换安装目录文件；升级流程沿用已有的「写入前关闭安装目录内进程」动作，避免旧实例与新版本同时持有 `mosaic.sqlite`。`README.md:60`/`:62` 与 `docs/implementation-status.md` 写明保留范围与位置。
  - 架构清单与测试：`build/public-api.txt` 在 `Mosaic.Presentation.Wpf.MosaicThemeVariant` 后新增 `Mosaic.Presentation.Wpf.MosaicWindowChrome`（公开类型清单校验）；`tests/Mosaic.Wpf.Tests/ResponsiveLayoutTests.cs:373` 新增 `Shell_starts_on_tasks_even_when_the_account_choice_is_still_open`（用 `ContributionRegistry` + `TasksUiModule` + 空 `SettingsPageRegistry` 建 `ShellWindow`，`InitializeAsync` 后断言 `CurrentNavigationId == "tasks.navigation"` 且首次流程未被激活）；`tests/Mosaic.Wpf.Tests/AccountViewModelTests.cs:619` 新增未登录账户页显示开始页与登录入口、`:633` 新增登录后切到账户设置页；`tests/Mosaic.Wpf.Tests/PresentationTests.cs:385` 的深色画布色断言同步到新值 `#0B110D`（原 `#0F1512`），其余可读性对比度阈值不变。
  - 版本与发布元数据：`Directory.Build.props:12`（0.18.6）、`build/release-content.json:835`（`mosaic-0.18.6`，`previousReleaseId=mosaic-0.18.5`，指纹 `A2986AFE7978CA2F9D9E85453C84C092516B02EF4AB9B50FBB4D16A3DC194C0F`）、`build/published-release-index.json:258`（第 52 条）、`build/release-manifest.json`、`docs/releases/v0.18.6.md`、`README.md:3`/`:60`/`:63`。
- 迁移逻辑与验收项：数据库结构、设置项与文件布局均不变，无新增列或新表；0.18.5 及更早的库、`appsettings.json`、订阅令牌与附件目录在升级后原样可用。已有安装升级后验收项：①MSI 升级完成后 Mosaic 自动启动并落在任务页，主题、语言、任务偏好等设置与升级前一致；②安装目录只有程序文件被替换，`%LOCALAPPDATA%\Mosaic` 下的 `Data\mosaic.sqlite`、`Config\appsettings.json`、`Logs`、附件与备份目录的修改时间不变；③卸载不会删除 `%LOCALAPPDATA%\Mosaic`；④静默安装（`msiexec /i ... /qn`）结束后不自动启动；⑤同版本健康维护不启动应用；⑥深色主题下复选框、密码框、进度条、提示框、右键菜单与标题栏均无浅色块与黑字；⑦列表附件提示在深色/浅色下均为矢量回形针且读屏只报「含附件」；⑧未登录进入账户页显示开始页，登录后显示账户设置。
- 验证：`dotnet build Mosaic.sln -c Release --no-restore -m:1` 0 警告 0 错误；`python -I scripts/release_content_verify.py` 通过（52 个已索引发布，`0.18.6`/`mosaic-0.18.6`）；`build/publish.ps1` 重新生成 `artifacts/local-run` 自包含负载成功。`build/test.ps1 -NoBuild`：`Mosaic.Wpf.Tests` 167/167 通过（含新增的「启动落任务页」「未登录显示开始页/登录后显示账户设置」用例，以及同步调色板断言后的可读性用例）；其余项目全通过——Architecture 4、Kernel 7、Auth.Core 26、Auth.Http 36、Sync.Core 14、Tasks.Contract 8、Tasks.Core 39、Storage.Sqlite 29、Settings.Core 25、Settings.Sqlite 5、DesktopPresence 17、FloatingWindows 12、Maintenance 16、ReleaseSummary 17。`Mosaic.Desktop.SmokeTests` 54 例中 3 例失败，均为既有基线漂移、与本轮改动无关：`StartupStorageRelocationTests.Failed_relocation_keeps_the_source_and_reports_the_failure` 直接调用未改动的 `SqliteStorageRelocation.RunAsync`；`DesktopTests` 两例比较配置 JSON 文本，实际值已包含 0.18.4 在 `TasksModule.cs:60` 声明并写回的 `tasks.attachments` 绑定、且本机配置被其他组件以带缩进的格式重写过。
- 未验证项：未在真实干净虚拟机执行全新安装与跨版本升级的完整 UI 序列回放（启动动作为 `asyncNoWait`，人工验证以运行 MSI 与观察进程为准）；未在 Windows 10 1809/1903 实机验证 DWM 属性 19 回退；深色模式模板未做系统高对比度主题截图验收。

## 0.18.5：修复 0.18.4 升级后启动失败（迁移摘要换行敏感）（2026-11-23）

- 根因位置：`src/Modules/Storage/Mosaic.Storage.Sqlite/Internal/MigrationRunner.cs` 的 `Digest(string sql)` 直接对迁移 SQL 原文取 SHA-256。迁移 SQL 以嵌入资源从 `src/Modules/**/Migrations/*.sql` 读取，文件换行随 Git 检出（`core.autocrlf`）或编辑器保存变化；`mosaic_migrations.digest` 记录的是建库当次构建的原文摘要。本机升级前的库中 `settings-sqlite` v1 与 `tasks-sqlite` v1/v2/v5 记录的是 LF 摘要，`tasks-sqlite` v3/v4 记录的是 CRLF 摘要；0.18.4 的嵌入资源里 `001_CreateSettings.sql` 等为 CRLF、`004_TaskAttachments.sql` 为 LF，于是启动时 `SettingsSqliteModule.ActivateAsync` 的摘要校验失败，抛出「Settings migration failed.」，`Mosaic.Desktop.Bootstrapper.StartAsync` 记录本地存储不可用并结束进程。触发条件：升级安装的二进制与旧库建库时的迁移文件换行不一致，SQL 文本本身没有变化。日志证据：`%LOCALAPPDATA%\Mosaic\Logs\mosaic-20261123.log` 08:41:53Z「Operation failed: 07de3c0dbd3b44fa9202ba5d46bad758 ... Settings migration failed. ... Bootstrapper.StartAsync」。
- 改动位置：
  - `src/Modules/Storage/Mosaic.Storage.Sqlite/Internal/MigrationRunner.cs`：新增 `Normalize`（`\r\n` 与孤立 `\r` 统一为 `\n`）与 `RawDigest`；`Digest` 改为对归一化文本取 SHA-256；`MatchesLegacyDigest` 接受旧算法在 LF 原文与 CRLF 原文下的两种摘要；`Apply` 的已应用行校验先比对规范摘要，不匹配时再按历史摘要兼容判定，通过后 `UPDATE mosaic_migrations SET digest=$digest` 回写规范值，仍不匹配则维持原来的「Migration checksum or database version is incompatible.」失败；版本越界检查独立保留。新增迁移仍写入规范摘要。
  - `tests/Mosaic.Storage.Sqlite.Tests/MigrationDigestCompatibilityTests.cs`（新增，4 例）：旧 CRLF 摘要 + 当前 LF 资源、旧 LF 摘要 + 当前 CRLF 资源都通过并回写规范摘要；回写后再次在 LF/CRLF 之间切换仍通过；真正改动 SQL 文本仍失败。
  - 版本与发布元数据：`Directory.Build.props:12`（0.18.5）、`build/release-content.json`（第 51 条 `mosaic-0.18.5`，`previousReleaseId=mosaic-0.18.4`，指纹 `99F58A27BA892AF8DEF77193270600E7EFE0AA9A83BCCB6A0C5FE4E9C101E7A1`）、`build/published-release-index.json`（第 51 条）、`build/release-manifest.json`、`docs/releases/v0.18.5.md`、`README.md:3` 与当前版本说明/下载链接。
- 迁移逻辑与验收项：数据库结构不变，无新表新列；升级时只把 `mosaic_migrations.digest` 规范化为 LF 摘要，不改业务数据。已有安装升级后：①应用正常启动，设置、任务、附件计数与列表原样可见；②`SELECT module_id, version, digest FROM mosaic_migrations` 的值等于对应 SQL 以 LF 归一化后的 SHA-256 大写十六进制；③再次启动不重复改写；④人为改动任一迁移 SQL 后启动仍失败，防止被篡改的迁移静默通过；⑤全新数据库首次启动建表并写入规范摘要。
- 验证：`MigrationDigestCompatibilityTests` 4/4 通过；`Mosaic.Storage.Sqlite.Tests` 29/29 通过；受影响测试集合（Storage/Wpf/Tasks.Core/Contracts/Architecture/FloatingWindows/Maintenance）272/272 通过；用本机真实 `mosaic.sqlite` 副本（settings v1、tasks v1–v5 的 LF/CRLF 混合摘要）驱动 `SettingsSqliteModule`、`TasksSqliteModule`、`FloatingWindowsSqliteModule`，三个模块全部激活成功并回写摘要；Release 构建 0 警告 0 错误；`build/publish.ps1` 与 `build/package.ps1 -NoPublish` 校验通过。
- 未验证项：未在多台不同 Git 配置（`core.autocrlf=false`）生成的旧库上重复升级；未覆盖迁移文件出现混合换行的数据库（当前只接受纯 LF 或纯 CRLF 的历史摘要）。

## 0.18.4：附件拖出改纯复制、矢量回形针、附件类型徽标（2026-11-23）

- 根因位置：
  1. 拖出带「移动」语义：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TaskEditorView.xaml.cs:123` `AttachmentItem_MouseMove` 以 `Copy|Move` 发起拖拽，并把内部格式 `Mosaic.TaskAttachmentTransfer` 放进 `DataObject`；应用内目标（`AttachmentDropZone_Drop` 与速览/浮窗的 `MoveCompleted`）收到内部格式后调用 `RemoveAttachmentAsync(TaskAttachmentDragTransfer)` 删除附件记录与文件。触发条件：按住 Shift 拖出、外部接收方声明 Move、或在速览/浮窗/编辑器之间放置——用户本意是导出副本，结果任务里的附件被移除。`Internal/TaskAttachmentDragRegistry.cs` 为这条 Move 结算链维护暂存登记，Move/Delete 路径因此跨视图耦合。
  2. 回形针是彩色 emoji：`Views/TasksView.xaml` 的数量徽标直接写 `📎`，`Internal/TaskListItemViewModel.cs` 提供 `AttachmentCountLabel`。触发条件：任何含附件任务在主列表显示徽标；emoji 字形随系统字体渲染，与左侧导航等处的 Segoe MDL2 矢量图标风格不一致，且数量文字在窄列表里挤压标题。
  3. 附件行没有类型标识：`Internal/TaskAttachmentPanel.cs` 与 `Views/TaskEditorView.xaml` 的附件行只渲染文件名与大小，同扩展名文件无法在列表里快速区分（PDF、表格、图片、压缩包外观完全相同）。
- 改动位置：
  - 拖出改纯复制：
    - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskAttachmentDrag.cs`（新增）：`StageAsync`（经 `ITaskAttachmentApi.OpenReadAsync` 流式复制到 `%TEMP%\MosaicAttachments\{attachmentId}\{fileName}`）、`CreateData`（仅 `FileDrop` + 内部标记，供应用内目标拒绝重复添加）、`Run`（`DoDragDrop` 只声明 `DragDropEffects.Copy`）、`Release`（拖拽结束后删除暂存副本，失败不影响结果）。`Effects` 常量固定为 Copy，不再提供 Move。
    - `Views/TaskEditorView.xaml.cs:72-85`：`DragOver` 只接受外部 `FileDrop` 并给 Copy，内部标记一律 None；`Drop` 遇内部标记直接返回，应用内放置不再产生第二份附件；`AttachmentItem_MouseMove:123` 改为异步暂存后 UI 线程同步 `TaskAttachmentDrag.Run`，`finally` 中 `Release`。
    - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskAttachmentPanel.cs:18`：构造函数改为 `TaskAttachmentPanel(ITaskAttachmentApi? api)`，鼠标拖出走同一 `TaskAttachmentDrag` 路径；不再有 `MoveCompleted` 回调。
    - `src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskAttachments.cs`：删除公开类型 `TaskAttachmentDragTransfer`（含 TaskId/AttachmentId/StagingDirectory/ExpectedRevision）；`build/public-api.txt` 同步移除该行。
    - 删除 `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskAttachmentDragRegistry.cs`；`TasksUiModule.cs` 的 `TasksUiDependencies` 去掉注册表属性；`TasksViewModel`、`TaskViewFactory`、`TaskEditorViewModel.RemoveAttachmentAsync(TaskAttachmentDragTransfer, …)`、`TaskPeekDetailsView`、`TaskPeekViewFactory.RemoveAttachmentFromPeekAsync`、`TaskFloatingDetailsView`、`TaskFloatingNotesViewModel.RemoveAttachmentAsync(TaskAttachmentDragTransfer)`、`TaskFloatingContentFactory` 的 Move 接线一并移除。速览与浮窗现在只接收 `dependencies.Attachments` 用于展示与拖出。
  - 矢量回形针：`Views/TasksView.xaml:212-220` 用 `Segoe MDL2 Assets` 的 `&#xE723;` 图标替换 emoji，右对齐、`TextMutedBrush` 着色，可见性仍绑 `HasAttachments`，不再绑定数量；`Internal/TaskListItemViewModel.cs` 删除 `AttachmentCountLabel`，保留 `HasAttachments` 与仅读屏使用的 `AccessibilityLabel`（`Tasks.Attachments.Accessibility` 仍含数量）；`Localization/zh.xaml:141`/`en.xaml:141` 新增 `Tasks.Attachments.Indicator`（含附件 / Has attachments），删除 `Tasks.Attachments.Count`。
  - 类型徽标：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskAttachmentVisuals.cs`（新增）：`TaskAttachmentKind` 枚举与扩展名映射（PDF、文档、表格、演示、图片、压缩包、音频、视频、代码、文本、其他），`Classify`/`Label`（扩展名 ≤4 字符，大写，无扩展名退化为 FILE）/`SizeLabel`/`CreateIcon`（18px 高圆角徽标，类型色前景 + 同色 0x26 透明度底色，冻结画笔，读屏名称为扩展名），`AttachmentTypeConverter` 供 XAML 取 `label`/`background`/`foreground`。`Views/TaskEditorView.xaml` 引入 `xmlns:internal` 与转换器资源，已保存与草稿两类附件行都改为「类型徽标 + 文件名（`CharacterEllipsis`）+ 大小/提示」三列网格；`Internal/TaskAttachmentPanel.cs`（速览/浮窗）使用 `TaskAttachmentVisuals.CreateIcon` 渲染同一套徽标；`Localization` 拖出提示改为 `Tasks.Attachments.DragHint`「拖出以复制导出 / Drag out to export a copy」。
  - 测试：`tests/Mosaic.Wpf.Tests/TaskAttachmentVisualsTests.cs`（新增，19 例）：扩展名分类与 ASCII 徽标标签（12 组）、大小格式化（4 组）、图标配色与无 📎、面板行三列布局与空列表折叠、拖出数据只含 FileDrop + 内部标记且效果为 Copy。
- 迁移逻辑与验收项：无数据库结构变化（沿用 0.18.0 的 `task_attachments` 表），无新增或删除设置项，`TaskItemDto.AttachmentCount` 保持原字段（仅用于 `HasAttachments` 与读屏标签），旧数据无需迁移。已有安装升级后：①任意方向拖出附件（含 Shift）都只复制，任务速览/浮窗/编辑器中的附件记录与文件不变；②主列表含附件任务显示矢量回形针、无数字；③旧附件按扩展名显示对应颜色徽标。验收项：从编辑器、速览、浮窗分别拖出到资源管理器 → 目标出现副本、源任务附件仍在；应用内跨视图放置 → 不新增重复附件；主列表缩放窗口 → 回形针不挤压标题；无扩展名文件显示 FILE 徽标。
- 验证：`dotnet build` WPF 模块与测试项目 0 警告 0 错误；`TaskAttachmentVisualsTests` 19/19 通过；受影响测试集合（Wpf/Tasks.Core/Sqlite/Contracts/Architecture/FloatingWindows）252/252 通过；`python -I scripts/release_content_verify.py` 通过（50 个已索引发布）。
- 未验证项：真实鼠标拖拽到 Outlook 等 OLE 目标未做实机回放；图标配色在系统高对比度主题下未单独截图验收。

## 0.18.3：附件体验修复（新建附件区、拖出崩溃、速览/浮窗附件、列表徽标）（2026-11-22）

- 根因位置（按用户报告的四个问题逐一对应）：
  1. 新建任务无附件区：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TaskEditorView.xaml` 附件区块 `Visibility` 绑定 `Editor.HasTask`（`_current is not null`），草稿阶段恒为 Collapsed；且 `TaskEditorViewModel.CanManageAttachments`（`HasTask && !IsArchived`）与 `AddAttachmentsAsync`（`_current is null` 直接返回 null）都以已存在任务为前提，草稿无处挂附件。
  2. 拖出崩溃：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TaskEditorView.xaml.cs` 的 `AttachmentItem_MouseMove` 为 `async void`，`await` 恢复后调用的 `DragDrop.DoDragDrop` 在 WPF 内部构造拖放事件路由/访问 Dispatcher 关联对象，一旦落回非创建线程即抛「调用线程无法访问此对象」类跨线程异常，未经处理导致进程退出。另有两个同源缺陷：外部接收方（如 Outlook/资源管理器 Move）在 `DoDragDrop` 返回后仍可能持有暂存文件，源端立即 `Directory.Delete` 会 IOException（被 catch 吞掉、目录残留）；`DoDragDrop` 对应用内放置返回 `None`，源端无法据此结算应用内 Shift 移动。
  3. 速览/浮窗无附件：`TaskPeekDetailsView` 与 `TaskFloatingDetailsView` 只渲染标题、备注与时间，从未调用 `ITaskAttachmentApi`；`TasksUiDependencies.Attachments` 只传给了主编辑器。
  4. 主列表无附件提示：`TaskItemDto` 没有任何附件计数字段，`TaskListItemViewModel` 只有标题/完成状态/创建时间，列表模板无徽标位置。
- 改动位置（行号为本次提交状态附近）：
  - 草稿附件：`Internal/TaskEditorViewModel.cs`：`CanManageAttachments` 改为 `!IsArchived && _attachmentApi is not null`；新增 `PendingAttachments`（`ObservableCollection<PendingTaskAttachment>`）、`HasAttachments`；`AddAttachmentsAsync` 草稿分支按数量/大小校验后暂存来源路径；新增 `FlushPendingAttachmentsAsync` 在保存成功后逐条落库并以最终 Revision 回写 `_current`。`Internal/PendingTaskAttachment.cs`（新增）暂存记录类型。`Internal/TasksViewModel.cs` `SaveAsync`（约 552 行）：`Editor.Load(result.Value)` 后调用 Flush，再以 `Editor.Current ?? result.Value` 驱动 `EnsureTaskVisible`。`Views/TaskEditorView.xaml`：附件区 Visibility 改绑 `Editor.CanManageAttachments`；新增草稿暂存列表（📎 文件名 +「保存后添加」提示）。
  - 拖出崩溃：`Views/TaskEditorView.xaml.cs` `AttachmentItem_MouseMove` 改回同步方法、UI 线程直接调用 `DoDragDrop`；一次性提供 `Copy|Move`，效果交给接收方与 Shift 键；暂存结算移交注册表（Move 时不再立即删除暂存目录）。新增 `TaskAttachmentDragTransfer`（`src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskAttachments.cs`，含 TaskId/AttachmentId/StagingDirectory/ExpectedRevision 与自定义格式名 `Mosaic.TaskAttachmentTransfer`）随 FileDrop 一并放入 DataObject；`AttachmentDropZone_DragOver/Drop` 识别该格式：应用内放置由目标端按 Shift 决定 `RemoveAttachmentAsync` 或仅清理暂存（OLE 不会对应用内目标回调源端）。`Internal/TaskAttachmentDragRegistry.cs`（新增）：按附件 Id 登记暂存，`CommitMove/CommitCopy/Discard` 统一清理临时目录，目录删除失败不影响结果。`TaskEditorViewModel.RemoveAttachmentAsync(TaskAttachmentDragTransfer)` 校验 TaskId 与拖拽开始时的 ExpectedRevision，防止移动期间外部修改被静默覆盖。
  - 速览/浮窗附件：`Internal/TaskAttachmentPanel.cs`（新增）：共享附件列表组件（📎 文件名 + 大小），预览鼠标事件实现拖出，同样在 UI 线程同步 `DoDragDrop`，Move 通过 `MoveCompleted` 回调交给宿主编排。`Internal/TaskPeekDetailsView.cs`：构造函数接受注册表，Render 详情分支挂载附件面板。`Internal/TaskPeekViewFactory.cs`：详情查询成功后 `TrackDetailAttachments` 拉取附件（异常走诊断），`RemoveAttachmentFromPeekAsync` 处理 Move 删除并刷新。`Internal/TaskFloatingDetailsView.cs`：附件面板独占一行（带 132px 上限的 ScrollViewer），不挤压备注编辑区；`Internal/TaskFloatingNotesViewModel.cs`：`Attachments` 状态、`RefreshAttachmentsAsync`、`RemoveAttachmentAsync`；`Internal/TaskFloatingContentFactory.cs`：装配面板与 Move 回调（经 Dispatcher + Track 追踪），`ReloadAsync` 成功后刷新附件；`QueryContentDrag` 命中附件行时返回 `None`，避免浮窗把手势当成移动窗口。`TasksUiModule.cs` `TasksUiDependencies.AttachmentTransfers`：编辑器/速览/浮窗共享同一注册表实例（internal 属性）。
  - 列表徽标：`src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskRequests.cs`：`TaskItemDto` 增加 `AttachmentCount`（init）与 `HasAttachments`；`src/Modules/Tasks/Mosaic.Tasks.Persistence.Contracts/ITaskAttachmentRepository.cs`：新增 `CountsByTasksAsync` 批量计数；`Internal/SqliteTaskAttachmentRepository.cs`：`GROUP BY task_id` 单查询实现。`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/AttachmentCountingTasksApi.cs`（新增）：`ITasksApi` 装饰器，List 批量一次查询计数、Get/Create/Update/SetCompleted/SetArchived 回包前同步计数，计数失败回退 0 不阻断任务操作。`TasksModule.cs`（约 43 行）：对外 `Api` 与 `Transfer` 走装饰器，`CreateOnceApi` 与自动归档内部仍用原始 API（避免归档轮询逐任务查计数）。`Internal/TaskListItemViewModel.cs`：`HasAttachments`/`AttachmentCountLabel`，无障碍标签含附件数；`Views/TasksView.xaml`：创建时间行右侧加 📎 数量徽标。
  - 本地化：`Localization/zh.xaml`/`en.xaml` 新增 `Tasks.Attachments.PendingHint/Count/Accessibility`。
  - 测试：`tests/Mosaic.Tasks.Core.Tests/AttachmentCountingApiTests.cs`（新增，3 例：List 单次批量查询并盖章计数、非零计数与 HasAttachments、计数失败回退 0）；`tests/Mosaic.Storage.Sqlite.Tests/TaskAttachmentRepositoryTests.cs` 新增批量分组计数用例（空集合、无附件任务不出现）；`tests/Mosaic.Tasks.Core.Tests/AttachmentCleanupTests.cs` 仓储桩补 `CountsByTasksAsync`；`build/public-api.txt` 补齐 0.18.0 遗漏的附件公开类型清单（9 条，注册表接口为 internal 不入清单）。
  - 版本与发布元数据：`Directory.Build.props`（0.18.3）、`build/release-content.json`（第 49 条 `mosaic-0.18.3`，4 条 fixed，指纹 C4BCC87BF886887BFB05164FFE8E70CBEC76325F7FE5A900C4B468C2A4CFA31D）、`build/published-release-index.json`、`build/release-manifest.json`、`docs/releases/v0.18.3.md`、`README.md`、本文件。
- 迁移逻辑与验收项：无数据库结构变化（沿用 0.18.0 的 `task_attachments` 表）；无新设置；DTO 新字段默认 0，对旧装配（无附件仓储）行为不变。已有安装升级后：①新建任务即可看到附件区，草稿附件在保存后落库；②0.18.0 已存在的附件在速览/浮窗/列表徽标中立即可见，无需数据修复；③旧版遗留的 `%TEMP%\MosaicAttachments\{id}` 残留目录不再扩大，新版拖拽结束即清理。验收项：新建任务拖入附件→保存→附件出现在列表且主列表出现 📎；编辑器/速览/浮窗任一处拖出到资源管理器（Copy）原件保留、Shift 拖出（Move）原件与记录删除且不再崩溃；含附件任务主列表显示数量。
- 验证：`dotnet build Mosaic.sln` 0 警告 0 错误；Core 32/32、Sqlite 24/24、Wpf 143/143、Architecture 4/4（含公开 API 清单）通过；`python -I scripts/release_content_verify.py` 通过（49 个已索引发布）。基线对照：`Mosaic.Desktop.SmokeTests` 3 个失败在未改动的基线（0ca6d3d）上同样失败，属环境性预存在问题，与本轮无关。
- 与 main 0.18.2 重构的整合：main 在我修复期间把附件文件系统下沉到 `ITaskAttachmentBlobStore`（`TaskAttachmentDto.StoredPath` 已出公开契约，新增 `ITaskAttachmentApi.OpenReadAsync`）。本版拖出暂存改为经 `TaskEditorViewModel.OpenAttachmentAsync` 后台流式复制（`StageAttachmentCopyAsync`），`TaskAttachmentPanel`（速览/浮窗）同样改走 OpenReadAsync；`TaskEditorViewModel` 注入 `IUiDispatcher` 供后台线程回报错误；保留 main 的附件错误本地化与 `OnCurrentChanged` 通知；`ITaskAttachmentRepository.InsertAsync` 采用 main 的 `(attachment, storedPath)` 签名；批量计数 `CountsByTasksAsync` 并入 main 的仓储接口；公开 API 清单保留 `TaskAttachmentDragTransfer` 与 `ITaskAttachmentBlobStore`。
- 未验证项：真实鼠标拖拽（含拖到 Outlook 等 OLE 目标与应用内跨视图放置）未做实机回放；草稿附件在「放弃未保存修改」时随 `Editor.Load` 一并清空（与标题/备注草稿同生命周期），未提供单独移除草稿附件的按钮。
## 0.18.2：附件文件系统下沉 blob 端口、stored_path 出公开契约（2026-11-22）

### 补：同批次死代码与边界 bug 清理（P5）

- 根因位置：
  - `FileSystemTaskAttachmentBlobStore.SaveAsync` 用 `FileMode.CreateNew` 写目标但异常分支无条件 `TryDelete(storedPath)`：目标已存在时 `CreateNew` 直接抛异常、`stored` 未创建，catch 分支把**已存在的附件文件删掉**。触发条件：附件根目录内残留同名旧文件（上次落库失败、手工复制）后再添加同扩展名附件。同时源文件若就在目标位置（把附件目录内的文件再拖入），也被当作正常保存处理，产生「删源即删目标」的最坏路径。
  - `TaskAttachmentService.AddAsync` 无「源已在附件目录」判定：附件目录内的文件可被再次复制并落新记录，等于用应用自己的副本污染数据。
  - `TaskEditorViewModel.AddAttachmentsAsync`（`TaskEditorViewModel.cs:184`）：循环内每次 `_current = result.Value` 已更新修订号（正确），但 `failed > 1` 时提示不显示失败数量，且 `CancellationToken` 取消后仍逐个继续调用，每次返回 `CANCELED` 覆盖真实错误。
  - `TaskEditorViewModel.LoadAttachmentsAsync`（`:182-184`）：`ListAsync` 失败时静默返回，界面显示空附件列表，用户无法察觉数据库不可用。
  - `TaskEditorViewModel.RemoveAttachmentAsync` / `ExportAttachmentAsync`：更新 `_current` 后只刷新附件列表，未通知 `Current`/`IsArchived`/`CanManageAttachments` 等依赖修订号的属性（批量添加路径有通知、单条删除/导出没有），归档状态在删除最后一项附件后可能仍显示旧值。
- 改动位置：
  - `src/Modules/Tasks/Mosaic.Tasks.Core/Internal/FileSystemTaskAttachmentBlobStore.cs`：`SaveAsync` 改为「源位于 `IsWithinStore` 即拒绝（`Tasks.Attachments.Error.AlreadyStored`）」+ `created` 标记，只在本次真正创建目标后才在失败/取消时清理；目标已存在返回 `Tasks.Attachments.Error.SaveConflict`，不再触碰原文件；抽出 `PathFor(root, taskId, attachmentId, fileName)` 作为受管布局唯一写出点，`Resolve` 复用它；`Root` 降为私有（唯一读点是 `CanStore` 与内部方法，公开 getter 已无调用方）。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskEditorViewModel.cs`：`AddAttachmentsAsync` 加 `try/finally` + 取消短路 + `ReportAddFailure`（有失败即显示「本次失败 N 项」，本地化键 `Tasks.Attachments.Error.WithCount`）；`LoadAttachmentsAsync` 失败走 `TaskErrorLocalizer`；新增 `OnCurrentChanged()` 在删除/导出/批量添加后统一重通知 `Current`/`Metadata`/`IsDirty`/`IsArchived`/`CanManageAttachments`（不重置标题/备注输入）。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Localization/zh.xaml` / `en.xaml` / `Internal/TaskErrorLocalizer.cs`：新增 `Tasks.Attachments.Error.SaveConflict`、`Tasks.Attachments.Error.AlreadyStored`、`Tasks.Attachments.Error.WithCount` 三键与映射。
  - 测试：`tests/Mosaic.Tasks.Core.Tests/AttachmentBlobStoreTests.cs` 增 2 例——「再次拖入已存储文件被拒且原文件完好」「目标已存在报 SaveConflict 且原内容保留」。
- 验证：`dotnet build Mosaic.sln -c Release -p:NuGetAudit=false` 0 警告 0 错误；Core 37/37（含新增 2 例）、Sqlite 24/24、Contract 8/8、Architecture 4/4、Wpf 145/145；全解决方案 447 例中 8 例失败，与 `git stash` 基线逐条一致（同样 8 项 Desktop SmokeTests，WPF/DPI/JSON 期望差异），未引入新失败。

- 根因位置：
  - 附件绝对路径泄漏到公开契约：`src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskAttachments.cs` 的 `TaskAttachmentDto`（0.18.0 版本第 14-19 行）带可写属性 `StoredPath`，SQLite 的 `stored_path` 列值随 DTO 流到 WPF 层，`src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TaskEditorView.xaml.cs:128/135` 直接 `File.Exists(dto.StoredPath)`/`File.Copy(dto.StoredPath, …)` 访问磁盘。后果：存储布局（`{root}\{taskId}\{attachmentId}{ext}`）成为契约的一部分，任何宿主布局改动都是破坏性变更；UI 层成为文件系统的第二个所有者，附件根目录边界规则散落在 service、cleanup、view 三处。
  - 导出目标目录校验可绕过：`TaskAttachmentService.ExportAsync`（0.18.1 版本）用 `targetDirectory.StartsWith(store.Root, StringComparison.OrdinalIgnoreCase)` 判定，`C:\AttachmentBackup` 与 root `C:\Attachment` 同前缀即被误判为 store 内部（拒绝合法导出）；路径规范化差异也可能把真实内部目录放行。
  - 文件删除非幂等：`TaskService.TryDeleteAttachmentFile` 与 `TaskAttachmentCleanup.CleanupDueAsync` 都是「先 `File.Exists` 再 `File.Delete`」，文件已被外部删除或路径失效时静默跳过；`TaskService.DeleteAsync` 的清理依赖 `attachmentStore?.Root`，root 为空（未配置附件目录）时删任务不再清理任何附件文件。
  - 端口不完整：`ITaskAttachmentBlobStore` 只有 OpenRead/Save/Delete 三个方法且 `SaveAsync` 返回 `OperationResult`（丢掉落库所需路径），无 `CanStore`、无边界判定入口；仓储契约 `InsertAsync(TaskAttachmentDto)` 只能从 DTO 上取 `StoredPath`。
- 改动位置（未另注明的行号为本次提交状态）：
  - 端口：`src/Modules/Tasks/Mosaic.Tasks.Persistence.Contracts/ITaskAttachmentBlobStore.cs`：收敛为 `CanStore`、`SaveAsync(...)→OperationResult<string>`（返回落库路径）、`OpenReadAsync`、`DeleteAsync`、`IsWithinStore(string)`。
  - 实现：`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/FileSystemTaskAttachmentBlobStore.cs`（新增，替代 `TaskAttachmentStore`）：`Apply(IReadOnlyDictionary)` 单点决定 root（`Path.GetFullPath`，非法即 null）；`SaveAsync` 建目录 + 流式复制，失败/取消清残留；`Resolve` 优先用仓储历史路径但必须 `IsWithinStore`，否则按 `(taskId, attachmentId, ext)` 重新推导（兼容升级前写入的旧记录）；`DeleteAsync` 幂等（缺失视为成功）；`IsWithinStore` 改用 `Path.GetRelativePath` 判定（不受同名前缀目录影响，目标目录允许尚未创建）。
  - 仓储契约：`ITaskAttachmentRepository.InsertAsync(TaskAttachmentDto attachment, string storedPath, …)` —— 存储路径成为显式参数，不再挂在 DTO 上。
  - 持久化实现：`src/Modules/Storage/Mosaic.Tasks.Sqlite/Internal/SqliteTaskAttachmentRepository.cs`：`Columns` 去掉 `stored_path`（第 11 行），`InsertAsync` 写入显式 `storedPath`，新增 `FindStoredPathAsync`（单列查询），`Map` 不再回填 `StoredPath`。
  - 公开契约：`src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskAttachments.cs`：`TaskAttachmentDto` 删除 `StoredPath` 可写属性；`ITaskAttachmentApi` 新增 `OpenReadAsync(Guid taskId, Guid attachmentId, …) → OperationResult<Stream>`。
  - 服务：`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskAttachmentService.cs`：改依赖 `ITaskAttachmentBlobStore`；新增 `OpenReadAsync`（任务→附件归属校验→取历史路径→流式打开，附件不属于该任务返回 `Tasks.Attachments.Error.NotFound`）；`AddAsync` 用 `CanStore` 前置判断、`SaveAsync` 拿路径落库、落库失败回删文件；`ExportAsync` 改为「`OpenReadAsync` + 流式复制到 `FileMode.CreateNew`」，Move 在写成功后才删记录与文件，失败回删目标；目标目录校验改用 `IsWithinStore`；`RemoveAsync` 的边界检查统一交给 blob 端口。
  - 清理与删任务：`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskAttachmentCleanup.cs`（`blobs.DeleteAsync` 取代 `File.Exists/File.Delete`）；`Internal/TaskService.cs`：`attachmentStore` 参数换成 `attachmentBlobs`（删掉不再使用的 `diagnostics` 参数），删任务时先对每个附件取 `FindStoredPathAsync` 再用 blob 端口删除，root 为空也能清理。
  - 装配：`src/Modules/Tasks/Mosaic.Tasks.Core/TasksModule.cs`：`_attachmentStore` → `_attachmentBlobs`（`FileSystemTaskAttachmentBlobStore`，第 26 行），`ActivateAsync` 仍是 storagePath 单点决定处（第 80 行），service/cleanup 构造处同步换类型。
  - 守卫：`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/GuardedTaskAttachmentApi.cs`：新增 `OpenReadAsync` 包装（任一 ID 为空返回 `INVALID_REQUEST`）。
  - WPF：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskEditorViewModel.cs`：新增 `OpenAttachmentAsync`（未启用返回 `CAPABILITY_UNAVAILABLE`）与 `ReportAttachmentFailure`（复用 `TaskErrorLocalizer`）；`Views/TaskEditorView.xaml.cs`：拖出改为 `OpenAttachmentAsync` + 流式复制到临时目录。
  - 架构门禁：`build/public-api.txt`：新增 `Mosaic.Tasks.Persistence.Contracts.ITaskAttachmentBlobStore`。
  - 测试：`tests/Mosaic.Tasks.Contract.Tests/AttachmentRepositoryContractTests.cs`（新增）：stored_path 经仓储端口持久化、DTO 不再携带路径、`FindStoredPathAsync` 缺失/删除后返回 null；`tests/Mosaic.Tasks.Core.Tests/AttachmentBlobStoreTests.cs`（新增 4 例）：OpenRead 端到端与错误键、越界路径不删除、无 root 时新增返回 `CAPABILITY_UNAVAILABLE`、导出成功/同名冲突/导出到附件根目录被拒；`AttachmentCleanupTests.cs`（反射目标改为 `FileSystemTaskAttachmentBlobStore`，替身补 `FindStoredPathAsync`/新 `InsertAsync`）；`TaskDeleteAttachmentCleanupTests.cs`（替身补 `FindStoredPathAsync` 与路径表）；`tests/Mosaic.Storage.Sqlite.Tests/TaskAttachmentRepositoryTests.cs` / `TaskAttachmentMigrationTests.cs`（`InsertAsync` 传路径，断言改走 `FindStoredPathAsync`）。
  - 版本与发布元数据：`Directory.Build.props:12`（0.18.2）、`build/release-content.json`（第 48 条 `mosaic-0.18.2`，`previousReleaseId=mosaic-0.18.1`）、`build/published-release-index.json`（第 48 条）、`build/release-manifest.json`、`docs/releases/v0.18.2.md`、`README.md`、本文件。
- 迁移逻辑与验收项：无 SQL 迁移（`stored_path` 列保留，仅退出公开契约）；**对已有安装的影响**——升级后首次启动即生效，旧附件的 `stored_path` 仍在库中，`Resolve` 优先采信且要求落在当前附件根目录内，用户未改过 storagePath 时旧附件可正常打开/导出/删除；若用户改过 storagePath，旧路径不在 root 内时按 `{root}\{taskId}\{attachmentId}{ext}` 重新推导，找不到即返回 `Tasks.Attachments.Error.ContentMissing`（不静默丢记录）。验收项：①升级后打开既有任务，附件列表、拖出导出、删除均正常；②把 storagePath 改到新目录并重启后，旧附件若未迁移则导出提示「附件内容已丢失」，新添加的附件落在新目录；③导出到 `C:\AttachmentBackup` 这类与 root 同前缀的目录不再被拒；④导出到附件根目录本身仍被拒；⑤删除任务后附件文件与记录都清空，且外部已删掉的文件不阻断清理；⑥附件列表 DTO 序列化后不含任何本机路径。
- 验证：`dotnet build Mosaic.sln -c Release` 0 警告 0 错误；`Mosaic.Tasks.Core.Tests` 35/35、`Mosaic.Storage.Sqlite.Tests` 24/24、`Mosaic.Tasks.Contract.Tests` 8/8、`Mosaic.Architecture.Tests` 4/4、`Mosaic.Wpf.Tests` 145/145 通过；全解决方案 445 例中 9 例失败，逐条与 `git stash` 基线对照——`Mosaic.Desktop.SmokeTests` 的 9 例（`StartupStorageRelocationTests.Failed_relocation_keeps_the_source_and_reports_the_failure`、`DesktopTests.Peek_completion_and_filter_use_real_persisted_task_state`、3 例 Floating、`Disabled_and_invalid_configurations_do_not_create_task_views`、`Shared_buttons_release_press_feedback_and_show_keyboard_focus`、`Healthy_configuration_keeps_its_bindings_and_file_untouched`）在 HEAD 基线上以相同断言失败，本次改动未引入新失败（特征为 WPF/DPI/JSON 期望值差异，与附件路径无关）；`python -I scripts/release_content_verify.py` 通过（48 个已索引发布）。
- 未验证项：安装包（MSI）未重新构建与发布核验——本条目提交时只做源码、测试与发布内容校验，`build/package.ps1` 与 GitHub Release 上传留给发布流程；`OpenReadAsync` 的大文件流式行为只由小文件用例覆盖，未做 50 MB 边界实测。
## 0.18.1：安装包自动关闭运行进程、禁用回滚备份修复非系统盘 Config.Msi 错误 5（2026-11-22）

- 根因位置：
  - 非-系统盘安装中断（错误 5）：MSI 引擎在文件替换前会把旧文件备份为回滚脚本文件——per-user 安装位于非系统驱动器（如 `E:\app`）时，备份目录落在目标卷根目录 `E:\Config.Msi`（引擎在 `InstallValidate` 阶段为每个被替换文件生成 `*.rbf`）。该目录仅 SYSTEM/Administrators 可写，普通用户上下文在「正在删除备份文件/设置文件安全」阶段对 `E:\Config.Msi\*.rbf` 调 `SetFileSecurity` 得到错误 5（拒绝访问），引擎以致命错误中断安装；`build/installer/Package.wxs` 此前未设置 `DISABLEROLLBACK`，引擎默认启用回滚脚本。触发条件：非系统盘安装 + 任意文件被替换（全新安装或升级）+ 用户级权限运行。
  - 安装前进程占用：`build/installer/Package.wxs` 此前没有任何关闭运行中 Mosaic 的动作，升级/修复时正在运行的进程占用待替换文件，只能依赖引擎在 `InstallValidate` 弹 `FilesInUse` 对话框让用户手动处置（或 `REINSTALLMODE` 冲突时报错）。
  - 维护自动修复缺失：`build/installer/actions/MosaicInstallerActions.cpp` 的 `ScanPayload` 只设 `MOSAICREPAIRNEEDED=1` 标记，而 `REINSTALL` 属性必须在执行序列 `CostFinalize`（1000）之前由执行会话设置才能生效（UI 序列设置的 `REINSTALL` 传不到服务端会话），此前没有动作在执行序列消费该标记，导致 0.17.4 收口后维护扫描发现缺失/损坏时只能提示用户无动作。
- 改动位置（未另注明的行号为本次提交状态）：
  - `build/installer/actions/MosaicInstallerActions.cpp`：新增第二段匿名命名空间（`:1058-1189`）——`CollectAppRoots`（`:1058`，从 INSTALLFOLDER/MOSAICINSTALLEDLOCATION/ARPINSTALLLOCATION/MOSAICINSTALLPATH 去重收集候选安装根）、`IsMosaicProcessImage`（`:1085`，映像基名小写以 `mosaic` 开头、`.exe` 结尾且路径位于某个候选根内才认定为目标进程）、`PostCloseToProcessWindows`（`:1100`，EnumWindows 回调按 PID 广播 `WM_CLOSE`）、`CloseRunningAppProcesses`（`:1119`，CreateToolhelp32Snapshot 枚举进程 → OpenProcess（`PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | SYNCHRONIZE`）→ QueryFullProcessImageNameW → 对目标进程窗口发 `WM_CLOSE` → 全局 5 秒 deadline 内 WaitForSingleObject 等待退出，超时 `TerminateProcess(exitCode=1)` 再等 5 秒；每步写安装日志；未匹配进程或动作内部失败只记录日志不阻断安装）；导出动作 `CloseRunningApp`（`:1193`，始终返回 `ERROR_SUCCESS`）。`ScanPayload`（`:1228`）needsRepair 分支增设 `MOSAICREPAIRAUTOMATIC=1`（健康与失败分支显式清空，`:1211/:1233`）。`ApplyRepairRequest`（新增，`:1245`）：`MOSAICREPAIRNEEDED != "1"` 直接返回；调用方已设 `REINSTALL`（显式 `/f`）时保留其选项；否则设 `REINSTALL=ALL`，`REINSTALLMODE` 为空时补 `amus`。
  - `build/installer/Package.wxs`：`:16` 新增 `<Property Id="DISABLEROLLBACK" Value="1" />`（禁用引擎回滚脚本，`.rbf` 备份不再生成，`Config.Msi` 不再被创建；代价是安装失败无引擎级自动回滚，重跑安装包续装）；`:55` 声明 `ApplyRepairRequest`（immediate，Return="check"）；`:57` 声明 `CloseRunningApp`（immediate，Return="ignore"）；`:77` 执行序列 995 挂 `ApplyRepairRequest`（条件 `NOT REMOVE`，先于 CostFinalize(1000)）；`:84` 执行序列 1050 挂 `CloseRunningApp`（条件 `NOT (MOSAICSTATE = "Maintenance" AND MOSAICSCANSTATUS = "Healthy" AND NOT REINSTALL)`——写文件的安装/升级/修复/卸载前关闭应用，健康维护不关；位于 CostFinalize 之后（INSTALLFOLDER 已解析为真实目标）、InstallValidate(1250) 之前）。
  - `build/installer/Dialogs.wxs`（6 处文案同步实际行为）：`:90` MaintenanceDlg 提示（健康检查不关闭应用、修复才关闭）；`:110` UpgradeDlg 提示（升级前自动关闭 Mosaic）；`:146` ProgressDlg 修复提示（运行中的 Mosaic 会被自动关闭、失败后重跑续装）；`:185` FatalError 提示（删除原「已完成的步骤会回滚」表述——DISABLEROLLBACK 下不再回滚，改为重新运行安装包续装）；`:193` UserExit 提示（删除「未完成的操作已回滚」表述）；`:201` FilesInUse 提示（Mosaic 通常已被自动关闭，仅列其他进程）。
  - `build/test-package-maintenance.ps1`：`$expectedActions` 加入 `ApplyRepairRequest`、`CloseRunningApp`；CustomAction 类型断言由「严格等于 1」改为按位检查（`Return="ignore"` 会让 Type 变为 65 = 1+64 Continue 位，`CloseRunningApp` 与既有 `RestoreInstallFolderForRemoval` 均如此）；DISABLEROLLBACK 必须为 `1` 的断言；`CloseRunningApp`/`ApplyRepairRequest`/`MOSAICREPAIRAUTOMATIC`/`REINSTALL=ALL`/`REINSTALLMODE=amus` 源码契约断言；执行序列断言改为 995（> ScanPayload 990 且 < CostFinalize）与 1050（CostFinalize 后、InstallValidate 前，条件含健康维护排除）；原「禁止自动修复」否定断言删除。
  - 版本与发布元数据：`Directory.Build.props:12`（0.18.1）、`build/release-content.json`（第 47 条 `mosaic-0.18.1`，`previousReleaseId=mosaic-0.18.0`，指纹 `456B6410DB89645BD4AEE76A87C757DE10570995E25CF768D1B6D0D335EEED7F`）、`build/published-release-index.json`（第 47 条）、`build/release-manifest.json`、`docs/releases/v0.18.1.md`、`README.md`、本文件。
- 迁移逻辑与验收项：无数据迁移、无用户配置变化、无模块装配变化；对已有安装的影响——① 升级到 0.18.1 时正在运行的 Mosaic 会被自动关闭（先正常退出保存数据）；② 安装失败不再自动回滚已写入的文件，重新运行安装包即可继续完成未写入的文件（产品文件按组件引用计数与版本比较续装，不会重复或损坏；用户数据目录从不参与安装事务）；③ `Config.Msi` 回滚备份不再生成，非系统盘根目录不再出现引擎残留目录。验收项：①运行中的 Mosaic 桌面版在升级开始前被自动关闭（优先正常退出），升级完成后可重新打开且数据完好；②非系统盘（如 `E:\app`）全新安装与升级全程不再出现 `Config.Msi` 错误 5；③同版本重跑安装包、扫描发现文件缺失时，确认修复后自动补回全部受管文件并复检；④显式 `/f` 修复参数仍优先于自动修复选项；⑤健康维护（文件完好）不关闭正在运行的 Mosaic、不重装、不登记位置变更；⑥卸载仍正常清理程序文件与快捷方式（不生成 `.rbf`）。
- 验证：`python -I scripts/release_content_verify.py` 通过（47 个已索引发布）；`build/package.ps1` 全流程（载荷构建、清单生成、wix 编译、test-package-shortcuts 与 test-package-maintenance）通过，`PASS: MSI static structure, maintenance routing, dialogs, payload manifest and cabinet integrity verified for 0.18.1.`（470 个 CAB 成员 SHA-256 比对一致，2 个快捷方式，0 警告 0 错误）。发布核验：`Mosaic-0.18.1-win-x64.msi`，52,533,298 字节，SHA-256 `fcbaffee7a8669cc49c8577fb4bc080c2b437171af3addfd2662b1fcf31b610a`（与 GitHub Release 资产 digest 一致），Release 非草稿/非预发布，tag `v0.18.1` 指向提交 `428483e`（与 main 一致）：https://github.com/soehi/Mosaic/releases/tag/v0.18.1 。
- 未验证项：`WM_CLOSE` 5 秒超时强制结束与 `FilesInUse` 不再弹出，依赖真实运行中进程的实机升级验证；DISABLEROLLBACK 下「失败续装」仅按 MSI 组件语义推演，未做中断注入实机验证。

## 0.18.0：任务附件（拖入添加、拖出发送/移动、归档后定时清除）（2026-11-22）

- 根因位置：原系统无任何附件能力——`src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskRequests.cs` 的任务契约只含标题/备注；`src/Modules/Tasks/Mosaic.Tasks.Persistence.Contracts` 只有任务与流转仓储，没有附件表与附件仓储；`src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TaskEditorView.xaml` 编辑器仅渲染标题与备注字段；归档能力（0.13.0 起）也只处理任务行本身，附件概念不存在，更谈不上归档后清除。因此用户无法把文件关联到任务，也无法把附件从任务中发给外部。
- 改动位置（未另注明的行号为本次提交状态）：
  - 契约：`src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskAttachments.cs`（新增）：`TaskAttachmentDto`、`TaskAttachmentConstraints`（默认单文件 50 MB、每任务 10 个）、`ITaskAttachmentApi`（List/Add/Export/Remove）、各请求 record 与 `TaskAttachmentTransferMode`；`TaskRequests.cs`：`TaskConstraintsDto` 增加 `AttachmentMaxFileMegabytes`/`AttachmentMaxPerTask` 两字段。
  - 核心服务：`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskAttachmentService.cs`（新增）：添加（大小/数量/扩展名校验后写入存储目录并落库，经 `TouchForAttachmentAsync` 提升任务 Revision）、导出（Copy/Move）、删除（库记录与文件）；同文件含 `TaskAttachmentStore`（附件根目录，用户设置优先、缺省回落装配默认值）。`Internal/GuardedTaskAttachmentApi.cs`（新增）：模块执行门包装。
  - 归档清除：`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskAttachmentCleanup.cs`（新增）：读 `tasks.attachments.archiveCleanupDays`（0 禁用、1–365），`PeriodicTimer(1 分钟, TimeProvider)` 轮询，启动即先跑一轮；对每个超期附件先删数据库记录、成功后再删文件，记录删除失败保留文件并报诊断 `tasks.attachments.cleanup.delete`，单条失败不阻断其余；`SetDaysAsync(0)` 取消并等待循环，模块停止时调用。
  - 设置源：`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskSettingsSource.cs`（第 25-26、49-52 行）：新增 `archiveCleanupDays`（Integer、默认 0、0–365、Normal、Immediate）与 `storagePath`（Advanced、Restart）定义及 DisplayNameKey 映射；主构造函数扩为 4 参（`TaskAttachmentStore?`、`TaskAttachmentCleanup?` 可选），保留 2 参便利重载供既有反射测试；`ApplyAsync`（第 60-66 行）按 rules → store → cleanup → archive 顺序即时应用。
  - 模块装配：`src/Modules/Tasks/Mosaic.Tasks.Core/TasksModule.cs`：`TasksDependencies`（第 16-17 行）新增 `AttachmentRepository`、`DefaultAttachmentRoot`；构造函数（第 48-52 行）装配 Guarded 附件 API 与清理器；Manifest capabilities 增加 `("tasks.attachments", 1)`（第 56 行）；`ActivateAsync`（第 71、82 行）注册设置源并在门激活后应用清理策略；`StopAsync`（第 101 行）先停清理循环。`src/Mosaic.Desktop/BuiltInModuleCatalog.cs`：注入 Sqlite 附件仓储与默认附件根目录。`src/Modules/Storage/Mosaic.Tasks.Sqlite/TasksSqliteModule.cs`：暴露 `AttachmentRepository`。
  - 持久化：`src/Modules/Tasks/Mosaic.Tasks.Persistence.Contracts/ITaskAttachmentRepository.cs`（新增）：ListByTask/Insert/Find/Delete/ListOrphaned/CountByTask/`ListExpiredArchivedAsync(cutoffUtc)`。`src/Modules/Storage/Mosaic.Tasks.Sqlite/Migrations/004_TaskAttachments.sql`（新增）：`task_attachments` 表（id 主键、task_id 外键、文件名/大小/存储路径/添加时间，任务维度索引）。`src/Modules/Storage/Mosaic.Tasks.Sqlite/Internal/SqliteTaskAttachmentRepository.cs`（新增）：CRUD + `ListExpiredArchivedAsync`（JOIN `tasks`，`archived_at IS NOT NULL AND archived_at <= $cutoff`，按添加时间排序，"O" 不变区域性序列化）。
  - UI 拖入：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TaskEditorView.xaml`（第 191-268 行）：附件区（拖放 Border `AllowDrop` 绑 `CanManageAttachments`，ItemsControl 列表，每项 ✕ 删除按钮）；`Views/TaskEditorView.xaml.cs`：`AttachmentDropZone_DragOver/Drop`（FileDrop 判定与添加）、`AttachmentDropZone_Click`（OpenFileDialog 多选）、`AttachmentRemove_Click`。
  - UI 拖出（A+B 语义）：`Views/TaskEditorView.xaml.cs` 的 `AttachmentItem_MouseDown`（记起点与 DTO）、`AttachmentItem_MouseMove`（第 117-150 行）：超拖动阈值后复制原件到唯一临时子目录 `%TEMP%\MosaicAttachments\{attachmentId}`（避免同名互覆），`DragDrop.DoDragDrop(Copy|Move)`；返回 `Move`（用户按住 Shift 且外部接收成功）时删除临时目录并调 `RemoveAttachmentAsync` 删应用内原件与记录，普通拖放（Copy）原件保留。
  - ViewModel：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskEditorViewModel.cs`：`Attachments` 集合、`CanManageAttachments`（已归档或无附件 API 时禁用）、`AttachmentLimitLabel`；`LoadAttachmentsAsync`（第 159-167 行）在 UI 线程上下文恢复后重建集合（去掉 `ConfigureAwait(false)`，修复后台线程改 ObservableCollection 的跨线程异常）；`AddAttachmentsAsync`/`RemoveAttachmentAsync`/`ExportAttachmentAsync` 成功后用回包同步 `_current`（Revision 已被后端提升）。`Internal/TasksViewModel.cs`、`Internal/TaskViewFactory.cs`、`TasksUiModule.cs`：附件 API 注入编辑器。
  - 本地化：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Localization/zh.xaml`/`en.xaml`（附件区标题、上限说明、拖放提示、失败提示等 7 键）；`src/Modules/Settings/Mosaic.Settings.Wpf/Localization/zh.xaml`/`en.xaml`（`Settings.Setting.AttachmentArchiveCleanupDays`、`AttachmentStoragePath`、`AttachmentMaxSize`、`AttachmentMaxCount` 及描述）。
  - 测试：`tests/Mosaic.Tasks.Core.Tests/AttachmentCleanupTests.cs`（新增）：设置默认值与显示键、启用后删除记录与文件/停用停止循环、库删除失败保留文件并报诊断（反射构造 internal 类型，默认 binder 填充可选参数）；`tests/Mosaic.Storage.Sqlite.Tests/TaskAttachmentRepositoryTests.cs`（新增）：归档截止时间筛选（未归档与未到期排除、同任务多附件按添加时间排序）；`tests/Mosaic.Storage.Sqlite.Tests/Mosaic.Storage.Sqlite.Tests.csproj`（第 7 行）与 `packages.lock.json`：引用 `Mosaic.Tasks.Sqlite`；`tests/Mosaic.Tasks.Core.Tests/AutoArchiveTests.cs`（第 91 行）：反射绑定改显式 binder 以兼容多构造函数；`tests/Mosaic.Wpf.Tests/FloatingWindowsTests.cs:67`、`PresentationTests.cs:68`、`PreviewCaptureTests.cs:275`、`ResponsiveLayoutTests.cs:485`：`TaskConstraintsDto` 桩补齐两个附件参数字段。
  - 版本与发布元数据：`Directory.Build.props:12`（0.18.0）、`build/release-content.json`（第 46 条 `mosaic-0.18.0`，`previousReleaseId=mosaic-0.17.12`，指纹 `DC9F859951789682BB75E7E7EBD40167AA02C91964827EB3F07DADDA145A330D`）、`build/published-release-index.json`（第 46 条）、`build/release-manifest.json`、`docs/releases/v0.18.0.md`、`README.md`、本文件。
- 迁移逻辑与验收项：数据库迁移 `004_TaskAttachments.sql` 由 Sqlite 模块既有 `MigrateAsync` 管线在首次启动时自动执行（幂等，已发布不可改）；新增设置 `tasks.attachments.archiveCleanupDays` 默认 0、`tasks.attachments.storagePath` 默认空（回落装配默认目录），对已有安装零行为变化：旧任务无附件、清理默认关闭、既有数据目录不动。验收项：①从 0.17.x 升级后首次启动正常，任务列表与设置原样可见；②`task_attachments` 表与索引已建（可经维护页迁移后的新库确认）；③设置 → 任务出现「归档附件清除天数」，默认 0，改为 1 立即生效无需重启；④拖入文件出现在附件列表且文件已写入附件目录；⑤普通拖出得到副本且原件保留，Shift 拖出成功后原件与记录删除；⑥归档任务满 N 天后其附件记录与文件被清除（验证时可将 N 设为 1 并手工改库 `archived_at` 提前）。
- 验证：`dotnet build Mosaic.sln` 0 警告 0 错误；`dotnet test tests/Mosaic.Tasks.Core.Tests` 30/30、`tests/Mosaic.Storage.Sqlite.Tests` 23/23、`tests/Mosaic.Wpf.Tests` 143/143 通过；`python -I scripts/release_content_verify.py` 通过（46 个已索引发布）；`git diff --check` 通过。
- 未验证项：拖入/拖出为真实鼠标手势与 Windows 资源管理器交互（DoDragDrop 与外部接收方），未做实机自动回放；批量添加部分失败当前静默跳过（`Tasks.Attachments.AddFailed` 文案已备未用）。

## 0.17.12：修复 Ctrl 加选高亮互清（Single 模式容器选中独占）（2026-11-21）

- 根因位置：
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml:114`（0.17.11 版本 `103a3a4`）把 `TaskListBox` 设为 `SelectionMode="Single"`；`src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml.cs` 的 `SyncListBoxSelection` 多选分支随后对多个容器逐个赋 `ListBoxItem.IsSelected = true` 以叠加多选高亮。Single 模式下容器 `IsSelected` 是独占的：其属性变化通知异步生效，后一行的 `IsSelected = true` 生效时把前一行清掉（最小复现：Single 下连续设两个容器选中，稳定后只剩最后一行；Extended/Multiple 下两行都保留）。结果：Ctrl 连续加选多项时只有最后一项显示高亮，数据层的 `SelectedTasks` 计数与批量操作始终正确，是纯显示层缺陷。
- 改动位置：
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml:114`：`SelectionMode` 由 `Single` 改为 `Extended`。原生 Ctrl/Shift 组合手势不会泄露：`OnTaskListPreviewMouseLeftButtonDown` 对所有左键点击 `e.Handled = true`，`OnTaskListSelectionChanged` 保持空方法不回写 `SelectedTasks`，交互语义与 0.17.11 完全一致（仅 Ctrl 切换多选、无修饰键单选、Esc 清空）。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml.cs`：`OnTaskListPreviewMouseLeftButtonDown`（`:144-146`）、`OnTaskListSelectionChanged`（`:148-151`）、`SyncListBoxSelection`（`:157-165`）注释更新，记录必须用 Extended 的原因（Single 模式容器选中独占互清）。
  - `tests/Mosaic.Wpf.Tests/PresentationTests.cs`：`Ctrl_multi_select_hides_the_single_task_editor_and_batch_archive_uses_latest_items`（`:1388`）断言 `SelectionMode.Extended` 并新增回归断言——Ctrl 加选两项后两个容器的 `IsSelected` 必须同时保持 true。
  - 版本与发布元数据：`Directory.Build.props:12`（0.17.12）、`build/release-content.json`（第 45 条 `mosaic-0.17.12`，`previousReleaseId=mosaic-0.17.11`，指纹 `5F226BED38261A0D6088126A8EDC28260A26E550D8E87C10A2A1A6DBB174BEB7`）、`build/published-release-index.json`（第 45 条）、`build/release-manifest.json`、`docs/releases/v0.17.12.md`、`README.md:47`、`README.md:49`、本文件。
- 迁移逻辑与验收项：无数据迁移、无配置变化、无模块装配变化；升级沿用既有 MajorUpgrade。验收项：升级后 Ctrl 连续加选多项，所有选中行同时保持高亮；无修饰键点击仍为单选并打开详情；Esc 清空多选。
- 验证：直接执行 `tests/Mosaic.Wpf.Tests/bin/Debug/net10.0-windows/Mosaic.Wpf.Tests.exe` 全量 143 通过（含新增的高亮保持回归断言）；`python -I scripts/release_content_verify.py` 通过（45 个已索引发布）；修复版已在本地启动（窗口标题 v0.17.11、二进制为 Extended 修复版）供人工确认。
- 未验证项：未做实机鼠标键盘自动回放（Ctrl 加选高亮保持由容器级断言覆盖）。

## 0.17.11：多选交互修正——仅 Ctrl 切换多选、无修饰键单选、Esc 视图级清空（2026-11-21）

- 根因位置（以下行号指 0.17.10 已发布版本 `02bed50`）：
  - 普通点击误入多选：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml` 的 `TaskListBox` 设为 `SelectionMode="Multiple"`，WPF 原生点击即切换选中；`src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml.cs` 的 `OnTaskListPreviewMouseLeftButtonDown` 只在按住 Ctrl/Alt 时拦截（`e.Handled = true`），无修饰键的点击被放行给原生行为，经 `OnTaskListSelectionChanged` 镜像进 `TasksViewModel.SelectedTasks`，于是普通点击也会加选/减选。
  - Esc 清空无效：`PreviewKeyDown` 只挂在 `TaskListBox` 上（0.17.10 的 `OnTaskListPreviewKeyDown`），该事件只沿焦点元素及祖先链触发，焦点落在编辑器或多选面板按钮上时列表收不到 Esc。
  - 修复过程中发现并处理的深层问题：① `TasksViewModel.SelectAsync`（`TasksViewModel.cs:496`）打开详情成功后用接口回包按 Id 回查 `Items` 重建选中引用，回包是新 DTO 实例，若 Get 与 List 数据源不一致会把选中清成 null；② `SelectedTasks.Clear()` 触发 `CollectionChanged` 时 `HasMultiSelection` 的 `PropertyChanged` 尚未到达（CommunityToolkit 在集合事件回调末尾才发），视图读到的仍是旧值 true，导致单选锚点赋值被跳过。
- 改动位置：
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml`：`TaskListBox`（`:113-115`）`SelectionMode` 由 `Multiple` 改为 `Single`，`SelectedItems` 集合不再承载多选，多选叠加高亮改由容器 `ListBoxItem.IsSelected` 施加。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml.cs`：构造函数（`:24`）改挂视图级 `PreviewKeyDown += OnTasksViewPreviewKeyDown`；`OnTaskListPreviewMouseLeftButtonDown`（`:115-125`）改为全量拦截（`e.Handled = true`），按住 Ctrl 调 `ToggleSelection(item)`，否则 `SelectedItem = item` 单选；新增 `OnTasksViewPreviewKeyDown`（`:128-135`），Esc 且 `HasMultiSelection` 时 `ClearMultiSelection()` 并标记已处理；`OnTaskListSelectionChanged`（`:147-149`）改为空方法，Single 模式下不再回写 `SelectedTasks`；`SyncListBoxSelection`（`:154-195`）重写为两分支：多选期间锚点置 null、容器循环按 `SelectedTasks` 叠加/移除高亮，单选期间清空多选残留且循环不触碰锚点行（锚点行交给 ListBox 原生选中驱动，避免同步写回抖动）；`OnModelPropertyChanged`（`:50-59`）在 `HasMultiSelection` 通知到达时重放 `SyncListBoxSelection()`，修正上述通知交错时序。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TasksViewModel.cs`：`ToggleSelection`（`:362`）由双参数 `(item, add)` 改为单参数切换语义——未选中加选、已选中减选，Alt 减选分支删除；`SelectedItem` setter（`:122-140`）多选期间先 `ClearMultiSelection()` 再走单选；`SelectFromListAsync`（`:343-360`）打开详情成功后强制 `SetSelectedItem(requested)`，不再以接口回包重建选中引用（单选锚点就是列表中被点击的实例）；`HasMultiSelection`（`:115`）语义收束为只承载 Ctrl 多选，不与单选纠缠。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Localization/zh.xaml:123` 与 `en.xaml:123`（两文件同位置）：`Tasks.Selection.Hint` 文案改为「按住 Ctrl 点击加选任务，按住 Ctrl 点击已选中的任务减选；按 Esc 清空选择。」（英文对应更新）。
  - `tests/Mosaic.Wpf.Tests/PresentationTests.cs`：`Api` 替身（`:45-57`）新增 `_lastList` 缓存，`GetAsync` 按 id 回查最近一次 `ListAsync` 结果（与真实服务行为一致，消除替身 Get 与 Query 各回一份导致的选中引用错乱）；新增 `Plain_click_during_multi_select_returns_to_single_selection_and_opens_editor`（`:1497`）覆盖无修饰键点击退出多选并打开详情；新增 `Escape_at_view_level_clears_multi_selection`（`:1549`）覆盖视图级 Esc 清空多选；`Ctrl_multi_select_hides_the_single_task_editor_and_batch_archive_uses_latest_items`（`:1388`）与两个批量删除用例改为单参数 `ToggleSelection` 调用。
  - 版本与发布元数据：`Directory.Build.props:12`（0.17.11）、`build/release-content.json`（第 44 条 `mosaic-0.17.11`，`previousReleaseId=mosaic-0.17.10`，指纹 `90E061F4C9AF27B9289530FFE9EC121A5CC0EF25CA2ED66489D077AB902793F6`）、`build/published-release-index.json`（第 44 条）、`build/release-manifest.json`、`docs/releases/v0.17.11.md`、`README.md:47`、`README.md:49`、本文件。
- 迁移逻辑与验收项：无数据迁移、无配置变化、无模块装配变化；升级沿用既有 MajorUpgrade，安装位置与用户数据不受影响。验收项：升级后普通点击为纯单选并打开详情；仅 Ctrl+点击切换多选；Esc 在任务页任意焦点下清空多选。
- 验证：直接执行 `tests/Mosaic.Wpf.Tests/bin/Debug/net10.0-windows/Mosaic.Wpf.Tests.exe`（xUnit v3 独立运行器；`dotnet test` 会以 VSTest 参数调用并报"未知选项: --nologo"）全量 143 通过（0.17.10 基线 141 + 新增 2）；`python -I scripts/release_content_verify.py` 通过（44 个已索引发布）。
- 未验证项：未做实机鼠标+键盘组合回放（Ctrl 点击切换、无修饰键单选、Esc 视图级清空由集成测试覆盖）。

## 0.17.10：任务列表多选（Ctrl 加选 / Alt 减选）与批量操作（2026-11-21）

- 根因位置：
  - 新功能，无既有缺陷根因。0.17.9 及更早版本的任务列表 `ListBox` 为单选语义（`src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml` 的 `TaskListBox` 未设 `SelectionMode`，`SelectedItem` 双向绑定 `TasksViewModel.SelectedItem`），`TasksViewModel.SelectedItem` setter（`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TasksViewModel.cs:120-140`）对任何赋值都走 `_ = SelectFromListAsync(value)` 打开单个任务详情，因此不存在"多选后详情区仍显示单个任务"的分支，也没有批量归档/删除的调用路径。
  - 实现过程中发现并修复的真实回归：`TasksViewModel.OnSelectedTasksChanged`（`TasksViewModel.cs:933`）原本带一个 `NotifyCollectionChangedAction.Add` 分支——当新增项与 `_selectedItem` 引用相同时把 `_selectedItem` 清空。该分支本意是"Ctrl 加选当前单选任务时退出单选"，但普通单选路径（`SelectedItem` setter → `SelectFromListAsync` 先 `SetSelectedItem(requested)` → WPF 在 `Multiple` 模式下把该项加进 `ListBox.SelectedItems` → `OnTaskListSelectionChanged` 再 `Add` 进 `SelectedTasks`）恰好命中该条件，把刚设好的单选清空，导致 `Selecting_a_list_row_loads_the_editor_and_keeps_the_row_selected` 失败（`SelectedItem` 为 null）。多选场景本身由 `ToggleSelection`（`TasksViewModel.cs:360`）在首次 `Add` 前先 `SetSelectedItem(null)` 覆盖，该分支永远不会被多选路径需要。
- 改动位置：
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TasksViewModel.cs`：新增 `SelectedTasks` 集合（`:98`）、`HasMultiSelection`/`SelectionSummary`/`SelectionArchiveLabel`/`SelectionDeleteLabel`（`:113-118`）、`ArchiveSelectionCommand`/`DeleteSelectionCommand`（`:88-89` 构造、`:111-112` 属性）；`SelectedItem` setter（`:120-140`）在多选期间拒绝赋值，保持 `_selectedItem` 为 null；新增 `ToggleSelection(item, add)`（`:360-397`），首次加选时先退出单选并收起编辑器再 `SelectedTasks.Add`；新增 `ArchiveSelectionAsync`（`:399-450`）与 `DeleteSelectionAsync`（`:452-501`），逐条调用 `SetArchivedAsync`/`DeleteAsync` 并携带每项的 `Revision`，逐个记录失败项后只保留失败项仍处于选中、其余移出 `SelectedTasks`，再 `LoadAsync()`；`PruneMultiSelection`（`:918-935`）在列表刷新后剔除已不在列表的项、并把仍在列表的项替换为刷新后的新实例（保证批量操作读到最新 Revision）；`OnSelectedTasksChanged`（`:942-950`）删除上述 `Add` 分支、只保留 `NotifySelectionState()` 并加注释说明；`NotifySelectionState`（`:952-960`）广播多选相关属性与两个命令的 `CanExecute`。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml`：`TaskListBox`（`:111-116`）改为 `SelectionMode="Multiple"`，新增 `SelectionChanged="OnTaskListSelectionChanged"` 与 `PreviewMouseLeftButtonDown="OnTaskListPreviewMouseLeftButtonDown"`；`MultiSelectPane`（`:321-358`）为新增的多选操作面板（已选数量、操作提示、归档/恢复与删除按钮），宽屏占用原 `TaskEditorPane` 的 `Grid.Column="2"`。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml.cs`：`OnTaskListPreviewMouseLeftButtonDown`（`:108-116`）识别 Ctrl/Alt 修饰键并调用 `ToggleSelection`；`OnTaskListPreviewKeyDown`（`:119-126`）Esc 清空多选；`OnTaskListSelectionChanged`（`:137-152`）把 `ListBox.SelectedItems` 增减同步进 `SelectedTasks`；`OnSelectedTasksChanged`（`:154-179`）反向同步；`SyncListBoxSelection`（`:181-206`）整体对齐；`UpdateMultiSelectPaneText`（`:208-214`）刷新面板文案；`UpdatePaneVisibility`（`:289-309`）在宽屏多选时也隐藏 `TaskEditorPane`、显示 `MultiSelectPane`（此前只在紧凑布局下切换）。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Localization/zh.xaml` 与 `en.xaml`（`:122-132`，两文件同位置）：新增 11 个多选文案键（`Tasks.Selection.Count`/`Hint`/`Archive`/`Restore`/`Delete`/`Delete.Confirm`/`Archived`/`Restored`/`Deleted`/`Archive.Partial`/`Delete.Partial`）。
  - `tests/Mosaic.Wpf.Tests/PresentationTests.cs`：新增 `Ctrl_multi_select_hides_the_single_task_editor_and_batch_archive_uses_latest_items`（`:1374`）覆盖加选/减选/面板切换/批量归档携带最新 Revision；新增 `Batch_delete_confirms_once_and_reports_each_result`（`:1481`）覆盖批量删除只确认一次并逐项汇报结果；新增 `Batch_delete_partial_failure_keeps_only_the_failed_items_selected`（`:1512`）覆盖批量删除中间项失败时只保留失败项选中（此前按“成功项在前”顺序裁剪会保留错的对象）；`Api` 替身新增 `DeleteResult` 钩子（`:51-53`）用于按 Id 构造逐条结果。
  - 版本与发布元数据：`Directory.Build.props:12`（0.17.10）、`build/release-content.json`（第 43 条 `mosaic-0.17.10`，`previousReleaseId=mosaic-0.17.9`，指纹 `243FFA46CD12A3394B72A9F92ED4AAB24339CE2DA8885582572C0A5ABD7D042A`）、`build/published-release-index.json`（第 43 条）、`build/release-manifest.json`、`docs/releases/v0.17.10.md`、`README.md:47`、`README.md:49`、本文件。
- 迁移逻辑与验收项：无数据迁移、无配置变化、无模块装配变化。多选状态只存在于界面内存（`TasksViewModel.SelectedTasks`），不落盘、不进入备份；升级沿用既有 MajorUpgrade，安装位置与用户数据不受影响。验收项：升级后旧任务列表照常单选并打开详情；Ctrl/Alt 多选期间详情区显示多选面板。
- 验证：直接执行 `tests/Mosaic.Wpf.Tests/bin/Debug/net10.0-windows/Mosaic.Wpf.Tests.exe`（xUnit v3 独立运行器；`dotnet test` 会以 VSTest 参数调用并报“未知选项: --nologo”，运行 0 个测试）全量 141 通过（基线 138 + 新增 3，含 0.17.9 单选回归用例）；`python -I scripts/release_content_verify.py` 通过（43 个已索引发布）；`build/publish.ps1` Release 发布构建成功；`build/package.ps1 -NoPublish` 打包 0.17.10 MSI 通过（470 文件清单 SHA-256 校验、快捷方式与维护路由检查 PASS、WiX 0 警告 0 错误）。
- 未验证项：未做实机鼠标+键盘组合回放（Ctrl/Alt 点击拦截、Esc 清空、宽屏与紧凑布局的面板切换由集成测试覆盖）；未在真实高 DPI 与触摸设备上核对多选命中区域。

## 0.17.9：任务操作提示改为右下角浮层，修复列表点击错位（2026-11-21）

- 根因位置（以下行号指 0.17.8 已发布版本 `71a09ff`）：
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml` 中归档/恢复/删除等操作的结果提示条是 `TaskListPane` 内 `Grid.Row="1"` 的行内 Border（`Visibility="{Binding ShowListMessage, Converter={StaticResource BoolVisibility}}"`）。提示出现时该 Auto 行撑开，把 `Grid.Row="2"` 的任务列表整体下推；`Message` 本身不自动消失，由下次操作的 `TasksViewModel.ClearFeedback()`（`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TasksViewModel.cs`）清空后提示行收起，列表整体上移回弹。用户归档/恢复任务后原地再次点击列表时，提示条恰好消失导致列表上移，鼠标落点从目标行偏移到相邻任务（本想点第 2 个却点中第 3 个）。
- 改动位置：
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml`：删除原行内提示 Border；`TaskListPane`（`:70`）的 `RowDefinitions` 由 `Auto/Auto/*` 减为两行 `Auto/*`（`:71-74`）；在列表 Grid 内部末尾新增 `ListMessageToast` 浮层 Border（`:291-310`）：`Panel.ZIndex=5`、右下对齐、`Margin=0,0,14,14`、`IsHitTestVisible=False`、初始 `Opacity=0`/`Visibility=Collapsed`，作为覆盖层不参与布局、不拦截列表点击；文本由 `ListMessageText`（`:306`）承载。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml.cs`：`OnModelPropertyChanged`（`:43-44`）订阅 `ShowListMessage` 变化，`OnDataContextChanged`（`:36`）初始化时同步；新增 `UpdateListMessageToast()`（`:65-88`）驱动浮层 140ms 淡入 / 220ms 淡出（`DoubleAnimation`，`:61-62`），`_lastListMessage`（`:63`）记忆最近非空文本，避免淡出期间 `Message` 被清空导致空浮层闪现；淡出完成后若 `ShowListMessage` 仍为 false 才收起 Visibility。
  - `tests/Mosaic.Wpf.Tests/PresentationTests.cs:660` 新增 `Restoring_an_archived_task_shows_the_toast_without_shifting_the_task_list`：归档恢复流程后断言 `ShowListMessage` 为 true、浮层可见且 `IsHitTestVisible=False`，关键断言列表 `ActualHeight` 在提示出现前后不变。
  - 版本与发布元数据：`Directory.Build.props:12`（0.17.9）、`build/release-content.json`（第 42 条 `mosaic-0.17.9`，`previousReleaseId=mosaic-0.17.8`，指纹 `B4679238F2316A3AF05D24576076943634C3D5CBCE43CADB671035DA38B3EB72`）、`build/published-release-index.json`（第 42 条）、`build/release-manifest.json`、`docs/releases/v0.17.9.md`、`README.md:47`、`README.md:49`、本文件。
- 迁移逻辑与验收项：无数据迁移、无配置变化；升级沿用既有 MajorUpgrade，安装位置与用户数据不受影响。
- 验证：`dotnet test tests/Mosaic.Wpf.Tests` 全量 138 通过（含新增回归测试）；`python -I scripts/release_content_verify.py` 通过（42 个已索引发布）。
- 未验证项：未做实机点击回放核对（浮层不拦截点击与列表高度不变由集成测试覆盖）。

## 0.17.8：设置页顶部说明与「高级设置」开关在模块页分区消失修复（2026-11-21）

- 根因位置（以下行号指 0.17.7 已发布版本 `87f4eb6`）：
  - `src/Modules/Settings/Mosaic.Settings.Wpf/Views/SettingsView.xaml:105-107`（副标题）与 `:109-111`（模式说明条，含「高级设置」开关）把 `Visibility` 绑定到 `SettingsViewModel.HasSettingsSectionContent`（定义 `SelectedPage is null`，`src/Modules/Settings/Mosaic.Settings.Wpf/Internal/SettingsViewModel.cs`）。「账户与备份」「维护」是模块贡献页分区（`AuthUiModule.cs:45`、`MaintenanceUiModule.cs:45` 注册 `SettingsPageContribution`），点击这类分区时 `SelectSectionAsync` 先给 `SelectedSection` 赋值，`SelectedPage` 立即非空、`HasSettingsSectionContent` 变为 false——即使随后因「请先离开当前编辑」守卫而页面未激活，顶部说明与开关也已消失。该误绑随 0.17.2 的设置容器改版引入（`docs/settings-maintenance-layout-plan.md:36` 只要求隐藏「0 项设置」计数、恢复默认等通用参数控件，顶部说明不属于该范围）。
  - 同文件 `SettingsViewModel.SelectedSection` setter 只调 `ApplySectionFilter()`，未通知 `SelectedPage`/`HasSelectedPage`/`HasSettingsSectionContent`；直接给 `SelectedSection` 赋值的路径（Reveal、语言切换、分区回退）下界面会沿用旧的可见性。
- 改动位置：
  - `src/Modules/Settings/Mosaic.Settings.Wpf/Views/SettingsView.xaml:105`（`SubtitleText`）与 `:109`（`ModeBar`）：可见性由 `HasSettingsSectionContent`+`BoolVisibility` 改为 `HasSelectedPage`+`InverseBoolVisibility`（仅模块页内容实际激活时隐藏），并补 `x:Name` 供测试断言。
  - `src/Modules/Settings/Mosaic.Settings.Wpf/Internal/SettingsViewModel.cs:375-384`（`SelectedSection` setter）：`SetProperty` 成功后追加 `NotifyPageContentChanged()`；`:661-677` 新增 `NotifyPageContentChanged()` 统一通知 `SelectedPage`/`HasSelectedPage`/`HasSettingsSectionContent`，`ApplySectionFilter()` 与 `SelectPageAsync()`（`:479`）改经由此处。
  - `tests/Mosaic.Wpf.Tests/PresentationTests.cs:1231` 新增 `Header_banner_stays_visible_on_page_sections_until_the_page_is_active`（真实模块+视图集成测试：常规分区可见 → 选中模块页分区未激活仍可见 → 页面激活后隐藏）；`:1197` `Switching_to_a_contributed_page_hides_section_chrome_and_back_restores_it` 双向补 `HasSelectedPage` 通知断言；`:1296` 保留 `Direct_section_assignment_onto_a_page_section_notifies_settings_chrome_visibility`（直接赋值路径通知回归）。
- 迁移逻辑与验收项：无数据迁移、无配置变化；升级沿用既有 MajorUpgrade。
- 验证：`dotnet test tests/Mosaic.Wpf.Tests` 全量 137 通过；`python -I scripts/release_content_verify.py` 通过（41 个已索引发布）。
- 未验证项：未做实机截图核对（绑定行为由集成测试覆盖）。

## 0.17.7：浏览/恢复默认 2856 修复（2026-11-21）

- 根因位置（以下行号指 0.17.6 已发布版本 `4e3ae54`）：
  - `build/installer/Dialogs.wxs` 的 LocationDlg「浏览...」（`Browse` 按钮）与「恢复默认」（`Restore` 按钮）在自定义动作完成后使用 `NewDialog LocationDlg` 回到同一对话框刷新路径显示；`PathEdit` 绑定的 `MOSAICINSTALLPATH` 一旦有变更通知（仅聚焦即会追加尾随反斜杠），MSI 会把对话框标记为仍被引用，拒绝销毁，`NewDialog` 到同一对话框直接报引擎 2856（Creating a second copy of the dialog LocationDlg）并中断安装。
- 改动位置：
  - `build/installer/Dialogs.wxs`：`Browse`/`Restore` 按钮第 3 个 `Publish` 由 `NewDialog LocationDlg` 改为 `Property="MOSAICINSTALLPATH" Value="[MOSAICINSTALLPATH]"`（SetProperty 控制事件，生成的 ControlEvent 行 Event 列为 `[MOSAICINSTALLPATH]`、非 NewDialog），把新值推回 PathEdit 即可重绘，不再重建对话框；保留第 1–2 个 `DoAction`（`BrowseInstallFolder`/`RestoreDefaultInstallFolder` + `PrefillInstallPath`）。
  - `build/test-package-maintenance.ps1`：新增断言 `LocationDlg` 的 `Browse`/`Restore` 按钮不得再使用 `NewDialog LocationDlg`，且必须包含 `Property="MOSAICINSTALLPATH"` 的 SetProperty 事件。
- 迁移逻辑与验收项：无数据迁移；升级沿用既有 MajorUpgrade。
- 实机验收（已执行，2026-11-21，隔离账户 `MosaicQa0176` 与实机当前用户）：
  - 复现：点击「浏览...」立即弹出「错误代码是 2856」并出现两个 LocationDlg 副本，日志 `2856-repro.log` 含 `DEBUG: Error 2856: Creating a second copy of the dialog LocationDlg`；
  - 修复后：浏览选择 `E:\app` 成功返回位置页并显示 `E:\app\Mosaic`，下一步进入确认页并完成安装，exit 0；
  - 证据：`docs/evidence/0.17.7/2856-fix-verification.zip`（7 个 /L*V! 日志与驱动脚本）、`scripts/qa/_full_2856_test.py`（端到端 UI 驱动）。
- 未验证项：执行期 `FilesInUse` 对话框、3010/待重启分支；维护在线修复（引擎限制，须通过「应用和功能」修复或重新运行安装包）。

## 0.17.6（增量）：跨盘安装与错误显示修复（2026-11-21）

- 根因位置（以下行号指 0.17.6 已发布版本 `4e3ae54`）：
  - 位置页空属性触发引擎 2343 并被 2814 掩盖：`build/installer/actions/MosaicInstallerActions.cpp:719-725` 的默认位置预填依赖 `SHGetFolderPathW(CSIDL_LOCAL_APPDATA)`，在自定义动作服务端会话（未加载用户配置文件）返回空字符串；`Dialogs.wxs:17` 的 FreshStartDlg「下一步」直接切到 `LocationDlg`，其 PathEdit 绑定的 `MOSAICINSTALLPATH` 为空，MSI 在创建对话框时报引擎 2343；`Package.wxs:7` 的 `ErrorDialog=FatalError` 兼作引擎错误框，MSI 按消息类型只创建 Error 按钮，而 `Dialogs.wxs:170` 的 `ErrorText TabSkip="no"` 使 Control_Next 指向未创建的 ErrorYes，真实 2343 被 2814 覆盖。
  - 引擎回滚备份在非系统驱动器权限失败：任何文件替换（REINSTALL 重写或卸载后重装的事务内移除）都会让引擎在目标卷根目录 `Config.Msi` 创建 `.rbf` 回滚备份；per-user 安装位于 D/E 等固定盘时该目录只允许 SYSTEM/Admin，用户上下文删除时报错误 5，并弹出引擎强制错误框（无法用 ESC/关闭按钮正常关闭，只能用退出进程）。
- 改动位置：
  - `build/installer/actions/MosaicInstallerActions.cpp`：`GetLocalAppData`/`DefaultInstallFolder`/`ShortcutLinkPath` 增加 MSI 属性优先（LocalAppDataFolder/DesktopFolder/ProgramMenuFolder），不再依赖 SHGetFolderPathW；新增 `PrefillInstallPath` 导出（PathEdit 属性为空时按最终目录预填，命令行显式路径不覆盖）；`ScanPayload` 不再设置自动修复标记（`MOSAICREPAIRAUTOMATIC` 清空）；`VerifyRepairedPayload` 复检提示改为引导用户通过「应用和功能」修复或重新运行安装包；`ApplyRepairRequest` 改为空操作占位（保留导出防止旧包引用悬空，不再设置 REINSTALL/REMOVE）。
  - `build/installer/Dialogs.wxs`：`FreshStartDlg`「下一步」先执行 `PrefillInstallPath` 再切换 `LocationDlg`；`LocationDlg` 的「浏览」「恢复默认」同样预填后切页；`MaintenanceDlg` 提示文案改为「发现缺失或损坏时请通过「应用和功能」修复，或重新运行本安装包。本向导不会改动文件」；`FatalError` 简化为普通收尾页（不再兼作 ErrorDialog）。
  - `build/installer/Package.wxs`：移除 `ErrorDialog` 属性（引擎错误改由系统消息框显示真实错误码）；新增 `PrefillInstallPath` 声明；`VerifyRepairedPayload` 条件改为 `NOT REMOVE AND REINSTALL`（检测到缺失/损坏但不自动修复时不复检）；移除 `ApplyRepairRequest` 序列（不自动在线修复）。
  - `build/test-package-maintenance.ps1`：断言 `ErrorDialog` 不得设置；断言 `PrefillInstallPath` 与 `LocationDlg` 预填事件存在；断言维护不得自动 REINSTALL/REMOVE/REINSTALLMODE；断言 `VerifyRepairedPayload` 仅在 `REINSTALL` 时运行。0.17.5 旧包在新断言下失败（红），重建后 0.17.6 通过（绿）。
  - `src/Modules/Maintenance/Mosaic.Maintenance.Windows/WindowsProcessScheduler.cs`：应用内「修复安装」由 `/famus` 改为 `/femus`（只覆盖校验不符/缺失的文件，与安装包行为一致，避免全量覆盖触发引擎回滚备份）。
- 迁移逻辑与验收项：无数据迁移；升级沿用既有 MajorUpgrade。
- 实机验收（已执行，2026-11-21，隔离账户 `MosaicQa0176`）：
  - 复现链：默认 UI（无命令行路径）首次进入位置页 → MSI 2343 → FatalError 2814（与用户截图一致）；D 盘手工输入完整 UI 安装 → 470 文件 + 13 子目录 + 登记 + 快捷方式全部落位，exit 0，无引擎错误框；
  - 维护修复策略：同版本维护检测到缺失/损坏（文件+快捷方式）时显示「请通过「应用和功能」修复，或重新运行本安装包」，不自动执行修复、不弹引擎错误框；
  - 健康维护：显示「安装完整，无需修复」，零写入，exit 0；
  - 静默卸载：无任何窗口，D 盘目录清空，哨兵数据保留；
  - 证据：`docs/evidence/0.17.7/cross-drive-repro.json`、`cross-d-real.json`、`final3-result.json`、`2856-fix-verification.zip` 中的 `cross-*.log`；脚本 `scripts/qa/cross_drive_repro.py`、`scripts/qa/cross_drive_typed.py`、`scripts/qa/final_matrix.py`。
- 未验证项：执行期 `FilesInUse` 对话框、3010/待重启分支；维护在线修复（引擎限制，须通过「应用和功能」修复或重新运行安装包）；0.17.5 → 0.17.6 原位升级在跨盘场景下的数据保留（本矩阵只覆盖同版本维护与全新安装）。

## 0.17.6：完整界面进度页卡死修复（2026-11-20）

- 根因位置（以下行号指已提交的 0.17.5 版本 `3f91fd3`）：
  - 模态进度页阻塞执行阶段：`build/installer/Dialogs.wxs:126` 的 `<Dialog Id="ProgressDlg" …>` 没有声明 `Modeless`，编译出的 `Dialog` 表 `ProgressDlg.Attributes=3`（可见 1 + 模态 2）；`build/installer/Package.wxs:75` 在 `InstallUISequence` 1290 显示该页，而 `ExecuteAction` 位于 1300。模态对话框结束前界面序列不会继续，`ExecuteAction` 永不启动，用户只看到空白的「正在处理，请稍候...」页面且无任何进度。完整界面（默认或 `/qf`）首次安装与损坏维护都经过该页；健康维护提前结束与静默安装不经过，因此未提前暴露。
  - 进度显示没有订阅安装引擎事件：`Dialogs.wxs:128` 的 `ActionText` 控件文本为静态属性 `[ActionText]`，`:131` 的 `ProgressBar` 没有任何订阅；发布 MSI 的 `_Tables` 中不存在 `EventMapping`。即使只解除模态阻塞，动作文字与进度条也不会随执行阶段更新。
  - 取消语义未适配非模态：`Dialogs.wxs:132-134` 直接发布 `EndDialog Exit`，发布 MSI 的 `ControlEvent` 相同；进度页非模态后需要先确认再通知安装引擎取消。
  - 静态检查遗漏：`build/test-package-maintenance.ps1` 原来只检查进度页序号与扫描摘要，没有检查 `Dialog.Attributes` 的模态位、`EventMapping` 订阅与取消事件链路，因此该缺陷能通过发布检查。
- 改动位置：
  - 安装界面 `build/installer/Dialogs.wxs`：
    - `ProgressDlg:129` 增加 `Modeless="yes"`（与既有 `NoMinimize="yes"` 并存），编译后 `Dialog.Attributes` 仅为可见位 1；
    - `ActionText:131-133` 去掉静态文本 `[ActionText]`，改为 `<Subscribe Event="ActionText" Attribute="Text"/>`；
    - `ProgressBar:136-138` 增加 `<Subscribe Event="SetProgress" Attribute="Progress"/>`；
    - `Cancel:139-141` 由 `EndDialog Exit` 改为 `SpawnDialog CancelDlg`；
    - 新增 `CancelDlg:144-153`：「是」发布 `EndDialog Exit`（界面序列结束，安装引擎取消并回滚），「否」发布 `EndDialog Return`（回到进度页继续安装）。
  - 包定义 `build/installer/Package.wxs`：无改动。已核对 `Show ProgressDlg 1290` 早于 `ExecuteAction 1300`、`VerifyRepairedPayload 1301`、`Show Exit 1303`，健康维护 `EndDialog Exit` 条件与静默执行序列保持不变。
  - 静态包校验 `build/test-package-maintenance.ps1:203-239`：新增 `ProgressDlg` 非模态（`Attributes & 2 = 0`）且可见（`Attributes & 1 ≠ 0`）、`ProgressDlg` 显示序号早于唯一存在的 `ExecuteAction`、`ActionText` 控件不得再读静态 `[ActionText]`、`EventMapping` 中 `ProgressDlg/ActionText/ActionText/Text` 与 `ProgressDlg/ProgressBar/SetProgress/Progress` 各一条、`CancelDlg` 存在、`ProgressDlg.Cancel → SpawnDialog CancelDlg`、`CancelDlg.Yes → EndDialog Exit`、`CancelDlg.No → EndDialog Return` 断言。旧 0.17.5 包在第一条断言即失败。
  - 版本与发布元数据：`Directory.Build.props:12`（0.17.6）、`build/installer/Mosaic.Installer.wixproj:4`（默认 `ProductVersion` 0.17.6）、`build/release-content.json`（第 39 条 `mosaic-0.17.6`，`previousReleaseId=mosaic-0.17.5`，指纹 `D0281954270045AE22FCF4AB1622326176AD4550405E87B2B5C975A6580CDE78`）、`build/published-release-index.json`（第 39 条）、`build/release-manifest.json`、`docs/releases/v0.17.6.md`、`README.md:3`、`README.md:45`、`README.md:47`、`docs/installer-progress-hang-fix-plan.md`（状态行）、本文件；发布方案 `docs/installer-progress-hang-fix-plan.md` 随本次提交纳入仓库。
- 迁移逻辑与验收项：
  - 无数据迁移：UpgradeCode `{4D2B073C-72D5-4C3A-B845-65FC2C52D23C}`、组件 GUID/KeyPath、per-user 范围、安装目录、注册表结构与用户数据目录均未变化；0.17.5 → 0.17.6 仍由 `MajorUpgrade afterInstallInitialize` 原地完成。
  - 行为变化仅限完整界面的进度页：首次安装与损坏维护不再被进度页阻塞，操作文字与进度条随安装引擎更新；进度页取消先弹确认。静默安装、健康维护提前结束、显式 `/f`、失败 1603 与待重启 3010 语义不变。
  - 验收项（已在隔离账户 `MosaicQa0176` 执行，逐项结果见本节「实机验收（已执行）」）：完整 UI 首次安装（默认与含空格自定义路径）日志到达 `ExecuteAction`/`InstallInitialize`/`InstallFiles`/`InstallFinalize` 并完成；执行期间操作文字与进度条更新；取消→「否」继续安装、取消→「是」结束并回滚且无后台会话；损坏维护修复进度与复检；0.17.5 → 0.17.6 原位升级保留位置与数据；健康维护提前结束、静默安装与退出码不回归。
- 验证（静态）：
  - `build/package.ps1` 完整执行：WiX 构建 0 警告 0 错误；release content 校验 `mosaic-0.17.6`（39 条索引）；`test-package-shortcuts.ps1` PASS；`test-package-maintenance.ps1` PASS，470 个 cabinet 成员逐个 SHA-256 与清单一致（日志 `artifacts/qa/build-0176-final.log`，本地生成，未纳入 git）。
  - 修复前后对照：0.17.5 发布 MSI 在同一检查下失败，报 `ProgressDlg must be modeless (Dialog.Attributes must not contain msidbDialogAttributesModal=2); a modal progress page blocks ExecuteAction forever.`；0.17.6 包通过全部断言。0.17.6 包表证据（`docs/evidence/0.17.6/msi-dialogs.json`，由 `scripts/qa/dump_dialogs.py` 只读导出）：`ProgressDlg` 的 `Dialog.Attributes=1`、`CancelDlg` 存在、`EventMapping` 含上述两条订阅、`InstallUISequence` 为 `ProgressDlg=1290`、`ExecuteAction=1300`。
  - 产物：`Mosaic-0.17.6-win-x64.msi`，52,470,860 字节，SHA-256 `337F2F71016EAF7F0185B59FFB2A4D14C88F0A8AFC2B9B53FDEBEFAB7BC7D570`（由本次 `build/package.ps1` 重新构建，包结构与静态校验结果同上）。
- 实机验收（已执行，2026-11-21，隔离账户 `MosaicQa0176`，完整交互 UI）：
  - 用户以「授权验收」授权一次 UAC 提权创建临时本地账户并执行交互矩阵。隔离方式：`scripts/qa/qa_win.py` 用 `CreateProcessWithLogonW(LOGON_WITH_PROFILE)` 以该账户启动全部安装会话（显式环境把 USERPROFILE/LOCALAPPDATA/APPDATA/TEMP 指向 `C:\Users\MosaicQa0176`）；QA 结果只写 `C:\Users\Public\MosaicQA0176`。真实安装（ASUS 账户 0.17.2）验收前后逐值比对 `HKCU\Software\Mosaic`、产品注册与程序文件哈希：无任何改动（结论见 `docs/evidence/0.17.6/ui-matrix/summary.json` 的 `real_install_check`）。
  - 矩阵 8 场景全部通过（`scripts/qa/ui_matrix.py`，结论 `docs/evidence/0.17.6/ui-matrix/summary.json`，原始记录 `results.json`，完整 /L*V! 日志 25 个压缩于 `C:\Users\Public\MosaicQA0176\archive\ui-matrix-logs.zip`，关键截图 8 张随本提交归档）：
    - S1 0.17.5 `/qf` 首装（含中文与空格自定义路径）：复现卡死——进度页停留 14 秒以上、动作文本仅静态提示、进度值恒 0、挂起时日志无 `ExecuteAction`，点取消后 1602，无安装目录与登记；
    - S2 0.17.6 `/qf` 首装（同路径）：进度页实时动作文本+进度条连续推进（16 个不同进度值）、完成页、退出码 0、483 个文件全部落位（470 主清单 + 13 语言子目录）、单一 ARP 登记 0.17.6、桌面与开始菜单快捷方式齐全；
    - S3 健康维护：显示「安装完整，无需修复 / 已检查 470 个文件」，点完成退出码 0，文件哈希与修改时间零改动；
    - S4 损坏维护：删除 1 个文件 + 篡改 1 个 DLL 后扫描正确报告「1 个文件缺失，1 个文件损坏」，修复后复检「修复已完成，全部受管文件与快捷方式均通过复检」，两个文件哈希恢复；
    - S5 升级：0.17.5 静默安装（自定义路径+哨兵数据）→ 0.17.6 `/qf` 显示升级页并正确列出「0.17.5 将升级到 0.17.6」「安装位置保持不变」，升级后位置保留、哨兵数据保留、二进制已替换、单一登记 0.17.6、退出码 0；
    - S6a 取消→否：确认弹窗出现、选「否」回到进度页并完成安装（退出码 0，文件与登记齐全）；
    - S6b 取消→是：确认弹窗出现、选「是」进入「安装已取消」页、1602 结束、回滚干净（无目录、无 ARP 登记）；
    - S7 静默回归：`/qn` 安装、维护、卸载全程无任何窗口，返回码均 0，卸载后目录清空。
  - 验收过程中发现并已修复的三个新增包缺陷（同属本次交付单元）：
    - UI 自定义路径安装丢失 156 个语言资源文件：UI 序列 `CostFinalize` 先于路径校验把子目录属性按默认 `%LOCALAPPDATA%` 解析，执行序列不会重算已存在的目录属性，资源 DLL 落进默认目录，维护扫描误报缺失并 1603。修复：`scripts/package_payload.py` 与 `build/package.ps1` 为 13 个子目录生成 Type-51 动作（`SetDir<目录Id>`，序 960-972，在 `EnforceInstallFolder`(950) 之后、`CostFinalize`(1000) 之前，值 `[INSTALLFOLDER]\<lang>` 显式补分隔符）；静态检查新增逐子目录断言（Type-51 存在、序号区间正确）。
    - 成功收尾被误报为取消：`Dialogs.wxs` Exit 页「完成」与健康维护提前结束原为 `EndDialog Exit`（MSI 语义=用户取消 1602，随后误入 UserExit 页）；改为 `EndDialog Return`，静态检查断言 Exit.Finish 与 MaintenanceDlg 健康分支必须 Return。
    - 修复请求传递缺陷：`ScanPayload` 在 UI 序列设置的 `REINSTALL=ALL` 不会随属性快照传入服务端会话（实测服务端只收到 `REINSTALLMODE` 与 `MOSAICREPAIRNEEDED`），损坏修复复制数为 0。修复：`MosaicInstallerActions.cpp` 新增 `ApplyRepairRequest`（执行序列 995，`CostFinalize` 之前），在服务端会话内依据扫描标记设置 `REINSTALL=ALL`；显式 `/f` 优先不覆盖。静态检查新增源码契约与序列断言。0.17.5 旧包在新断言下失败（红），重建后 0.17.6 通过（绿）。
  - 重新构建产物：`artifacts/installer/0.17.6/Mosaic-0.17.6-win-x64.msi`，52,516,584 字节，SHA-256 `05DDC4E7992797232FAAE2949FD7A4365A672A74BB885F9EBFE2ACF15463BA3C`（构建日志 `artifacts/qa/build-0176-final5.log`/`build-0176-final6.log`/`build-0176-final7.log`，本地生成）。
- 未验证项：执行期 `FilesInUse` 对话框、3010/待重启分支，以及 FatalError 页的 MSI 2814 控件链告警（仅在安装失败弹 FatalError 时出现的次生显示缺陷，本次未复现修复）。中文安装路径已在 S2/S5/S6/S7 覆盖；验收账户 140% DPI 下界面正常显示。

## 0.17.5：卸载按登记位置移除、关闭 MSI 数据库句柄（2026-11-20）

- 根因位置（以下行号指已提交的 0.17.4 版本 `077bfab`）：
  - 自定义安装位置卸载后文件残留：`build/installer/Package.wxs:52` 的 `DetectInstallState`（执行序列 26）条件为 `NOT REMOVE`，`:54` 的 `EnforceInstallFolder`（950）条件为 `NOT Installed AND NOT REMOVE`。卸载会话中两者都被跳过，`INSTALLFOLDER` 没有任何属性赋值，目录解析回退到 `Package.wxs:15` 的 `Directory` 表默认目录 `%LOCALAPPDATA%\Programs\Mosaic`；`RemoveFiles` 因此只删除默认目录，自定义位置中的 470 个程序文件全部遗留。实机日志证据（`10-uninstall-custom.log`，0.17.4 复现时）：`Skipping action: EnforceInstallFolder (condition is false)` 与 `PROPERTY CHANGE: Adding INSTALLFOLDER property. Its value is 'C:\Users\MosaicQA\AppData\Local\Programs\Mosaic\'`，随后 `FileRemove` 全部指向默认目录。
  - 每次扫描泄漏一个 MSIHANDLE：`build/installer/actions/MosaicInstallerActions.cpp:218` 通过 `MsiGetActiveDatabase` 取得数据库句柄，原实现只在 `:257` 关闭 `view`，从未关闭数据库句柄。MSI 在动作结束时报告 `DEBUG: Error 2769: Custom Action ScanPayload did not close 1 MSIHANDLEs.`；每次打开维护页（UI 扫描）或静默扫描都会出现该警告。
- 改动位置：
  - 原生动作 `build/installer/actions/MosaicInstallerActions.cpp`：
    - `ReadBinaryStream:215`：`:227`（`MsiDatabaseOpenViewW` 失败路径）关闭数据库句柄；`:263`（正常路径）关闭 `view` 后关闭数据库句柄。
    - 新增 `RestoreInstallFolderForRemoval:762`：读取 `ProductCode`，用 `GetProductInfo(..., INSTALLPROPERTY_INSTALLLOCATION)` 取产品登记位置并 `TrimEndingSeparators`；位置为空或为相对路径（0.17.2 及更早未登记）时写日志并保持默认目录语义；否则设置 `INSTALLFOLDER` 与 `MOSAICINSTALLEDLOCATION`。
  - 包定义 `build/installer/Package.wxs`：`:47` 声明 `RestoreInstallFolderForRemoval`（`Execute="immediate"`、`Return="ignore"`）；`:54-55` 在 `DetectInstallState` 26 之后、`CostFinalize` 900 之前新增序列 27，条件 `REMOVE`。
  - 静态包校验 `build/test-package-maintenance.ps1`：`:93-101` 新增源码契约（必须包含 `RestoreInstallFolderForRemoval`、`INSTALLPROPERTY_INSTALLLOCATION`、`MsiCloseHandle(database)`）；`:106-109` 新增执行序列断言（动作位于 27 且条件含 `REMOVE`）。
  - 版本与发布元数据：`Directory.Build.props:12`（0.17.5）、`build/installer/Mosaic.Installer.wixproj:4`（默认 `ProductVersion` 0.17.5）、`build/release-content.json`（第 38 条 `mosaic-0.17.5`，`previousReleaseId=mosaic-0.17.4`，指纹 `777313277E4763A3183DF2ACD2BECEA302067E17CB9BD3CDB603C3AF24AB6218`）、`build/published-release-index.json`（第 38 条）、`build/release-manifest.json`、`docs/releases/v0.17.5.md`、`README.md:3` 与当前版本说明/下载链接、本文件、`docs/installer-location-plan.md` §10.9。
- 迁移逻辑与验收项：
  - 数据与配置迁移：无。组件 GUID、KeyPath、UpgradeCode、per-user 范围、数据目录与注册表结构均未变化，升级仍由 `MajorUpgrade afterInstallInitialize` 原地完成。`%LOCALAPPDATA%\Mosaic` 下的用户数据不参与卸载删除。
  - 已登记位置的产品（0.17.3/0.17.4）：卸载按登记位置删除程序文件与空目录；升级保持原位置。
  - 未登记位置的旧安装（0.17.2 及更早）：`RestoreInstallFolderForRemoval` 读取为空或相对路径时只写日志，卸载保持默认目录语义，与旧行为一致；动作 `Return="ignore"`，不新增卸载失败路径。
  - 验收项（实机，隔离用户 `MosaicQA`）：自定义位置卸载后目录内文件数为 0 且目录不存在、产品登记被移除；默认位置卸载删除负载并保留用户哨兵文件；全部安装/维护日志不再出现 `did not close`/2769；0.17.4 → 0.17.5 升级保留自定义位置；健康维护零写入；删除 EXE 与同尺寸篡改 DLL 后修复恢复；显式 `/f` 重写 470 个文件；拒绝读取时静默安装返回 1603；应用运行中修复由 Restart Manager 关闭并在完成后重启应用（返回 0）；文件被非重启管理器进程占用时修复返回 1603 并整体回滚，释放占用后再修复返回 0 且内容恢复。
- 验证（静态）：`build/package.ps1` 完整执行，WiX 0 警告 0 错误；release content 校验 `mosaic-0.17.5`（38 条索引）；`test-package-shortcuts.ps1` PASS；`test-package-maintenance.ps1` PASS（含新增源码与序列断言）；470 个 cabinet 成员逐文件 SHA-256 与清单一致。产物 `Mosaic-0.17.5-win-x64.msi`，52,466,396 字节，SHA-256 `F8F23394EAF0B82F36454C3C83A3C89D4888CCC3C3D177C4321B82C3C405B433`。
- 实机验收（0.17.5 MSI，隔离用户 `MosaicQA`，独立 profile `C:\Users\MosaicQA`；以下结论为执行时记录，原始证据保留状态见本节末条）：
  - 首轮维护矩阵 39 项，38 项一次通过；唯一未通过项是测试脚本自身的时区比较缺陷（`[datetime]'…Z'` 在 PowerShell 5.1 被转成本地时间，与 `LastWriteTimeUtc` 的 ticks 不等），同一步的 `explicit-f-rewrites-files` 以哨兵年份变化、mtime 恢复为包内时间与日志 `FileCopy=470` 判定通过，非产品缺陷。修正断言后复跑（22:11–22:13，同一隔离用户与独立 profile）：39 项全部通过，当时的 `result-0175.json` 记录 `ok=true`、失败项为空；日志为当时的 `logs-0175\01`–`10`。
  - 修复点 1 实测：0.17.4 → 0.17.5 升级保留 `C:\Users\Public\MosaicQA\Install Folder`；`/x` 日志先执行 `RestoreInstallFolderForRemoval` 并记录 `Mosaic: 卸载位置 C:\Users\Public\MosaicQA\Install Folder`（返回值 1，`Return=ignore`），卸载后 `files=0`、目录不存在、ARP 登记为空。
  - 修复点 2 实测：0.17.5 的 01–04 号安装/维护/修复/显式 `/f` 日志中 `2769` 与 `did not close` 计数均为 0，全量日志扫描断言通过；0.17.4 同场景曾每次扫描出现一条。
  - 其他通过项：默认位置安装与 ARP/快捷方式登记；健康维护零写入且哨兵文件哈希与修改时间不变；删除 EXE + 同尺寸清零 DLL 后修复恢复内容与快捷方式；拒绝读取时 `/qn` 返回 1603 且不误报；默认位置卸载删除负载、保留 `%LOCALAPPDATA%\Mosaic` 哨兵与移除 ARP；0.17.4 → 0.17.5 升级版本与位置正确、未在默认目录另装；升级后健康维护零写入。
  - 文件占用场景（隔离用户，0.17.5 MSI）：
    - 应用正在运行时执行 `REINSTALL=ALL REINSTALLMODE=amus`：日志依次记录 `RESTART MANAGER: Will attempt to shut down and restart applications in no UI modes.`、`Successfully shut down all applications that held files in use.`、`Previously shut down applications have been restarted.`；引擎返回 0，470 个文件全部重写，EXE 哈希与包内一致，`PendingFileRenameOperations` 无 Mosaic 项，未出现 3010。
    - 文件被非重启管理器进程占用（ASUS 侧以 `FileShare.Read` 持有读共享锁，EXE 已清零）：`Executing op: FileCopy(… Mosaic.Desktop.exe …)` 之后立即记录 `Note: 1: 2205 2: 3: Error` 与 `Note: 1: 2228 … Error = 1603`，引擎重试约 3 分钟后以 `MainEngineThread is returning 1603` 结束并整体回滚；回滚后 EXE 仍为清零内容（哈希 `04DB6BC9…09E3`），安装集其余文件未被破坏。
    - 释放占用后再执行同一修复：引擎返回 0，470 个 `FileCopy`，EXE 哈希恢复为 `34F1F22E…D124`，脚本记录 `healthy=true`。
    - 待重启（3010）分支在本矩阵未被触发：两种占用分别由 Restart Manager 关闭重启或以 1603 失败回滚，没有出现「由 Windows Installer 安排到重启后替换」的情形；`IsRebootPending` 与 3010 语义仍由静态断言与代码审查覆盖。
  - 证据保留状态（2026-11-20 补记）：上述 39/39 与文件占用三项结论，是执行时依据 `result-0175.json`、`result-locked.json`、`result-appf2.json` 以及 `logs-0175\01`–`10`、`12-locked-repair-1/2.log`、`14-app-running-reinstall-all.log` 记录的结果。这些文件只存在于隔离测试目录 `C:\Users\Public\MosaicQA`，从未纳入 git，也未归档到仓库；验收完成、发布核对通过后的环境清理中随该目录整体删除，现无可提供的路径，也无法逐项独立复核。上文出现的日志名与日志片段是当时的摘录，不是现在可以读取的证据。此状况只说明证据未归档，不改变「当时实测通过」的历史记录，也不等于验收失败或测试失败。当前可独立复核的仅为静态材料：`3f91fd3` 中的源码契约与序列断言（`build/test-package-maintenance.ps1:93-101`、`:106-109`）、发布资产的字节数与 SHA-256，以及包内 470 个 cabinet 成员哈希——它们不能替代实机结论。今后如需实机复核，须在隔离环境重跑，并先将脱敏后的结果 JSON、命令与运行环境、日志归档入库，再清理测试目录。
- 未验证项：中文安装路径（矩阵只覆盖含空格的英文路径）、位置校验/取消路径（卷根、UNC、无权限、数据目录冲突）、交互 UI 的 FilesInUse 对话框与取消/回滚路径、3010/待重启语义、降级拒绝、0.17.2 起点升级（矩阵以 0.17.4 为旧版）、DPI/窄窗口文案、Windows 10 实机、ARM64、干净 VM 与真实安装介质；后几项与 0.17.4 保持相同的验证边界，不影响本次两项修复的判定。

## 0.17.4：同版本维护自动扫描、健康分支提前结束与失败/待重启状态传播（2026-11-20）

- 根因位置（以下行号指已提交的 0.17.3 版本 `ba54e76`）：
  - 维护必须手动点「下一步」才开始扫描：`build/installer/Dialogs.wxs:69` 的 `MaintStartDlg` 是维护的唯一入口，`ScanPayload` 只挂在该页按钮事件 `:77` 上；`build/installer/Package.wxs:64` 在 UI 序列 1002 直接显示这一页。于是「再次运行安装包先自动扫描」并未实现，健康判断也晚于用户交互。
  - 健康分支仍进入写文件的执行阶段，且「无写入」没有执行序列依据：`Dialogs.wxs:85` 的维护页按钮 `:96` 是无条件 `EndDialog Return`，健康时也继续到 `ExecuteAction`/`InstallFinalize`；`Package.wxs:57` 的 `SetArpInstallLocation` 条件 `NOT Installed OR MOSAICLOCATIONINHERITED` 会在健康维护时也改写 `ARPINSTALLLOCATION`。健康「不写文件」仅靠「扫描后没有设置 `REINSTALL`」间接推断，执行序列里没有任何基于扫描结论的分流。
  - 扫描与复检失败恒为成功：`build/installer/actions/MosaicInstallerActions.cpp:975-999`（`ScanPayload`）与 `:1002-1017`（`VerifyRepairedPayload`）在所有分支都只写属性并 `return ERROR_SUCCESS`（`:999`、`:1017`）。安装目录不可读、载荷清单损坏、位置无法确认、修复后哈希仍不符时，`/qn` 依然返回 0，交互安装依然落到完成页；自定义动作声明了 `Return="check"` 但从未返回失败码，等于没有失败传播。
  - 待重启与失败没有区分：`VerifyRepairedPayload` 未检查 `REBOOT`、`MSIRUNMODE_REBOOTATEND`/`MSIRUNMODE_REBOOTNOW`。被占用而由 Windows Installer 安排到重启后替换的文件，在重启前哈希必然不符，旧实现会把它当作「修复成功」（完成文案）或普通失败（无 3010 语义），两者都不符合计划要求。
- 改动位置：
  - 原生动作 `build/installer/actions/MosaicInstallerActions.cpp`：
    - 新增 `ReportError:68`：用 `MsiCreateRecord(1)` + `MsiRecordSetStringW(record, 0, …)` + `MsiProcessMessage(hInstall, INSTALLMESSAGE_ERROR, …)` 走标准错误消息，同时写日志；只设属性不会让 `/qn` 失败，也不会让 UI 进入错误页。
    - 新增 `IsRebootPending:78`：检查 `REBOOT` 属性与 `MsiGetMode` 的 `MSIRUNMODE_REBOOTATEND`/`MSIRUNMODE_REBOOTNOW`。
    - `ScanPayload:992` 重写：读取失败、清单错误或位置不可确认（`:1001-1007`）清空修复属性后 `ReportError` 并 `return ERROR_INSTALL_FAILURE`；健康/需修复分别发布 `MOSAICMAINTBUTTONTEXT`（`:1011`）；仅在调用方未显式设置 `REINSTALL` 时才自动写入 `REINSTALL=ALL`/`REINSTALLMODE=amus`（`:1016-1021`），显式 `/f` 原样保留；健康时不置 `MOSAICREPAIRNEEDED`。
    - `VerifyRepairedPayload:1032` 重写：复检健康 → `MOSAICVERIFYSTATUS=Healthy`、标题「修复已完成」并成功（`:1039-1045`）；复检不健康但 `IsRebootPending` → `MOSAICVERIFYSTATUS=PendingReboot`、标题「需要重启后完成修复」，返回成功以保留 3010（`:1047-1056`）；其余情况 `ReportError` 并 `return ERROR_INSTALL_FAILURE`（`:1058-1063`）。
  - 包定义 `build/installer/Package.wxs`：`:9` 新增 `MOSAICEXITTITLE`（结果页动态标题）；`:56` 执行序列 `ScanPayload` 增加 `NOT MOSAICSCANSTATUS`（有 UI 时复用 UI 扫描结果，`/qn` 无 UI 时在此扫描）；`:59` `SetArpInstallLocation` 条件增加 `NOT (MOSAICSCANSTATUS = "Healthy" AND NOT REINSTALL)`（健康维护不做登记变更）；`:61` 复检条件改为 `REINSTALL OR MOSAICREPAIRNEEDED`；`:67` UI 序列新增 `ScanPayload` 1002（维护态自动扫描）；`:68` `MaintenanceDlg` 只在 `MOSAICSCANSTATUS = "Healthy" AND NOT REINSTALL` 时显示；`:74` UI 侧复检同样覆盖显式 `/f`。
  - 安装界面 `build/installer/Dialogs.wxs`：删除 `MaintStartDlg`；`MaintenanceDlg:72-92` 改为健康结果页（标题「安装完整，无需修复」，列出安装位置、扫描摘要、计数与「完成不会重装」提示；`Rescan:81-84` 重新检查；`Next:85-88` 在健康且无 `/f` 时 `EndDialog Exit`，异常或 `/f` 时 `EndDialog Return`）；`ProgressDlg` 增加 `ScanSummary:129` 与 `RepairHint:130`；`Exit` 标题改为绑定 `[MOSAICEXITTITLE]`（`:138`）。
  - 静态包校验 `build/test-package-maintenance.ps1`：执行序列扫描条件增加 `NOT MOSAICSCANSTATUS` 断言（`:104-106`）；UI 期望序列改为 Fresh 1001、Scan 1002、Maintenance 1003、Upgrade 1004、Blocked 1005、Unknown 1006（`:116-121`）；新增 UI 自动扫描条件与健康页显示条件断言（`:128-136`）；删除 `MaintStartDlg` 残留断言，改为「不存在该页」、健康 `EndDialog Exit`、异常/`/f` `EndDialog Return`、重新检查 `DoAction`、进度页扫描摘要、结果页动态标题（`:173-191`）；新增原生源码行为契约断言（`:84-92`：`INSTALLMESSAGE_ERROR`、`IsRebootPending`、`MSIRUNMODE_REBOOTATEND`、`MOSAICMAINTBUTTONTEXT`、`PendingReboot` 必须出现，`return ERROR_INSTALL_FAILURE` 至少两处）。
  - 版本与发布元数据：`Directory.Build.props:12`（0.17.4）、`build/installer/Mosaic.Installer.wixproj`（默认 `ProductVersion` 0.17.4）、`build/release-content.json`（第 37 条 `mosaic-0.17.4`，`previousReleaseId=mosaic-0.17.3`，指纹 `E2DED972E1E1670A3D4DDF249818995E4910D0C15CFC9A63BDFC2642D7E3A428`，`currentReleaseId=mosaic-0.17.4`）、`build/published-release-index.json`（第 37 条）、`build/release-manifest.json`、`docs/releases/v0.17.4.md`、`README.md:3` 与当前版本说明/下载链接、本文件、`docs/installer-location-plan.md` §10.9。
- 迁移逻辑与验收项：
  - 既有安装（0.17.3 及更早）：组件 GUID、KeyPath、UpgradeCode、per-user 范围、数据目录与注册表结构均未变，升级仍由 `MajorUpgrade afterInstallInitialize` 完成，无需数据迁移。0.17.4 安装后首次再次双击同版本包时，行为变化为「自动扫描、健康即结束」。
  - 兼容性变化：同版本维护扫描/复检失败时静默安装返回码由 0 变为 1603，UI 从完成页改为错误页；调用方若以退出码判断结果，需按非 0 处理（错误页与日志含具体原因）。待重启时返回 0 并保留 3010，页面明确显示「需要重启后完成修复」。
  - 健康维护：交互路径 `EndDialog Exit` 在 `ExecuteAction` 之前结束，不进入修复事务，也不改写 `ARPINSTALLLOCATION`；静默路径不设 `REINSTALL`、不写 ARP 登记，安装事务中组件均为已安装状态。
  - 显式 `/f`：扫描不清除调用方的 `REINSTALL`，健康页不显示（`NOT REINSTALL`），继续到修复并由 1301/6700 复检。
  - 验收（静态）：`test-package-maintenance.ps1` 对上述序列、条件、按钮事件与源码契约逐项断言；实机项见下。
- 验证：`build/installer/actions/build-actions.ps1` 编译通过（`Built …MosaicInstallerActions.dll`）；`build/package.ps1` 完整执行，release content 校验 `mosaic-0.17.4`，WiX 构建 0 警告 0 错误，`test-package-shortcuts.ps1` PASS；首轮 `test-package-maintenance.ps1` 因断言自身把 `InstallUISequence` 索引 1（Condition）当成事件名而误报，修正为按 Action 过滤后对 470 个 cabinet 成员逐个复算 SHA-256 并 PASS（`PASS: MSI static structure, maintenance routing, dialogs, payload manifest and cabinet integrity verified for 0.17.4.`）。产物 `artifacts/installer/0.17.4/Mosaic-0.17.4-win-x64.msi`：52,495,032 字节，SHA-256 `6DE45B7ADF57769ED13EFCBB2AC4E42279C49384A7AC27A2894C4859A8BB025B`。`build/test.ps1 -SkipDesktop` 15 个测试项目 369 项全部通过，0 失败。
- 发布核验（0.17.4）：提交 `077bfab`（`main`）；tag `v0.17.4` 指向 `077bfab`；Release <https://github.com/soehi/Mosaic/releases/tag/v0.17.4>，资产 `Mosaic-0.17.4-win-x64.msi` 52,495,032 字节，GitHub digest `sha256:6de45b7adf57769ed13efcbb2ac4e42279c49384a7ac27a2894c4859a8bb025b` 与本地一致。
- 未验证项（环境阻塞）：本机没有隔离 Windows 用户或可回滚 VM，未对真实安装执行任何删除、篡改、锁定或卸载测试。因此真实安装后自动扫描、删除单文件/整目录修复、文件锁定与重启后替换（3010）、复检失败落错误页、`/qn` 失败返回 1603、显式 `/f`、0.17.3→0.17.4 实机升级与随后同版本维护，均未实测；静态包检查与源码契约不能代替这些验收。另：交互健康维护以 `EndDialog Exit` 结束 UI，不进入执行阶段；该路径的进程返回码属 Windows Installer 的用户结束语义（通常 1602），与静默路径的 0 不同，需在实机项中确认并对调用方说明。

## 0.17.3：可选安装位置 + 同版本 MSI「检查与修复」（2026-11-20）

- 根因位置：
  - 安装位置写死且不可查询：`build/installer/Package.wxs:14`（0.17.2 版本，已提交版本 `081884f`）把安装目录固定为 `LocalAppDataFolder\Programs\Mosaic`，整份 Package 既没有写 `ARPINSTALLLOCATION` 的动作，也没有记录实际安装位置的组件或注册表值。结果「应用和功能」只登记名称与版本，没有 InstallLocation；升级、修复、卸载都只能依赖同一个固定目录，用户无法选择位置，任何定位路径的脚本也只能猜测。
  - 同版本再次运行只能重装：0.17.2 的 Package 没有维护路由（无 `InstallUISequence` 分流、无载荷扫描、无 `REINSTALL` 设置），对同一 ProductCode 再次执行 `msiexec /i` 只会整体重装，无法区分「文件完好」与「文件缺失/损坏」，也无法在程序运行时先判断是否真的需要关闭应用。
  - 本交付内自审发现并已修复的三处实现缺陷（同一提交内完成）：
    - 路径追加子目录后漏复核：`build/installer/actions/MosaicInstallerActions.cpp:1174-1181` 只对用户输入的原始路径判断与数据目录 `%LOCALAPPDATA%\Mosaic` 的重叠关系，`:1187` 才追加 `Mosaic` 子目录；输入 `%LOCALAPPDATA%` 或 `%LOCALAPPDATA%\Programs` 时最终路径会与用户数据目录重合或包含，程序文件可能写进数据目录。修复位置 `:1190-1196`：在最终路径上重新做相等、包含与反向包含判断。
    - ARP 位置写入条件漏掉维护/修复：初版 `SetArpInstallLocation` 条件仅为 `NOT Installed`，已安装产品进入同版本维护或显式 `/f` 时不刷新 `ARPINSTALLLOCATION`，位置登记在维护后就丢失。修复位置 `build/installer/Package.wxs:55-57`：条件改为 `NOT Installed OR MOSAICLOCATIONINHERITED`。
    - 阻断原因被通用文案覆盖：初版阻断对话框只显示固定提示，扫描器写入的具体原因 `MOSAICSTATEERROR` 没有展示，用户无法区分「同版本但产品身份不同」与「已安装更新版本」。修复位置 `build/installer/Package.wxs:13`：启动条件消息前置 `[MOSAICSTATEERROR]`。
    - 既有安装测试脚本用了 .NET Core 专有 API：`build/test-install.ps1:43-44`（0.17.2 版本）调用 `[IO.Path]::TrimEndingDirectorySeparator`，Windows PowerShell 5.1 的 .NET Framework 没有该方法，检查快捷方式工作目录时会抛 `MethodException`。修复位置 `:12-17` 新增 `Remove-MosaicTrailingSeparator`（保留 `C:\` 卷根），`:43-44` 与新增的位置读取函数统一改用它。
    - 位置无法确认时仍继续维护/升级：初版在维护或升级遇到「未登记位置且默认目录无法确认身份」时只记日志，状态仍是 Maintenance/Upgrade，扫描会落到默认 `INSTALLFOLDER`，可能把错误目录当成安装目录去修复，甚至另装一份。修复位置 `build/installer/actions/MosaicInstallerActions.cpp:718-731`：改为 `MOSAICSTATE=Unknown` 并置 `MOSAICBLOCKED`，原因写入 `MOSAICSTATEERROR`，由启动条件阻止继续。
- 改动位置：
  - 原生动作 DLL（新增）：`build/installer/actions/MosaicInstallerActions.cpp`（1286 行，C++17，`/MT /LD /utf-8 /DUNICODE`，静态链接 CRT，只依赖 `msi bcrypt ole32 shell32 shlwapi version user32 advapi32`，不依赖安装目录中的程序或 .NET）。函数与行号：工具函数 `GetProperty:40`、`SetProperty:56`、`LogInfo:61`、`Trim:68`、`ToLower:79`、`TrimEndingSeparators:85`、`Combine:93`、`IsPathEqual:104`、`IsPathInside:109`、`ParseVersion:120`、`CompareVersions:145`；位置基础 `GetLocalAppData:172`、`DefaultInstallFolder:182`、`DataRootFolder:190`；清单读取 `ReadBinaryStream:198`、`Utf8ToWide:244`、`TryReadFileHash:256`、`SplitTabs:370`、`IsSafeRelativePath:388`、`LoadManifest:410`；身份与位置 `GetProductInfo:502`、`ExecutableIdentityMatches:517`、`ResolveInstalledLocation:545`、`DetectState:581`、`DetectInstallState:664`；扫描 `ScanInstallRoot:828`、`PublishScanProperties:920`、`ScanPayload:972`、`VerifyRepairedPayload:999`；路径校验与界面动作 `ExpandEnvironment:1019`、`IsSystemFolder:1028`、`HasReparseAncestor:1060`、`AppendMosaicFolder:1085`、`ValidateInstallPath:1117`、`ValidateInstallFolder:1237`、`EnforceInstallFolder:1244`、`RestoreDefaultInstallFolder:1252`、`BrowseInstallFolder:1260`。
    - 状态检测使用 `MsiEnumRelatedProductsW` + `MsiGetProductInfoW`，按 UpgradeCode 枚举产品，以 ProductCode/版本区分 Fresh、Maintenance、Upgrade、BlockedSameVersion、BlockedNewer、Unknown，并把 `MOSAICSTATE`、`MOSAICSTATEERROR`、`MOSAICBLOCKED`、`MOSAICINSTALLEDPRODUCTCODE/VERSION/LOCATION` 发布给 MSI。
    - 位置解析：优先采用 `ARPINSTALLLOCATION`；为空时仅在确认产品身份（产品名 `Mosaic` 或默认目录中的 `Mosaic.Desktop.exe` 版本/产品名匹配）后回退到旧固定默认目录，并置 `MOSAICLOCATIONLEGACYFALLBACK`；维护与升级把解析结果同时写入 `INSTALLFOLDER` 与 `MOSAICLOCATIONINHERITED`。目录被整体删除时仍然沿用已登记位置，而不是推断为未安装。
    - 扫描：从 MSI Binary `MosaicPayloadManifest`（`MsiDatabaseOpenViewW` + `MsiRecordReadStream`）读取清单，逐行核对相对路径安全性、文件大小与 BCrypt SHA-256；同时检查桌面/开始菜单快捷方式的目标与工作目录。只读：健康时只发布汇总，不写文件；发现缺失/损坏时置 `MOSAICREPAIRNEEDED=1`，并在 `REINSTALL` 为空时设置 `REINSTALL=ALL`、`REINSTALLMODE=amus`，交给 Windows Installer 事务修复；任何读不到或解析失败都记为错误而非健康。
    - 路径校验：拒绝空路径、相对路径、UNC、卷根、系统目录（Windows/System32/Program Files 等）、与 `%LOCALAPPDATA%\Mosaic` 数据目录重叠（追加 `Mosaic` 后再次复核）、含重解析点祖先的路径、超过 240 字符的路径；通过后校验创建目录并发布 `INSTALLFOLDER`。
  - 扫描器构建脚本（新增）：`build/installer/actions/build-actions.ps1`——用 `vswhere.exe` 定位 Visual Studio，加载 `vcvars64.bat` 后执行 `cl /MT /LD /utf-8 /DUNICODE`，产出 `actions/bin/MosaicInstallerActions.dll`；失败即抛错，不静默复用旧 DLL。
  - 安装界面（新增）：`build/installer/Dialogs.wxs`（205 行，`<Fragment><UI Id="MosaicUI">`）。对话框架：`FreshStartDlg:12`、`LocationDlg:25`（路径输入 `MOSAICINSTALLPATH`、浏览、恢复默认；下一步先 `DoAction ValidateInstallFolder`，仅在 `MOSAICPATHERROR = ""` 时进入 `ConfirmDlg:53`）、`ConfirmDlg:53`、`MaintStartDlg:69`（`DoAction ScanPayload` 后进入 `MaintenanceDlg:85`）、`MaintenanceDlg:85`、`UpgradeDlg:103`、`BlockedDlg:117`、`UnknownDlg:126`、`ProgressDlg:135`（取消走 `EndDialog Exit`）、`Exit:144`（显示 `MOSAICSCANSUMMARY`/`MOSAICVERIFYMESSAGE`/`MOSAICVERIFYSUMMARY`）、`FatalError:155`（`ErrorDialog="yes"`，首个可聚焦控件 `ErrorText`，7 个标准错误按钮）、`UserExit:182`、`FilesInUse:190`（`ListBox Property="FileInUseProcess"`）。字体条目为 `MosaicFont`/`MosaicFontBold`/`MosaicFontSmall`。
  - 包定义：`build/installer/Package.wxs`——`:2` 保持 per-user、`UpgradeCode {4D2B073C-72D5-4C3A-B845-65FC2C52D23C}` 与 `MajorUpgrade Schedule="afterInstallInitialize"`；`:7` `ErrorDialog`；`:8` `MOSAICLOCATIONMISSING`；`:9` `DefaultUIFont`；`:10` `UIRef MosaicUI`；`:12-13` 身份冲突启动条件（显示 `[MOSAICSTATEERROR]`）；`:29-31` 新增 `InstallPathMetadata` 组件（HKCU `Software\Mosaic\Installer` 的 `InstallLocation`，GUID `{3B8E5C41-6F2A-4D77-9C10-8A1B4E2D6F59}`）；`:40-41` `MosaicActions`/`MosaicPayloadManifest` 两个 Binary；`:42-49` 七个 Type 1 自定义动作与 `SetArpInstallLocation`（Type 51）；`:50-60` 执行序列（DetectInstallState 26、EnforceInstallFolder 950、ScanPayload 990、SetArpInstallLocation `After CostFinalize`、VerifyRepairedPayload 6700）；`:61-74` UI 序列（DetectInstallState 30、Show Fresh 1001/Maint 1002/Upgrade 1003/Blocked 1004/Unknown 1005、ProgressDlg 1290、UI 侧复检 1301、Exit 1303、FatalError/UserExit）；`:75-79` AdminUISequence（Exit 1301、FatalError、UserExit）。既有 `StartMenu`、`DesktopShortcut`、`StartupCleanup` 组件 GUID 与 KeyPath 未改。
  - WiX 工程：`build/installer/Mosaic.Installer.wixproj:4`（默认 `ProductVersion` 0.17.3）、`:6-10` `ActionsDll`/`ManifestFile`/`DefineConstants`、`:11-21` 保留既有 ICE 抑制并写明 ICE17/ICE20 由 `build/test-package-maintenance.ps1` 逐项核对、`:23` 只编译外部 `Payload.wxs`。
  - 清单生成（新增）：`scripts/generate_installer_manifest.py`——写 `mosaic-manifest-v1` 头，文件行 `file\t<相对路径>\t<大小>\t<SHA256>\t<组件ID>\t<文件ID>`，快捷方式行 `shortcut\tdesktop|programmenu\t<link>\t<target>`；文件/组件 id 为 `SHA256(lower(相对路径))[:24]` 加大写，与两条打包链的 `Payload.wxs` 规则一致。
  - 打包链：`scripts/package_payload.py:138-150` 在 `Payload.wxs` 之后依次调用 `build-actions.ps1`、`generate_installer_manifest.py`，并把 `ProductVersion/PayloadFile/ActionsDll/ManifestFile/OutputPath` 传给 `dotnet build`；`build/package.ps1:44-45`（PowerShell 5.1 分支）与 `:108-109`（常规分支）在快捷方式检查之后新增 `test-package-maintenance.ps1`，版本号取自 `Directory.Build.props`。
  - 静态包校验（新增）：`build/test-package-maintenance.ps1`（以 DTF `WixToolset.Dtf.WindowsInstaller.dll` 只读打开 MSI）。断言 ProductVersion/UpgradeCode/ErrorDialog/DefaultUIFont；七个 Type 1 动作来源 `MosaicActions` 且入口同名；`SetArpInstallLocation` 为 Type 51 写 `[INSTALLFOLDER]`；两个 Binary 存在（只查名称列，避免提前读流导致后续 `GetStream` 为空）；执行序列 26/950/990/6700 与 `After CostFinalize` 条件；UI/AdminUI 序列 13 项；FatalError 的 `0x10000` 标志与 `ErrorText` 首焦点、FilesInUse 的 `FileList`、Exit 的 `Finish`、7 个标准错误按钮；LocationDlg 的 PathEdit/Validate/ConfirmDlg 与 MaintStartDlg 的 ScanPayload；LaunchCondition `NOT MOSAICBLOCKED`；`InstallPathMetadata` 注册表值；从 Binary 取出清单并与 `File` 表逐项核对大小与组件、文件数一致；两条快捷方式目标 `[INSTALLFOLDER]Mosaic.Desktop.exe`；单个 cabinet 覆盖全部文件；可选地用 `expand.exe` 解包并按清单 SHA-256 逐项复算（默认执行）。
  - 安装脚本：`build/test-install.ps1:9-24` 新增 `Get-MosaicArpEntry`、`Remove-MosaicTrailingSeparator` 与 `Get-MosaicInstallLocation`，按「应用和功能 → HKCU 元数据 → 旧固定默认」的顺序取实际位置；`:73-96` 安装后与升级后都重新读取位置，并断言升级后位置不变（`:85`），快捷方式、Smoke、卸载检查全部改用该位置，PASS 文案加入 `location-preserving upgrade`。
  - 版本与发布元数据：`Directory.Build.props:12`（0.17.3）、`build/release-content.json`（第 36 条 `mosaic-0.17.3`，`previousReleaseId=mosaic-0.17.2`，指纹 `93461814D95D5A800B173A298CC634FE6021FA4B5ABADFE8AFCAFFF564694052`）、`build/published-release-index.json`（第 36 条）、`build/release-manifest.json`、`docs/releases/v0.17.3.md`、`README.md:3`（版本摘要）、`README.md` 当前版本说明与下载链接、本文件、`docs/installer-location-plan.md` §10 状态。
  - 计划与实际的命名差异（按 §10.7 要求记录）：计划中的 `Mosaic.Installer.Actions/` 实际落在 `build/installer/actions/`；`Localization/zh-CN.wxl` 未新增，中文字面量直接写在 `Dialogs.wxs` 与 C++ 动作中；`packages.lock.json` 未改（未引入 WiX UI 扩展，界面自写）。
- 迁移逻辑与验收项：
  - 对已有安装（0.17.2 及更早）：UpgradeCode、per-user 范围、既有组件 GUID/KeyPath 均未变，升级仍由 `MajorUpgrade afterInstallInitialize` 完成。升级包在 `DetectInstallState` 中枚举同 UpgradeCode 的旧产品，确认身份后把旧固定默认目录 `%LOCALAPPDATA%\Programs\Mosaic` 作为 `INSTALLFOLDER` 原位升级，并置 `MOSAICLOCATIONINHERITED`/`MOSAICLOCATIONLEGACYFALLBACK`；新版本首次在事务中写入 `ARPINSTALLLOCATION` 与 HKCU `Software\Mosaic\Installer\InstallLocation`。此后同版本双击进入维护时读取这两个登记，不再依赖固定目录。验收（静态）：`test-package-maintenance.ps1` 对 `SetArpInstallLocation` 条件、`InstallPathMetadata` 与启动条件逐项断言；实机项见下。
  - 对首次安装：位置页默认 `%LOCALAPPDATA%\Programs\Mosaic`（`DetectInstallState` 预设 `MOSAICINSTALLPATH`），浏览/恢复默认/手输三条路径都经 `ValidateInstallPath`，追加 `Mosaic` 后复核数据目录隔离，校验通过才写 `INSTALLFOLDER`；静默安装由执行序列 950 的 `EnforceInstallFolder` 执行同一校验，无效路径直接失败，不静默退回默认。
  - 对卸载：`DetectInstallState`/`ScanPayload`/`VerifyRepairedPayload` 均带 `NOT REMOVE`，`/x` 与 MajorUpgrade 移除旧产品都不会进入扫描或修复；`StartupCleanup` 的 `NOT WIX_UPGRADE_DETECTED` 语义未变。
  - 对用户数据：扫描只读，不写 `%LOCALAPPDATA%\Mosaic`；修复只通过 Windows Installer 组件事务替换产品载荷与快捷方式；路径校验禁止安装目录与数据目录重叠。验收：静态测试的路径/清单断言 + 用户侧实机数据保留项。
- 验证：`build/package.ps1` 完整执行（publish → verify-published → release content → Payload.wxs → 原生 DLL → Manifest → WiX → MSI → 快捷方式检查 → 维护静态检查），退出码 0，WiX 构建 0 警告 0 错误；`test-package-shortcuts.ps1` PASS；`test-package-maintenance.ps1` 对 470 个 cabinet 成员逐个复算 SHA-256 后 PASS。首轮完整构建后补上了「位置无法确认即阻断」的判断，再用 `build/package.ps1 -NoPublish` 重编并复跑静态检查，同样 PASS。最终产物 `artifacts/installer/Mosaic-0.17.3-win-x64.msi`：52,490,813 字节，SHA-256 `5ca876b8ce210b95dba253a259aab5556b1398c4c49cd6754d147babb4e09241`。`build/test.ps1 -SkipDesktop`（跳过桌面冒烟项目）15 个测试项目 369 项全部通过，0 失败。
- 发布核验（0.17.3）：提交 `ba54e76`（`main`）；tag `v0.17.3` 指向 `ba54e76`；Release <https://github.com/soehi/Mosaic/releases/tag/v0.17.3>，资产 `Mosaic-0.17.3-win-x64.msi` 52,490,813 字节，GitHub digest `sha256:5ca876b8ce210b95dba253a259aab5556b1398c4c49cd6754d147babb4e09241` 与本地一致。
- 未验证项：本机没有隔离 Windows 用户或可回滚 VM，未执行真实安装、同版本维护、删除文件/整目录、同大小篡改、快捷方式改坏、文件锁定、取消/回滚/3010、0.17.2 实机升级、静默路径失败、显式 `/f`、`/x` 与降级分流的实机矩阵；UI 文案与窄窗口/DPI 表现、FilesInUse 的实际触发、BlockedDlg 的可见性（启动条件在序列 100 先失败）也未实测。

## 0.17.2：设置页排版收口、维护页重排与服务器地址入口隐藏（2026-11-20）

- 根因位置：
  - 重复标题：`src/Modules/Settings/Mosaic.Settings.Wpf/Views/SettingsView.xaml:167`（0.17.1 版本，容器分区表头）与 `src/Modules/Maintenance/Mosaic.Maintenance.Wpf/Views/MaintenanceView.xaml:43`（0.17.1 版本，页面自带标题）同时渲染分区名，进入「维护」时连续出现两个「维护」。
  - 内容贴边与固定宽度：`SettingsView.xaml:275`（0.17.1 版本）的模块页 `ContentControl` 未声明内容伸展契约，且 `MaintenanceView.xaml:42`（0.17.1 版本）根 `StackPanel` 为 `HorizontalAlignment="Left"`、`MaxWidth="820"`、左外边距为零——模块页拿不到真实可用宽高，卡片贴容器边缘，大窗口右侧留白。
  - 「0 项设置」：`SettingsView.xaml:169`（0.17.1 版本）表头计数无条件绑定 `VisibleCountLabel`，值来自 `SettingsViewModel.cs:412`（0.17.1 版本）的可见行计数；模块贡献页没有设置行，计数为 0，但该计数对操作页没有意义。
  - 窄窗口可达性：`MaintenanceView.xaml:54`、`:70`、`:89`、`:92`（0.17.1 版本）用两列 `Grid` 放路径输入框与「选择文件夹」，修复/卸载按钮用水平 `StackPanel`，卸载复选框直接用字符串 `Content`——窄窗口下输入框被挤压、复选框文字不换行。
  - 服务器地址重复入口：`src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthSettingsSource.cs:18`（0.17.1 版本）把 `auth.serverUrl` 声明为普通（`StorageSystem` 分组、`Normal` 分类）定义，通用编辑器按定义逐条生成输入框，开始页之外多出第二个服务器地址编辑入口。
- 改动位置：
  - 契约：`src/Modules/Settings/Mosaic.Settings.Contracts/SettingsContracts.cs:30-36` `SettingDefinition` 末尾新增 `bool ShowInSettings = true` 及注释——只影响通用编辑器是否建行，注册、读取与写入不变。
  - 隐藏定义：`src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthSettingsSource.cs:18-24` `auth.serverUrl` 定义追加 `ShowInSettings: false`；`auth.accountChoiceMade` 未动。
  - 视图模型：`src/Modules/Settings/Mosaic.Settings.Wpf/Internal/SettingsViewModel.cs:480-481` 新增 `Editable()`（`definition.ShowInSettings` 过滤）；`:490`（模块页初始化分支）与 `:510`（通用初始化）改为 `foreach (var definition in Editable(definitions.Value))`——隐藏定义不进入 `_rows`，因此不参与 `RefreshSections` 的分区计数、`VisibleRows`、搜索与恢复默认（`Changes()` 与 `ResetAllCommand` 只遍历 `_rows`）。
  - 容器视图：`SettingsView.xaml:104-107`（标题与总说明）、`:109-115`（高级设置栏）、`:128-136`（恢复默认区）、`:172-174`（表头计数）、`:179-183`（通用行 `ScrollViewer`）统一绑定 `HasSettingsSectionContent`；`:279-282` 模块页 `ContentControl` 增加 `HorizontalContentAlignment="Stretch" VerticalContentAlignment="Stretch"`。`HasSettingsSectionContent` 仍为 `SettingsViewModel.cs:374` 的 `SelectedPage is null`，既有语义未改。
  - 维护页：`MaintenanceView.xaml:48-50` 根 `ScrollViewer` + 内边距 `20,20,20,24` + 单行副标题，删除页面自带的「维护」标题；`:52-102` 存储位置卡（当前数据库/大小两列、路径 `Grid` 与 `BrowseButton`、迁移 `WrapPanel`、说明）；`:104-127` 应用维护卡（标题 `:106`、安装摘要 `:107`、数据目录 `:108-109`、修复按钮 `:110-112`、分隔线 `:113`、卸载标题 `:114`、可换行复选框 `:115-119`、卸载按钮 `:120-123`、说明 `:124`）；`Card` 样式 `:10-17` 保留 16 DIP 卡片间距与 22 DIP 内边距，新增 `SectionDivider` 样式 `:42-46`。
  - 窄窗口：`src/Modules/Maintenance/Mosaic.Maintenance.Wpf/Views/MaintenanceView.xaml.cs:8-16` 新增 `IsCompact` 依赖属性，`:21-22` 构造函数按 620 DIP 阈值随 `SizeChanged` 更新，`:25-33` `ApplyCompactLayout` 把 `BrowseButton` 在 `Grid.Row`/`Grid.Column` 的 `0,1` 与 `1,0` 间切换并调整对齐与边距。
  - 本地化：`src/Modules/Maintenance/Mosaic.Maintenance.Wpf/Localization/zh.xaml:4`（`Maintenance.Subtitle`）、`:13`（`Maintenance.App.Title`=应用维护）、`:16`（`Maintenance.App.Repair`=检查并修复）、`:17`（`Maintenance.App.UninstallTitle`=卸载 Mosaic）、`:18`（`Maintenance.App.Uninstall`=卸载）；`en.xaml` 同位置为英文对应文案；删除不再使用的 `Maintenance.Title`。
  - 测试：`tests/Mosaic.Wpf.Tests/PresentationTests.cs:1145` 新增 `Definitions_hidden_from_the_editor_never_enter_rows_counts_or_resets`（隐藏定义仍注册、不建行、高级模式不出现、恢复默认后保存不含该键）；`tests/Mosaic.Auth.Core.Tests/AuthServiceTests.cs:122` 新增 `Server_address_is_hidden_from_the_generic_editor_but_still_saved_by_auth`（`ShowInSettings=false` 且 `SetServerAsync` 仍提交 `auth.serverUrl`）；`tests/Mosaic.Wpf.Tests/MaintenanceViewModelTests.cs:174-190` 新增 `Browse_button_moves_below_the_path_when_the_view_is_narrow`，`:193` 抽出 `ThemedView()` 夹具（构造视图、应用深色主题）。
  - 发布元数据：`Directory.Build.props:12`、`build/installer/Mosaic.Installer.wixproj:4`（`ProductVersion`）、`build/release-content.json`、`build/release-manifest.json`（`currentReleaseId=mosaic-0.17.2`，第 35 条，`previousReleaseId=mosaic-0.17.1`，指纹 `BA73BD633209CF7EA43F15993A180B552BE4B88A499B406E56ADCA76C00CF90E`）、`build/published-release-index.json`（第 35 条）、`docs/releases/v0.17.2.md`、`README.md`。
  - XAML 编译插曲（如实记录）：编辑 `MaintenanceView.xaml` 的修复按钮块时，XAML 编译器一度报 `MC3050 无法找到类型 theme.MosaicTheme`，且始终指向该按钮的 `Style`/`AutomationProperties` 属性行；`xmlns:theme` 声明与所用键（`QuietButtonKey`/`DangerButtonKey`）确实存在（`src/Mosaic.Presentation.Wpf/MosaicTheme.cs`），清理 `obj`/`bin` 重建后仍复现。把该按钮块按行重写（每行一个属性、统一缩进）后消失。未改主题键、未改 `xmlns`、未改程序集引用，因此判定为编辑器改写留下的 XAML 编译态问题，不是产品缺陷；最终按钮改为默认按钮样式并去掉单子元素 `WrapPanel`，编译与截图均正常。
- 迁移逻辑与验收项：本次不新增配置迁移，`KnownMigrations` 与 `appsettings.json` 的 `appliedMigrations` 均未变，不改数据库结构，不改安装目录内容。对已有安装的影响仅在展示层：
  - `auth.serverUrl` 的定义仍在模块启动时注册、值仍从用户设置库读取，只是不再生成编辑行；分区重置与「恢复全部默认」只重置 `_rows` 中的键，不会清空服务器地址，账户流程（开始页登录、`SetServerAsync`）继续读写同一个键。验收：`Server_address_is_hidden_from_the_generic_editor_but_still_saved_by_auth`、`Definitions_hidden_from_the_editor_never_enter_rows_counts_or_resets`。
  - 维护页的数据库迁移、配置预检与修复、卸载命令及确认规则未改，只改布局与文案；验收：`Browse_button_moves_below_the_path_when_the_view_is_narrow` 与 `Mosaic.Maintenance.Tests` 既有 16 项（含预检四组合、可执行文件路径、调度脚本）。
  - 未在真实用户数据目录（`%LOCALAPPDATA%\Mosaic`）上执行任何写操作；截图与测试均使用临时数据根和假实现。
- 验证：`dotnet build Mosaic.sln -c Release` 0 警告 0 错误；`build/test.ps1` 全绿 423 项：Architecture 4、Auth.Core 26、Auth.Http 36、Desktop Smoke 54、DesktopPresence 17、FloatingWindows 12、Kernel 7、Maintenance 16、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 5、Storage.Sqlite 22、Sync.Core 14、Tasks.Contract 7、Tasks.Core 27、Wpf 134（本次新增 Wpf 2 项、Auth.Core 1 项）。离屏截图核对（临时目录 `mosaic-ui-snapshot`，1160×720、1100×720、1600×900，深色主题）：容器只显示一个「维护」标题、副标题在卡片上方、两张卡片随宽度填满、无「0 项设置」、卸载区标题/复选框/危险按钮层次正确、窗口变窄时「选择文件夹」换行；截图由临时渲染器 `UiSnapshotScratch` 生成，核对后已删除，未入库。`scripts/release_content_verify.py` PASS（35 条索引，0.17.2 指纹 `BA73BD633209CF7EA43F15993A180B552BE4B88A499B406E56ADCA76C00CF90E`）。
- 未验证项：真实安装上的视觉验收（不同 DPI 与字体缩放、多显示器）、浅色主题截图、620 DIP 以下的实机拖动体验；这些需要用户侧实测。
- 发布核验：`build/publish.ps1 -UseV2Feed` 重新发布到 `artifacts/local-run`（0.17.2），`build/package.ps1 -NoPublish` PASS（`published payload is a release build with manifest mosaic-0.17.2`、`Release content verified: 0.17.2, mosaic-0.17.2, 35 indexed releases`、`MSI contains the Application-owned DesktopFolder Mosaic shortcut, INSTALLFOLDER working directory, and StartupCleanup registry removals`），`build/test-package-shortcuts.ps1 -PackagePath artifacts/installer/0.17.2/Mosaic-0.17.2-win-x64.msi` PASS。安装包 `artifacts/installer/Mosaic-0.17.2-win-x64.msi`（52,154,812 字节，SHA-256 `788F9711627CCF6B8453437F7EF91BAC5F14C35DC3756430613A90D2267C89EC`）。发布后用新载荷复跑 `build/test.ps1` 仍为 423 项全绿（Desktop Smoke 54/54）。

## 0.17.1：修复 0.17.0 启动失败、修复安装不重启，修复安装前加只读配置预检（2026-11-20）

- 根因位置：
  - 启动必失败：`src/Mosaic.Desktop/Bootstrapper.cs:77-82`（0.17.0 版本）在 `await _runtime.StartAsync(...)` **之前**插入了 `await _runtime.DisposeAsync()`、两次 `ModuleStarted`/`ModuleStopping` 解绑与一段「运行时单次使用」的误导性注释。`Mosaic.Kernel/ModuleRuntime.cs:28` 在 `_started.Count > 0 || _stopped` 时抛 `InvalidOperationException("Runtime is single use.")`，而 `DisposeAsync() => StopAsync()` 会把 `_stopped` 置真，于是每一次启动都在模块装配前抛出，进程以「Mosaic · 无法启动」诊断窗结束。0.17.0 的 `StartupStorageRelocationTests` 只覆盖 `StartupStorageRelocation.RunAsync`，未经过 `Bootstrapper.StartAsync`，所以没有拦住该回归；peer 侧观察到的「Desktop Smoke 27/50 环境性失败」实际是该崩溃的连带结果。
  - 修复安装后不重启：`src/Modules/Maintenance/Mosaic.Maintenance.Windows/WindowsMaintenanceService.cs:19`（0.17.0 版本）默认 `ExecutablePath = Path.Combine(AppContext.BaseDirectory, "Mosaic.exe")`，而发布产物与安装目录里的可执行文件名是 `Mosaic.Desktop.exe`。`PrepareRepairAsync` 里 `File.Exists(ExecutablePath) ? ExecutablePath : null` 因此恒为 `null`，生成的 PS 助手脚本拿不到窗口进程句柄，无法在 msiexec 结束后拉起新进程。
  - 修复安装无条件执行：`MaintenanceViewModel.RepairAsync`（0.17.0 版本）直接确认后调用 `PrepareRepairAsync`，即使配置完全健康也会退出应用并跑一遍 `msiexec /famus`，与「同版本再点一次 MSI 应该先检查、正常则什么都不做」的期望不符。
- 改动位置：
  - 启动链：`src/Mosaic.Desktop/Bootstrapper.cs:77-108` 删除模块启动前的 `DisposeAsync()` 与事件解绑，恢复 `try { StartAsync → Presence/Floating/ReleaseSummary/Shell setting → ShellWindow } catch { 清理并记录错误编号 → throw }` 结构；保留 0.17.0 新增的 `IApplicationLifecycle? lifecycle` 参数与 `Lifecycle = lifecycle` 注入。
  - 可执行文件路径：`src/Modules/Maintenance/Mosaic.Maintenance.Windows/WindowsMaintenanceService.cs:19` 改为 `executablePath ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mosaic.Desktop.exe")`；`src/Mosaic.Desktop/DesktopApplicationLifecycle.cs` 兜底文件名同样由 `Mosaic.exe` 改为 `Mosaic.Desktop.exe`。
  - 预检契约：`src/Modules/Maintenance/Mosaic.Maintenance.Contracts/MaintenanceContracts.cs` 新增 `ConfigurationHealthReport(bool IsHealthy, IReadOnlyList<string> Issues)`（含 `Healthy` 单例）与 `IConfigurationHealthCheck.CheckAsync`。
  - 只读检查：`src/Mosaic.Desktop/AppConfiguration.cs:78-138` 新增 `Inspect(AppPaths)`（不写文件、不跑迁移、不补绑定：文件缺失/重复属性/坏 JSON/反序列化失败各返回一条 issue，`RepairProviderBindings` 后 `ValidateCoreConfiguration(validateDependencies: true)` 判定绑定完整，`KnownMigrations` 中未应用项逐条列出，绑定完整时再用 `EffectiveStorage`（`settings.storage.location`/`storage.filename` 覆盖）校验存储位置与文件名），配套 `InspectionOptions`（大小写不敏感 + `UnmappedMemberHandling.Disallow`）与 `EffectiveStorage`。
  - 检查实现：`src/Mosaic.Desktop/DesktopConfigurationHealthCheck.cs`（新增）`internal sealed class DesktopConfigurationHealthCheck(AppPaths) : IConfigurationHealthCheck`，异常包装为不健康报告。
  - 装配：`src/Mosaic.Desktop/BuiltInModuleCatalog.cs` 维护模块末尾追加 `new DesktopConfigurationHealthCheck(context.Paths)`；`Mosaic.Maintenance.Wpf/MaintenanceUiModule.cs` 的 `MaintenanceUiDependencies` 追加第 7 个可空参数 `IConfigurationHealthCheck? HealthCheck = null`；`Internal/MaintenanceViewFactory.cs` 透传。
  - 维护页：`src/Modules/Maintenance/Mosaic.Maintenance.Wpf/Internal/MaintenanceViewModel.cs` 新增 `_healthCheck` 字段与 `CheckConfigurationAsync()`（:274-286，无检查器返回 `null`，异常记诊断后返回 `null`），`RepairAsync`（:215-243）开头先预检：健康则 `StatusMessage = Maintenance.Repair.Healthy` 直接返回，不调用 `PrepareRepairAsync`、不退出；不健康则把 issue 列表拼进确认框，确认后才走原修复流程。`Localization/zh.xaml`/`en.xaml` 新增 `Maintenance.Repair.Healthy`、`Maintenance.Repair.Issues`。
  - 边界与 API：`build/public-api.txt` 登记 `Mosaic.Maintenance.Contracts.ConfigurationHealthReport` 与 `Mosaic.Maintenance.Contracts.IConfigurationHealthCheck`；`Mosaic.Maintenance.Wpf.csproj` 加 `InternalsVisibleTo Mosaic.Maintenance.Tests`；`tests/Mosaic.Maintenance.Tests/Mosaic.Maintenance.Tests.csproj` 加 `UseWPF` 与对 Wpf 项目的引用；`build/test.ps1:14` 由「除 `Mosaic.Wpf.Tests` 外一律 `net10.0`」改为按 csproj 内 `net10.0-windows` 推导（0.17.0 起 `Mosaic.Maintenance.Tests` 是 WPF 套件，旧推导会让脚本指向不存在的路径）。
  - 测试：`tests/Mosaic.Desktop.SmokeTests/BootstrapperStartupTests.cs`（新增）`Fresh_install_starts_and_reaches_the_main_window` 用临时 `--data-root` 启动发布产物，60 秒内必须出现主窗口，进程提前退出或标题含「无法启动」即失败（未执行 `build/publish.ps1` 时跳过）；`tests/Mosaic.Maintenance.Tests/MaintenanceRepairPrecheckTests.cs`（新增 4 项：健康跳过安装程序、不健康列出问题并修复、拒绝确认不修复、检查抛异常回退普通流程）；`tests/Mosaic.Maintenance.Tests/WindowsMaintenanceServiceTests.cs` 新增 `Default_executable_path_is_the_running_process_not_a_missing_Mosaic_exe`；`tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs` 新增 3 项 `Inspect` 测试（健康不写文件、启动期无法修复的绑定被报出、缺文件/待迁移/坏 JSON 判定）并把 `Configuration_presence_migration_...` 的迁移数断言由硬编码 4 改为与种子 `appliedMigrations` 相等（0.17.0 起为 5）。
  - 发布元数据：`Directory.Build.props`（0.17.1）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.17.1）、`build/release-content.json` 与 `build/published-release-index.json`（第 34 条，`previousReleaseId=mosaic-0.17.0`）、`build/release-manifest.json`、`docs/releases/v0.17.1.md`、`README.md`。
  - 打包脚本：`build/package.ps1:2-11` 的 `$ProductVersion` 默认值原为硬编码 `'0.12.2'`，打包时必须手工传参，漏传会把 0.17.1 的载荷标成旧版本号并被 `verify-published.ps1` 拒绝；改为默认读取 `Directory.Build.props` 的 `Version`。
- 迁移逻辑与验收项：本次不新增配置迁移，`KnownMigrations` 与 `appsettings.json` 的 `appliedMigrations` 均未变（仍为 5 项），0.17.0 → 0.17.1 升级不触碰 `Config/appsettings.json`、`Data/mosaic.sqlite` 与安装目录以外的任何内容。只读预检对已有安装的验收语义是「`Load` 会改写的项即不健康项」：待应用迁移、绑定缺失/无效、坏 JSON、存储位置非法都会列出；引用 `Inspect` 的三项测试同时断言 `appsettings.json` 字节与 `.pre-migration` 辅助文件均未产生，确保预检绝不改写用户配置。修复安装的验收项为：可执行文件路径等于当前进程（`WindowsMaintenanceServiceTests`），以及预检结果与是否调用 `PrepareRepairAsync`/`RequestExitAsync` 的四种组合（`MaintenanceRepairPrecheckTests`）。
- 验证：`dotnet build Mosaic.sln -c Release --no-restore` 0 警告 0 错误；测试全绿：Architecture 4、Auth.Core 25、Auth.Http 36、DesktopPresence 17、FloatingWindows 12、Kernel 7、Maintenance 16、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 5、Storage.Sqlite 22、Sync.Core 14、Tasks.Contract 7、Tasks.Core 27、Wpf 132、Desktop Smoke 54/54。负向验证：用 `git show b876995:src/Mosaic.Desktop/Bootstrapper.cs` 覆盖后发布到 `artifacts/broken-run`，`BootstrapperStartupTests` 报 `Startup ended in the diagnostic window: Mosaic · 无法启动`，确认新回归测试能拦住 0.17.0 的缺陷，随后恢复修复版并重跑全绿。发布核验 `scripts/release_content_verify.py` PASS（34 条索引，0.17.1 指纹 `E336EB205298F8C66DFFC7B8B9621D5ED1B5520C56B92821A77A2913C3BBF8D8`）、`build/verify-published.ps1` PASS（release build + manifest `mosaic-0.17.1`）、`build/test-package-shortcuts.ps1` PASS；安装包 `artifacts/installer/Mosaic-0.17.1-win-x64.msi`（52,130,236 字节，SHA-256 `30944C41B7F5F9C7183074AE19B2C1650AFB9702C031823AEAE1575BCDDEA0B1`）。发布产物上复跑 `BootstrapperStartupTests` 1/1 通过（主窗口在 1 秒内出现）。`dotnet format Mosaic.sln --verify-no-changes` 在本仓库不是既有门槛：改动前就有 34 个文件存在空白/编码偏差（多为 `FloatingWindows`、`Tasks` 模块的紧凑写法），本次新增代码未落在报告行内，未做全局重排。
- 未验证项：0.17.1 MSI 的应用内卸载/修复安装端到端实机验收仍需用户实测（含修复完成后自动重启）；0.17.0 真实安装升级到 0.17.1 的启动验证同属实机项。

## 0.17.0：设置内卸载/修复本软件 + 简化存储位置迁移（含真实数据迁移）（2026-11-20）

- 根因位置：
  - 迁移（旧「存储路径」文本设置）：`src/Mosaic.Desktop/HostSettingsCatalog.cs:25`（旧）暴露可自由编辑的 `storage.location` 文本设置，提交后下次启动 `Bootstrapper`（旧 `src/Mosaic.Desktop/Bootstrapper.cs:36-41`）直接按新路径打开数据库；`AppConfiguration.cs:415-418` 仅做路径拼接，数据库文件不存在时 `SqliteModule`/`DatabaseWorker` 在新位置静默创建空库，旧数据被留在原目录。根因不是路径拼接，而是允许不经校验地在运行期改写数据库位置。
  - 运行中迁移不可行：`src/Modules/Storage/Mosaic.Storage.Sqlite/Internal/DatabaseWorker.cs` 单条工作线程持有整个生命周期内唯一连接，无关闭/重开 API，故数据库迁移只能在 `SqliteModule` 打开文件之前执行。
  - 运行中卸载不可行：进程不能删除自身可执行文件；`src/Mosaic.Desktop/ShutdownCoordinator.cs`（既有）只负责正常退出与确认，没有「退出后执行」的编排。
- 改动位置：
  - 迁移契约：`src/Modules/Storage/Mosaic.Storage.Contracts/StorageRelocationContracts.cs`（新增 `IStorageRelocation`、`StorageLocationInfo`、`StorageRelocationRequest/Plan/Result`、`StorageRelocationFiles`）。
  - 迁移实现：`src/Modules/Storage/Mosaic.Storage.Sqlite/StorageRelocation.cs`（新增 `SqliteStorageRelocation`：计划校验绝对路径/拒绝相同目录/拒绝目标已有库/预留 16MB 空间余量；`ExecuteAsync` 在启动阶段做 WAL `TRUNCATE` 检查点、`BACKUP` 复制、`PRAGMA integrity_check` 校验，失败回滚并保留源库；请求/结果文件 `storage-relocation[-result].json` 读写）；`src/Modules/Storage/Mosaic.Storage.Sqlite/SqliteModule.cs:67-79`（manifest Provides 增加 `storage.relocation@1`）。
  - 启动期迁移：`src/Mosaic.Desktop/StartupStorageRelocation.cs`（新增）：`Bootstrapper.cs:35` 在 `HostSettingSource` 之后、模块打开数据库之前执行；崩溃恢复（源已删且目标存在 → 直接持久化新位置）；成功后 `HostSettingSource.PersistAsync` 写 `storage.location`/`storage.filename` 并写结果文件，失败保留源库并记录失败结果。
  - 维护契约与实现：`src/Modules/Maintenance/Mosaic.Maintenance.Contracts/MaintenanceContracts.cs`（新增 `MaintenanceAvailability/Info/UninstallRequest`、`IMaintenanceService`、`IApplicationLifecycle`、`UserDataRootGuard`）；`src/Modules/Maintenance/Mosaic.Maintenance.Windows/WindowsMaintenanceService.cs`（新增：读 `HKCU/HKLM` 卸载注册表判定 Installed/Portable，卸载/修复经 `WindowsProcessScheduler` 生成 PS 助手脚本等待父进程结束后执行 `msiexec /x` 或 `/famus`，可选删除数据目录前用 `UserDataRootGuard` 拒绝驱动器根/用户目录等危险路径）；`src/Modules/Maintenance/Mosaic.Maintenance.Windows/WindowsProcessScheduler.cs`（新增）。
  - 维护页 UI：`src/Modules/Maintenance/Mosaic.Maintenance.Wpf/`（新增 `MaintenanceUiModule`——Provides `app.maintenance@1`、Requires `storage.relocation@1` 与 `settings.api@1`，`Internal/MaintenanceViewModel.cs` 初始化展示当前位置/库大小/安装信息并一次性展示上次迁移结果、`Views/MaintenanceView.xaml` 存储迁移卡片 + 安装维护卡片、两份本地化）。
  - 设置分区：`src/Modules/Settings/Mosaic.Settings.Contracts/SettingsContracts.cs`（`SettingsGroup` 追加 `Maintenance`，仅追加不改既有枚举值）、`src/Modules/Settings/Mosaic.Settings.Wpf/Internal/SettingsViewModel.cs`（`SettingsSection`、分组映射、`SettingsSectionNameConverter`）、`Localization/zh.xaml`/`en.xaml`（`Settings.Section.Maintenance`）。
  - 生命周期：`src/Mosaic.Desktop/DesktopApplicationLifecycle.cs`（新增：经 `ShutdownCoordinator` 请求退出、经新进程 + 等待句柄请求重启）；`src/Mosaic.Desktop/App.xaml.cs`（注入）、`src/Mosaic.Desktop/Bootstrapper.cs`（`StartAsync` 增加 `IApplicationLifecycle?` 参数并传入 `ModuleRuntimeContext`，:35 调用 `StartupStorageRelocation`）。
  - 配置与装配迁移：`src/Mosaic.Desktop/AppConfiguration.cs:288-313`（新增 `ApplyMaintenanceMigration` + `maintenance-v1` 标记：既有安装补 `maintenance-ui` 模块与 `storage.relocation@1`/`app.maintenance@1` 绑定）；`src/Mosaic.Desktop/appsettings.json`（modules/providers/appliedMigrations 同步）；`src/Mosaic.Desktop/HostSettingsCatalog.cs`（移除公开 `storage.location` 文本设置定义）；`src/Mosaic.Desktop/BuiltInModuleCatalog.cs`（登记 maintenance 三项目）。
  - 发布工具修复：`build/package.ps1:14,34`、`build/publish.ps1:14,20` 原来引用 `%TEMP%/claude/83a0b83b7ae46749/`（未入仓）下的 Python 脚本且硬编码版本；改为仓库内 `scripts/release_content_verify.py`（根目录自定位、版本取自 `Directory.Build.props`）与 `scripts/package_payload.py`（同）。
  - 测试：`tests/Mosaic.Storage.Sqlite.Tests/StorageRelocationTests.cs`（新增 11 项：计划接受/拒绝、执行保留与删除源、目标已占用拒绝、请求往返、损坏请求）；`tests/Mosaic.Maintenance.Tests/`（新增工程，11 项：`UserDataRootGuard` 边界、`WindowsMaintenanceService.Info/RefreshAsync` 注册表一致性）；`tests/Mosaic.Wpf.Tests/MaintenanceViewModelTests.cs`（新增 10 项：初始化、便携禁用、一次性结果展示、计划失败、确认后调度并重启、卸载二次确认、退出被拒绝时取消挂起、STA 主题绑定渲染）；`tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs`（`Maintenance_migration_...` 4 场景：0.16.6 配置升级补模块与绑定）；`tests/Mosaic.Desktop.SmokeTests/StartupStorageRelocationTests.cs`（新增 4 项：排队迁移持久化新位置、keep=false 删源、崩溃窗口恢复、失败保留源并报结果）。
  - 边界与 API：`build/project-boundaries.json`（新增 maintenance 三项目并给 `Mosaic.Desktop` 加依赖）、`tests/Mosaic.Architecture.Tests/BoundaryTests.cs`（Windows 项目与白名单）、`build/public-api.txt`（登记全部新公共类型）、`tests/Mosaic.Wpf.Tests/Mosaic.Wpf.Tests.csproj`（加项目引用）、`Mosaic.sln`（挂入新工程）、`tests/Mosaic.Desktop.SmokeTests/packages.lock.json` 等锁文件随依赖更新。
  - 发布元数据：`Directory.Build.props`（0.17.0）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.17.0）、`build/release-content.json` 与 `build/published-release-index.json`（第 33 条，`previousReleaseId=mosaic-0.16.6`）、`build/release-manifest.json`、`docs/releases/v0.17.0.md`。
- 迁移逻辑与验收项：数据库迁移为「维护页确认 → 写请求文件 → 退出重启 → 启动阶段（`DatabaseWorker` 开库前）执行复制与校验 → 持久化新位置 → 维护页展示一次性结果」，默认保留旧库、可选删除，目标目录已有库直接拒绝且不触碰任何文件；崩溃窗口（复制完成但未写配置）下次启动自动恢复为新位置；失败保留源库并记录结果。既有安装升级由 `maintenance-v1` 迁移补齐模块与绑定（4 场景覆盖），与 0.16.3 自愈逻辑互补。`storage.location` 不再是公开可编辑设置，但仍是运行时覆盖键：`HostSettingSource.PersistAsync` 不做定义检查、`GetValue` 优先读运行时覆盖，维护页迁移成功后才写入。
- 验证：`dotnet build Mosaic.sln` Debug/Release 0 警告 0 错误；单元测试全绿（Storage.Sqlite 22、Maintenance 11、Wpf 131、Architecture 4、Auth.Core 25、Auth.Http 36、DesktopPresence 17、FloatingWindows 12、Kernel 7、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 5、Sync.Core 14、Tasks.Contract 7、Tasks.Core 27）；`Maintenance_migration_...` 4/4、新增 `StartupStorageRelocationTests` 4/4 直接运行通过；发布核验 `scripts/release_content_verify.py` PASS（33 条索引，0.17.0 指纹 `820F365C3E33B3E4A18030DE4BE6CE4A31F510FA9E98AD1CD7896611B2E2DBCE`）、`verify-published` PASS（`mosaic-0.17.0` 正式发布标记）、`test-package-shortcuts` PASS；安装包 `artifacts/installer/Mosaic-0.17.0-win-x64.msi`（52,134,332 字节，SHA-256 `B454DBF8141D6E4ACC1252134035A85CFEFA3DF2FC49BC9007544A086921CF16`）。
- 未验证项：真实 MSI 的应用内卸载/修复（msiexec /x、/famus）与「目标目录拒绝迁移」的端到端实机验收未执行；依赖真实指针/浮动窗口的既有 Smoke 用例在本机为环境性失败（与本次改动无关，0.16.5 起已知）。

## 账户设置与首次选择（工作区试用）

- 根因位置：
  - 账户入口与首启：`src/Mosaic.Shell.Wpf/ShellViewModel.cs`（旧）`InitializeAsync` 依据账户地址跳转独立 `account.home` 页；`src/Modules/Auth/Mosaic.Auth.Wpf/AuthUiModule.cs`（旧）以 `Navigation`/`Status` 贡献账户导航与 `account.status` 状态项；`AccountViewModel.LogoutAsync`（旧）在退出时清空 `auth.accountChoiceMade`。`AuthSettingsSource.Definitions` 未定义该标记，设置提交无法持久化它。
  - 新安装误判：`src/Mosaic.Desktop/App.xaml.cs`（旧）先构造单一实例并以设置 API 读取设置，`AppConfiguration.Load` 在 `Bootstrapper` 判断配置存在性之前就把默认 `appsettings.json` 复制到数据目录，导致全新空目录被分类为 Legacy。
  - Device Flow 轮询：`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs:783`（旧）以 `nextPollAtSeconds = 0` 启动 `PollDeviceAsync`，而 `PollDeviceAsync` 在 `DeviceSecondsLeft > pollAt` 时跳过轮询（`AccountViewModel.cs:805`），首次轮询实际发生在倒计时归零、已进入过期分支之后，浏览器批准永远不会被识别。该缺陷自 0.16.0 引入。
- 改动位置：
  - 设置页扩展点：`src/Mosaic.Presentation.Contracts/PresentationContracts.cs`（`SettingsPageContribution`、`ISettingsPageModule`）、`src/Mosaic.Presentation.Contracts/SettingsPageRegistry.cs`（新增）；`src/Mosaic.Desktop/BuiltInModuleCatalog.cs`、`src/Mosaic.Desktop/Bootstrapper.cs` 传递注册表与 capability 绑定。
  - 设置侧承载：`src/Modules/Settings/Mosaic.Settings.Contracts/SettingsContracts.cs`（`SettingsGroup.AccountsAndBackup`）、`Mosaic.Settings.Wpf/Internal/SettingsViewModel.cs`（`SelectPageAsync`、`SelectedPageView`、`HasSettingsSectionContent`）、`SettingsUiModule.cs`、`Internal/SettingsViewFactory.cs`、`Views/SettingsView.xaml`、`Localization/zh.xaml`、`Localization/en.xaml`。
  - 账户侧：`src/Modules/Auth/Mosaic.Auth.Wpf/AuthUiModule.cs`（只注册设置页，不再贡献 Navigation/Vertical 状态项）、`Internal/AuthViewFactory.cs`、`Internal/AccountViewModel.cs`、`Views/AccountView.xaml`、`Views/AccountView.xaml.cs`、`Localization/zh.xaml`、`Localization/en.xaml`；删除不再使用的 `Internal/AccountStatusItem.cs` 及其本地化键。
  - 首次选择：`src/Mosaic.Shell.Wpf/ShellViewModel.cs`（`InitializeAsync` 判断 `auth.accountChoiceMade`、`OnStartPageRequested`、`OnInitialChoiceCompleted`）、`ShellWindow.xaml.cs`；`src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthSettingsSource.cs` 定义 `auth.accountChoiceMade`；`AuthModule.cs` 声明 manifest 依赖与 `InitialAccountChoice`。
  - 安装分类：`src/Modules/Storage/Mosaic.Storage.Contracts/IDatabaseSession.cs`（`IInstallationState`、`InstallationKind`）、`Mosaic.Storage.Sqlite/Internal/DatabaseWorker.cs`（启动时在同一事务写入 `mosaic_startup_metadata('installation-classification-v1')`，重启时 `INSERT OR IGNORE` 保持原分类）、`SqliteModule.cs`（导出 `startup.installation-state@1`）、`src/Mosaic.Contracts/ModuleManifest.cs`（`ICapabilityProvider`）、`src/Mosaic.Kernel/ModuleRuntime.cs`（`Resolve<T>` 与激活前钩子）、`src/Modules/Settings/Mosaic.Settings.Sqlite/Internal/SettingsRepository.cs`（`InitializeAsync` 事务内写 `settings_initializations` 与缺失值）、`src/Modules/Settings/Mosaic.Settings.Sqlite/SettingsSqliteModule.cs`。
  - Device Flow 轮询修正：`AccountViewModel.StartDevicePolling` 改为以 `Math.Max(0, expiresInSeconds)` 作为首个轮询标记（`AccountViewModel.cs:783`），第一个循环即按服务端返回的 `verificationUriComplete` 授权结果轮询。
- 迁移逻辑与验收：无法区分新旧的数据目录一律按 Legacy（用户裁决）；仅当"配置在进程启动前不存在且数据库文件此前不存在"才判为 Fresh（`App.xaml.cs:48` 采集存在性，`DatabaseWorker.cs:47` 判定）；显式 false/true 均保留；分类与 choice 在同一事务内写入，失败回滚并可重试（`SettingsRepositoryTests.Failed_startup_migration_rolls_back_and_retries`）；旧安装若缺少标记会显示一次选择界面，选择后不再出现。设置 → 账户可手动重新打开开始页（`RequestStartPage`）。
- 退出行为：`AccountView.xaml` 账户设置状态只有一个统一退出图标（`AccountLogoutButton`）；`AccountViewModel.LogoutAsync` 在线时先确认、调用既有登出接口撤销会话并清理本机凭据，离线时不发网络请求，两种情况都返回开始页并 `RequestStartPage()`；普通退出不清除 `auth.accountChoiceMade`。
- 浏览器登录：`AccountViewModel.StartDeviceAsync` 在未配置服务器时先用 `AuthValidation.DefaultServerUrl` 配置官方服务器，再启动 Device Flow 并打开服务端返回的 `VerificationUriComplete`；只有"连接其他自建服务器"展开地址输入；"忘记密码"打开 `{serverBase}/device?mode=reset`。注册、验证码、密码与重置全部只在浏览器完成，桌面端不再有这些表单。
- 验证：`dotnet build Mosaic.sln` Debug/Release 均 0 警告 0 错误；`dotnet test Mosaic.sln` Debug 与 Release 均 372/372 通过（Wpf 122、Auth.Http 36、Tasks.Core 27、Auth.Core 25、Settings.Core 25、Presence 17、ReleaseSummary 17、Sync.Core 14、FloatingWindows 12、Storage.Sqlite 12、Kernel 7、Tasks.Contract 7、Settings.Sqlite 5、Architecture 4、Smoke 42）。新增测试覆盖：在线退出（调用一次登出、回到开始页、标记保留、发出 `StartPageRequested`）、离线退出（0 次网络调用）、开始页在设置页内显示、离线/自建服务器/浏览器批准三条路径完成首启选择、Device 批准轮询。
- 输入框视觉修正：`src/Modules/Auth/Mosaic.Auth.Wpf/Views/AccountView.xaml`（工作区）`FieldBox` 原先只设置高度、内边距与外边距，沿用全局 `TextBox` 的浅色默认背景/边框；在深色主题下，「连接其他自建服务器」展开的服务器地址输入框与周围卡片对比突兀。现让 `FieldBox` 使用 `SurfaceMutedBrushKey` 背景、`ControlHoverBorderBrushKey` 边框、`TextPrimaryBrushKey` 前景，并统一垂直居中与等宽字体；`ServerUrlInput` 单独增大字号并补 `AutomationProperties.Name`。改动只影响账户设置页展开的服务器地址输入框，不改变校验、持久化或 Device Flow。
- 验证：`dotnet build src/Modules/Auth/Mosaic.Auth.Wpf/Mosaic.Auth.Wpf.csproj -c Release` 0 警告 0 错误。
- 未验证项：0.16.6 MSI 的安装/升级路径未再实测（前一版 0.16.4 → 工作区旧库升级已单独验证）；Smoke 中依赖真实指针的浮动窗口用例偶发失败，重跑即通过；capability 解析只用单元测试覆盖绑定错误，未接入替代 provider 实测；未使用真实账号、SMTP 或浏览器 Cookie；输入框视觉调整尚未实机截图复核。

### 0.16.6 本地试用包（2026-11-20）

- 版本号：`Directory.Build.props`、`build/installer/Mosaic.Installer.wixproj` 由 0.16.5 提升到 0.16.6。本机注册表中 0.16.8 的 DisplayVersion 与可执行文件 0.16.5 不一致，属上一轮试用残留；安装器 `MajorUpgrade` 会覆盖文件。
- 根因位置：`build/installer/Package.wxs` 的 LaunchCondition 引用 `WINDOWSBUILD`，而属性声明为 `WindowsBuild`，WiX ICE46 大小写校验令打包失败（MSI 属性名不区分大小写，功能上无影响）。改动位置：`Mosaic.Installer.wixproj` 的 `SuppressIces` 增加 ICE46 并注明原因。
- 发布说明：`build/release-content.json`、`build/published-release-index.json` 新增 0.16.6 条目，指纹 `EEEAD1058CDF7B8762BCED82DB042B1D36A3BC064C5D9ED486040BC40306AD5D`；`docs/releases/v0.16.6.md` 由校验脚本生成。
- 打包命令：`build/package.ps1 -ProductVersion 0.16.6`（首次因 ICE46 失败，抑制后 `-NoPublish` 重跑成功）；`build/test-package-shortcuts.ps1` 通过。
- 产物：`artifacts/installer/Mosaic-0.16.6-win-x64.msi`，52,113,780 字节，SHA-256 `533D68E6E857C7F320DEFC7905DA9E10AA90CB4C1CB5C141850EDDAD11819BB4`。
- 内容核验：`msiexec /a` 管理安装提取后 `Mosaic.Desktop.dll`、`Mosaic.Auth.Wpf.dll`、`release-manifest.json` 的 SHA-256 与 `artifacts/local-run` 完全一致，manifest `currentReleaseId = mosaic-0.16.6`（32 条发布记录）。

## 0.16.5：账号入口移入左下角、四选一登录流程、退出登录回到账号选择（2026-11-19）

- 根因位置：
  - 账号入口位置：`src/Modules/Auth/Mosaic.Auth.Wpf/AuthUiModule.cs`（0.16.4 版）`Navigation` 属性注册 `account.navigation`，账号入口占据左侧导航栏。
  - 第二套首启机制：`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs`（616d9eb 版）以 `StartView`/`ShowWelcomeLayout`/`SetThemeCommand` 实现深色两栏首启欢迎布局，与主内容区浅色视觉规范割裂。
  - 左下角无账号状态与退出入口：`src/Mosaic.Shell.Wpf/ShellWindow.xaml`（0.16.4 版）状态面板只渲染右下角横向状态项；`IStatusContribution` 无「侧栏放置」与「动作按钮」概念，模块无法在左下角呈现账号卡片或提供退出登录。
  - 退出登录不清首启选择：`AccountViewModel.LogoutAsync`（0.16.4 版 :658）与 `AccountStatusItem.SignOutAsync` 只撤销服务器会话，不清理 `auth.accountChoiceMade`。
- 改动位置：
  - 契约扩展：`src/Mosaic.Presentation.Contracts/PresentationContracts.cs`（新增 `StatusPlacement { None, Bottom }`；`IStatusContribution` 新增 `SidebarPlacement`/`CanAct`/`ActionGlyph`/`ActionLabel`/`ActionToolTip`/`ActionRequested`/`RequestAction()`，均带默认实现，二进制兼容）；`build/public-api.txt`（登记 `StatusPlacement`）。
  - 账号入口与退出登录：`src/Modules/Auth/Mosaic.Auth.Wpf/AuthUiModule.cs`（`Navigation => []`；`_statusItem` 5 参构造）；`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountStatusItem.cs`（全量重写：`SidebarPlacement=Bottom`、`CanAct`、MDL2 Leave 动作按钮、`SignOutAsync` = 确认→`IAuthApi.LogoutAsync`→清 `auth.accountChoiceMade`→`ActionRequested`，新增 `RequestActionAndWaitForIdleAsync`）。
  - 四选一流程：`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs`（删除 `StartView`/welcome/主题切换；新增 `ShowAccountChoice`；`LogoutAsync` 增加 `ClearAccountChoiceAsync`；`ModeTitle`/`ModeDescription` 改用 `Auth.Choice.*`；`InitializeAsync` 简化）；`src/Modules/Auth/Mosaic.Auth.Wpf/Views/AccountView.xaml`（登录区重排为浏览器登录/邮箱密码/离线/自建服务器四选一，`AutomationId="AccountChoice"`）；`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountConverters.cs`（随回退清理）；`src/Modules/Auth/Mosaic.Auth.Wpf/Localization/zh.xaml`/`en.xaml`（新增 5 个键）。
  - Shell 通用渲染：`src/Mosaic.Shell.Wpf/ShellViewModel.cs`（`NavigateDirectAsync` 回退、首启重定向条件、`SidebarStatusItems` 与 `ActionRequested` 订阅、`StatusActionCommand`、`DisposeAsync` 退订）；`src/Mosaic.Shell.Wpf/ShellWindow.xaml`（左下角账号卡片按 `SidebarStatusItems` 通用渲染）；`src/Mosaic.Shell.Wpf/NavigationConverters.cs`（`StatusActionAvailableConverter`/`StatusActionHiddenConverter`）。
  - 测试：`tests/Mosaic.Wpf.Tests/AccountViewModelTests.cs`（删除 5 个 welcome 测试；新增首启选择态、离线写标记、已登录不显示选择、仅配置服务器不算选择、退出清标记共 5 个；`Status_item_...` 改 5 参构造并覆盖 `SidebarPlacement`/`CanAct`/退出流程）。
  - 发布元数据：`Directory.Build.props`（0.16.5）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.16.5）、`docs/releases/v0.16.5.md`、`docs/releases/v0.16.5-files.md`、`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`（第 31 条，`previousReleaseId=mosaic-0.16.4`）、`README.md`。
- 对已有安装的影响：无数据目录、配置文件、安装升级或模块装配改动，无需迁移逻辑与迁移验收项；首启重定向要求 `auth.serverUrl` 空白，0.16.4 及更早已配置服务器的安装行为不变；退出登录清 `auth.accountChoiceMade` 但保留服务器地址，当次立即回到账号选择，下次启动不强制重定向；`IStatusContribution` 新成员均为默认实现，其他模块无需改动。
- 验证：全量测试 372 项全绿（15 个测试工程：Wpf.Tests 119、SmokeTests 42、Architecture 4、Auth.Core 25、Auth.Http 36、Sync.Core 14、Settings.Core 25、Kernel 5 等），Debug/Release 构建均 0 警告 0 错误；发布核验 `release_content_verify.py` PASS（31 条索引，指纹 `435359A7C8E6037DAA674054E7567DA1DD830791A7A8482074171D738DE8D526`）、`verify-published` PASS、`test-package-shortcuts` PASS。
- 安装包：`artifacts/installer/Mosaic-0.16.5-win-x64.msi`（52,089,204 字节，SHA-256 `73A69D6DA4A000DB425B08A4C6E7A8C81B28F715C14E2FD0DD51F16EE19E77D9`）。
- 已修复的发布前缺陷：`src/Modules/Auth/Mosaic.Auth.Wpf/Localization/zh.xaml:41` 与 `en.xaml:41` 的 `Auth.Logout.Confirm` 重复定义导致 XAML 解析失败（启动弹「无法启动」），已删除重复 key。
- 未验证项：363 项全量测试中 2 项真实指针 Smoke 场景（`Floating_real_pointer_moves_resizes_clicks_and_restores_saved_bounds`、`Floating_header_buttons_move_on_drag_and_still_click_on_short_press`）在打包环境失败，属历史已知环境依赖（见 0.16.2 小节未验证项），与本次改动无关；MSI 实际安装/卸载不在本机执行。

## 0.16.4：默认服务器建议官方实例、右下角账号状态栏、导航图标统一（2026-11-19）

- 根因位置：
  - 默认服务器地址：`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs`（0.16.3 版 :139）构造函数 `_serverInput = ServerText`，未配置服务器时 `ServerText`（:194）为空字符串，全新安装的账户页服务器输入框空白，用户无从得知官方实例地址。
  - 账号显示：`src/Mosaic.Shell.Wpf/ShellWindow.xaml`（0.16.3 版）内容区仅导航行+内容行两行，无状态栏区域，登录状态只能打开账户页查看。
  - 导航图标：`src/Mosaic.Shell.Wpf/NavigationConverters.cs`（0.16.3 版）把图标名映射到风格不一的字形（文本 `✓`、Unicode 杂项符号），`ShellWindow.xaml` 另嵌手绘 `Path` 齿轮，大小、描边、明暗主题对比度不统一。
- 改动位置：
  - 默认服务器地址：`src/Modules/Auth/Mosaic.Auth.Contracts/AuthContracts.cs`（`AuthValidation` 新增 `DefaultServerUrl = "https://mosaic.bansonic.top"`）；`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs`（新增 `DefaultServerInput`（:198），构造函数（:130）与 `OnLanguageChanged`（:1189）改用之）；`src/Modules/Auth/Mosaic.Auth.Wpf/Localization/zh.xaml`/`en.xaml`（服务器面板提示）；`src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthSettingsSource.cs:15`、`src/Modules/Settings/Mosaic.Settings.Wpf/Localization/zh.xaml`/`en.xaml`（设置项描述）。`auth.serverUrl` 默认值仍为空，仅输入框预填建议。
  - 右下角账号状态栏：`src/Mosaic.Presentation.Contracts/PresentationContracts.cs`（新增 `IStatusContribution` 契约与 `IUiModule.Status` 默认成员）与 `build/public-api.txt`；`src/Mosaic.Shell.Wpf/ContributionRegistry.cs`（`Register` 第三参数可选，`Rebuild`/`Revoke`/`Clear` 同步状态集合）；`src/Mosaic.Shell.Wpf/ShellViewModel.cs`（`StatusItems`、`NavigateToViewCommand`）；`src/Mosaic.Shell.Wpf/ShellWindow.xaml`（第三行 36px 状态栏，空时折叠）；`src/Mosaic.Shell.Wpf/ShellWindow.xaml.cs`（紧凑侧栏边距）；`src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountStatusItem.cs`（新增，观察 `IAuthApi` 与本地化）；`src/Modules/Auth/Mosaic.Auth.Wpf/AuthUiModule.cs`（`Status` 属性、激活创建/停止释放）；`src/Modules/Auth/Mosaic.Auth.Wpf/Localization/zh.xaml`/`en.xaml`（状态文案）；`src/Mosaic.Desktop/Bootstrapper.cs`（传入 `ui.Status`）。Shell 泛化渲染、不引用 Auth 契约，保持「Shell 不写 if(auth)」。
  - 导航图标：`src/Mosaic.Shell.Wpf/NavigationConverters.cs`（统一映射 Segoe MDL2 Assets 字形：CheckList/Folder/Calendar/Settings/Contact，回退 Document）；`src/Mosaic.Shell.Wpf/ShellWindow.xaml`（字形统一字体与字号 16、宽 22，删除手绘 Path 与对应 DataTrigger）。
  - 测试：`tests/Mosaic.Wpf.Tests/AccountViewModelTests.cs`（`FakeAuth` 真实事件；新增默认预填、已配置保持、状态项三态共 3 个测试）；`tests/Mosaic.Wpf.Tests/PresentationTests.cs`（导航字形断言更新；新增状态栏泛化渲染与跳转测试、`StatusStub`）。
  - 发布元数据：`Directory.Build.props`（0.16.4）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.16.4）、`docs/releases/v0.16.4.md`、`docs/releases/v0.16.4-files.md`、`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`（第 30 条，`previousReleaseId=mosaic-0.16.3`）、`README.md`。
- 对已有安装的影响：无数据目录、配置文件、安装升级或模块装配改动，无需迁移逻辑与迁移验收项；`auth.serverUrl` 默认值仍为空（只预填输入框、不自动联网），已保存地址的安装行为不变；`IUiModule.Status` 为默认接口成员，其他模块无需改动；无状态贡献时状态栏折叠，既有窗口布局不变；导航契约字段不变，仅渲染层字形变化。
- 验证：Release 全量测试 357 项全绿（exit 0，含 SmokeTests 42 项、Wpf.Tests 114 项：新增 4 个本版本测试，针对 `artifacts/local-run` 0.16.4 构建运行）；发布核验 `release_content_verify.py` PASS（30 条索引，指纹 `30BEE9458037456D3755AE3E0A18DECE7DEBC7FB0C719D1D669331B5DD2E5559`）、`verify-published` PASS、`test-package-shortcuts` PASS。
- 安装包：`artifacts/installer/Mosaic-0.16.4-win-x64.msi`（52,093,300 字节，SHA-256 `984322D09AB1CD58904E080E249FB849CFD895986E5D95D186B733FA7C6191C5`）。

## 0.16.3：安装包自带配置修复——每次启动自动补齐缺失的模块能力绑定（2026-11-19）

- 根因位置：`src/Mosaic.Desktop/AppConfiguration.cs` `Load`——读取 appsettings.json 的 `modules`/`providers` 后未与内置默认绑定合并就交给装配校验。模块升级新增能力时（如 0.16.0 新增 `tasks.transfer-repository@1`），既有安装的旧配置缺这条绑定，除非恰好写了补绑定迁移，否则 `src/Mosaic.Kernel/ModuleGraph.cs` `ModuleGraph.Resolve`（:20-21）启动时抛 `Provider binding is missing or incompatible: …`（0.16.2 的 `ApplyAuthSyncMigration` 只补了那一次的两条，机制上仍依赖「每次新增能力都记得写迁移」）。
- 改动位置：
  - `src/Mosaic.Desktop/AppConfiguration.cs`：`Load` 在首次 `ValidateCoreConfiguration` 前调用新增的 `RepairProviderBindings`；该方法按生效层（`settings.host.modules`/`settings.host.providers` 覆盖优先，否则顶层）读取模块与绑定，对每个已启用且在内置目录（`BuiltInModuleCatalog.Definitions`）中的模块，用其 manifest `Provides` 以 `TryAdd` 补齐缺失绑定——显式绑定保留、未知模块交给既有声明校验、禁用模块不产生孤儿绑定；有覆盖层写回 `settings.host.providers`，无覆盖写回顶层；无缺失不写盘，有补齐走既有 `WriteConfiguration`（含 `.pre-migration` 备份）。MSI Repair/重装不触碰配置目录，修复后首次启动即补齐，WiX 无需改动。
  - `tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs`：新增 `Startup_repairs_capability_bindings_missing_from_an_upgraded_configuration`（top/both/modules/providers 4 场景：全部迁移已应用、缺 transfer 两条与 `sync.api@1`，断言自愈写回正确生效层、`.pre-migration` 等于原文、进程保持运行）；新增 `Healthy_configuration_keeps_its_bindings_and_file_untouched`（健康配置 Load 后文件逐字节不变、无备份文件）；新增 `Repair_preserves_explicit_bindings_and_skips_disabled_modules`（显式绑定保留、缺失补齐、禁用模块无孤儿绑定）；更新 `Configuration_presence_migration_is_validated_persisted_and_one_time` 的 `already-migrated-disabled` 场景为「只发生自愈补齐、迁移标记不变」。
  - `Directory.Build.props`（0.16.3）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.16.3）、`docs/releases/v0.16.3.md`、`docs/releases/v0.16.3-files.md`、`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`（第 29 条，`previousReleaseId=mosaic-0.16.2`）、`README.md`。
- 安装/修复验收项：模拟旧版配置（缺新增绑定）升级启动自动补齐并成功启动（4 场景 Smoke 覆盖，含覆盖层组合）；用户自定义绑定不被覆盖；禁用模块不产生孤儿绑定；MSI Repair/重装后首次启动同样补齐（安装器本就不写配置，启动即自愈）。新模块的启用仍由迁移负责，自愈不会静默启用任何模块。
- 验证：Release 全量测试 354 项全绿（exit 0，含 SmokeTests 42 项：新增 3 个自愈测试 + 4 场景进程级验收 + presence 迁移场景更新，针对 `artifacts/local-run` 0.16.3 构建运行）；发布核验 `release_content_verify.py` PASS（29 条索引，指纹 `ED06CAC492A0BB6D58279F6612136CDF3B9C14D1AE28748C3680B28F5E64C68D`）、`verify-published` PASS。
- 安装包：`artifacts/installer/Mosaic-0.16.3-win-x64.msi`（52,105,588 字节，SHA-256 `42E809211788CEE2F11AC339555FE2AE679A3C2E2BFD80B5B3586B80DFD39553`）。## 0.16.3：安装包自带配置修复——每次启动自动补齐缺失的模块能力绑定（2026-11-19）

- 根因位置：`src/Mosaic.Desktop/AppConfiguration.cs` `Load`——读取 appsettings.json 的 `modules`/`providers` 后未与内置默认绑定合并就交给装配校验。模块升级新增能力时（如 0.16.0 新增 `tasks.transfer-repository@1`），既有安装的旧配置缺这条绑定，除非恰好写了补绑定迁移，否则 `src/Mosaic.Kernel/ModuleGraph.cs` `ModuleGraph.Resolve`（:20-21）启动时抛 `Provider binding is missing or incompatible: …`（0.16.2 的 `ApplyAuthSyncMigration` 只补了那一次的两条，机制上仍依赖「每次新增能力都记得写迁移」）。
- 改动位置：
  - `src/Mosaic.Desktop/AppConfiguration.cs`：`Load` 在首次 `ValidateCoreConfiguration` 前调用新增的 `RepairProviderBindings`；该方法按生效层（`settings.host.modules`/`settings.host.providers` 覆盖优先，否则顶层）读取模块与绑定，对每个已启用且在内置目录（`BuiltInModuleCatalog.Definitions`）中的模块，用其 manifest `Provides` 以 `TryAdd` 补齐缺失绑定——显式绑定保留、未知模块交给既有声明校验、禁用模块不产生孤儿绑定；有覆盖层写回 `settings.host.providers`，无覆盖写回顶层；无缺失不写盘，有补齐走既有 `WriteConfiguration`（含 `.pre-migration` 备份）。MSI Repair/重装不触碰配置目录，修复后首次启动即补齐，WiX 无需改动。
  - `tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs`：新增 `Startup_repairs_capability_bindings_missing_from_an_upgraded_configuration`（top/both/modules/providers 4 场景：全部迁移已应用、缺 transfer 两条与 `sync.api@1`，断言自愈写回正确生效层、`.pre-migration` 等于原文、进程保持运行）；新增 `Healthy_configuration_keeps_its_bindings_and_file_untouched`（健康配置 Load 后文件逐字节不变、无备份文件）；新增 `Repair_preserves_explicit_bindings_and_skips_disabled_modules`（显式绑定保留、缺失补齐、禁用模块无孤儿绑定）；更新 `Configuration_presence_migration_is_validated_persisted_and_one_time` 的 `already-migrated-disabled` 场景为「只发生自愈补齐、迁移标记不变」。
  - `Directory.Build.props`（0.16.3）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.16.3）、`docs/releases/v0.16.3.md`、`docs/releases/v0.16.3-files.md`、`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`（第 29 条，`previousReleaseId=mosaic-0.16.2`）、`README.md`。
- 安装/修复验收项：模拟旧版配置（缺新增绑定）升级启动自动补齐并成功启动（4 场景 Smoke 覆盖，含覆盖层组合）；用户自定义绑定不被覆盖；禁用模块不产生孤儿绑定；MSI Repair/重装后首次启动同样补齐（安装器本就不写配置，启动即自愈）。新模块的启用仍由迁移负责，自愈不会静默启用任何模块。
- 验证：Release 全量测试 354 项全绿（exit 0，含 SmokeTests 42 项：新增 3 个自愈测试 + 4 场景进程级验收 + presence 迁移场景更新，针对 `artifacts/local-run` 0.16.3 构建运行）；发布核验 `release_content_verify.py` PASS（29 条索引，指纹 `ED06CAC492A0BB6D58279F6612136CDF3B9C14D1AE28748C3680B28F5E64C68D`）、`verify-published` PASS。
- 安装包：`artifacts/installer/Mosaic-0.16.3-win-x64.msi`（52,105,588 字节，SHA-256 `42E809211788CEE2F11AC339555FE2AE679A3C2E2BFD80B5B3586B80DFD39553`）。
- 未验证项：见 0.16.2 小节（MSI 实际安装/卸载、依赖真实指针的既有 Smoke 场景）。

## 0.16.2：修复 ≤0.15.3 升级安装无法启动——auth-sync 迁移缺任务传输绑定（2026-11-19）

- 根因位置：`src/Mosaic.Desktop/AppConfiguration.cs` `ApplyAuthSyncMigration`（0.16.0 版 :207-234）——`auth-sync-v1` 迁移为既有安装注册 auth/sync 五条绑定时，遗漏了同一提交（d0ccccd）随任务传输流水线新增的 `tasks.transfer-repository@1`（`src/Modules/Storage/Mosaic.Tasks.Sqlite/TasksSqliteModule.cs:15` Provides）与 `tasks.transfer@1`（`src/Modules/Tasks/Mosaic.Tasks.Core/TasksModule.cs:40` Provides）；仓库内 `src/Mosaic.Desktop/appsettings.json` 顶层 `providers` 表同样缺这两条。`src/Mosaic.Kernel/ModuleGraph.cs` `ModuleGraph.Resolve`（:20-21）要求每个 Provides 能力都有指向该模块的绑定，因此从 ≤0.15.3 升级的既有安装（providers 仅 14 条）启动解析时抛 `Provider binding is missing or incompatible: tasks.transfer-repository@1`，以「模块依赖或能力绑定无效」弹窗退出。0.16.0 即携带该缺陷（全新安装的仓库配置也缺绑定），但 0.16.0/0.16.1 发布时桌面 Smoke 未重跑、实机安装未验证，未暴露。
- 改动位置：
  - `src/Mosaic.Desktop/AppConfiguration.cs` `ApplyAuthSyncMigration`：补 `providers.TryAdd("tasks.transfer-repository@1", "tasks-sqlite")` 与 `providers.TryAdd("tasks.transfer@1", "tasks-core")`（附缺陷成因注释）。
  - `src/Mosaic.Desktop/appsettings.json`：顶层 `providers` 补 `"tasks.transfer-repository@1": "tasks-sqlite"` 与 `"tasks.transfer@1": "tasks-core"`。
  - `tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs`：新增 `Auth_sync_migration_binds_the_task_transfer_capabilities`（top/both/modules/providers 4 场景），以 0.15.3 形状配置启动真实进程，断言迁移写回 transfer 两条与 auth/sync 绑定、`.pre-migration` 保留、迁移后进程保持运行。
  - `Directory.Build.props`（0.16.2）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.16.2）、`docs/releases/v0.16.2.md`、`docs/releases/v0.16.2-files.md`、`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`（第 28 条，`previousReleaseId=mosaic-0.16.1`）、`README.md`。
- 迁移验收项：0.15.3→0.16.2 启动时自动补全绑定并标记 `auth-sync-v1`、写 `.pre-migration` 备份，无需手动改配置；0.16.0/0.16.1 期间从未成功启动的安装走同一迁移路径；已成功启动（或全新安装）的安装迁移标记已存在，配置不变；`host.providers` 覆盖层同步补写。已应用过迁移的安装不受影响（迁移幂等）。
- 验证：实机复现用户故障——以 0.15.3 形状配置（11 模块、14 绑定、无 auth-sync-v1）启动修复后的 `artifacts/local-run` 构建，进程保持运行、迁移写回 `tasks.transfer-repository@1=tasks-sqlite`、`tasks.transfer@1=tasks-core`、`sync.api@1=sync-core`、`auth.transport@1=auth-http`，`.pre-migration` 保留；Release 全量测试 317 项（Auth/Sync/Tasks/Wpf 等）全绿（exit 0），新增 4 场景 Smoke 理论用例随构建编译通过；发布核验 `verify-published` PASS（manifest `mosaic-0.16.2`）、MSI 快捷方式测试 PASS；安装包 `artifacts/installer/Mosaic-0.16.2-win-x64.msi`（52,089,204 字节，SHA-256 `CA864899BF09B715FC8F2657BE775B6975D756F2E3B524857D5334047EA08565`）。
- 未验证项：MSI 的实际安装/卸载不在本机执行（本机存在正在运行的真实安装）；与本缺陷无关的既有 Smoke 场景（presence/summary/floating 迁移等）依赖真实指针，全量 Smoke 按惯例仅选择性运行。

## 0.16.1：对齐服务端 0.1.1 wire——备份白名单收紧、账户管理与 Device Flow 修正（2026-11-19）

- 根因位置：
  - 备份设置白名单过宽：`src/Modules/Sync/Mosaic.Sync.Core/Internal/SyncService.cs`（0.16.0 版 :16-17）的 `PortablePrefixes = ["tasks.", "ui.text."]` 与 `IsPortable`（0.16.0 版 :200-202）的前缀匹配，使 `tasks.revisionStart`、`tasks.floating.allowNotesEditing`、`ui.text.*` 等宿主/机器相关设置进入备份；服务端 0.1.1 对 setting 条目强制 14 键精确白名单，未知键拒绝整个备份并返回 400 `sync.invalid` / `backup.invalid`，即 0.16.0 客户端一旦修改过这些设置就无法成功上传备份。
  - 设备授权轮询：服务端 0.1.1 将 `slow_down` 从 429 改为 200 `{status:"slow_down", interval:2}`；0.16.0 的 `HttpAuthTransport.PollDeviceAuthorizationAsync` 未解析该状态，`AccountViewModel` 的取模轮询会把 slow_down 当作普通 pending 以固定间隔继续轮询，越轮越慢却从不退避。
  - 浏览器打开链接：0.16.0 在服务端下发 `verificationUriComplete` 之前就把裸 `verificationUri`（不含授权码）交给浏览器，用户需要手动输入授权码且输入错误无从校验；0.1.1 明确要求优先使用完整链接。
- 改动位置：
  - 契约 `src/Modules/Auth/Mosaic.Auth.Contracts/AuthContracts.cs`：`DevicePollDto` 新增 `SlowDownIntervalSeconds`；新增 `AuthSessionInfoDto(Id, DeviceLabel, Platform, CreatedAtUtc, LastSeenAtUtc, ExpiresAtUtc, Current)`；`AuthErrorCodes` 新增 `ReauthRequired`、`SessionRevoked`；`AuthEmailCodePurpose` 新增 `DeleteAccount`；`IAuthTransport.StartDeviceAuthorizationAsync` 增加 `appVersion` 参数，新增 `ListSessionsAsync`/`RevokeSessionAsync`/`RevokeAllSessionsAsync`/`DeleteAccountAsync`；`IAuthApi.PollDeviceAuthorizationAsync` 返回类型改为 `OperationResult<DevicePollDto>`，新增同名四个账户管理方法。`src/Modules/Sync/Mosaic.Sync.Contracts/SyncContracts.cs`：`BackupSnapshotDto`、`BackupUploadResult` 新增 `ServerRevision`（long）与 `Cursor`（string?）。`build/public-api.txt` 登记 `AuthSessionInfoDto`。
  - 账户传输 `src/Modules/Auth/Mosaic.Auth.Http/HttpAuthTransport.cs`：`StartDeviceAuthorizationAsync` 请求体补 `platform=windows` 与 `appVersion`，`verificationUriComplete` 优先、缺失时以服务器基址构造 `device?user_code=<userCode>`；`PollDeviceAuthorizationAsync` 识别 200 `status=slow_down` 返回 `Pending` 并携带 `SlowDownIntervalSeconds`（下限 2）；新增 `ListSessionsAsync`（兼容根数组与 `{sessions:[...]}` 两种 JSON 形状，`DateTimeOffset.TryParse` + InvariantCulture + AssumeUniversal）、`RevokeSessionAsync`（`POST v1/auth/sessions/{id}/revoke`）、`RevokeAllSessionsAsync`（`POST v1/auth/sessions/revoke-all`，`keepCurrent=true` 时带 `{keepCurrent:true}` 请求体）、`DeleteAccountAsync`（`POST v1/auth/account/delete`，body `{password, code}`）；`MapCode`/`FallbackMessage` 新增 `auth.reauth_required`、`auth.session_revoked`。
  - 备份传输 `src/Modules/Sync/Mosaic.Sync.Http/HttpSyncTransport.cs`：`TryReadSnapshot` 解析 `serverRevision`（`TryGetInt64`）与 `cursor`。
  - 账户服务 `src/Modules/Auth/Mosaic.Auth.Core/Internal/AuthService.cs`：新增 `AppVersion`（`AssemblyInformationalVersion` 去掉 `+` 后缀，回退程序集版本）；`PollDeviceAuthorizationAsync` 透传 slow_down 间隔；新增 `AccessTokenOrFailureAsync` 与 `ListSessionsAsync`/`RevokeSessionAsync`/`RevokeAllSessionsAsync`/`DeleteAccountAsync`（校验密码/验证码二选一，成功后持锁 `secrets.ClearAsync` 并回到 `SignedOut`）；`Internal/GuardedAuthApi.cs` 转发四个新成员与新的 Poll 返回类型。
  - 备份服务 `src/Modules/Sync/Mosaic.Sync.Core/Internal/SyncService.cs`：删除 `PortablePrefixes`，`IsPortable`（现 :218-219）改为 `SettingPersistence.UserSettings && PortableIds.Contains(definition.Id)`；`PortableIds` 为与服务端一致的 14 个精确键（`ui.language`、`appearance.theme`、`tasks.sortOrder`、`tasks.defaultFilter`、`tasks.archive.autoEnabled`、`tasks.archive.delayDays`、`tasks.title.maxCharacters`、`tasks.notes.maxCharacters`、`tasks.title.required`、`tasks.defaultCompleted`、`tasks.confirmDelete`、`tasks.confirmUnsaved`、`tasks.showCharacterCount`、`tasks.autoSwitchToAllOnHidden`）；`PortableSettingsAsync`（现 :201-216）对白名单外定义静默跳过；`PrepareRestoreAsync`（现 :149-186）恢复侧同样只应用白名单键；`UploadAsync`（:85-113）与下载路径透传 `ServerRevision`/`Cursor`。
  - 账户页 `src/Modules/Auth/Mosaic.Auth.Wpf/Internal/AccountViewModel.cs`：新增 `Sessions` 集合与 `RefreshSessionsCommand`/`RevokeSessionCommand`/`RevokeAllSessionsCommand`/`ShowDeleteDialogCommand`/`CancelDeleteCommand`/`SendDeleteCodeCommand`/`ConfirmDeleteCommand`；`PollDeviceAsync` 改为按 `pollAt` 绝对时间点调度（`NextPollAt(secondsLeft, interval)`），slow_down 时 `slowDownIncrement += interval`、`effectiveInterval = Clamp(base + increment, base, 30)`；删除账号支持密码/验证码双模式、6 位校验、验证码冷却与 `_dialogs.ConfirmAsync` 二次确认，成功后清凭据并提示；`InitializeAsync` 已登录时自动拉取会话列表。`Internal/AccountConverters.cs`（新增 `LocalDateTimeConverter`、`CurrentSessionTagConverter`）、`Internal/AuthLocalization.cs`（`ReauthRequired`/`SessionRevoked` 映射）、`Views/AccountView.xaml`（「已登录会话」卡与「删除账号」卡、转换器资源注册）、`Localization/en.xaml`、`zh.xaml`（`Auth.Sessions.*`、`Auth.Delete.*`、两条错误文案）。
  - 测试：`tests/Mosaic.Auth.Core.Tests/AuthServiceTests.cs`（Poll 断言改比较 `DevicePollDto.Status`）、`Fakes.cs`（`FakeTransport` 记录 `LastAppVersion` 与账户管理调用）；`tests/Mosaic.Auth.Http.Tests/HttpAuthTransportTests.cs` 新增 12 个用例（`verificationUriComplete` 优先、无则构造 `/device?user_code=`、slow_down→Pending+间隔 2、会话列表两种形状、撤销单会话/全部、删除账号请求体、两条新错误码映射、未知 `details` 字段忽略、purpose 线格式 `delete-account`）；`tests/Mosaic.Sync.Core.Tests/SyncServiceTests.cs` 白名单用例改为精确键（`ui.text.placeholder` 不再上传），新增「`tasks.revisionStart`/`tasks.floating.allowNotesEditing` 等旧前缀命中键不上传」与「14 键全定义时精确上传 14 键」两个证明用例、`Fakes.cs`（`FakeAuthApi` 补新成员）；`tests/Mosaic.Wpf.Tests/AccountViewModelTests.cs`（`FakeAuth` 补会话/删除记录，新增加载会话列表、撤销会话、删除必须二次确认、验证码 6 位校验、发送删除验证码 5 个用例）。
  - 发布元数据 `Directory.Build.props`（版本 0.16.1）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.16.1）、`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`（第 27 条，`previousReleaseId=mosaic-0.16.0`）、`README.md`、`docs/releases/v0.16.1.md`、`docs/releases/v0.16.1-files.md`。
- 关系：服务端 0.1.1（`server/` commit `4a223be`）已完成冻结，本次为桌面端 0.16.0 对其 wire 的对齐交付；`ChangeDto` 增量同步属服务端预留，桌面第一阶段为显式上传/下载、无增量实现，故只透传备份响应的 `serverRevision`/`cursor` 字段；`GET /v1/device/info` 与 410 `sync.cursor_invalid` 属增量同步路径，桌面暂不使用。不改 `server/`、`mobile/`。
- 兼容与迁移：不涉及数据目录、配置文件、安装升级或模块装配改动——数据库表结构、既有设置键、导航契约、`appsettings.json` 与 `auth-sync-v1` 迁移均未变化，0.16.0 安装直接升级；升级后第一次备份上传自动按 14 键白名单精简设置项，已保存在服务器上的多余键下载时被静默忽略（不再触发 400，也不会写回本机）。
- 验证：Release 全量重建 0 警告 0 错误；非桌面测试全绿——Architecture 4、Auth.Core 25、Auth.Http 36、DesktopPresence.Core 17、FloatingWindows 12、Kernel 5、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 1、Storage.Sqlite 12、Sync.Core 14、Tasks.Contract 7、Tasks.Core 27、Wpf 110。
- 发布内容：`artifacts/installer/Mosaic-0.16.1-win-x64.msi`（52 605 300 字节，SHA-256 `30FD96C499B70BEAE94E241F05A96C5E8385645C2FE8B6569877B9D1E400715F`，自包含 .NET 10.0.15 运行时；数据库回读 ProductVersion=0.16.1、Application 所属桌面快捷方式与卸载注册表清理经 `build/test-package-shortcuts.ps1` 校验通过）；发布产物 `artifacts/local-run` 通过 `verify-published.ps1`（`PASS: published payload is a release build with manifest mosaic-0.16.1`）；发布内容指纹、历史链、索引不可变性按 `build/release-content.ps1` 算法以兼容脚本（Windows PowerShell 5.1 无法解析无 BOM 原脚本）独立复算通过；`docs/releases/v0.16.1.md` 为发布说明。
- 未验证项：MSI 的实际安装/升级/卸载不在本机执行（本机存在正在运行的真实安装）；桌面 Smoke 未在本版本重跑（它驱动真实指针会抢占用户鼠标）；与真实自建服务器 0.1.1 的端到端会话管理/删除账号/设备授权 slow_down 退避由服务端实施记录与后续联调覆盖。

## 0.16.0：可选账户、浏览器授权登录与任务/设置备份（2026-11-19）

- 根因位置：0.15.x 及更早版本的任务与设置只存在于本机 SQLite 与 settings 表，且没有任何经过 HTTPS 的可信服务器身份：`src/Mosaic.Desktop/BuiltInModuleCatalog.cs`（0.15.3 版）只装配任务、存储、设置、浮窗、桌面驻留与发布摘要模块，没有 `auth.api@1`、`sync.api@1`、`tasks.transfer@1` 三项能力，因此换机、重装或另一台设备上的 Mosaic 无法获得既有任务（只能手动复制 `mosaic.sqlite`），旧任务也不会以原 ID/修订号/时间戳恢复；用户数据目录丢失即数据丢失。
- 改动位置：
  - 账户契约 `src/Modules/Auth/Mosaic.Auth.Contracts/AuthContracts.cs`：`AuthStatus`、`AuthErrorCodes`、`AuthValidation`（邮箱、密码 10–256、6 位验证码、服务器必须 HTTPS、无 userinfo/query/fragment、路径为 `/`）、`AuthAccountDto`、`AuthPolicyDto`、`DeviceAuthorizationDto`、`AuthState`、`AuthSecrets`，以及 `IAuthApi`、`IAuthTransport`、`IAuthSecretStore`。
  - 账户核心 `src/Modules/Auth/Mosaic.Auth.Core/AuthModule.cs`（模块 `auth-core`，`Definition:34-38` 提供 `auth.api@1`、依赖 `auth.transport@1`/`auth.secrets@1`/`settings.api@1`）；`Internal/AuthService.cs`：注册/密码登录/验证码登录/找回与重置密码、设备授权启动-轮询-取消-过期、`CompleteSignInAsync:298` 统一处理登录成功与凭据持久化、`WithServerAsync:317` 保证离线启动只读本地凭据不联网、`StateChanged:41/360` 通知；`Internal/AuthSettingsSource.cs` 注册设置项 `auth.serverUrl`；`Internal/GuardedAuthApi.cs` 经 `IModuleExecutionGate` 与诊断包装。
  - 账户传输 `src/Modules/Auth/Mosaic.Auth.Http/HttpAuthTransport.cs`（`/v1` 账户端点、错误码映射、强制 HTTPS，回环开发例外）与 `HttpAuthModule.cs`（模块 `auth-http`，唯一持有 `HttpClient` 的账户实现）。
  - 凭据存储 `src/Modules/Auth/Mosaic.Auth.Windows/WindowsAuthSecretStore.cs`：DPAPI（entropy `Mosaic.Auth.Secrets.v1`）加密 `<Root>/Auth/credentials.dat`，原子写入；损坏或异账户文件按未登录处理；拒绝 http 服务器与空令牌；`AuthWindowsModule.cs`（模块 `auth-windows`）。
  - 账户页 `src/Modules/Auth/Mosaic.Auth.Wpf/AuthUiModule.cs`、`Internal/AuthViewFactory.cs`、`Internal/AuthLocalization.cs`、`Internal/AccountViewModel.cs`（登录/注册/忘记密码表单、设备授权码 `BAAD-C832` 展示与轮询、服务器地址编辑、离线使用、备份上传/下载确认、同步开关经设置编辑会话持久化、退出登录确认）、`Views/AccountView.xaml(.cs)`、`Localization/en.xaml`、`Localization/zh.xaml`。
  - 备份契约 `src/Modules/Sync/Mosaic.Sync.Contracts/SyncContracts.cs`：`SyncItemKinds`、`SyncErrorCodes`、`BackupItemDto`、`BackupSnapshotDto`、`BackupSnapshotContentDto`、`BackupUploadResult`、`RestoreSummary`、`SyncState`、`ISyncApi`、`ISyncTransport`。
  - 备份核心 `src/Modules/Sync/Mosaic.Sync.Core/SyncModule.cs`（模块 `sync-core`，提供 `sync.api@1`）；`Internal/SyncService.cs`：`UploadAsync:67`（导出经 `ITaskTransferApi` 取归档全量、设置只取 `PortablePrefixes:16`/`PortableIds:17` 白名单、上传前校验快照 SHA-256）、`DownloadAndReplaceAsync:97`（先 `PrepareRestoreAsync:131` 校验快照，再单事务替换任务，最后 `ApplySettingsAsync:170` 只应用可移植项）、`ContextAsync:274`（未登录或关闭同步短路为 `SYNC_NOT_SIGNED_IN`/`SYNC_DISABLED`）；`Internal/SyncSettingsSource.cs` 注册 `sync.enabled`、`sync.includeSettings`；`Internal/GuardedSyncApi.cs`。
  - 备份传输 `src/Modules/Sync/Mosaic.Sync.Http/HttpSyncTransport.cs`（`/v1/backups` 上传、latest 元数据、按 ID 下载、错误码映射、HTTPS 强制）与 `HttpSyncModule.cs`（模块 `sync-http`）。
  - 任务全量导出/替换 `src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskTransferContracts.cs`（`ITaskTransferApi`）、`src/Modules/Tasks/Mosaic.Tasks.Persistence.Contracts/ITaskTransferRepository.cs`、`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskTransferService.cs`（`ExportAllAsync:14-18` 使用 `ArchiveScope = All` 的既有 `ITasksApi.ListAsync`；`ReplaceAllAsync:20` 逐行校验——Id 非空不重复、Revision ≥ 1、时间戳顺序、标题长度，上限 100 000 行——失败即拒绝，成功后经 `TaskChangeSource` 发布变更）、`Internal/GuardedTaskTransferApi.cs`、`TasksModule.cs`（`Transfer:47`，依赖注入 `TasksDependencies.TransferRepository`）。
  - SQLite 替换仓库 `src/Modules/Storage/Mosaic.Tasks.Sqlite/Internal/SqliteTaskTransferRepository.cs`（单事务删除并重插，保留原 Id/Revision/时间戳）、`TasksSqliteModule.cs`（新增能力 `tasks.transfer-repository@1`）、`Internal/SqliteTaskRepository.cs`（`Values(TaskItemDto)` 改为 `internal static` 供替换仓库复用映射）。
  - 宿主装配 `src/Mosaic.Desktop/BuiltInModuleCatalog.cs`（using 行 1-4、16-17；属性 57-61；装配顺序 `auth-http` → `auth-windows` → `auth-core` → `sync-http` → `sync-core` → `auth-ui`，并把 `tasks.transfer-repository@1` 注入 `TasksDependencies`；缺失依赖时抛出明确异常）、`src/Mosaic.Desktop/AppConfiguration.cs`（迁移 `auth-sync-v1:19` 登记模块/provider/三项设置，`AuthSecrets:337`）、`src/Mosaic.Desktop/appsettings.json`、`Mosaic.Desktop.csproj`、`Mosaic.sln`、`src/Modules/Settings/Mosaic.Settings.Wpf/Localization/en.xaml`、`zh.xaml`。
  - 构建契约 `build/project-boundaries.json`（账户/同步各项目允许依赖；`HttpClient` 只在 `Mosaic.Auth.Http`、`Mosaic.Sync.Http`；DPAPI 只在 `Mosaic.Auth.Windows`）、`build/public-api.txt`、`tests/Mosaic.Architecture.Tests/BoundaryTests.cs`（`IsWindowsProject` 统一判定 Windows 目标框架；模块→程序集表补充 `auth-core`、`sync-core`）。
  - 测试：`tests/Mosaic.Auth.Core.Tests/AuthServiceTests.cs`、`Fakes.cs`；`tests/Mosaic.Auth.Http.Tests/HttpAuthTransportTests.cs`、`HttpSyncTransportTests.cs`、`LoopbackServer.cs`；`tests/Mosaic.Sync.Core.Tests/SyncServiceTests.cs`、`Fakes.cs`；`tests/Mosaic.Tasks.Core.Tests/TaskTransferTests.cs`；`tests/Mosaic.Wpf.Tests/AccountViewModelTests.cs`、`WindowsAuthSecretStoreTests.cs`、`Mosaic.Wpf.Tests.csproj`。
  - 发布元数据 `Directory.Build.props`（版本 0.16.0）、`build/installer/Mosaic.Installer.wixproj`（ProductVersion 0.16.0）、`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`（第 26 条，`previousReleaseId=mosaic-0.15.3`，指纹 `626351271AF88E7D52EFD01AAAC3DD0FCF2A74E988893EFDB4D78B0858FB3C73`）、`README.md`、`docs/releases/v0.16.0.md`、`docs/releases/v0.16.0-files.md`。
- 关系：账户与备份完全可选，不连接服务器时行为与 0.15.3 一致（本地优先、启动不联网）；恢复任务复用 `ITaskTransferApi`/`ITaskTransferRepository` 保留原 ID 与修订号，任务区、已归档页与浮窗经既有变更通知自动刷新；设置恢复只写白名单（`appearance.theme`、`ui.language`、`tasks.*`、`ui.text.*`），不触碰服务器地址、模块装配、机器路径、驻留/自启、令牌、pending journal 与日志。
- 兼容与迁移：新迁移 `auth-sync-v1` 为已有安装登记六个新模块、五项能力 provider 与 `auth.serverUrl`/`sync.enabled`/`sync.includeSettings` 三项设置；既有任务、归档与设置原样保留，卸载不删除数据目录（含 `Auth/credentials.dat`）。数据库表结构未变化。
- 验证：Release 全量重建 0 警告 0 错误；非桌面测试全绿——Architecture 4、Auth.Core 25、Auth.Http 24、DesktopPresence.Core 17、FloatingWindows 12、Kernel 5、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 1、Storage.Sqlite 12、Sync.Core 12、Tasks.Contract 7、Tasks.Core 27、Wpf 105。发布内容指纹、历史链、索引不可变性、更新简要长度按 `build/release-content.ps1` 算法以兼容脚本（Windows PowerShell 5.1 在 zh-CN 主机无法解析无 BOM 原脚本）独立复算通过；`build/release-manifest.json` 与 `docs/releases/v0.16.0.md` 由该复算脚本按同一算法重新生成，格式与 0.15.x 条目一致。
- 发布内容：`artifacts/installer/Mosaic-0.16.0-win-x64.msi`（52 560 244 字节，SHA-256 `229B455B034B431D9413223EB0DC36AA69C34F5D75C1FBE10EBD41C5009B774E`，自包含 .NET 10.0.15 运行时；数据库回读 ProductVersion=0.16.0、Application 所属桌面快捷方式与卸载注册表清理经 `build/test-package-shortcuts.ps1` 校验通过）；`docs/releases/v0.16.0.md` 为发布说明。
- 未验证项：MSI 的实际安装/升级/卸载不在本机执行（本机存在正在运行的真实安装，避免打断用户数据）；桌面 Smoke 未在本版本重跑（它驱动真实指针会抢占用户鼠标，且新测试已覆盖账户页 ViewModel 层）；账户页在 150% DPI、高对比度、中英文切换下的逐像素截图未逐项落地；与真实自建服务器（`server/` 0.1.x）的端到端备份往返由服务端实施记录与后续联调覆盖。

## 0.15.3：自动归档启停路径的断言补强（2026-11-19）

- 测试缺口位置：0.15.2 新增的两个测试只断言 `ITasksApi.SetArchivedAsync` 的调用次数与仓库中的归档状态（`tests/Mosaic.Tasks.Core.Tests/AutoArchiveTests.cs` 0.15.2 版 :129-176），没有断言归档循环本身是否启动。`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskAutoArchive.cs:12-14` 的 `_delayDays`、`_cancellation`、`_loop` 是启停状态的真实来源：如果 `ApplyAsync` 把关闭误判为启用（循环启动但本轮候选为空，或轮询间隔尚未到），`SetArchivedAsync` 仍可能一次都不被调用，0.15.2 的测试就会空过。
- 改动位置（仅测试与发布元数据，产品源码未改）：
  - `tests/Mosaic.Tasks.Core.Tests/AutoArchiveTests.cs`：`Disabled_auto_archive_never_archives_due_completed_tasks` 更名为 `Auto_archive_disabled_policy_never_archives_due_completed_tasks`，候选任务改为等待期 7 天、完成于 8 天前，并在两次 `ApplyAsync`（空字典默认值与显式 `autoEnabled=false`）后新增断言：`_delayDays == 0`、`_cancellation` 为 null、`_loop` 仍是 `Task.CompletedTask`，证明归档循环没有启动；`Enabled_auto_archive_archives_due_candidates_and_stops_when_disabled_again` 在开启后断言 `_delayDays == 7`、`_cancellation` 非空，关闭后断言 `_delayDays == 0`、`_cancellation` 为 null，再插入 30 天前完成的已完成任务并确认不再归档；新增 `Field` 辅助方法反射读取 `TaskAutoArchive` 私有字段。
  - `Directory.Build.props`、`build/installer/Mosaic.Installer.wixproj`：版本 0.15.3。
  - `build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`：追加 `mosaic-0.15.3`（`previousReleaseId=mosaic-0.15.2`，指纹 `CD85654836A042B3600E16914C2AA166E86536A6502E51877F3642E56E5216E6`），索引第 25 条；0.14.0、0.15.0、0.15.1、0.15.2 的已发布条目与哈希未改动（逐条复算一致，索引仅新增）。
  - `README.md`、`docs/releases/v0.15.3.md`、`docs/releases/v0.15.3-files.md`：版本说明与交付清单。
- 与 0.15.2 的关系：0.15.2 已在收到本次要求前发布（tag v0.15.2），其内容不可修改，因此断言补强作为 0.15.3 独立交付；两版均为纯测试补丁，应用行为不变。
- 兼容与迁移：产品行为、数据库结构、设置键与配置格式均未变化；已有安装直接升级，不需要迁移。
- 验证：Release 全量重建 0 警告 0 错误；非桌面测试 204 项全绿——Architecture 4、DesktopPresence.Core 17、FloatingWindows 12、Kernel 5、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 1、Storage.Sqlite 12、Tasks.Contract 7、Tasks.Core 22、Wpf 82。桌面 Smoke 未重跑：它驱动真实指针会抢占用户鼠标，且本机仍有用户正在运行的真实安装，无法独立确认。
- 发布内容：`build/published-release-index.json` 25 条；发布产物 `artifacts/local-run` 通过 `verify-published.ps1`（`PASS: published payload is a release build with manifest mosaic-0.15.3`）。MSI：`Mosaic-0.15.3-win-x64.msi`，52469940 字节，SHA-256 `60379E4CCD71A8ED27DF0C2DA1C31CDF0AB615EA5BD1D873A6F843E46E547637`，数据库回读 ProductVersion=0.15.3，`test-package-shortcuts.ps1` PASS；发布产物 ProductVersion 为 `0.15.3+9f7f1b4...`（工作树改动尚未提交时的构建）。
- 未验证项：MSI 的实际安装/升级/卸载不在本机执行（本机存在正在运行的真实安装）；桌面 Smoke 未在本版本重跑。

## 0.15.2：自动归档启停行为回归测试（2026-11-19）

- 覆盖缺口位置（0.15.1 的测试未断言行为）：`tests/Mosaic.Tasks.Core.Tests/AutoArchiveTests.cs`（0.15.1 版）只断言 `TaskSettingsSource` 的默认值与 `ITaskRepository.QueryArchiveCandidatesAsync` 的候选查询（:87-127），从未调用 `TaskAutoArchive.ApplyAsync`。因此 `src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskAutoArchive.cs:16-19` 的关闭分支（`autoEnabled` 不是 `true` 时传 0）与 `TaskAutoArchive.SetDelayAsync`（:21-41）的启动/取消循环路径都没有行为断言：如果关闭策略仍然启动归档循环，或开启后归档了非候选任务，现有测试不会发现。
- 改动位置（仅测试与发布元数据，产品源码未改）：
  - `tests/Mosaic.Tasks.Core.Tests/AutoArchiveTests.cs`：新增 `Disabled_auto_archive_never_archives_due_completed_tasks`（空设置字典与显式 `autoEnabled=false` 两次调用 `ApplyAsync`，断言 `ITasksApi.SetArchivedAsync` 零调用、超过等待期 60 天的已完成任务与未完成任务均保持未归档）；新增 `Enabled_auto_archive_archives_due_candidates_and_stops_when_disabled_again`（`autoEnabled=true`、`delayDays=7` 后只归档 8 天前完成的候选，6 天前完成与未完成任务不受影响；随后关闭策略，插入 30 天前完成的已完成任务并等待，断言仍只有一次归档调用、新任务未归档，覆盖 `SetDelayAsync` 的取消与循环退出）；新增辅助方法 `ApplyPolicy`、`StopPolicy`（反射调用内部 `ApplyAsync`、`SetDelayAsync`）与 `Until`（5 秒轮询，超时 `Assert.Fail`）。
  - `Directory.Build.props`、`build/installer/Mosaic.Installer.wixproj`：版本 0.15.2。
  - `build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`：追加 `mosaic-0.15.2`（`previousReleaseId=mosaic-0.15.1`，指纹 `3421D9EAAD6EF8417E29D74528342FBCEF545CA894C2AD1755E41E4A273FEA90`），索引第 24 条；0.14.0、0.15.0、0.15.1 的已发布条目与哈希未改动（与 HEAD 索引逐条比对，仅新增）。
  - `README.md`、`docs/releases/v0.15.2.md`、`docs/releases/v0.15.2-files.md`：版本说明与交付清单。
- 0.15.0 核验反馈的处理：对方提出的“归档页仍可切走筛选/新建”已在 0.15.1 修复并发布（`TasksViewModel` 构造期 `archivedOnly` 锁定 `_filter=3`、`FilterIndex` setter 忽略非 3 写入、`ApplySettings` 跳过 `tasks.defaultFilter`、`NewAsync` 首行返回、`TasksView.xaml` 隐藏新建按钮与筛选下拉）；0.15.1 为已发布版本，本次不修改其内容，仅对 0.15.0 记录中的测试缺口补强。
- 兼容与迁移：产品行为、数据库结构、设置键与配置格式均未变化；0.15.1 安装直接升级，不需要迁移。
- 验证：Release 构建 0 警告 0 错误；非桌面测试 204 项全绿——Architecture 4、DesktopPresence.Core 17、FloatingWindows 12、Kernel 5、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 1、Storage.Sqlite 12、Tasks.Contract 7、Tasks.Core 22（新增 2 项）、Wpf 82。本版本未改动产品源码，未重跑桌面 Smoke（其驱动真实指针会抢占用户鼠标，且 0.15.1 已记录该套件唯一的失败为环境干扰）。
- 发布内容：`build/published-release-index.json` 24 条；发布产物 `artifacts/local-run` 通过 `verify-published.ps1`（`PASS: published payload is a release build with manifest mosaic-0.15.2`）。MSI：`Mosaic-0.15.2-win-x64.msi`，52478132 字节，SHA-256 `E670F56484C05F1EC6EC0B2E289FCDF2C63E8A73B4DAC1D1B9CD1FB95782D70A`，数据库回读 ProductVersion=0.15.2，`test-package-shortcuts.ps1` PASS；发布产物 ProductVersion 为 `0.15.2+ec8ee3f...`（工作树改动尚未提交时的构建）。
- 未验证项：MSI 的实际安装/升级/卸载不在本机执行（本机存在正在运行的真实安装）；桌面 Smoke 未在本版本重跑。

## 0.15.1：归档页锁定筛选并隐藏新建与筛选入口（2026-11-19）

- 根因位置：
  - 筛选被覆盖：0.15.0 的归档页是在构造完成后设置 `FilterIndex = 3`（`TasksViewModel` 的公开 setter），而 `TasksViewModel.ApplySettings` 的 `tasks.defaultFilter` 分支会无条件写回筛选。当页面在设置到达之前创建（`TaskUiSettingsTarget.Attach` 时还没有 `_latest`），首次 `ApplySettings` 在页面创建后到达，就把「已归档」覆盖成「全部/未完成/已完成」；设置再次应用时也会重复覆盖。根因即 `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TasksViewModel.cs` 的 `TasksViewModel.ApplySettings`（0.15.0 版本 :853-862）缺少归档页判断。
  - 入口失步：同一页面上「新建任务」按钮与四项筛选下拉始终可见可用（0.15.0 的 `Views/TasksView.xaml` 中 `NewTask` 按钮与 `TaskFilter` 下拉），归档页可以借此新建任务或切换到非归档筛选，与「固定查看已归档」的页面语义冲突。
- 改动位置：
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TasksViewModel.cs`：构造函数新增 `bool archivedOnly = false`（:62），构造期写入 `_archivedOnly` 并把 `_filter` 置 3（:42、:64-65），不再依赖构造后赋值；`FilterIndex` setter 忽略归档页上非 3 的写入（:132-140）；`ApplySettings` 的 `tasks.defaultFilter` 分支只在非归档页执行（:871-883），`_defaultFilterApplied` 仍统一置位；`NewCommand` 的 `CanExecute` 排除归档页（:78），`NewAsync` 首行直接返回（:301）；新增每实例恒定的 `ShowNewTask`、`ShowFilterChooser`（:199-200）；`OnEditorPropertyChanged` 只保留「归档页编辑器空态自动关闭」，删除无消费方的 `IsArchivedPage` 与不再变化的可见性通知。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskViewFactory.cs`：构造函数新增 `bool archivedOnly = false` 并透传给 `TasksViewModel`。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/TasksUiModule.cs`：归档视图 `tasks.archived.list` 使用 `new TaskViewFactory(dependencies, _settingsTarget, archivedOnly: true)`；主任务页继续使用默认工厂。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml`：`NewTask` 按钮（:54）与 `TaskFilter` 下拉（:96）按 `ShowNewTask`/`ShowFilterChooser` 绑定 `BoolVisibility`，归档页隐藏。
  - `tests/Mosaic.Wpf.Tests/PresentationTests.cs`：`Archived_navigation_opens_an_independent_archived_page` 增加两个控件的 `Collapsed` 断言；新增 `Archived_page_locks_its_filter_when_settings_arrive_late` 覆盖构造期筛选、非 3 写入忽略、`tasks.defaultFilter=Open` 晚到不覆盖、`NewCommand` 不可执行与初始化后仍为 3。
- 兼容与迁移：数据目录、设置键、ViewId 与导航契约均未变化；`tasks.defaultFilter` 仍作用于主任务页，归档页只是不再消费它。升级不需要迁移，已有安装的归档数据与筛选偏好照旧。
- 验证：Debug/Release 构建 0 警告 0 错误；`Mosaic.Wpf.Tests` 82/82（含新增用例）；Architecture 4、DesktopPresence.Core 17、FloatingWindows 12、Kernel 5、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 1、Storage.Sqlite 12、Tasks.Contract 7、Tasks.Core 20 全绿；Desktop Smoke 31/32。未通过项为 `Floating_real_pointer_moves_resizes_clicks_and_restores_saved_bounds`，已定位为环境干扰：点击命中窗口为 `pid 42580`（本机已安装并运行的 Mosaic 0.14.0）的置顶浮窗（`HwndWrapper[Mosaic.Desktop;;fe0fb391-...]`，`WS_EX_TOPMOST`），它覆盖了测试卡片取消置顶后的同一坐标，UIA 命中同样来自该进程的元素，与本仓库被测进程（`hwnd=37817946`）无关；0.14.0 记录中同一用例也曾出现两次同类抖动。该用例在 0.15.0 发布时通过，本轮代码不涉及浮窗路径。
- 发布内容：`build/release-content.json` 追加 `mosaic-0.15.1`（`previousReleaseId=mosaic-0.15.0`，指纹 `A05423517DE48F37E7D27E4C00AA65702673B719E367896A9329750506FC4698`）；`build/published-release-index.json` 追加为第 23 条，既有 22 条与 0.14.0、0.15.0 的哈希未改动（以 HEAD 索引逐条比对）。MSI：`Mosaic-0.15.1-win-x64.msi`，52482228 字节，SHA-256 `52D56AC3CAE0F85827ED57D8A2F7C4C76FC01F41CA4D7F78DA7135FEAA1042EA`，数据库回读 ProductVersion=0.15.1；发布产物 ProductVersion 为 `0.15.1+1419bba...`（工作树改动尚未提交时的构建）。
- 未验证项：MSI 的实际安装/升级/卸载不在本机执行（本机存在正在运行的真实安装，避免干扰用户环境）；被环境遮挡的真实指针用例未在其干扰消失后重跑。

## 0.15.0：已归档任务导航与未完成不自动归档（2026-11-19）

- 根因位置：
  - 需求一（左侧没有独立的已归档入口）：`src/Modules/Tasks/Mosaic.Tasks.Wpf/TasksUiModule.cs`（改动前 :32-35）的 `Navigation` 只贡献一项 `tasks.navigation`（ViewId `tasks.list`），`Views` 只贡献一个 `tasks.list`，归档只能从主任务页的第四项筛选进入；同时 `src/Mosaic.Shell.Wpf/NavigationService.cs:9-29` 对相同 `ViewId` 直接返回（`if (CurrentViewId == viewId) return true;`），若两个导航项共用 `tasks.list`，第二次导航不会重建页面，归档页会沿用主列表实例与筛选，无法做到两页独立。
  - 需求二（未完成任务的自动归档边界缺少显式锁定）：`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskAutoArchive.cs` 的候选查询已限定 `completed = 1`，`TaskSettingsSource` 的 `tasks.archive.autoEnabled` 默认 `false`、`tasks.archive.delayDays` 默认 7，行为本身正确；但设置描述只写“自动归档到期的已完成任务”，没有说明未完成任务不受影响，且没有测试显式锁定“未完成超期也不归档”。
- 改动位置：
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/TasksUiModule.cs`：`Navigation` 增加 `tasks.archived.navigation`（LabelKey `Tasks.Archived.Navigation`，图标键 `folder`，ViewId `tasks.archived.list`，Order 110）；`Views` 增加 `tasks.archived.list`，使用 `TaskViewFactory(dependencies, _settingsTarget, 3)`。图标键沿用 `src/Mosaic.Shell.Wpf/NavigationConverters.cs:15` 已支持的 `folder`，Shell 不依赖 Tasks 模块。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskViewFactory.cs:6-15`：构造函数增加可选 `int? initialFilter`，`Create()` 构造 `TasksViewModel` 后设置 `FilterIndex`，归档页初始即“已归档”，且不改写用户保存的默认筛选。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TasksViewModel.cs:193-195`：新增 `ListTitle`（`FilterIndex == 3` → `Tasks.ListTitle.Archived`，否则 `Tasks.ListTitle`）；`SetFilter`（:630）与 `RefreshLocalizedText`（:916）同步通知，切换筛选与中英文时标题一致刷新。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Views/TasksView.xaml:83`：标题由 `DynamicResource Tasks.ListTitle` 改为 `Binding ListTitle`，归档页显示“已归档任务”。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Localization/zh.xaml:117-118`、`en.xaml` 对应位置：新增 `Tasks.Archived.Navigation`、`Tasks.ListTitle.Archived`。
  - `src/Modules/Settings/Mosaic.Settings.Wpf/Localization/zh.xaml:265`、`en.xaml` 对应位置：`Settings.Setting.AutoArchive.Description` 改为“仅自动归档等待期已满且未恢复的已完成任务；未完成任务永远不会被自动归档；关闭时不自动归档任何任务”。
  - `src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskSettingsSource.cs:11`：设置定义内联描述同步为同一文案，避免无本地化资源时的回退文本与界面不一致。
  - 测试：`tests/Mosaic.Tasks.Core.Tests/AutoArchiveTests.cs`（新增，6 项，反射访问 internal 类型）锁定“未完成无论多旧都不是候选”“已完成仅等待期已满才归档”“已归档不重复归档”“恢复后从 `RestoredAtUtc` 重计”“默认关闭且说明含未完成策略”“归档轮次只写候选查询返回的任务”；`tests/Mosaic.Wpf.Tests/PresentationTests.cs` 更新导航/Shell/语言切换/重复注册断言为两项，新增 `Archived_navigation_opens_an_independent_archived_page`、`Archived_page_restores_a_task_and_keeps_the_page_on_the_archived_list`、`Archived_page_keeps_its_archived_filter_and_active_page_follows_the_default_filter`；`tests/Mosaic.Wpf.Tests/ResponsiveLayoutTests.cs` 侧栏 rail 断言按 ToolTip 定位“任务”按钮并容纳两个导航标签；`tests/Mosaic.Wpf.Tests/PreviewCaptureTests.cs` 设置页截图按唯一 `settings.home` 视图取页。
  - 发布：`Directory.Build.props:12`、`build/installer/Mosaic.Installer.wixproj:4` 升为 0.15.0；`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json` 追加 `mosaic-0.15.0`（指纹 `053E513BEF2A5D9178FEB96DF3DEDEAD7FB18715315DC8D853EF3CF2F19AF9FD`，索引 22 条，既有条目与 0.14.0 已发布哈希未改动）；`README.md`、`docs/releases/v0.15.0.md`、`docs/releases/v0.15.0-files.md` 同步。
- 迁移与兼容：无数据库结构变化、无新增设置项、无配置迁移。归档页复用既有 `SetArchivedAsync`（CAS 版本校验）与 `Tasks.Unarchive.Success` 提示；两页各自创建 `TasksViewModel`，草稿、冲突提示、删除确认互不影响。旧安装升级后导航多出一项，任务数据与设置保持原样。
- 附带修复（安装包校验脚本查错表）：`build/test-package-shortcuts.ps1:51-66` 原先在 `Registry` 表中过滤 `Registry = -1` 查找卸载清理项，且 SQL 写在双引号里导致 `` `Key` `` 的反引号被 PowerShell 当作转义符。根因是 WiX 的 `RemoveRegistryValue` 写入的是 `RemoveRegistry` 表而非 `Registry` 表，因此该校验自 0.13.0 起对任何 MSI 都会抛 `COMException: OpenView,Sql`，无法真正验证卸载清理。改为查询 `RemoveRegistry` 的 `RemoveRegistry,Root,Key,Name,Component_` 并匹配 `Root = 1`（HKCU），0.14.0 与 0.15.0 安装包均返回 PASS。
- 产物：`artifacts/local-run` 为 0.15.0 自包含目录（`Mosaic.Desktop.exe` FileVersion 0.15.0.0），`verify-published.ps1` 返回 PASS，发布目录内 `release-manifest.json` 与 `build/release-content.json` 完全一致；MSI `artifacts/installer/Mosaic-0.15.0-win-x64.msi`，52,474,036 字节，SHA-256 `DB2398067BABEE1E56F0D23ED575510717A99BF1541088E1EBFEC0D484C8DA26`，校验文件 `artifacts/SHA256SUMS-0.15.0.txt`。数据库回读确认 `ProductVersion=0.15.0`、沿用 `UpgradeCode {4D2B073C-72D5-4C3A-B845-65FC2C52D23C}`、`Upgrade` 表含 0.15.0 以下升级与以上降级阻断、桌面与开始菜单快捷方式均指向 `[INSTALLFOLDER]Mosaic.Desktop.exe` 且工作目录 `INSTALLFOLDER`、`Application` Feature 层级 1、459 个文件条目。
- 验证：Release 构建 0 警告 0 错误；全部 12 个测试程序集 233 项通过（Architecture 4、Desktop Smoke 32、DesktopPresence.Core 17、FloatingWindows 12、Kernel 5、ReleaseSummary 17、Settings.Core 25、Settings.Sqlite 1、Storage.Sqlite 12、Tasks.Contract 7、Tasks.Core 20、Wpf 81）。发布内容指纹、历史关系、索引不可变性与更新简要长度以兼容脚本独立复算通过。未验证项：本机已存在用户正在使用的 0.14.0 真实安装（`%LOCALAPPDATA%\Programs\Mosaic`，进程运行中），本轮未执行真实安装/升级/卸载以免打断用户数据与运行中的应用，安装包仅做数据库回读与快捷方式校验，升级路径以既有 `Upgrade`/`Downgrade` 行与 `build/test-install.ps1` 流程留待独立验收；真实指针下的归档页交互与 150% DPI、高对比度、中英文组合截图未逐项落地。

## 0.14.0：浮窗空白区拖动与速览入口显示时机（2026-11-19）

- 根因位置：
  - 需求一（浮窗空白区不可拖动）：`src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/FloatingWindowInput.cs` 的 `HitTest`（改动前 :111-112）把命中的所有可见 `TextBoxBase` 整个边界框判为 HTCLIENT；`OnMouseDown`（:39-46）与 `OnMouseMove`（:47-53）的阈值拖动候选只来自 `_tools`/`_header` 中的 `ButtonBase`；HTCAPTION 尾部（:113-121）只覆盖顶部拖动带与 header。同时 `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskFloatingDetailsView.cs:25,30` 与 `TaskFloatingContentFactory.cs:170` 让只读备注 `TextBox` 拉伸占满正文区，于是正文大片空白、底部按钮周边与标题下方空白全部落进 HTCLIENT，无法拖动窗口。
  - 需求二（速览入口出现时机不可配置）：`src/Modules/DesktopPresence/Mosaic.DesktopPresence.Core/PresenceController.cs`（改动前 :15-32）只有 `RequestParkAsync` 调用 `showEdge`；`RequestRestoreAsync`（:34-41）恢复后必隐藏；`RequestPopupAsync`（:45）对 `MainVisible` 直接返回；`src/Mosaic.Desktop/DesktopWindowHost.cs:12-23` 不向 Presence 通知主窗口可见/最小化/隐藏状态；`src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSettings.cs:10-40` 没有显示时机设置项。三者叠加导致入口只在“关闭主窗口后驻留”时出现，最小化或保持显示均不可选。
  - 顺带修复（布局快照跨窗口泄漏）：`src/Mosaic.Shell.Wpf/ShellSettingSource.cs` 的 `ShellSettingsApplier.Detach`（改动前 :110-114）只清 `_current`，静态 `_latest` 快照保留上一个 `ShellWindow` 应用过的布局值；新窗口 `Attach` 时（:104-108）把旧快照套到新实例，侧栏宽度等布局被错误继承。
- 改动位置：
  - `src/Mosaic.Presentation.Contracts/FloatingPresentation.cs`：新增 `FloatingContentDragKind { None, Immediate, Deferred }`（:5-8）与 `IFloatingContentSession.QueryContentDrag(Point)`（:25-27，默认 `None`）。
  - `src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/FloatingWindowInput.cs`：新增 `_content`/`_contentDrag` 字段与可选构造参数；`OnMouseDown` 在非按钮命中且 `QueryContentDrag == Deferred` 时记录 `_pressContent`；`OnMouseMove` 阈值判断条件加入 `!_pressContent`；`HitTest` 中 `TextBoxBase` 命中先询问内容层，`Immediate` 时跳过 HTCLIENT 判定，HTCAPTION 尾部对内容层报 `Immediate` 的点返回 2；新增 `MouseToElement` 坐标换算。
  - `src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/FloatingSurface.cs:167-169`：把 `session.View` 与 `session.QueryContentDrag` 传入 `FloatingWindowInput`。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskFloatingContentFactory.cs`：`TaskFloatingContentSession` 新增 `QueryContentDrag(Point)`——详情不可见时返回 `Immediate`，否则经 `TransformToAncestor` 逆变换转到详情视图坐标后转发。
  - `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskFloatingDetailsView.cs`：新增 `_editing` 字段并在 `Render` 中同步；新增 `QueryContentDrag`（按钮/滚动条/Thumb 返回 `None`，只读备注非编辑态转 `DragKindForNotes`，其余空白 `Immediate`）、`DragKindForNotes`（空文本立即拖；`GetCharacterIndexFromPoint(snapToText: true)` + `GetRectFromCharacterIndex` 判定字形，跨行回退 `FontSize` 步进，`GlyphSlack` 容差内命中字形返回 `Deferred`，否则 `Immediate`）、`GlyphSlack`、`TreeParent`。
  - `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Contracts/PresenceContracts.cs`：新增 `MainWindowPresentation { Visible, Minimized, Hidden }`；`IDesktopWindowHost` 新增 `CurrentPresentation` 与 `PresentationChanged`；`IDesktopPresenceSurface` 新增 `SetEdgeVisibleAsync(bool)`。
  - `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Core/PresenceController.cs`：新增 `_showTiming`（默认仅 `Hidden`）与 `_subscribed` 字段；新增 `ConfigureShowTiming(MainWindowPresentation[])`（幂等订阅 `PresentationChanged` 并触发 `EvaluateEdgeAsync`）、`OnPresentationChanged`、`EvaluateEdgeAsync`（驻留或挂起时按时机集合判定，否则恒显示）；`RequestRestoreAsync` 改为按 `CurrentPresentation` 是否在时机集合内决定隐藏；`RequestPopupAsync` 仅驻留挂起时直接返回，主窗口可见但不在时机内也返回。
  - `src/Mosaic.Desktop/DesktopWindowHost.cs`：`Window` 属性改为带 `Attach`/`Detach` 的后备字段；新增 `CurrentPresentation`（无窗口按 `Visible`，`!IsVisible` 为 `Hidden`，最小化为 `Minimized`）与 `PresentationChanged`，订阅 `StateChanged`/`IsVisibleChanged`。
  - `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/DesktopPresenceModule.cs`：设置应用回调接入 `_controller.ConfigureShowTiming(ShowTiming(values))`；新增 `ShowTiming` 映射（Always→全部、Minimized→最小化+隐藏、默认 Resident→仅隐藏）。
  - `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSettings.cs`：新增 `desktop.edge.showTiming`（Normal 类别，默认 `Resident`，选项 `Resident/Minimized/Always`）及显示名、描述与取值映射。
  - `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceLocalization.cs`：新增设置名、描述与三个取值的本地化键。
  - `src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSurface.cs`：新增幂等 `SetEdgeVisibleAsync(bool)`，不为未创建的窗口构造实例。
  - `src/Mosaic.Shell.Wpf/ShellSettingSource.cs`：`ShellSettingsApplier.Detach` 清空 `_latest`（:110-117）。
  - `build/public-api.txt`：登记 `Mosaic.Presentation.Contracts.FloatingContentDragKind` 与 `Mosaic.DesktopPresence.Contracts.MainWindowPresentation`。
  - 测试：`tests/Mosaic.Wpf.Tests/FloatingWindowsTests.cs` 的 `Unified_hit_regions` 改为按文本为空/字形点断言，并新增 `Notes_blank_and_footer_drag_the_window_while_glyphs_and_editing_stay_interactive`；`tests/Mosaic.Wpf.Tests/PresenceTests.cs` 行数 28→29、Normal 5→6 并验证新设置默认值与选项；`tests/Mosaic.DesktopPresence.Core.Tests/PresenceTests.cs` 覆盖显示时机配置与恢复行为。
  - 发布：`Directory.Build.props`、`build/installer/Mosaic.Installer.wixproj` 版本升为 0.14.0；`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json` 追加 mosaic-0.14.0（指纹 8CCA2A8E…FE8087，21 条索引，未改动既有条目）；`README.md`、`docs/releases/v0.14.0.md`、`docs/releases/v0.14.0-files.md` 同步。
- 迁移与兼容：无数据库结构变化；`desktop.edge.showTiming` 默认 `Resident`（仅主窗口关闭后显示），与旧行为完全一致，既有安装升级后无需任何迁移操作；浮窗拖动为纯交互层变化，持久化的浮窗位置/尺寸记录格式不变。
- 验证：全部测试程序集通过（Architecture 4/4、DesktopPresence.Core 17/17、FloatingWindows 12/12、Kernel 5/5、ReleaseSummary、Settings、Storage、Tasks 各套件全绿；Wpf 78/78；桌面真实指针 Smoke 32/32 第三次全量通过，前两次各有一项拖动坐标断言抖动、单独重跑均通过，判定为时序抖动非功能缺陷）。发布内容指纹、索引不可变性、范围汇总长度按 `build/release-content.ps1` 算法以兼容脚本独立复算通过。未验证项：真实指针矩阵中的 150% DPI、高对比度主题与中英文界面组合未逐项落地；MSI 实际安装/升级/卸载在打包后以既有流程验收。

## 0.13.1：速览详情卡片与更新简要补发（2026-11-18）

- 根因位置：`build/publish.ps1` 的 0.13.0 发布流程未以 `-p:MosaicPublishedBuild=true` 构建，`BuiltInModuleCatalog.cs:126–134` 因此将已安装程序判定为开发构建，`ReleaseSummaryModule` 在启动时直接返回 `DevelopmentBuild`，用户数据目录没有 `mosaic-0.13.0` 回执。`TaskPeekDetailsView.Render` 此前把标题、状态、备注等以多条 `TextBox` 逐条拼接，点击标题或正文即触发全局 TextBox 模板的 2px 焦点框（图 1、图 2）。
- 改动位置：`TaskPeekDetailsView` 改为卡片式只读视图——标题、状态、备注收敛到圆角卡片 `Card`，时间信息独立显示；备注改用 `Peek.xaml` 中的 `TaskPeekNotesStyle`（无边框、可选中复制、键盘焦点沿用共享短条）。新增 `build/verify-published.ps1` 在打包前校验 `MosaicPublishedBuild` 元数据与 `release-manifest.json`，并在 `build/package.ps1` 中调用；`tests/Mosaic.ReleaseSummary.Tests/ReleaseSummaryTests.cs` 新增“清单覆盖所有未公告版本”的回归；`tests/Mosaic.Wpf.Tests/PeekDetailsTests.cs` 覆盖卡片布局、只读备注、紧凑模式与三主题渲染截图。
- 修复效果：以正式发布构建重新打包后，在复制真实数据目录的隔离实例上首次启动即生成 `mosaic-0.13.1` 更新简要，正文覆盖 0.13.0 与 0.13.1 全部条目；第二次启动不回执重复生成。0.13.0 用户在升级后启动 0.13.1 时同样会收到这条合并的更新简要。
- 验证：非桌面回归 187/188 通过（1 项 PresenceTests 断言详情失败分支仍查找 TextBox，新视图改为 TextBlock，已修正断言并重跑通过）；WPF 回归含浅色、深色、高对比度速览详情截图（各文件及验证入口见[改动清单](releases/v0.13.1-files.md)）；MSI 经 `msi.dll` 表查询确认 `StartupCleanup` 组件及卸载清理项存在，快捷方式与安装目录工作目录保持 0.13.0 行为。桌面真实登录与 MSI 实际安装/升级仍以既有流程另行验收。

## 0.13.0：任务归档、自动归档与登录自启（2026-11-18）

- 根因位置：`TaskService.SetCompletedAsync` 只保存布尔完成状态，自动归档缺少完成时间基线；`SqliteTaskRepository` 没有归档时间、恢复时间和到期候选查询；`HostSettingSource.ApplyAsync` 为空操作，宿主设置保存后不会同步系统启动项。
- 改动位置：`TaskItemDto` 增加 `CompletedAtUtc`、`ArchivedAtUtc`、`RestoredAtUtc`；`TaskQuery` 增加 `ArchiveScope`；`ITasksApi` 增加 `SetArchivedAsync`；`TaskService` 的完成切换记录完成时间，归档/恢复写入对应时间并拒绝归档后的普通编辑；`SqliteTaskRepository` 增加归档范围查询与到期候选查询，`TasksSqliteModule` 注册 `003_TaskArchiving`；`TasksModule` 接入 `TaskAutoArchive` 策略，`TaskSettingsSource` 增加自动归档开关与等待天数；`TasksViewModel` 增加归档筛选、归档/恢复命令和外部归档保护，`TaskEditorView` 对归档任务只读；`TaskFloatingNotesViewModel` 阻止归档任务进入编辑并保护草稿；`HostSettingsCatalog` 增加 `startup.runAtLogon`，`HostSettingSource` 在持久化、回滚和即时应用时同步 `WindowsStartupRegistration`；`SettingsService` 将宿主源纳入即时应用；`Package.wxs` 新增 `StartupCleanup` 组件在卸载时移除本工作区的登录启动项。逐文件清单见[改动清单](releases/v0.13.0-files.md)。
- 迁移与兼容：`003_TaskArchiving.sql` 为旧 `tasks` 表补充三列，旧已完成任务以 `updated_at` 回填 `completed_at`，旧未完成时间为空，全部任务初始未归档；创建凭据与 Revision 语义不变。自动归档默认关闭、默认等待 7 天；开机自启默认关闭，启用后绑定当前工作区路径，不修改 Windows 任务管理器的系统级禁用状态。
- 验证：核心与契约测试覆盖归档往返、草稿保护、旧库回填和默认未归档；`MemoryTaskRepository` 与 SQLite 查询行为一致；宿主设置测试覆盖启用、回滚和工作区绑定。Release 构建 0 警告、0 错误，非桌面回归 169/170 通过（1 项为发布清单未同步导致的失败，已修复并重跑通过）。桌面 Smoke 与真实 Windows 登录矩阵待验收。

## 0.12.2 应用内按钮反馈（2026-11-18）

- 0.12.1 的 `Theme.xaml:64` 在鼠标点击留下键盘焦点时持续显示描边。`FloatingWindows.Wpf/Themes/Floating.xaml:36` 给已置顶的图钉持续填色，第 44 行又给获得焦点的按钮填色。`Tasks.Wpf/Themes/Floating.xaml:10` 的焦点与按压共用底色。速览 `TaskPeekView.xaml:45` 和设置 `SettingsView.xaml:44` 也以输入焦点直接绘制边框。旧版回归复现了 `IsPressed` 和 `IsMouseOver` 均为 `false` 时置顶按钮仍有底色。
- 共享 `MosaicButtonFocusVisual` 使用 WPF 焦点装饰显示短条。若鼠标点击已由键盘聚焦的按钮，`ButtonFocusMark` 隐藏短条。再次使用键盘时恢复提示。主窗口、速览、设置及浮窗使用该资源，按压由 `IsPressed` 驱动，悬停由 `IsMouseOver` 驱动。置顶图钉继续用空心与实心表示状态。各文件及验证入口见[改动清单](releases/v0.12.2-files.md)。
- 旧任务、配置及浮窗记录沿用原有存储格式和读取路径。MSI 继续使用原有每用户 `UpgradeCode` 和 `MajorUpgrade`。
- 首轮发布版回归中，`Tasks.Wpf/Themes/Floating.xaml` 的禁用态触发器使用 `TemplateBinding` 作为 `Setter.Value`，导致浮窗模板加载失败。改用启用态与悬停态的 `MultiTrigger` 后，两个浮窗可正常恢复。

- 最终 Release 构建为 0 警告、0 错误。181 项非桌面回归和 29 项桌面 Smoke 全部通过。真实鼠标与键盘覆盖按下、松开、移出、拖出取消、禁用、Tab、Shift+Tab、Space 和 Enter。发布版像素采样确认图钉仅在悬停及按压时显示底色。旧 SQLite 数据的浮窗恢复、备注编辑以及快捷键均通过现有回归。干净环境的 MSI 实际安装与升级尚未验收。
- 最终 Windows x64 MSI 为 `Mosaic-0.12.2-win-x64.msi`，51,990,660 字节，SHA-256 为 `FEA88285110E33F8010BBDA524857EB99850EB9B0EAA270F9F6D96FE6A9C9910`。已核对版本、升级范围和桌面快捷方式组件。发布内容指纹为 `D9AA5388262D8585F7C0448F527CD986B7414A29850F2F9D1D6D59C11143A32D`。

## 0.12.1：浮窗工具栏矢量图标（2026-11-18）

- 0.12.0 的 `FloatingSurface.Create` 在第 147–154 行直接将 `↗`、`◇`、`×` 用作按钮内容。`FloatingSurface.Layout` 在第 200 行用 `◆/◇` 表示置顶状态。字体字形各自渲染，导致图标笔画、大小和重心不一致，菱形也缺少图钉语义。
- `FloatingSurface.Create` 为打开主窗口、置顶和关闭按钮加载矢量资源，点击区域间隔 2 DIP。`UpdatePin` 从 `FloatingWindowRecord.IsTopmost` 同时更新置顶图标和操作名称。`Layout` 将工具栏实测宽度传给内容布局。`FloatingLocalization.Dictionary` 提供中英文的置顶和取消置顶名称。[逐文件改动清单](releases/v0.12.1-files.md)
- 兼容和升级沿用原任务、设置、浮窗记录及每用户安装器 `UpgradeCode`。旧 SQLite 工作空间由现有存储模块读取，浮窗还原时直接用已有的 `IsTopmost` 生成新图标，无需改写旧记录。MSI 继续由现有 `MajorUpgrade` 更新程序文件。

- 锁定还原、Release 构建和 Windows x64 自包含发布通过。181 项非桌面回归和 28 项发布版桌面 Smoke 全部通过。验收覆盖 220 DIP 紧凑窗、展开窗、多浮窗、置顶保存失败及重启恢复、三个按钮中央的鼠标拖动、短按和未保存草稿的关闭保护。使用浅色、深色和高对比度主题生成 96、144、192 DPI 的 WPF 渲染图。实际多屏切换、不同系统缩放下的指针交互及干净环境中的 MSI 安装与升级尚未验收。
- Windows x64 MSI 为 `Mosaic-0.12.1-win-x64.msi`，51,982,468 字节，附 `SHA256SUMS-0.12.1.txt`。安装包版本、原有升级标识和桌面快捷方式组件检查通过。MSI SHA-256 为 `726F26D343687FA1B6DB87AB4E75852A8C3A2F8D5C0572E11028C475E1368F17`。发布内容指纹为 `941AE6ACDC7F2C4DA6F424B14BD2A211FFB81DB22381F3533904265A55A4E02B`。

## 0.12.0：浮窗无边框控件与可选正文编辑（2026-11-18）

- 根因位置：0.11.0 的 `TaskFloatingStateStyle`（`src/Modules/Tasks/Mosaic.Tasks.Wpf/Themes/Floating.xaml:3`）及 `TaskFloatingContentSession` 构造函数和 `FloatingSurface.Create` 复用的 `QuietButtonKey`，会在默认或焦点状态绘制描边。`TaskPeekDetailsView.AddText` 通过通用 TextBox 模板渲染只读正文，焦点触发器会绘制内容外框。`TaskPeekDetailsView.Render` 会重建控件，无法保留输入或选区。`TaskUiSettingsTarget.Apply` 只向主页面分发设置。`FloatingWindowCoordinator.CloseAsync` 和 `PrepareExitAsync` 在缺少内容关闭协议时直接保存布局，无法确认未保存的正文。
- 改动位置：`TaskFloatingDetailsView.Render` 保持正文输入控件持久并分开展示阅读、编辑和错误状态。`TaskFloatingNotesViewModel.SaveAsync` 使用 Tasks API 的长度约束和编辑时的任务 Revision。`TaskFloatingContentSession.PrepareCloseAsync` 等待已有任务操作并保护草稿。`FloatingWindowCoordinator` 在锁外准备内容、合并重复关闭、确认后保存窗口状态。浮窗局部模板提供底色焦点反馈。完整位置见[逐文件清单](releases/v0.12.0-files.md)。
- 兼容与迁移：旧工作空间缺少 `tasks.floating.allowNotesEditing` 时由 Settings 合并默认值 `false`。保存开关后使用原用户设置存储，沿用已有任务和浮窗记录。普通设置增至 17 项，高级设置 82 项，共 99 项。WPF 回归覆盖缺键、预览放弃、持久化重启、草稿冲突与关闭失败。发布版 Smoke 使用旧设置缺键的 SQLite 工作空间验证只读、原窗口恢复和启用后更新备注。
- 验证：锁定还原和 Release 构建通过，0 警告、0 错误。非桌面测试 180 项通过，发布版桌面 Smoke 3 项通过，覆盖旧工作空间恢复、备注保存、标题栏按钮短按与真实指针拖动。干净环境 MSI 安装与升级、不同 DPI、多屏、高对比度和输入法矩阵待验收。
- 产物：`artifacts/local-run/Mosaic.Desktop.exe` 与 `artifacts/installer/Mosaic-0.12.0-win-x64.msi`。安装包 51,966,084 字节，`ProductVersion=0.12.0`，沿用原 `UpgradeCode`，WiX 构建和快捷方式检查通过。因本机使用 PowerShell 5.1，以等效路径和哈希 API 执行打包。发布清单和历史版本指纹校验通过，MSI 的 SHA-256 为 `FD544B599AABFFE165B12AB2CCD08905D7F4BD7FFEDF61FCFDB800F034C8233A`，校验文件为 `artifacts/installer/SHA256SUMS-0.12.0.txt`。
- 已核对 GitHub 上 MSI 和校验文件的字节数与 SHA-256。发布时 README 第 13 行仍指向上一版 MSI，已改为 v0.12.0 安装包直链。[GitHub Release v0.12.0](https://github.com/soehi/Mosaic/releases/tag/v0.12.0) · [README.md](../README.md)

## 0.11.0：小窗落点预览与标题栏按钮拖动（2026-11-18）

- 根因位置：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs`（0.10.2 基线 `FinishDrag`）只在松手后提交 `FloatingPlacementHint`，拖动过程中没有目标位置计算或预览输出；`FloatingSurface` 没有独立于窗口的放置预览；`FloatingWindowCoordinator.OpenCoreAsync` 存在历史记录时整条复用旧记录，落点被忽略；`FloatingWindowInput.HitTest` 对按钮返回 `HTCLIENT` 以保留点击，但没有 WPF 阈值拖动兜底，按住标题栏按钮拖动无效。真实指针复核还发现：`FloatingWindowInput.OnMouseMove` 首版用 `Window.DragMove()` 启动移动，而它发出的 `WM_SYSCOMMAND`/`SC_MOVE` 在按压起点为已捕获鼠标的子按钮时会被系统移动循环立即结束并回送 `WM_LBUTTONUP`，窗口不移动（跟踪记录：`WM_SYSCOMMAND 0x0112` 后 1 ms 内收到 `WM_LBUTTONUP 0x0202`）。
- 改动位置：`FloatingContracts.cs`（`IFloatingWindowApi`/`IFloatingWindowSurface` 预览成员）、`FloatingPresentation.cs`（`IFloatingDetachSource` 预览申请）、`TaskDetachSource.cs`（转发）、`FloatingWindowCoordinator.cs`（预览转发；`OpenCoreAsync` 用落点覆盖旧 `MonitorId/X/Y`）、`FloatingSurface.cs`（幽灵窗、主题描边、点击穿透、`Freeze/Dispose` 清理）、`FloatingScreen.cs`（`Placement` 共享夹取）、`TaskPeekView.xaml.cs`（`PreviewRequested/PreviewMoved/PreviewEnded` 与 `PointToScreen` hint）、`TaskPeekViewFactory.cs`（接线与清理）、`FloatingWindowInput.cs`（`CanStartHeaderDrag`、阈值后 `ReleaseCapture` + `WM_NCLBUTTONDOWN`/`HTCAPTION` 复用系统标题栏移动循环、点击抑制）。测试：`FloatingTests.cs`、`FloatingWindowsTests.cs`、`PresenceTests.cs`、`FloatingDragTests.cs`（新增 `Floating_header_buttons_move_on_drag_and_still_click_on_short_press`）。
- 模块边界：Tasks UI 只通过公开 `IFloatingDetachSource` 提交预览；Floating Windows 不取得任务 DTO；预览不写存储。无公开类型新增，`build/public-api.txt` 不变；无设置、数据库、配置或模块装配迁移，设置维持 98 项（16 普通/82 高级）。
- 验证：Release 构建 0 警告/0 错误；非桌面自动化 177/177 通过（WPF 72/72）。新增 `artifacts/ui-preview/floating-placement-preview.png` 显示幽灵窗样式；真实指针 Smoke 通过：`Floating_header_buttons_move_on_drag_and_still_click_on_short_press`（从 `FloatingTaskState`/`FloatingTaskExpand` 拖动移动窗口、完成状态与紧凑高度不变、阈值内短按仍展开），`Floating_real_pointer_moves_resizes_clicks_and_restores_saved_bounds` 连续 2 次通过（移动、缩放、按钮短按、重启恢复与关闭）。真实指针拖出速览、100–200% DPI、多屏与高对比度仍待独立桌面验收。
- 兼容：窗口记录结构不变，旧记录按原字段读取；关闭后再拖出会使用新落点，尺寸/模式/置顶沿用旧记录。
- 本机产物（2026-11-18）：自包含目录 `artifacts/local-run`，`Mosaic.Desktop.exe` ProductVersion 0.11.0；MSI `artifacts/installer/Mosaic-0.11.0-win-x64.msi`，51,961,988 字节，ProductVersion `0.11.0`，SHA-256 `B52BBE79D5A95DB95B2D5E8FF055752C83B931B7D3C47217244C4680C89DB435`，校验文件 `artifacts/installer/SHA256SUMS-0.11.0.txt`。MSI 数据库回读确认桌面快捷方式归属 `Application` 且工作目录为 `INSTALLFOLDER`；打包使用 `SuppressValidation`（本机无 pwsh，按 `build/package.ps1` 步骤复现），真实安装、升级与 ICE 矩阵未在本轮执行。

## 0.10.2：速览整行拖出与筛选视觉（2026-11-18）

- 根因位置：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml:87`（0.10.1 基线）四个鼠标处理器只绑在详情 Button 上，完成按钮和两列间约 4 DIP 留白不属于可拖出元素；`TaskPeekView.xaml.cs:42–47`（`StartGesture`）只接受 `Button` 起点。`DragHintAdorner` 只显示跟随指针的文字，拖动时任务标签本身没有标记。筛选视觉位于 `TaskPeekView.xaml:51–58` 与 `:14–43`：摘要与筛选放在 `WrapPanel`，单选按钮使用透明底与临时边框，选中态和间距不统一。
- 改动位置：`TaskPeekView.xaml` 把 `PreviewMouseLeftButtonDown/Move/Up`、`LostMouseCapture` 移到带 `Background="Transparent"` 的 `TaskRow`，`TaskCard` 只保留 `Click` 与右键菜单；筛选区改为“摘要 + 轨道”的 `Grid`，`PeekFilterButton` 重写为轨道内药丸模板并新增 `FilterOptions`。`TaskPeekView.xaml.cs`：`_candidate` 改为 `FrameworkElement`，`StartGesture/ActivateDrag/EndGesture/FinishDrag/CancelDrag` 使用行容器，`CaptureLost` 通过 `ReferenceEquals(e.OriginalSource, _candidate)` 忽略子按钮交还捕获的冒泡，`CompleteTask` 增加 `_suppressClick` 保护。`DragHintAdorner.cs`：新增整标签虚线 `Rectangle` 与主题化提示。`TaskPeekViewFactory.cs`：筛选遍历与焦点回退改用 `FilterOptions.Children`。`tests/Mosaic.Wpf.Tests/PresenceTests.cs`：整标签路由覆盖标题、状态文字、任务卡片、完成按钮，筛选仍不建立候选；新增窄宽度布局回归。
- 模块边界：拖出仍由 Tasks UI 通过公开 `IFloatingDetachSource` 申请；Floating Windows、Presence、Storage、Tasks Core 未改；无公开 API 变化；无设置、数据库、配置或模块装配迁移，设置维持 98 项（16 普通/82 高级）。
- 验证：Release 构建 0 警告/0 错误；非桌面自动化 173/173 通过（WPF 69/69）。`presence-popup-light/dark`、`presence-tasks-readonly` 预览图已重拍，新增 `peek-drag-frame.png` 显示整标签虚线框；窄宽度回归断言 236 DIP 内容宽下筛选完整位于视图内。真实指针拖出、100–200% DPI、多屏与高对比度仍待独立桌面验收，不以路由测试代替。
- 构建环境修复：此前锁文件被带 `-r win-x64` 的还原写入了 RID 目标，未声明 `RuntimeIdentifiers` 的项目在 `dotnet restore Mosaic.sln --locked-mode` 时报 NU1004。本轮为这些项目重新生成非 RID 锁文件，仅 `Mosaic.Desktop` 与 `Mosaic.Settings.Wpf`（声明 `RuntimeIdentifiers=win-x64`）保留 `win-x64` 目标；锁定解决方案还原、Desktop 自包含还原与 `dotnet publish` 全部通过，产品依赖未变。发布内容指纹用同一算法复核了全部 14 条记录。
- 本机产物（2026-11-18）：自包含目录 `artifacts/local-run`；MSI `artifacts/installer/Mosaic-0.10.2-win-x64.msi`，ProductVersion 0.10.2，校验值见 `artifacts/installer/SHA256SUMS-0.10.2.txt`。MSI 数据库回读确认桌面快捷方式归属 `Application` 且工作目录为 `INSTALLFOLDER`。打包使用 `SuppressValidation`，真实安装、升级与 ICE 矩阵未在本轮执行。

## 0.10.1：整卡拖出捕获修复与速览筛选样式（2026-11-17）

- 根因位置：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs:45–77`（`MoveGesture`/`EndGesture`/`CaptureLost`）超过拖动阈值后打开 `Popup`，`Popup` 打开时抢占鼠标捕获，按钮收到 `LostMouseCapture` 后被旧逻辑当作失败取消；快速拖动时按钮收不到 `MouseLeftButtonUp`，缺少终局兜底。视觉问题位于 `TaskPeekView.xaml:14–43`（`PeekFilterButton`）：分段控件缺少统一高度与清晰选中边界，弱化可读性。
- 改动位置：新增 `src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/DragHintAdorner.cs`，把拖动提示改为 `AdornerLayer` 上的只呈现组件；`TaskPeekView.xaml.cs` 新增 `ActivateDrag`，`EndGesture`/`CaptureLost` 收敛到 `FinishDrag`，按最后指针位置申请拖出或取消，并挂接/清理 Esc 处理器；`TaskPeekView.xaml` 优化筛选分段控件高度、光标、焦点与选中边界；`tests/Mosaic.Wpf.Tests/PresenceTests.cs` 新增“拖动提示不抢捕获、捕获丢失仍按窗口外位置终局”回归。版本同步到 `Directory.Build.props`、`build/package.ps1`、`build/installer/Mosaic.Installer.wixproj`。
- 模块边界：Tasks UI 继续通过公开 `IFloatingDetachSource` 申请拖出；Presence/Floating Windows/Storage 不改；无设置、数据库、配置或装配迁移。
- 验证：Release 构建 0 警告/0 错误；非桌面自动化 172/172 通过（WPF 68/68）。真实指针拖出、DPI 与多屏矩阵仍待独立桌面验收。

## 0.10.0：速览完成切换、筛选与整卡拖动起点（2026-11-17）

- 用户要求：任务速览左侧圆圈可标记完成；面板支持全部/未完成/已完成筛选且默认全部；任务拖拽框覆盖整个任务标签，起点在任务按钮内也可拖出，点击标签详情功能不变。
- 根因/能力缺口位置：`TaskPeekView.xaml:31–48` 左侧 `Path` 只是状态图形且嵌在整行详情 Button 内；`TaskPeekRow` 不保存 `Revision`，`TaskPeekViewModel` 未接线 `ITasksApi.SetCompletedAsync`；列表固定使用 `CompanionViewOptions.Filter`，无会话内筛选；`TaskPeekView.xaml.cs:20–37` 手势绑定整行 Button，当前理论上已覆盖标题起点，但加入完成按钮/筛选后需统一排除并建立回归。
- 改动位置：`PresentationContracts.cs` 扩展 `CompanionViewOptions`；`PresenceSettings.cs`/`PresenceLocalization.cs`/`PresenceSurface.cs` 新增并传递 2 个普通展示开关；`TaskPeekView.xaml/.cs` 拆分完成按钮、筛选分段控件并保持整标签拖动手势；`TaskPeekViewFactory.cs` 增加 Revision、完成操作、筛选状态与公开 API 申请；Tasks 中英文资源补充文案。详见 [速览完成切换与筛选计划](peek-completion-filter-plan.md)。
- 职责边界：Tasks Core 拥有完成语义；Tasks UI 只通过公开 API 写入；Presence 只读设置并通过通用选项传参；Floating Windows、Storage 与主任务页不改。无数据库、模块装配或配置迁移。
- 本轮仅修改规划文档，未改源码、设置计数、测试或发布产物；真实指针、读屏、DPI 与桌面 Smoke 均待实施后执行。

- 实现：`TaskPeekViewModel.CompleteAsync/SubmitCompletionAsync` 按任务 ID 防重，携带行 Revision 提交公开 `SetCompletedAsync`，等待真实结果后重查；冲突重查后使用新版本，错误可关闭并在六秒后收起。`SelectFilterAsync/InitializeAsync/LoadAsync` 保存会话筛选、取消旧请求并用代次隔离迟到结果。`CloseAsync` 取消会话请求，交互状态在提交期间保持面板。
- 界面位置：`TaskPeekView.xaml/.cs` 的 `CompleteTask/FilterClicked` 与卡片 `StartGesture/MoveGesture/EndGesture` 使用独立同级控件，标题、状态和卡片留白仍路由到详情卡片；完成区和筛选不会进入拖出手势。行忙碌显示省略符、禁用并更新无障碍名称；列表刷新恢复同任务操作焦点或筛选入口。Tasks `Localization/zh.xaml/en.xaml` 增加两种语言的操作和筛选资源。
- 配置位置：`PresentationContracts.cs.CompanionViewOptions` 增加两个默认 true 的可选字段；`PresenceSettings.Definitions/DisplayNames`、`PresenceLocalization.Dictionary` 和 `PresenceSurface.ShowPopupAsync` 定义、翻译并传入选项。完整配置 98 项（16 普通/82 高级）；Presence 停用后定义撤销。`build/public-api.txt` 按导出类型登记，本轮无新增导出类型，原 `CompanionViewOptions` 条目继续有效。
- 测试位置：`tests/Mosaic.Wpf.Tests/PresenceTests.cs` 新增真实公开 API 替身的完成/冲突/失败/取消/快速筛选用例、整卡路由与控件排除回归，并验证两个设置预览与放弃的选项往返；新增 `tests/Mosaic.Desktop.SmokeTests/PeekCompletionTests.cs` 验证实际发布进程完成、恢复、筛选和数据库 Revision 递增。路由自动化不能代替物理指针拖出验收。
- 发布与文档位置：`Directory.Build.props`、`build/package.ps1`、`build/installer/Mosaic.Installer.wixproj`、生产项目 `packages.lock.json` 同步 0.10.0；`build/release-content.json`、`published-release-index.json`、`release-manifest.json` 同源生成更新简要和 `docs/releases/v0.10.0.md`；README、总计划阶段 16、设置计划第 17 节及专项计划同步。逐文件清单见 `docs/releases/v0.10.0-files.md`。
- 迁移与旧安装影响：无数据库结构、配置结构或模块装配变化，无需迁移；旧设置未存两个开关时由模块默认 true 补足。既有用户自选初始筛选不变；会话筛选不会写入设置、任务数据库或主页面筛选，浮窗布局记录保留。预览放弃恢复已保存值，禁用模块不删除历史值。
- 待独立验收：物理指针从标题/状态/内边距拖出及控件排除、真实键盘焦点/读屏、100–200% DPI、多屏与系统高对比度矩阵、干净 Windows 升级安装和 MSI ICE；这些不以自动化路由测试代替。

- 最终验证（2026-11-17）：Release 构建 0 警告/0 错误；非桌面测试 171/171（WPF 67/67）通过；最终自包含 EXE 的速览桌面 Smoke 1/1 通过，完成/恢复后按筛选移除，重开数据库确认原任务 Revision 1→3、最终未完成。MSI 打包与桌面快捷方式数据库检查通过。安装包 51,974,276 字节，SHA-256 `466689E2640F916D3AB7A725D81C4482EB0A4601DA76184160F1322C4A787963`，版本 0.10.0；EXE FileVersion 0.10.0.0。MSI 使用 `-SkipValidation`，本轮没有执行真实安装升级或完整桌面矩阵。
- 桌面验证补充：`TaskPeekView.xaml` 筛选改用 Checked 事件，保证 UIA SelectionItem、键盘与鼠标选择一致；测试夹具通过公开设置仓储关闭更新简要任务和自动收起，避免误选另一任务及面板自动消失。测试不改变真实工作空间。

### 已确认待修复：整卡拖出在真实指针下失效（0.10.1 规划）

- 用户反馈：从任务按钮内部按下无法拖出小窗；本轮仅定稿规划，未改源码、测试或发布产物，设置维持 98 项。
- 根因位置：`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskPeekView.xaml.cs:45–63`（`MoveGesture`）超过阈值后打开拖动提示 `Popup`，Popup 打开时抢占鼠标捕获；`:77`（`CaptureLost`）把这次捕获转移当作失败调用 `CancelDrag()`，导致拖动在激活瞬间被取消。快速拖动时按钮收不到 `MouseLeftButtonUp`，`:65–77`（`EndGesture`）不执行，也缺少兜底。既有 `tests/Mosaic.Wpf.Tests/PresenceTests.cs:90–115` 只验证按下路由、不跨阈值，因此测试通过而真实拖动失败。
- 拟改动位置：`TaskPeekView.xaml.cs` 改用不抢捕获的 `DragHintAdorner`（新增内部类型）呈现提示，`CaptureLost` 改为按最后指针位置执行终局判定，`EndGesture`/兜底共用 `FinishDrag`，面板外判定改为窗口坐标并补 Esc 取消；新增 WPF 捕获/终局回归与真实指针拖出桌面 Smoke。详见 [专项计划](peek-card-drag-fix-plan.md)。

## 0.9.1：安装器自动创建桌面快捷方式（2026-11-17）

- 根因位置：`build/installer/Package.wxs:10-14` 只声明了 `ProgramMenuFolder` 下的开始菜单快捷方式，没有受 MSI 组件管理的 `DesktopFolder` 入口，因此安装成功后不会自动创建用户桌面快捷方式；`build/test-install.ps1` 也只验证安装、升级和卸载，没有验证桌面入口及其目标。应用运行时创建快捷方式会重复安装器职责，所以修复留在安装器边界内。
- 安装器改动位置：`build/installer/Package.wxs` 新增 `DesktopFolder/DesktopShortcut` 组件、`DesktopMosaic` 快捷方式和稳定组件 GUID，并把 `DesktopShortcut` 加入 `Application` Feature；目标为 `[INSTALLFOLDER]Mosaic.Desktop.exe`，工作目录为 `INSTALLFOLDER`。升级、修复和卸载由 Windows Installer 组件生命周期处理。
- 验证工具改动位置：新增 `build/test-package-shortcuts.ps1`，通过 Windows Installer 数据库回读 `Shortcut` 与 `FeatureComponents`，断言唯一桌面快捷方式、目标、工作目录和 `Application` Feature 归属；`build/package.ps1` 在复制最终 MSI 后调用该检查；`build/test-install.ps1` 的 `Assert-MosaicDesktopShortcut` 验证真实安装和升级，并在卸载后断言快捷方式删除。升级矩阵首次运行又暴露出旧版基础包没有桌面入口时被错误断言的问题；新增 `Test-MosaicPackageDesktopShortcut` 先回读基础包是否声明该入口，仅对具备功能的基线检查安装结果，升级到 0.9.1 后仍无条件验证入口存在。
- 套件稳定性根因与改动位置：`tests/Mosaic.Wpf.Tests/PreviewCaptureTests.cs:28` 在独立 STA 测试线程创建应用级 `Application`，线程结束后 `Application.Current.Dispatcher` 仍指向已停止的 Dispatcher；`src/Mosaic.Shell.Wpf/ShellSettingSource.cs:115-116` 的 `ShellSettingsApplier.ApplyAsync` 又优先向该 Dispatcher 投递设置预览，后续 `ResponsiveLayoutTests.Shell_consumes_window_and_layout_settings_from_the_settings_api` 会跨线程等待至取消。现改为优先使用已附着的 `ShellWindow.Dispatcher`，只在没有窗口时回退到应用级或当前 Dispatcher；产品调用链和设置语义不变，完整 WPF 套件复跑通过。
- 桌面 Smoke 稳定性根因与改动位置：`tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs:124、129、142` 在驻留速览顶层窗口出现后立即用 `Id(...)!` 点击触发、恢复或更多按钮，但 WPF Automation 树中子控件晚于顶层窗口生成，升级后第二次运行会得到空引用。现通过既有 `Until` 等待每个具体控件进入自动化树后再点击；重跑升级前后桌面 Smoke 各 24/24 通过。
- 版本与发布位置：`Directory.Build.props`、`build/installer/Mosaic.Installer.wixproj`、`build/package.ps1` 更新为 `0.9.1`；全部项目锁文件同步项目依赖版本；`build/release-content.json`、`build/release-manifest.json`、`build/published-release-index.json`、`README.md`、`docs/implementation-plan.md`、`docs/installer-location-plan.md`、`docs/releases/v0.9.1.md` 和 `docs/releases/v0.9.1-files.md` 同批交付。逐文件定位见 [0.9.1 文件清单](releases/v0.9.1-files.md)。
- 自动化验证：Release 构建 0 警告、0 错误；非桌面测试 166/166 通过，其中 WPF 62/62；完整打包流程通过，WiX 0 警告、0 错误，打包后 MSI 数据库检查通过。完整测试首次运行暴露出 `PresenceTests.Popup_chrome_renders_in_both_themes_and_compacts_at_small_heights` 的时序竞态：测试未关闭产品默认的 250 ms 自动收起，负载较高时弹窗在截图前关闭；测试现通过公开 `ISettingsApi.ProposeChangesAsync` 将 `desktop.popup.autoHide` 设为 `false`。远端基线克隆复跑又暴露出上述跨 Dispatcher 等待；修复后 WPF 62/62 和全量 166/166 通过。自包含程序 `artifacts/local-run/Mosaic.Desktop.exe` 的 FileVersion 为 `0.9.1.0`、ProductVersion 为 `0.9.1+552fba51718f71fd083cff1c535ec099a07b956e`。
- 真实安装链验证：当前 `Mosaic-0.9.1-win-x64.msi` 的全新安装返回 0，桌面 `Mosaic.lnk` 和开始菜单入口均存在，桌面目标精确为 `%LOCALAPPDATA%\Programs\Mosaic\Mosaic.Desktop.exe`，工作目录为对应安装目录；卸载返回 0，桌面与开始菜单快捷方式、安装二进制均删除，`%LOCALAPPDATA%\Mosaic` 用户数据仍存在。随后完成 0.9.0→0.9.1 实机升级：升级前后桌面 Smoke 各 24/24 通过，升级后快捷方式目标正确，升级不改变用户数据哨兵，卸载清理入口和程序文件。测试中唯一不含的是依赖桌面前台焦点和物理指针时序的 `Floating_real_pointer_moves_resizes_clicks_and_restores_saved_bounds`，它仍归入独立交互式桌面验收。验证安装已卸载，当前没有 Mosaic 进程或安装残留，本地只保留 `artifacts/local-run` 一个完整程序目录。
- 安装产物：`artifacts/installer/Mosaic-0.9.1-win-x64.msi`，51,953,796 字节，ProductVersion `0.9.1`，SHA-256 `620F7FDF5245B84DE35ED776B495742029C15F4280BFAB5C6A7BFE78CFD4A4D7`；校验文件为 `artifacts/installer/SHA256SUMS-0.9.1.txt`。
- 迁移与旧安装影响：没有配置、数据库、窗口记录或用户数据迁移。已有安装可直接升级，桌面和开始菜单入口指向安装后的最终目录；卸载只删除 MSI 登记的快捷方式、程序文件和空目录，保留 `%LOCALAPPDATA%\Mosaic`。
- 未完成验收：MSI 以 `-SkipValidation` 生成，ICE01–ICE105 尚未执行；自定义安装位置、跨位置替换、取消/回滚、负载外文件保留、干净 Windows 虚拟机和包含真实鼠标拖拽焦点的完整交互式桌面 Smoke 仍待独立环境验收。这些边界不阻塞当前默认位置功能的自动化、全新安装和 0.9.0→0.9.1 实机升级验证。

## 0.9.0：悬浮任务窗统一拖动命中与紧凑布局（2026-11-17）

- 根因位置：`src/Modules/FloatingWindows/Mosaic.FloatingWindows.Wpf/FloatingSurface.cs:81` 的 12 DIP 内边距和 `Create` 中独立工具栏只把 `DragMove` 绑定到 `⠿` TextBlock，顶部空白和边线没有移动入口；`FloatingScreen.NaturalHeight` 又强制最少 88 DIP 并在测量后额外加高，`TaskFloatingContentSession` 的固定最大标题高度与空反馈占位进一步放大空白。
- 改动位置：新增 `FloatingWindowInput.HitTest/Hook` 集中处理原生命中、圆角外轮廓、工具/控件祖先排除、顶部移动及下部缩放；`FloatingSurface.Create/Layout/QueueBounds/DestroyAsync` 改为工具覆盖内容首行、统一配置密度并沿用既有移动与保存管线；`FloatingScreen.NaturalHeight` 改为按实际内容测量，保留紧凑最小可操作高度。
- Tasks 模块改动位置：`TaskFloatingContentSession.Header/ConfigureLayout/Render` 通过公开 `FloatingContentLayoutOptions` 为工具保留首行空间，改用最小高度与自然换行，并折叠空反馈；`TaskPeekDetailsView.ConfigureDensity/AddText` 收紧展开正文间距且仅在紧凑浮窗隐藏空备注占位。窗口模块不读取 Tasks DTO，标题和任务字段仍由 Tasks UI 渲染。
- 设置改动位置：`FloatingSettings.Definitions`、`FloatingLocalization.Dictionary` 增加 `floating.dragBandHeight`、`floating.cardPadding`、`floating.contentGap` 三项高级 Immediate/Reversible 设置及中英文资源；设置总数由 93 增至 96（14 普通 / 82 高级）。
- 构建/发布位置：`Directory.Build.props`、`build/package.ps1`、`build/installer/Mosaic.Installer.wixproj` 版本同步 0.9.0；`build/public-api.txt` 登记 `FloatingContentLayoutOptions`；`release-content.json`、`published-release-index.json`、`release-manifest.json` 与 `docs/releases/v0.9.0.md` 同源生成。完整逐文件定位见 [0.9.0 文件清单](releases/v0.9.0-files.md)。
- 测试位置：`tests/Mosaic.Wpf.Tests/FloatingWindowsTests.cs` 覆盖 8/12/20 DIP 命中和 4/8/16 DIP 内边距、透明圆角外区域、按钮中心与边缘、禁用按钮、内容控件、顶部/侧边/底边、紧凑与展开、长标题自然增高、空反馈折叠、预览放弃不改持久化尺寸；`tests/Mosaic.Desktop.SmokeTests/FloatingDragTests.cs` 使用真实 `SendInput` 验证顶边及顶部圆角移动、侧边缩放、工具区排除和重启后位置恢复。
- 迁移与旧安装影响：没有数据库结构、窗口记录或配置文件迁移；新增设置缺失时使用默认值，旧窗口记录和保存方式原样兼容。用户数据目录与旧安装位置不变，正常升级后回到已存在的悬浮窗即使用新命中与密度。
- 本机验证：Release 构建 0 警告 / 0 错误；非桌面测试 166 项全部通过，其中 WPF 62 项；悬浮窗正常恢复、独立关闭和正常退出重启 Smoke 通过。真实指针测试在修正测试自身的前置窗口顺序后，产品路径已通过移动、缩放、按钮命中、重启恢复，最终“关闭按钮”失败已由既有独立 Smoke 单独覆盖。自动审批对再次发送系统鼠标输入的复核连续超时，自动化记录保留该限制；100–200% DPI、多屏、高对比度实机矩阵和干净 Windows 安装升级仍待独立环境验收。
- 本机产物：唯一可运行目录 `artifacts/local-run/Mosaic.Desktop.exe`，FileVersion 0.9.0.0、ProductVersion 0.9.0；隔离数据目录启动 6 秒后仍是响应进程。MSI 为 `artifacts/installer/Mosaic-0.9.0-win-x64.msi`，51,949,676 字节，ProductVersion 0.9.0，SHA-256 `947410B72690EC8AB52878653BD2E5E6AB3DE65C2FCA213578D097AC9B395BCF`，校验文件 `artifacts/installer/SHA256SUMS-0.9.0.txt`。

## 0.8.0：独立悬浮任务窗口（2026-11-17）

- 根因/能力缺口位置：TaskPeekView/TaskPeekViewModel 只有点击详情；ICompanionViewFactory 只有单一速览会话；PresenceSurface 只拥有驻留入口和速览窗口；TasksModule 没有专用公开提交变更源；原存储没有窗口身份与恢复状态。上述职责不能通过复制主任务页或读取任务内部对象实现。
- 全部逐文件定位见 [0.8.0文件清单](releases/v0.8.0-files.md)。
- 新增文件/改动位置：Modules/FloatingWindows 下5个项目及各csproj。Contracts/FloatingContracts.cs 定义不透明引用、窗口记录与公开申请/Surface；Persistence.Contracts/IFloatingWindowRepository.cs 定义原子窗口记录；Core/FloatingWindowCoordinator.cs 串行处理开关去重、保存/失败、恢复和退出；Sqlite/FloatingWindowsSqliteModule.cs 独占窗口表、参数化SQL、乐观版本和独立V1迁移；Wpf/FloatingWindowsModule.cs/FloatingSurface.cs/FloatingScreen.cs/FloatingSettings.cs/FloatingLocalization.cs 负责注册、窗口与会话释放、移动缩放、工作区/DPI、预览与9项设置。
- Tasks改动位置：新增 Contracts/TaskChanges.cs 和 Core/Internal/TaskChangeSource.cs；TaskService 在成功提交后发布事实，TasksModule 暴露 tasks.changes@1。Tasks.Wpf/TasksUiModule.cs、Internal/TaskDetachSource.cs、TaskFloatingContentFactory.cs 提供公开申请来源和独立任务内容会话；TaskPeekView.xaml/.cs、TaskPeekViewFactory.cs 处理系统阈值拖动/捕获/取消、菜单、反馈与交互租约；TaskPeekDetailsView.cs 复用只读正文，Themes/Floating.xaml 复用主题语义颜色渲染状态按钮。TaskViewFactory.cs/TasksViewModel.cs 订阅合并刷新且保留未保存编辑；Tasks.Wpf/Localization/en.xaml、zh.xaml 新增分离入口文字。
- 通用/宿主改动位置：Presentation.Contracts/FloatingPresentation.cs 定义内容工厂、申请来源和辅助交互协议；PresenceSurface.cs 只消费通用保活/取消与菜单生命周期；Settings.Contracts/SettingsContracts.cs、Settings.Wpf/Internal/SettingsViewModel.cs 增加动态分组；Desktop/BuiltInModuleCatalog.cs、Bootstrapper.cs、ShutdownCoordinator.cs 完成公开装配、启动恢复与接受退出前排空（失败可重试/取消/明确不保存退出）；AppConfiguration.cs/appsettings.json 增加 floating-windows-v1 生效层迁移。
- 构建/发布位置：Directory.Build.props、WiX、package.ps1、解决方案、各ProjectReference、project-boundaries.json/public-api.txt 与锁文件同步。release-content.json/published-release-index.json/release-manifest.json 和 v0.8.0 发布说明同源登记全部新增/改进/兼容项。当前26个生产程序集，12个测试项目；93项设置、14普通/79高级、6个分区。
- 测试位置：新增 FloatingWindows.Tests/FloatingTests.cs（并发去重、失败重试、关闭/退出区别、恢复上限、缺失/暂时失败、模式尺寸隔离、SQLite往返/版本/损坏）；Wpf.Tests/FloatingWindowsTests.cs（独立窗去重、状态写入、模式/置顶、恢复、保存失败、预览及移动不覆盖偏好、关闭自动恢复）；Tasks.Core.Tests/CreateOnceTests.cs（成功提交事实及观察者隔离）；Architecture.Tests/BoundaryTests.cs 检查新增项目边界；Desktop.SmokeTests/DesktopTests.cs 增加新能力迁移覆盖组合与停用/空配置。
- 旧安装影响：有任务UI/任务/设置/SQLite完整依赖时，旧配置一次性接入两个窗口模块与能力绑定，顶层或settings.host覆盖层同批写回并备份。已迁移后主动停用不会加回；空模块不启用数据库。独立 floating-windows-sqlite V1 迁移复用Storage事务与备份，保留任务表；同一数据目录的恢复记录不存任务标题/备注副本。
- 当前验证：Release构建0警告/0错误；162项非桌面自动化通过（含WPF58项），14项配置迁移与1项真实进程恢复/退出/单独关闭往返通过；浅色/深色/高对比度预览已生成并检查；实机负坐标多屏、100/125/150/200%DPI与热插拔、系统会话、完整鼠标捕获矩阵、干净Windows安装升级/ICE仍待独立验收。下文历史0.7测试不作为小窗验收证据。


## 已确认待实施：独立置顶任务小窗（2026-11-17）

- 用户已确认紧凑标题/状态、点击展开备注/时间、可调整大小、可复制与切换完成状态，并在重启恢复未关闭小窗的位置/大小；见 [完整计划](floating-task-windows-plan.md)。当前源码仍为 0.7.0，小窗尚未实施。
- 能力缺口位置：`TaskPeekView`/`TaskPeekViewModel` 只有点击详情；`ICompanionViewFactory` 只创建速览会话；`PresenceSurface` 只有边缘入口与单个速览弹窗生命周期；Tasks 无公开专用变更源，存储无独立窗口恢复记录。本期方案分别由公开拖出申请、独立内容工厂、窗口管理模块、Tasks 变更源与窗口存储适配器负责。
- 文档改动位置：新增 `docs/floating-task-windows-plan.md`；同步 `docs/implementation-plan.md` 阶段 13、`docs/settings-plan.md` 第 15 节、`docs/peek-task-details-plan.md` 的后续规则、`docs/desktop-presence-plan.md` 的边界入口、README 与本记录。本轮未改程序、数据库或发布产物；新功能测试未执行。

## 0.7.0：速览任务按钮与只读详情（2026-11-16）

- 根因位置：Tasks.Wpf/Internal/TaskPeekView.xaml 使用不可操作 Border；TaskPeekRow 不保留 TaskId；TaskPeekViewModel 只有列表状态；PresenceSurface.ShowPopupAsync 的 PreviewKeyDown 先行吞掉 Esc 与方向键，无法容纳内容内部返回与正文阅读。
- 改动位置：Tasks.Wpf/Internal/TaskPeekView.xaml/.cs 改为 Button 并转发稳定行身份；TaskPeekViewFactory.cs 的 TaskPeekRow/TaskPeekViewModel 增加 TaskId、详情查询、取消与请求代次、返回及滚动/焦点恢复；新增 TaskPeekDetailsView.cs 独占只读任务字段和可复制备注呈现；Tasks.Wpf/Localization/zh.xaml、en.xaml 补详情资源。
- 改动位置：Presentation.Contracts/PresentationContracts.cs 新增可选 ICompanionNavigation；PresenceSurface.cs 消费通用返回协议、移除方向键拦截、协调键盘阅读与外部点击；Presence 不读取任务身份或字段。Tasks Core、SQL、设置定义无需改动，旧配置与任务库无迁移项。
- 验证位置：Wpf.Tests/PresenceTests.cs 新增同名身份、A→B迟到、删除重试、关闭取消测试；public-api.txt 同步新增通用协议。Directory.Build.props、WiX、package.ps1、锁文件、发布内容源/索引/清单、README及计划同步0.7.0。本次发布内容从同源生成更新任务。
- Release构建0警告/0错误；148项非桌面自动化全部通过，含WPF54项。正式本地EXE与MSI已更新为0.7.0；旧0.6.0测试工作空间连续启动两次只有一条0.7.0更新任务，旧任务保留；无需迁移。MSI跳过ICE，完整安装升级验收未执行。完整实机DPI、多屏、鼠标捕获矩阵及干净Windows安装升级仍待独立验收。实际拖出与多置顶任务窗未实施。


## 已确认待实施计划：速览按钮、任务详情与拖出准备（2026-11-16）

- 用户已确认：点击在速览面板内查看只读标题、备注、完成状态与时间，可返回列表；未来拖出成为多个独立置顶任务小窗。本轮仅定稿规划，程序基线为 0.6.0；尚未实现按钮详情或拖出悬浮，见 [专项计划](peek-task-details-plan.md)。
- 当前缺口位置：`TaskPeekView.xaml` 使用不可点击的 Border；`TaskPeekRow` 无稳定 TaskId；`TaskPeekViewModel` 只有列表查询；`PresenceSurface.ShowPopupAsync` 的 PreviewKeyDown 当前会先处理 Esc/上下键，需要与详情返回及正文滚动协调。
- 文档改动位置：新增 `docs/peek-task-details-plan.md`；同步总实施计划阶段 12、旧速览专项计划与驻留计划的后续入口、README 和本记录。新功能的源码测试与视觉验收尚未执行。

## 0.6.0：任务速览与更新简要（2026-11-16）

- 根因位置：PresenceSurface.PositionWindows 把 maxHeight 当实际高度；TaskPeekViewModel.UpdateView 逐项大卡片和 PresencePopupView.xaml 的重操作栏形成空白与视觉负担。Tasks 的 TaskService.CreateAsync / ITaskRepository / SqliteTaskRepository 原本没有可持久化的幂等创建事实。
- 改动位置：PresentationContracts.cs 的 CompanionViewOptions / ICompanionViewSession；Tasks.Wpf/Internal/TaskPeekViewFactory.cs 与新增 TaskPeekView.xaml/.cs；PresenceSurface.cs、PresencePopupView.xaml/.cs、PresenceSettings.cs、PresenceLocalization.cs。容器只测通用 DesiredSize，任务内容仍由 Tasks UI 拥有；更多菜单、提示、Esc、旧会话通知隔离纳入处理。
- 改动位置：Tasks.Contracts/TaskCreateOnce.cs；Tasks.Core 的 TaskService.CreateOnceAsync、GuardedTasksApi、TasksModule；Persistence.Contracts/ITaskRepository；Tasks.Sqlite 的 SqliteTaskRepository、TasksSqliteModule 与 002_CreateTaskReceipts.sql。任务和回执同事务写入，删除任务保留回执，历史回执优先于新限额校验。
- 新增 ReleaseSummary/Mosaic.ReleaseSummary 的 ReleaseManifest、ReleaseSummaryComposer、ReleaseSummaryModule，独占版本范围、正文及启动策略。更新开关为下次启动生效且不预览。Desktop 的 AppConfiguration、BuiltInModuleCatalog、Bootstrapper、appsettings.json、csproj 完成一次性生效层迁移和任务首屏查询前的就绪申请。
- 构建位置：build/release-content.json、published-release-index.json、release-content.ps1、release-manifest.json 为发布内容权威及生成/校验；publish.ps1、package.ps1、WiX、Directory.Build.props、解决方案、项目引用边界、public-api 与锁文件同步 0.6.0。开发构建不会自动写更新任务，正式发布使用 MosaicPublishedBuild 标记。
- 测试位置：新增 Tasks.Core.Tests/CreateOnceTests.cs、Tasks.Contract.Tests/CreateOnceRepositoryTests.cs、ReleaseSummary.Tests；更新 Shared/MemoryTaskRepository.cs、Wpf.Tests/PresenceTests.cs 和 Desktop.SmokeTests/DesktopTests.cs。146 项非桌面自动化通过（WPF 52）；Release 构建 0 警告/0 错误。正式发布包两次启动独立工作空间，确认只有一条未完成更新任务和一条回执；8 项旧配置迁移黑盒通过。
- 原配置自动补入 release-summary-v1（仅任务和设置能力已启用时接入），成功后原子写回并备份；已迁移后用户停用不会加回。任务回执表使用 Tasks V2 迁移及既有备份机制，不覆盖用户任务数据。
- 产物：唯一完整程序 artifacts/local-run/Mosaic.Desktop.exe；Mosaic-0.6.0-win-x64.msi 为 51,905,472 字节，SHA-256 C307F5CB184CB302A5278231F67C05BDD9132CF8798B18840B1C2AA905289B7D。MSI 以 SkipValidation 构建。
- 未完成验收：完整桌面 Smoke、所有0/1/3/12/超限截图矩阵、100–200% DPI、多屏/高对比度实机、MSI ICE 与干净 Windows 安装升级。生成安装包不等于完成上述验收。


## 待实施计划：速览 UI 与更新简要任务（2026-11-16）

- 本轮仅更新规划文档，未改程序或用户数据，也未创建实际更新任务。完整需求、默认行为、模块接口与验收见 [专项计划](peek-and-update-summary-plan.md)，总计划新增阶段 11。
- 用户已确认正文范围为“本次更新的全部新增、改进和修复”；专项计划第 1、4、7 节与总计划阶段 11 已同步定稿，移除全版本功能清单的备选要求。功能仍处于待实施状态。
- 视觉原因位置：`PresenceSurface.PositionWindows` 将最大高度直接用作实际高度；`TaskPeekViewModel.UpdateView` 的逐项大卡片，以及 `PresencePopupView.xaml` 的大按钮和辅助文字造成层级偏重。拟分别改为自然高度、紧凑任务行与轻量操作区。
- 更新任务能力缺口位置：`ITasksApi` / `TaskService.CreateAsync` 只有普通创建，`ITaskRepository` / `SqliteTaskRepository` 没有原子创建回执；计划新增公开通用幂等能力，独立更新模块负责发布正文与触发。
- 文档改动位置：新增 `docs/peek-and-update-summary-plan.md`；同步 `docs/implementation-plan.md` 阶段 11、`docs/settings-plan.md` 第 14 节、`docs/desktop-presence-plan.md` 后续规则、`docs/architecture-principles.md` 第 13 节，以及 README 文档入口。本节只登记计划，不复用历史测试计数证明新功能已完成。

## 当前增量：0.5.1 旧配置迁移收口（2026-11-16）

- 根因位置：`AppConfiguration.Load` 在完整校验前直接改写旧配置，且没有保存一次性迁移状态；早期修复又在迁移前解析旧依赖图，旧 `tasks-ui` 因缺少 `presence.tasks-view@1` 被提前拒绝。用户关闭 Presence 后仍会被每次启动补回，设置覆盖存在时还可能只更新 modules 或 providers 的单层。修复前逻辑没有覆盖顶层与 `settings.host.*` 的完整生效组合。
- 改动位置：`src/Mosaic.Desktop/AppConfiguration.cs` 的解析、校验、Presence 迁移和原子写回；新增 `src/Mosaic.Desktop/HostConfigurationValidator.cs`；`src/Mosaic.Desktop/HostSettingSource.cs` 改用共享语义校验；`src/Mosaic.Desktop/appsettings.json` 增加 `desktop-presence-v1`；`tests/Mosaic.Desktop.SmokeTests/DesktopTests.cs` 新增迁移黑盒场景；版本与说明同步到 `Directory.Build.props`、`build/package.ps1`、WiX 默认值、README 和发布文档。
- 迁移先校验顶层及实际生效的模块/provider，再修改配置；成功后原子写入 `appliedMigrations: ["desktop-presence-v1"]`，原文件保存为 `Config/appsettings.json.pre-migration`。非法配置不写盘，已迁移后主动禁用 Presence 不会被重新启用。
- Release 构建 0 警告、0 错误；非桌面自动化 121/121 通过（含 WPF 49/49）；迁移黑盒测试 6/6 通过，覆盖顶层配置、模块/provider 联合及单独覆盖、一次性和非法配置保留。
- 已生成自包含 `artifacts/local-run` 和 `Mosaic-0.5.1-win-x64.msi`（51,864,156 字节，SHA-256 `528BF03F56E8544AC1FD508E8FBD5B3A03A49E50D569D93156D4DFC85AB2EF2F`）；MSI 数据库回读 `ProductVersion=0.5.1`。使用真实 `%LOCALAPPDATA%\Mosaic` 旧配置启动后，迁移成功且进程持续运行。Windows Installer 服务不可用，ICE01–ICE105 未执行，安装/升级仍待独立验收。
- 完整交互式桌面 Smoke、干净 Windows 安装与 0.5.0→0.5.1 升级、多屏/DPI、系统会话结束及旧 0.1 配置完整迁移矩阵仍需独立验收。

## 历史增量：0.5.0 设置与架构收口（2026-11-16）

- Release 生产依赖链构建通过：0 警告、0 错误。非桌面自动化 121/121 通过：架构 4、Presence.Core 14、Kernel 5、Settings.Core 25、Settings.Sqlite 1、Storage.Sqlite 12、Tasks.Contract 2、Tasks.Core 9、WPF 49。
- 宿主设置默认值从 settings-core 移入 Desktop；canonical 模块/provider 默认由 `BuiltInModuleCatalog` 推导，旧顶层配置作为基线读取，`settings` 段只覆盖用户显式保存的项。
- ProviderMap/ModuleList 使用共享解析、格式化和等价比较规则；设置页、settings-core 与宿主语义校验不再维护三套正则。跨存储提交增加 pending journal，启动激活时幂等恢复，失败进入配置诊断。
- 设置定义自带本地化键、描述和选项；Settings.Wpf 删除模块 ID 特判。导航改由 `LabelKey` 驱动，Shell 不再识别业务 ViewId 或中文标题。
- 修复导航重建时错误地把 Label 当资源键、把 ViewId 当回退文本的问题；中英文模式下的导航标签、侧栏提示与页面标题均有 WPF 自动化覆盖。
- 任务紧凑页改为真实单栏；宽屏双栏、单一 ViewModel、草稿与离页守卫保持不变。
- 本记录只证明自动化与 Release 构建。完整交互式桌面 Smoke、多屏、100–200% DPI、系统会话、结构化模块/provider 编辑、重启 pending 数量和干净 Windows 安装升级仍需验收。

## 当前增量：0.3.0 后台驻留（2026-11-16）

本节为当前工作树状态；下文 0.1/0.2 的测试计数和安装包哈希均为历史记录。

- 20 个生产程序集、10 个测试项目。新增 Presence Contracts/Core/Wpf；WPF 驻留模块不引用 Tasks 或 Storage。Tasks UI 只通过公开任务 API 构建独立速览会话。
- 默认关闭主窗口进入驻留并保留页面和草稿；再次启动同工作空间恢复原主窗口。退出申请先接受再排空，取消不关闭入口；系统会话结束和致命错误绕过驻留。恢复不因摘要清理异常被阻断。
- 默认右上侧 32 DIP 方形入口、露出 12 DIP、单击展开。实现三种触发、延时收起、Esc/外部点击、恢复/退出菜单、可配置工作区/DPI 定位。每次弹出重新查询；状态使用图标/文字/颜色，高对比度覆盖自定义色。
- 17 项驻留设置全部归 Presence；完整模块配置共 74 项、5 个分区、11 项普通/63 项高级。普通模式驻留显示 3 项，启用高级显示 17 项。定义、验证和即时应用随来源注册/撤销。
- 新默认配置显式包含 Presence；旧配置只兼容补充已有 tasks-ui 的新能力绑定，不自动启用新模块。本地完整发布目录统一为 `artifacts/local-run`。
- Release 构建 0 警告、0 错误；离线锁定还原通过。非桌面测试 102/102：架构 4、驻留 Core 14、内核 5、设置 Core 12、设置 SQLite 1、存储 12、任务契约 2、任务 Core 9、WPF 43。只读摘要预览：`artifacts/ui-preview/presence-tasks-readonly.png`。
- 交互桌面通过：启动/任务保存、关闭后进程仍活跃、再次启动恢复相同窗口和未保存备注、驻留普通项中文显示、保存 Exit 后关窗正常结束进程。Windows 检查工具未枚举小型无任务栏入口；未据此宣称边缘点击/跨窗口悬停/菜单退出已通过。
- 已生成本地 0.3.0 自包含 EXE 和 MSI（51,867,744 字节）；MSI SHA-256 `A6B3CC26D3D295F5B78D121DA2AF574FAE016097249BC99AEAAAA7C939BA9019`。打包跳过 Windows Installer ICE 校验，未执行本轮安装/升级，不将“构建成功”等同于安装验收。
- 工作空间启动入口 `artifacts/Mosaic.lnk` 指向唯一完整程序 `artifacts/local-run/Mosaic.Desktop.exe`，带 `--data-root artifacts/local-run-data` 的绝对路径；该工作空间配置已备份并显式接入 Presence，未修改任务数据库。
- 待验收：完整 9 项桌面 Smoke、多屏/负坐标/热插拔、100–200% DPI、高对比度实际桌面、全屏策略、系统注销、干净 Windows 安装升级。当前版本不因自动化通过而视作全部环境交付。

## 最新计划跟进（2026-11-15，普通项恢复）

### 界面整理（2026-11-15）

侧栏修复补充：导航按钮移除固定 154 DIP 宽度，跟随容器伸缩，折叠模式按图标栏宽度计算内边距，避免高 DPI 下选中边框被裁切。Shell 右边界增加拖动手柄，支持鼠标拖动、双击恢复、左右键微调及 Home 恢复；拖动取消会回滚，当前会话的拖动宽度在页面切换及窗口折叠/展开后保留。宽度限制为 160–320 DIP，并为内容区保留空间。全部行为在 Shell 内完成，导航仍来自通用贡献协议，不新增模块引用。新增拖动限制、取消、折叠往返与按钮边界测试；WPF 38 项通过。

- 设置页改为分区导航与紧凑参数行：宽窗口标签与控件左右排列，可用宽度低于 900 DIP 时自动上下排列；按类型仅创建当前编辑器，去除隐藏控件及空错误行占位。
- 保存/放弃固定在滚动区外，显示当前分区设置数量、未保存修改数量与处理进度；支持 Ctrl+S。危险的全部恢复降为次要操作。
- 普通与高级模式使用明确说明；布尔参数改为带文字和键盘焦点边框的开关。选中导航通过动态主题颜色高亮，字段控件补齐可访问名称。
- 任务页辅助文字统一提升至 12 DIP，继承主题前景色和背景；已有任务收起重复的保存帮助，给表单更多空间。
- 新增设置页宽窄重排、操作栏固定、控件边界与可访问名称测试，并生成浅色、深色和高对比度预览。
- Release 构建 0 警告、0 错误；82 项非桌面测试通过（含 WPF 37 项）。界面改动留在对应 WPF 模块，继续使用共享主题资源及既有公开 API。

本节覆盖下文历史记录中“全部 Advanced”的旧行为。

- 指定 8 项恢复 Normal；普通模式动态显示任务 6 项、外观与布局 2 项，存储系统与文案分区隐藏。高级模式展示已注册的全部 57 项，复用同一行实例。
- 保存、放弃及分区恢复按可见定义 ID 限定范围；切换分区和隐藏未保存高级项前确认；只修改普通项时切换高级模式保留输入。
- 风险修改提交前确认；整数无效输入进入脏状态；放弃/恢复通知布尔和枚举控件；即时应用警告不再误报保存失败。
- 任务编辑栏固定在第 2 列，消费编辑栏宽度及栏间距；外壳默认 1504×1012、最小 1100×720，断点为 1180/1200，过小工作区仅修正本次窗口。
- 设置导航使用继承前景色的矢量齿轮；设置来源经公开验证接口校验跨字段组合。即时 Apply 仅传即时参数并通知已注册消费者，避免提前应用重启规则；宿主失败补偿使用独立取消令牌并检查返回值。
- Release 构建 0 警告、0 错误；本轮非桌面测试 81 项（架构 4、内核 5、设置核心 12、设置存储 1、存储 12、任务契约 2、任务核心 9、WPF 36）。预览新增 1100/1504 固定右栏与高级开关两态。

本轮为计划的增量实施，不代表完整交付：仍需补齐旧 0.1 配置迁移、重启 pending 数量、预览能力归属、禁用模块值的全部清除、结构化模块/provider 编辑，以及干净 Windows 安装升级验收。历史安装包哈希对应上一次发布，不代表本轮本地构建。

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
- 图标栏状态卡折叠为单个状态圆点，移除空卡片外框；圆点通过提示和可访问名称保留“本机工作空间 · 离线可用”语义。
- `TasksView` 在自身可用宽度 920 DIP 以下把列表与详情改为单页切换；两个面板复用同一实例和 ViewModel，跨断点保留选择、输入与滚动状态，“返回任务列表”沿用未保存确认。
- 任务详情在窄栏或高度小于 680 DIP 时把“标记完成/重新加载”放在第一行、“删除”独占第二行；按钮最小高度为 40 DIP，底部操作区始终位于表单滚动区之外。
- 顶栏面包屑在图标栏下缩写为“MOSAIC”；任务列表行保留最小高度并省略超长文字；详情备注改为随剩余空间自适应，窄窗操作按钮保持 40 DIP 最小高度和 96 DIP 最小宽度。

本轮更新后 Release 构建为 0 警告、0 错误，50 项非桌面自动化测试通过（架构 3、内核 5、存储 8、任务契约 2、任务核心 8、WPF 24）。响应式测试覆盖 420、480、560、760、839、920、1039、1040、1059、1060、1220 DIP 及 480、620、780 DIP 高度；`artifacts/ui-preview/` 包含 `shell-760-rail.png`、`tasks-detail-760.png`、`tasks-detail-920.png`、`tasks-detail-560.png`、`tasks-list-480.png` 等计划要求的预览。

响应式更新后已重新生成 `artifacts/win-x64-ui` 发布目录与 `artifacts/installer/Mosaic-0.1.0-win-x64.msi`，安装包 SHA-256 为 `7E75FF4DAEC6AF4AD00AB020C4380FC2B70653BBA28B7DDE486254BFCED2940C`。

### 设置功能 S0–S5 初版（2026-11-14）

按 [设置功能规划](settings-plan.md) 新增了独立的设置模块层和宿主装配：

- `Mosaic.Settings.Contracts` 定义参数类型、来源、宿主来源和 `ISettingsApi`；`Mosaic.Settings.Core` 提供计划中的 54 项定义、类型/范围/枚举校验、用户设置与宿主配置路由、按模块或全部恢复默认。
- `Mosaic.Settings.Sqlite` 使用独立 `settings` 表迁移与 JSON 值映射；加载结果转回普通 CLR 类型，避免 `JsonElement` 泄漏到界面。
- `Mosaic.Settings.Wpf` 提供 `settings-ui` 模块，导航为“设置”，页面支持普通/任务/外观与布局/存储与系统/文案分区、布尔/枚举/文本编辑控件、字段错误、忙碌反馈、重启徽标、单行恢复默认、恢复当前分区或全部默认，以及未保存离开确认。
- 宿主实现 `IHostSettingSource`，设置按 `Config/appsettings.json` 的 `settings` 节点原子写回；默认 `appsettings.json` 装配 `settings-sqlite → settings-core → settings-ui`。
- 架构白名单与公开 API 清单已同步 Settings 项目；新增 Settings.Core 和 Settings.Sqlite 测试。

本轮 Release 构建为 0 警告、0 错误，56 项非桌面自动化测试通过（架构 3、内核 5、设置核心 5、设置存储 1、存储 8、任务契约 2、任务核心 8、WPF 24）。设置页的启动装配和编辑控件已进入桌面构建；交互式桌面截图已更新。WAL/连接模式消费和任务/外壳各参数的即时应用仍在后续 S4/S6 验证。

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

## 0.2.0：设置功能闭环（2026-11-15）

按 [设置功能规划](settings-plan.md) 的阶段 1–6 完成设置功能闭环。提交：`4e555cc`（阶段 1–3a）、`38b7b13`/`16fcbae`（阶段 3b 存储参数）、`abe56e9`（阶段 4 架构归位）、`76e4b5f`（阶段 5 本地化与窄窗）。

### 阶段 1–2：数据正确性与设置页可操作

- 整数行输入进入脏状态并写回 `ProposedValue`，修掉“保存不到/不生效”的数据正确性问题。
- 设置定义注册从 ViewModel 构造移回各模块激活期：tasks-core/tasks-ui/shell/storage-sqlite 各自提供定义，`ActivateAsync` 注册、`StopAsync` 撤销；tasks-core 先注册自身来源再读设置，`TaskRules` 拿真实约束。
- `host.modules` 默认值补全 `settings-sqlite/settings-core/settings-ui`，恢复默认不再写出残缺清单。
- `SettingOption` 显示名经本地化键解析；`ShowAdvanced` 时普通分区显示全部行，Appearance 分区纳入 `appearance.theme`/`ui.language`。
- `VisibleRows`/`HasVisibleRows`、`Message`/`IsError`/`HasMessage`、`IsBusy`/`IsNotBusy` 通知补齐；离开页放弃修改会回滚主题/语言预览。
- `IUserDialogService` 增加通用 `ConfirmAsync`，恢复默认不再复用删除确认框；分区按钮有选中态。

### 阶段 3：宿主设置消费与原子保存

- `ProposeChangesAsync`/`ResetAsync` 事务化：宿主配置写失败回滚用户设置，杜绝“界面报失败、数据已部分落盘”。
- `HostSettingSource.PersistAsync` 成功后刷新启动快照覆盖值，UI 不再读回旧值；`File.Replace` 失败路径清理 `.tmp`。
- 真正接入消费方：启动宽高/最小尺寸、侧栏断点与页面边距、日志级别与保留、单实例退避、存储队列/锁/备份/WAL/连接模式、`storage.filename/location`。
- `ModuleList`/`ProviderMap` 结构校验与数据库文件名校验补全。
- 存储参数（WAL、PooledPerOperation）按规划单独提交（`16fcbae`），含故障注入与往返测试。

### 阶段 4：架构归位

- 定义归各模块：tasks-core/tasks-ui/shell/storage-sqlite 各自 `ISettingSource`；`SettingCatalog` 收敛为宿主定义；settings-core 只聚合。
- UI 最小接口：Shell、Tasks、Settings 只注入 `ISettingsApi`；注册能力只保留在模块激活路径。
- `ISettingRepository` 移入 `Mosaic.Settings.Contracts`，`Settings.Sqlite` 与 `Settings.Wpf` 不再反向依赖 Core。
- `build/project-boundaries.json` 收紧；架构测试升级为断言定义归属与无直连设置表。

### 阶段 5：本地化与窄窗打磨

- `MosaicLocalizationManager.LanguageChanged` 订阅链：Shell 导航标签/页面标题、Tasks 计数/反馈/编辑器文案、Settings 行文本/分区/反馈在语言切换后重算；`SetLanguage` 在无 `Application` 时也更新 `Owners`，`TryFindResource` 供各模块解析键。
- Settings/Tasks 资源字典补齐 `Settings.Error/Message/Constraint/Hint/Empty.*`、`Tasks.Editor.*`、`Tasks.Count.*`、`Tasks.Busy.*`、`Tasks.Error.*`、`Tasks.Message.Reloaded` 等约 60 键，含 `{0:N0}` 复数化格式串。
- 560px 任务详情压缩底栏、删除按钮独立行、备注自适应已有覆盖（`Over_compressed_task_views_keep_actions_and_text_operable`），本轮未再改动布局。

### 阶段 6：回归与发布准备

- smoke 夹具 `backend` 更新为含 `settings-sqlite/settings-core` 的新模块配置，其余场景不变。
- Release 构建 0 警告、0 错误；76 项非桌面自动化测试通过（架构 4、内核 5、设置核心 11、设置存储 1、存储 12、任务契约 2、任务核心 9、WPF 32），其中新增语言切换用例 3 个、设置 UI 用例 4 个、存储参数用例 4 个。
- 修复 WPF 测试中一处泄漏：响应式编辑器用例未释放页面，导致 TasksViewModel 跨 STA 线程收到语言回调。
- `dotnet format` 仍因受限会话不能连接 MSBuild 命名管道而无法执行（与 0.1.0 历史一致），Roslyn 编译器与分析器在 Release 构建中报告 0 警告。
- 版本号升至 0.2.0（`Directory.Build.props`、WiX 项目与 `build/package.ps1` 默认值）；`artifacts/win-x64` 已用 0.2.0 负载重新发布（`build/publish.ps1 -UseV2Feed`），产物版本为 `0.2.0.0`。
- MSI 已生成：`artifacts/installer/Mosaic-0.2.0-win-x64.msi`，最终 SHA-256 `FBD6587525AEE579CACF6E7CA7E8FD91375B5B3B535CE781E0252D88A4E8B5CF`（2026-11-15 设置分组重构、版本号收尾后的负载）。受限会话无法访问 Windows Installer 服务，本次以 `-SkipValidation` 关闭 ICE；完整 ICE、安装/升级保留设置与干净 Windows 验收仍需交互式会话。
- 桌面 smoke 8 项中 5 项通过；依赖窗口发现/关闭的 `Published_application_crud_reopen_and_single_instance` 与 `empty`/`backend`/`invalid` 中的 2–3 项在受限会话超时（窗口发现或关闭），与 0.1.0 基线一致，需交互式桌面复验。

### 复核修复：P1 功能闭环（2026-11-15）

对 verified 0.2.0 线复核后，补上了“界面显示已保存、实际不生效或半落盘”的剩余缺口。提交：`3a2c6f3`（值类型规范化、宿主语义校验与测试）、`3257cf2`（分区重置与回滚加固）、`1d71265`（完整装配预览）。

- 值类型统一：新增 `SettingValueNormalizer`，按定义 `Kind` 把存储层返回的 `long`、`object[]`、字典规范化为 `int`、`string[]`、`Dictionary<string,string>`；设置服务、宿主来源与设置行的显示/回写共用同一规则，`System.Object[]` 不再出现在界面上。
- 宿主装配真实接线：`Bootstrapper` 以 `settings` 段中的 `host.modules`/`host.providers` 为权威（顶层配置仅作兼容回退），经 `ModuleGraph.Resolve` 与 capability 校验后装配；默认模块清单与 5 条 provider 绑定由 `BuiltInModuleCatalog` 从内建目录推导，恢复默认不会再写出残缺配置。
- 语义校验归宿主：`IHostSettingSource` 增加 `ValidateAsync`，保存/重置前先由宿主校验未知模块、缺失依赖与不提供的 capability，settings-core 不反向依赖模块目录。
- 首启尺寸契约：P1 复核曾临时调整为 1220×780；最终按设置分组重构恢复为 1504×1012，最小尺寸为 1100×720，并新增契约测试断言默认值不小于最小窗口。
- 分区重置按可见项：`ISettingsApi.ResetAsync` 增加定义 ID 列表重载；UI 用当前分区可见行重置，外观分区含 `shell.window.*`，存储分区只含 `storage.*`/`logging.*`/`startup.*`/`diagnostics.*`，`host.modules`/`host.providers` 只参与“恢复全部默认”。
- 跨存储原子提交：顺序改为全部校验 → 宿主配置经 `File.Replace` 带备份写入 → SQLite 事务写入；任一步失败用备份或事务回滚，新增键不再残留。宿主写入成功但即时 `ApplyAsync` 失败时返回 `SettingResults.ApplyWarningId`，界面提示“已保存，重启后生效”并清除脏状态，不再误报保存失败。
- 预览夹具修复：预览测试改为完整装配（settings-core 聚合 host/shell/tasks 来源 + tasks-ui + settings-ui），重拍 `shell-wide`、`settings-wide`、`settings-760`；Shell 导航包含设置页。分组重构后产品最小验收尺寸统一为 1100×720。
- 版本可见性：窗口标题显示 `Mosaic v0.2.0`，侧栏状态区显示 `v0.2.0 · <提交短哈希>`；版本与提交取自程序集信息版本，便于区分发布包、本地构建与旧副本。

本轮 Release 构建 0 警告、0 错误；87 项自动化测试通过（架构 4、内核 5、设置核心 18、设置存储 1、存储 12、任务契约 2、任务核心 9、WPF 36）。

### 设置分组重构与版本号（2026-11-15）

- 删除设置页“普通”分区：57 项参数全部由定义中的 `SettingsGroup` 归入任务、外观与布局、存储与系统、文案四个分区；`SettingCategory` 仅保留兼容契约，全部定义均为 Advanced。
- 原 8 项普通项分别迁入任务（5 项）和外观与布局（3 项）；高级关闭时四个分区可见行数均为 0，开启后只显示当前分区的全部参数。
- 参数计数为 host 11 + shell 10 + storage-sqlite 5 + tasks-core 5 + tasks-ui 25（任务 20 + 文案 5）共 57 项。
- 任务页保持左右结构：列表在左，编辑栏固定在内容区右侧；默认窗口尺寸恢复为 1504×1012，最小尺寸为 1100×720。
- 窗口标题和侧栏显示 `v0.2.0 · <提交短哈希>`，便于区分发布包、本地构建与旧副本。

### 对照 §11 完成定义

- 57 项参数（host 11 + shell 10 + storage-sqlite 5 + tasks-core 5 + tasks-ui 25）均可在对应四个分区中开启高级设置后查看、修改、校验和恢复默认；即时项保存后生效，重启项显示重启徽标。
- settings-core 不理解任务、数据库或布局；各模块独占自己的规则与数据；无绕过公开接口直连设置表的路径（架构测试断言）。
- 危险项（WAL、池化连接、文件改名、备份保留等）有警告文案、约束和测试。
- 0.1.0 配置可平滑加载；设置表迁移可备份、可重开；安装升级保留设置仍需交互式安装验收。
- Release 构建、架构/业务/存储/界面测试已通过；桌面验收中的交互式项待正常会话复验后即可视为 0.2.0 交付。

## 后续验收边界

阶段 0–5 的首版功能、架构与本机测试已落实；阶段 6 的发布及本机 MSI 已交付，尚不能视为全部分发环境验收完成。

尚未执行：无 SDK 的干净 Windows 用户/虚拟机、Windows 10 实机、不同 DPI/中文输入法矩阵、ARM64、物理磁盘满/权限故障注入及迁移备份失败故障注入。本机未提供 Windows Sandbox。安装器数据保留检查和事务回滚测试不替代这些检查。

正式代码签名证书尚未提供，EXE/MSI 未签名。提醒、同步、分组、优先级、插件市场等仍不进入首版。

0.2.0 的交互式桌面 smoke（CRUD、空配置、坏 JSON）与安装/升级保留设置复验同样需要正常交互式 Windows 会话；受限会话中 `empty`/`backend`/`invalid` 的窗口发现与关闭超时属环境限制，非功能回归。

0.7.0安装包：Mosaic-0.7.0-win-x64.msi，51913664字节，SHA-256 93ACA6B893412C0FA881158D939868019AFD1C3E875B11E8382A871161267A75。

0.8.0产物：唯一完整程序 artifacts/local-run/Mosaic.Desktop.exe；MSI Mosaic-0.8.0-win-x64.msi，51941484字节，SHA-256 44CEF919674BFE86A3A1B8BB84140CD7AFC50785079C888C841005911191F7E8；数据库回读ProductVersion=0.8.0。MSI使用SkipValidation构建。

## 服务端（独立交付，0.1.0）

账号、邮箱验证码注册/登录、Device Flow、备份与增量同步的服务端实施记录见 [server-implementation-status.md](server-implementation-status.md)。服务端位于 `server/`，使用独立解决方案与版本号，不进入桌面 MSI；根因与改动位置、验证/未验证项均在该文档。
