# Mosaic 手机端第一版实施记录（2026-11-19）

版本：手机端 0.1.1（对齐服务端 0.1.1 最终 wire）；基线协议：服务端 0.1.1 提交 `4a223be` / 共享协议文档；实施范围：`mobile/` 与 `docs/mobile-auth-sync-plan.md`。

## 1. 根因位置

### 1.1 第一版缺失（已交付）

手机端此前完全不存在：仓库只有桌面 WPF 实现（`Mosaic.sln` 无手机项目，`src/` 无网络/认证/跨设备同步模块），因此没有可复用的凭证保护、`/v1` 客户端、任务缓存与账户界面。本次新增独立 `mobile/` 解决方案与全部实现，未修改 `src/`、`tests/`、`build/`、`server/`。

### 1.2 与服务端 0.1.1 最终 wire 的偏差（本次修复）

第一版按旧的冻结样例实现，服务端 0.1.1 定稿后以下位置行为不一致，触发条件与原因如下：

- `mobile/src/Mosaic.Mobile.Wire/SyncDtos.cs` 旧 `ChangePushDto`/`SyncPushRequestDto(string? DeviceId, List<ChangePushDto> Changes)`：按批量写入建模，而服务端只接受单条 `POST /v1/sync/changes`，请求体为 `{deviceId,kind,key,payload,deleted,baseServerRevision,clientRevision,idempotencyKey}`；旧模型无法表达 `deleted`，且响应缺少 `{change,replayed}` 的冲突/重放语义。
- `mobile/src/Mosaic.Mobile.Sync/SyncEngine.cs` `SyncOnceAsync`：只把 `410`/`sync.cursor_invalid` 当游标失效；服务端对无法识别的游标返回 `400 sync.invalid`（可带 `details.requiresSnapshot=true`），旧逻辑会把它当普通失败，导致永远无法自愈。
- `mobile/src/Mosaic.Mobile.Auth/DeviceFlow.cs` `DeviceFlowPollingSession` 构造函数：要求 `verificationUri` 非空且忽略 `verificationUriComplete`，手机页面会打开不含授权码的链接；`SlowDown` 固定加 5 秒，忽略服务端 `200 {"status":"slow_down","interval":...}` 返回的间隔。
- `mobile/src/Mosaic.Mobile.Auth/AuthSessionManager.cs` `ApplySessionAsync`/`RefreshCore`：只读取 `session.User`，而服务端会话同时返回 `expiresIn` 与 `account{userId,email,displayName}`，缺少 `user` 或绝对过期时间时会把有效会话判为协议错误。
- `mobile/src/Mosaic.Mobile.Auth/AuthErrors.cs` `AuthError.Classify` 与 `mobile/src/Mosaic.Mobile.Wire/WireErrorCodes.cs`：仍以旧别名为主要常量（`auth.code_invalid`、`auth.password_policy` 等），缺少 `sync.invalid`、`auth.required`、`auth.session_expired`、`auth.invalid_refresh` 的规范分类，新增服务端码会落到通用协议错误。
- `mobile/src/Mosaic.Mobile.Presentation/ViewModels/DeviceViewModels.cs` `DeviceSignInViewModel.OpenLink`：使用不含授权码的 `VerificationUri`。
- `mobile/src/Mosaic.Mobile.Wire/SessionDtos.cs` `MeResponseDto`：只解析嵌套 `user`，而 `/v1/auth/me` 返回账户字段在根级。

## 2. 改动位置

新增目录 `mobile/`，关键文件与职责：

### 2.1 构建与解决方案

- `mobile/Directory.Build.props`、`mobile/Directory.Packages.props`：手机树独立的 TFM/版本/包管理，阻断根目录桌面设置。
- `mobile/Mosaic.Mobile.sln`：全部 10 个项目（含 App 头）。
- `mobile/Mosaic.Mobile.Core.slnf`：无 MAUI 工作负载时可构建/测试的 9 个项目。
- `mobile/tests/.editorconfig`：仅关闭 xUnit1051（测试内显式 `CancellationToken.None`），保留阻塞调用检查。

### 2.2 Wire（`mobile/src/Mosaic.Mobile.Wire`）

- `WireJson.Options`：camelCase、忽略未知字段、容忍尾部逗号。
- `WireErrorDto`：`code/message/traceId/details`、`RequiresSnapshot`、`DetailsAs<T>()`（读取 `details.current`）。
- `WireErrorCodes`：服务端 0.1.1 规范码（含 `sync.invalid`）+ 旧别名 + `IsSessionGone`。
- `AuthDtos`、`SessionDtos`、`DeviceDtos`、`SyncDtos`、`TaskItemDto`、`BackupDtos`：`/v1` DTO；`SessionDto` 支持 `expiresIn`/`account` 超集并 `Normalize`；`DevicePollResponseDto.Interval`；`SyncDtos` 为单条写入 + `{change,replayed}` + `SyncConflictCurrentDto`。
- `ChangeDto.ResolvedOperation/ResolvedKind/EffectiveTimestamp`：同时兼容 `operation/deleted` 与 `updatedAtUtc/createdAt`。
- `SessionListParser.Parse`：数组或 `{sessions:[]}` 两种信封。
- `DeviceFlowStateParser`：未知状态不崩溃。

### 2.3 Auth（`mobile/src/Mosaic.Mobile.Auth`）

- `MosaicApiClient`：唯一 HTTP 所有者；Bearer、401 单次刷新重试、GET 指数退避、`Retry-After`、错误信封解析、URL 构造。
- `ServerAddress.TryNormalize/IsLoopback`：HTTPS 强制，HTTP 仅回环。
- `TokenStore.LoadAsync/SaveAsync/ClearAsync/GetOrCreateDeviceIdAsync`：按服务器哈希隔离，仅持久化 refresh token/deviceId/serverUrl/账号/过期时间；损坏或不可解密时删除并返回 `Unavailable`。
- `ISecretStorage`、`StoredSession`、`SessionLoadResult`：平台加密存储抽象。
- `AuthSessionManager`：登录、验证码登录、注册、忘记/重置密码、登出（含仅本地）、账号删除、会话列表/撤销/全部撤销、`IAccessTokenProvider` 单飞刷新、离线保留令牌、拒绝时清令牌并只读标记、`LocalDataCleared` 事件。
- `DeviceFlowClient`、`DeviceFlowPollingSession`、`DeviceApprovalService`：两种 Device Flow；`AuthorizationUri` 优先 `verificationUriComplete`；`slow_down`（200 + `interval`）与 429 都按返回间隔递增、过期、切后台 `Stop/RunAsync` 恢复、批准后原子落盘再 `/auth/me` 校验。
- `AuthSessionManager.RevokeAllSessionsAsync(bool keepCurrent)`：无 body 撤销全部；`{keepCurrent:true}` 时保留本地会话。
- `AuthInputRules`、`DeviceUserCodes`、`SecretRedactor`：与服务端一致的 rune 计数/字母表/脱敏。
- `AuthError`/`AuthResult`、`IMosaicApi`、`ApiResult`。

### 2.4 Storage（`mobile/src/Mosaic.Mobile.Storage`）

- `MobileDatabase`：WAL、`PRAGMA user_version=1`、拒绝更新版本库、`tasks/task_tombstones/settings/meta/outbox` 建表。
- `SqliteSyncStore`：页 + 游标同事务；`serverRevision` LWW；tombstone 阻止旧 upsert 复活；过滤（全部/未完成/已完成/归档）；`ResetProjectionAsync`/`ReplaceProjectionAsync`/`ClearAllAsync`。

### 2.5 Sync（`mobile/src/Mosaic.Mobile.Sync`）

- `ChangeProjector.Project`：任务/设置/墓碑投影；未知 kind/设置、坏 key/payload 跳过并诊断。
- `PortableSettings` + `PortableSettingKeys`：规划 §7.2 的 14 个键；类型/范围校验；未知值保留旧值；`HighContrast→FollowSystem`；null 重置；`ResetAll`。
- `SyncEngine`：`limit=200`、单次最多 20 页、单飞合并、取消、401→需登录、游标失效→清投影 + 快照重建（`410 sync.cursor_invalid`，或 `400 sync.invalid` 且 `details.requiresSnapshot=true`）、二次失效停止、状态事件、离线保留游标；普通 `400 sync.invalid` 不清空投影。
- `SyncWriteClient`：单条 `POST /v1/sync/changes`；`Applied/Replayed/Conflict/Rejected/SignedOut`；解析 `409 sync.conflict` 的 `details.current`；写入 UI 与 outbox 排空留待第二阶段。
- `BackupRestoreService`：`/v1/backups/latest` + 快照下载 → 手机自有投影；快照条目 `serverRevision=0`，后续增量可覆盖。

### 2.6 Presentation（`mobile/src/Mosaic.Mobile.Presentation`）

- `LocalizationService`：中英双语文案表、跟随系统、切换事件、可绑定索引器。
- `AppThemeService`：跟随系统/浅/深解析。
- `TaskListViewModel`、`AccountViewModel`、`SignInViewModel`、`RegisterViewModel`、`ForgotPasswordViewModel`、`DeviceAuthorizeViewModel`、`DeviceSignInViewModel`、`SettingsViewModel`：页面状态、校验、错误本地化、两步确认（撤销全部/删除账号/批准桌面/仅本地登出/快照重建）。
- `AuthErrorText`、`SyncStatusText`、`MobileDeviceInfo`。

### 2.7 App（`mobile/src/Mosaic.Mobile.App`，MAUI 头）

- `MauiProgram`：DI 组装（DB、存储、API、认证、同步、视图模型、页面）。
- `MosaicApp`、`AppShell`、`StartupService`、`AppPreferences`：启动顺序（DB→偏好→会话→缓存设置→首个前台同步）、前后台暂停/恢复、非机密偏好持久化。
- `Pages/*`：任务、账户、设置、登录、注册、忘记密码、授权桌面、手机 Device Flow；浅深主题/中英文绑定；控件带 `AutomationProperties.Name` 或可见文本。
- `Platforms/Android/AndroidSecretStorage`：AndroidKeyStore AES-GCM，密钥名作 AAD，密文在私有 SharedPreferences；备份规则与 data-extraction 规则排除该文件；manifest 默认禁明文，仅回环域名例外。
- `Platforms/iOS/IosSecretStorage`：Keychain `AfterFirstUnlockThisDeviceOnly`。
- `Platforms/Android/MainActivity|MainApplication|AndroidManifest`、`Platforms/iOS/AppDelegate|Program|Info.plist`。
- `Converters/NotEmptyConverter`、`Resources/Styles/*`。

### 2.8 测试（`mobile/tests`）

- Wire 21 项：服务端/冻结两种变更形状、错误信封、未知字段/枚举、会话列表两种信封、请求序列化、快照、会话超集（`expiresIn`/`account`）、`/auth/me` 两种形状、`slow_down` 间隔、`device/info` 新字段、单条写入省略 `payload`、`{change,replayed}` 与 `details.current`、`revoke-all {keepCurrent}`。
- Auth 71 项：地址校验、输入规则、脱敏、令牌存取/隔离/损坏/不可用、登录/刷新单飞/拒绝/网络失败、登出三种路径、重置密码、账号删除、会话列表、Device Flow 状态机与授权页、`slow_down` 间隔下界、完整授权链接回退、`keepCurrent` 保留会话、规范码/别名分类。
- Sync 37 项：投影、白名单、SQLite 事务/幂等/LWW/tombstone/过滤/迁移、引擎分页/401/游标失效/二次失效/离线/并发合并/取消、`400 sync.invalid` 带/不带 `requiresSnapshot`、单条写入成功/重放/冲突/登出。
- Presentation 14 项：主题解析、跟随系统语言、错误文案、同步状态文案。

## 3. 对已有安装/数据的影响

- 不影响桌面或服务端：未修改其源码、数据库、配置或安装包。
- 手机数据库为全新独立文件，`user_version=1`；重装或清除数据后由服务器快照/增量重建。
- 卸载重装会生成新的本地 deviceId（Keystore/Keychain 密钥随应用数据销毁），符合规划。
- 切换到另一服务器前必须先登出；已登录时 `AuthSessionManager.ConfigureServer` 拒绝切换，避免跨账号复用缓存。
- Android 备份被整体禁用（`allowBackup=false`）并额外排除令牌文件。

## 4. 验证记录

已在本机执行并通过：

- 版本号：`mobile/Directory.Build.props`、`ApplicationDisplayVersion`、`CFBundleShortVersionString`、`MauiProgram`/`MobileDeviceInfo` 回退值统一为 `0.1.1`（构建号 2）。
- 发布材料：`mobile/docs/releases/v0.1.1.md`；Android 打包脚本骨架 `mobile/build/package-android.ps1`（仅通过语法校验，未执行）。
- `dotnet build mobile/Mosaic.Mobile.Core.slnf`：0 警告、0 错误。
- `dotnet test` 四个测试工程：Wire 21、Auth 71、Sync 37、Presentation 14，共 143 项全部通过。
- 未修改 `src/`、`tests/`、`build/`、`server/`；文档只更新 `docs/mobile-auth-sync-plan.md` 与 `mobile/docs/`。

**未验证**：

- MAUI App 头未编译：本机无 MAUI 工作负载、无 Android SDK、无 Xcode；XAML、平台 Keychain/Keystore 运行时、页面导航均未在真机/模拟器上运行。
- 未对接真实 TLS 服务端；认证/设备流/同步/写入用本地 fake HTTP 验证（服务端端点已就绪）。
- 真实 SMTP、推送、后台同步（规划为第二阶段）未实现。
- 应用包名已确认：`app.mosaic.tasks`（`Mosaic.Mobile.App.csproj` 的 `ApplicationId` 与 iOS `CFBundleIdentifier` 一致）；上架渠道与合规材料（隐私政策、账号删除入口审核材料）仍待准备。

## 5. 第二阶段预留

- `outbox` 表（id/kind/item_key/operation/payload/base_server_revision/client_revision/idempotency_key/state/时间）已建，未启用。
- `SyncWriteClient` 已按服务端 0.1.1 单条写入实现并有契约测试；离线编辑、outbox 排空与冲突选择 UI 尚未接入。
- 推送与后台同步未接。
