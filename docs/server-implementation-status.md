# Mosaic 服务端：实施与验证记录

## 0.1.0：账号、邮箱验证码、Device Flow、备份与增量同步（2026-11-19）

规划：`docs/auth-sync-server-plan.md`。桌面端实施记录：`docs/implementation-status.md`。服务端使用独立版本号（0.1.0），协议前缀 `/v1`，不参与桌面 MSI 打包。

### 根因位置

- 仓库原本没有服务端或任何账号/网络能力，`server/` 目录不存在；规划文档确认第一版需要自建 ASP.NET Core 服务端（`docs/auth-sync-server-plan.md` 第 1、3 节）。这是“服务端缺失”的直接原因。
- 任务只能本地同步：`src/Modules/Tasks/Mosaic.Tasks.Contracts/TaskRequests.cs:5` 的 `TaskItemDto.Revision` 是单机递增版本，`src/Modules/Tasks/Mosaic.Tasks.Core/Internal/TaskService.cs:119-121` 的 `DeleteAsync` 直接调用仓库删除（物理删除，无 tombstone）。跨设备同步时删除无法传播，任何“最后写入获胜”都会把已删任务复活或让两端的 Revision 互相覆盖。
- 可移植设置没有导出/备份模型：`src/Modules/Settings/Mosaic.Settings.Sqlite/Internal/SettingsRepository.cs:33-41` 以明文 JSON 存在本机 SQLite 的 `settings(setting_id, value_json)`，没有白名单、版本或快照概念，无法安全上传到服务器。
- 客户端令牌与服务端协议此前都不存在；桌面会话新增的 `src/Modules/Auth/Mosaic.Auth.Http/HttpAuthTransport.cs` 与 `src/Modules/Sync/Mosaic.Sync.Http/HttpSyncTransport.cs` 是本次服务端协议的消费方。服务端按其实际读取的字段实现，并对齐了错误码（如 `auth.code_attempts_exceeded`、`backup.not_found`）。

### 改动位置

服务端为新增目录 `server/`，未修改 `src/`、`tests/`、`build/` 或 MSI 打包：

- `server/Directory.Build.props`：服务端独立的 `net10.0`、`Version=0.1.0`、警告即错误；避免继承桌面端 `0.15.3` 版本号。
- `server/Directory.Packages.props`：`Microsoft.Data.Sqlite` 10.0.9、`SQLitePCLRaw.bundle_e_sqlite3` 3.0.5（覆盖传递依赖 2.1.11 的 GHSA-2m69-gcr7-jv3q）、xunit.v3。
- `server/NuGet.config`、`server/.editorconfig`、`server/.dockerignore`、`server/Dockerfile`、`server/publish-linux.ps1`、`server/publish-linux.sh`、`server/Mosaic.Server.sln`、`server/README.md`。
- `server/src/Mosaic.Server/Program.cs`：进程入口，转调 `ServerApplication.RunAsync`。
- `server/src/Mosaic.Server/ServerApplication.cs`：`RunAsync`（配置装载、`--healthcheck` 探针）、`CreateAsync`（DI、Kestrel 请求体限制、`RouteHandlerOptions.ThrowOnBadRequest`、数据迁移、受信代理、端点映射）。
- `server/src/Mosaic.Server/ServerOptions.cs`：`FromConfiguration`、`Normalize`、`Validate`；所有环境变量与默认值。
- `server/src/Mosaic.Server/Data/Database.cs`：`OpenAsync`（WAL、`busy_timeout`、外键）、`ReadAsync`、`WriteAsync`（`BEGIN IMMEDIATE` + 忙等重试）。
- `server/src/Mosaic.Server/Data/SqliteExtensions.cs`：参数绑定、标量、查询与字段读取辅助。
- `server/src/Mosaic.Server/Data/Schema.cs`：`Schema.Migration1`，创建 `users`、`sessions`、`refresh_tokens`、`email_codes`、`device_auth`、`backups`、`backup_items`、`sync_items`、`sync_counters`、`sync_changes`、`sync_cursors`、`settings_audit`、`server_settings` 及索引（含幂等键唯一索引）。
- `server/src/Mosaic.Server/Data/DatabaseMigrator.cs`：`ApplyAsync`（按版本执行、拒绝降级启动）；`MasterKeyStore.Resolve`（`MOSAIC_MASTER_KEY` / `master.key`，0600，现有数据库缺密钥时拒绝启动）。
- `server/src/Mosaic.Server/Security/Security.cs`：`PasswordHasher`（PBKDF2-HMAC-SHA256/310k/16B/32B、常量时间）、`PasswordPolicy`（10–256 个 Unicode 字符）、`Tokens`（令牌、6 位验证码、8 位授权码与规范化）、`SecretProtector`（AES-256-GCM）、`SecretHasher`（邮箱/IP/验证码/设备码 HMAC）。
- `server/src/Mosaic.Server/Http/EmailNormalizer.cs`：`Normalize`（trim、小写、基础 RFC 校验，不做 Gmail 点号/加号改写）。
- `server/src/Mosaic.Server/Http/ApiException.cs`、`Http/ApiPipeline.cs`：统一错误信封 `{code,message,traceId}`、`Retry-After`、`X-Trace-Id`、安全响应头、`/device` 的 CSP 与 `no-store`、`TimeFormat.Iso`。
- `server/src/Mosaic.Server/Auth/SessionService.cs`：`AuthenticateAsync`、`FindUserByEmailAsync`、`FindCredentialsAsync`、`CreateAsync`、`CreateWithinTransactionAsync`（设备授权原子签发）、`RefreshAsync`（轮换 + family 重用检测，失败结果先提交再抛错）、`LogoutAsync`、`RevokeAllAsync`、`CreateUserAsync`、`UpdatePasswordAsync`、`InsertAsync`、`RevokeFamilyAsync`、`RevokeUserTokensAsync`。
- `server/src/Mosaic.Server/Auth/EmailCodeService.cs`：`RequestAsync`（60 秒重发、按 IP/邮箱限流、账户枚举防护、未配置 SMTP 统一 503、发送失败回滚行）、`ConsumeAsync`（10 分钟过期、5 次锁定、条件更新保证一次性、并发安全）。
- `server/src/Mosaic.Server/Auth/DeviceFlowService.cs`：`StartAsync`、`PollAsync`（一次性领取加密会话）、`ApproveAsync`（同一事务内建会话并加密暂存）、`DenyAsync`。
- `server/src/Mosaic.Server/Auth/AuthEndpoints.cs`：`/v1/auth/policy|email-code|register|login|login/code|refresh|logout|forgot-password|reset-password|me`。
- `server/src/Mosaic.Server/Auth/DeviceEndpoints.cs`：`/v1/device/start|poll|approve|deny`、`/device` 页面、`/`。
- `server/src/Mosaic.Server/Auth/AuthSupport.cs`：`RequireUserAsync`、`IpHash`、`SessionJson`、`UserJson`、`SanitizeDeviceLabel`、`LogEvent`（仅 userId、IP 哈希前 12 位、结果与耗时）。
- `server/src/Mosaic.Server/Pages/DevicePage.cs`：内置授权页 HTML/JS（密码登录、验证码登录、注册、批准/拒绝；无外部资源，令牌仅存内存）。
- `server/src/Mosaic.Server/Backups/JsonCanonicalizer.cs`：`Canonicalize`、`SerializeBackupContent`（确定性 JSON 与内容哈希）。
- `server/src/Mosaic.Server/Backups/BackupService.cs`：`UploadAsync`（校验 kind/key/revision/payload、大小、配额、SHA256、去重、保留份数）、`GetLatestAsync`、`GetAsync`（读取时重算哈希）、`RecordRestoreAsync`。
- `server/src/Mosaic.Server/Backups/BackupEndpoints.cs`：`PUT /v1/backups`、`GET latest`、`GET {id}`、`POST {id}/restore`。
- `server/src/Mosaic.Server/Sync/SyncService.cs`：`PushAsync`（服务端 revision、冲突 409 携带 current、幂等键重放、tombstone）、`PullAsync`（cursor/limit、hasMore、设备游标）。
- `server/src/Mosaic.Server/Sync/SyncEndpoints.cs`：`GET/POST /v1/sync/changes` 与参数校验。
- `server/src/Mosaic.Server/Admin/AdminEndpoints.cs`：`GET/PUT /v1/admin/mail`、`POST /v1/admin/mail/test`；`X-Admin-Token` 常量时间校验；未配置令牌时 404。
- `server/src/Mosaic.Server/Emailing/EmailContracts.cs`、`EmailSettingsStore.cs`（加密存储、只读视图）、`SmtpEmailSender.cs`（TLS 1.2+、STARTTLS/隐式 TLS、AUTH PLAIN/LOGIN、无秘密日志）、`EmailTemplates.cs`、`EmailNormalizer` 复用。
- `server/src/Mosaic.Server/RateLimiting/WindowLimiter.cs`：固定窗口限流与只读检查。
- `server/src/Mosaic.Server/Health/HealthEndpoints.cs`：`/health` 与 `--healthcheck` 探针。
- `server/src/Mosaic.Server/appsettings.json`：监听地址与日志级别。
- 根 `.gitignore`：忽略 `server/data/` 与 `**/master.key`。
- 测试：`server/tests/Mosaic.Server.Tests/TestInfrastructure.cs`（真实 Kestrel、临时 SQLite、确定性时钟、内存邮件、日志捕获）、`SecurityTests.cs`、`AuthFlowTests.cs`、`SessionTests.cs`、`DeviceFlowTests.cs`、`BackupTests.cs`、`SyncTests.cs`、`AdminHealthTests.cs`、`LoggingAndMigrationTests.cs`。

### 协议要点与客户端对齐

- 会话对象：`accessToken`、`refreshToken`、`accessTokenExpiresAt`、`refreshTokenExpiresAt`、`user{id,email,createdAt,emailVerifiedAt}`；`id` 为 32 位十六进制 GUID，客户端 `Guid.TryParse` 兼容。
- 错误码：`auth.invalid_credentials`、`auth.invalid_code`、`auth.code_expired`、`auth.code_attempts_exceeded`、`auth.weak_password`、`auth.rate_limited`、`auth.registration_closed`、`auth.required`、`auth.session_expired`、`auth.invalid_refresh`、`auth.refresh_reused`、`device.invalid_code`、`device.rate_limited`、`backup.not_found` 等，与桌面 `HttpAuthTransport.MapCode`/`HttpSyncTransport` 的映射一致。
- 设备授权：`verificationUri = {MOSAIC_PUBLIC_URL}/device?code=XXXX-XXXX`，`expiresIn=600`，`interval=2`；状态 `pending|approved|denied|expired|consumed`，`approved` 仅能领取一次。（0.1.1 起 `verificationUri` 不再带 `code`，改用 `verificationUriComplete`，见下节。）
- 备份/同步 wire DTO 见 `server/README.md` 的 API 章节。`kind` 取值为 `task`、`setting`，与 `Mosaic.Sync.Contracts.SyncItemKinds` 一致。
- 未实现 `slow_down`；`interval` 为建议值，轮询过密由 `device.rate_limited` 兜底。（0.1.1 起已实现 `slow_down`。）

### 迁移与升级影响

- 服务端为全新组件，不影响现有客户端安装、`src/`、`tests/`、`build/` 与 MSI 内容；桌面未登录行为不变。
- 首次启动创建 `<数据目录>/mosaic-server.sqlite3` 与 `master.key`；迁移版本记录在 `schema_migrations`，已发布的迁移不可修改，只能追加。
- 现有数据库缺少 `master.key` 时拒绝启动；数据库结构版本高于程序支持版本时拒绝启动，避免静默破坏数据。
- 备份运维：拷贝整个数据目录（含 `master.key`）；恢复后用户会话、令牌、SMTP 密文与设备授权均可用。
- 单实例约束：限流为进程内计数、SQLite 单写者；横向扩容前需要外置限流与数据库。

### 验证与未验证

已验证（Windows 11，.NET SDK 10.0.300）：

- `dotnet build server/Mosaic.Server.sln` 0 警告 0 错误。
- `dotnet test server/Mosaic.Server.sln`：69 个用例全部通过。覆盖验证码过期/重放/五次锁定/并发唯一消费/60 秒重发、按 IP 与按邮箱限流、账号枚举一致性、SMTP 未配置统一 503、刷新令牌轮换与重用检测、登出/重置撤销、Device Flow 全流程与一次性领取、过期/拒绝/未登录批准、设备页与安全头、备份往返/去重/隔离/配额/哈希/非法条目、同步游标/冲突/tombstone/幂等/用户隔离/设备游标、管理接口加密存储与密码不回传、日志不含邮箱/密码/验证码/令牌、迁移幂等、重启后数据与会话可用、主密钥创建与缺失拒绝启动。
- `dotnet publish -r linux-x64 --self-contained` 产物生成（含原生 SQLite）。
- 真实进程冒烟：`/health` 返回 `ok`，`/v1/auth/policy` 正确报告 `emailAvailable=false`，`/device` 返回 HTML，未配置 SMTP 时 `email-code` 统一 503。

未验证：

- 在真实 Linux 主机上运行自包含包与 systemd 启动。
- Docker 构建与运行（本机没有 Docker）。
- 真实 SMTP 提供商的 TLS 连接、认证与投递（仅通过内存发送器验证业务流程）。
- 反向代理 HTTPS、`X-Forwarded-For/Proto` 与按真实客户端 IP 限流。
- 手机端 Device Flow 与手机 UI（本次只预留协议）。
- 多设备在真实网络条件下的并发同步冲突与弱网重试。

## 0.1.1：会话管理、账户删除、设备信息与同步/备份协议对齐（2026-11-19）

依据“计划”会话冻结的协议更新（`docs/auth-sync-server-plan.md` 3.4、4.3 与错误码约定）。服务端版本升到 0.1.1；仍只改动 `server/` 与本文件，未触碰 `src/`、`tests/`、`build/` 或 MSI。

### 根因位置

0.1.0 按当时的协议实现，缺少 0.1.1 冻结要求的能力，且两处语义与冻结协议不一致：

- 用户无法自助查看/撤销设备：`server/src/Mosaic.Server/Auth/SessionService.cs` 只有 `LogoutAsync`/`RevokeAllAsync`，`server/src/Mosaic.Server/Auth/AuthEndpoints.cs` 没有 `GET /v1/auth/sessions` 与撤销端点；会话表也没有记录平台与最后活跃时间（`server/src/Mosaic.Server/Data/Schema.cs` 的 `Migration1` 无 `platform`/`last_seen_at` 列）。
- 没有账户删除：`AuthEndpoints` 只有重置密码，`users` 的级联删除虽已在 schema 声明（`ON DELETE CASCADE`），但没有重新认证与去标识审计的入口，也没有 `delete-account` 验证码用途（`EmailCodeService.NormalizePurpose` 原先只接受 `register|login|reset`）。
- 设备授权信息不完整：`DeviceFlowService.StartAsync` 只保存 `device_label`，丢弃平台与版本；`PollAsync` 没有 `slow_down`（README 0.1.0 明确写“未实现”）；`verificationUri` 直接带 `?code=`，没有独立的 `verificationUriComplete`；没有认证后的 `GET /v1/device/info`。
- 可移植设置没有服务端白名单：`BackupService.UploadAsync` 与 `SyncService.PushAsync` 只校验 `kind`，任何 `setting` 键都能上传，与计划 4.3 的白名单不符。
- 备份不携带同步位置：`backups` 表没有 `server_revision`/`sync_cursor` 列，恢复快照后客户端无法判断增量游标是否仍然有效。
- 同步历史不会过期：`sync_changes` 只增不减，`server/src/Mosaic.Server/Sync/SyncService.cs` 的 `PullAsync` 对任意旧游标都返回剩余变更，缺少“历史已清理 → 410 + 需要快照”的语义。
- 错误信封没有 `details` 对象：`server/src/Mosaic.Server/Http/ApiPipeline.cs` 只把细节平铺到根级，冻结协议要求可选 `details`。

### 改动位置

- `server/src/Mosaic.Server/Data/Schema.cs`：新增 `Schema.Migration2` —— `sessions`/`refresh_tokens`/`device_auth` 增加 `platform`、`last_seen_at`、`app_version`、`last_polled_at`；`backups` 增加 `server_revision`、`sync_cursor`；新建 `sync_floor(user_id, floor_seq, updated_at)`。
- `server/src/Mosaic.Server/Data/DatabaseMigrator.cs`：迁移表追加 `(2, "session_management_and_sync_floor")`；现有 0.1.0 数据库启动时自动升级，旧行取默认值（空平台、`last_seen_at=0`、备份 `server_revision=0`/`sync_cursor=0`）。
- `server/src/Mosaic.Server/ServerOptions.cs`：新增 `SyncHistorySeconds`（`MOSAIC_SYNC_HISTORY_DAYS`，默认 90 天）并在 `Validate` 中要求为正。
- `server/src/Mosaic.Server/Auth/SessionService.cs`：
  - `SessionTokens` 增加 `AccessExpiresIn`；新增 `SessionPrincipal`。
  - `AuthenticateAsync` 区分 `auth.required`/`auth.session_revoked`/`auth.session_expired`，并在 `last_seen_at` 超过 60 秒时 `TouchAsync`。
  - `CreateAsync`/`CreateWithinTransactionAsync`/`InsertAsync` 记录 `platform` 与 `last_seen_at`；`RefreshAsync` 读取平台并在提交后保留。
  - 新增 `ListSessionsAsync`（按 `family_id` 聚合、`current` 标记）、`RevokeSessionAsync`（撤销指定家族的会话与刷新令牌）、`RevokeAllSessionsAsync`（可保留当前家族）、`DeleteAccountAsync`（`settings_audit` 匿名化为 `deleted:<emailHash 前 16 位>`、写入 `account.delete` 审计、删除该邮箱的全部验证码、删除 `users` 触发级联：`sessions`、`refresh_tokens`、`device_auth`、`backups`、`backup_items`、`sync_items`、`sync_changes`、`sync_cursors`、`sync_counters`、`sync_floor`）。
- `server/src/Mosaic.Server/Auth/AuthSupport.cs`：`RequirePrincipalAsync`、`HasBody`（兼容 chunked 请求体）、`SessionJson` 增加 `expiresIn` 与 `account{userId,email,displayName}`（保留 `user`）、`AccountJson`、`SanitizeShort`，请求记录增加 `Platform`、`AccountDeleteRequest`、`RevokeAllSessionsRequest`，`ResetPasswordRequest` 兼容 `password` 字段。
- `server/src/Mosaic.Server/Auth/AuthEndpoints.cs`：`register`/`login`/`login/code` 传递 `platform`；`/me` 返回账户字段 + `user`；新增 `GET /v1/auth/sessions`、`POST /v1/auth/sessions/{id}/revoke`、`POST /v1/auth/sessions/revoke-all`（可选 `keepCurrent`）、`POST /v1/auth/account/delete`（密码或 `delete-account` 验证码，失败 `401 auth.reauth_required`，按用户限流 5 次/15 分钟，成功后 `204`）。
- `server/src/Mosaic.Server/Auth/EmailCodeService.cs`：新增 `PurposeDeleteAccount = "delete-account"` 并纳入 `NormalizePurpose`。
- `server/src/Mosaic.Server/Auth/DeviceFlowService.cs`：`StartAsync` 接收并保存 `deviceLabel/platform/appVersion`，`verificationUri` 改为 `{MOSAIC_PUBLIC_URL}/device`，新增 `verificationUriComplete`；`PollAsync` 依据 `last_polled_at` 与 2 秒间隔返回 `200 {status:"slow_down", interval}`（不作为错误码，客户端递增间隔）；`ApproveAsync` 将会话的平台写入设备授权；新增 `InfoAsync`（用户码查询、越权或过期 `404 device.invalid_code`）；`DeviceSessionPayload` 增加 `AccessExpiresIn`。
- `server/src/Mosaic.Server/Auth/DeviceEndpoints.cs`：`/v1/device/start` 解析 `platform/appVersion` 并返回 `verificationUriComplete`；`/v1/device/poll` 返回 `interval`；新增 `GET /v1/device/info`；批准/拒绝日志的耗时改用 `Stopwatch.GetTimestamp()`。
- `server/src/Mosaic.Server/Sync/PortableSettings.cs`（新增）：计划 4.3 的 14 个可移植设置键白名单，备份与同步共用。
- `server/src/Mosaic.Server/Backups/BackupService.cs`：`kind: "setting"` 强制白名单（`400 backup.invalid`）；上传时在同一写事务内读取 `sync_counters.revision` 与 `sync_changes` 最大 `seq` 写入快照；去重命中返回首次记录的值；`BackupUploadResult`/`BackupManifest` 增加 `ServerRevision`、`Cursor`。
- `server/src/Mosaic.Server/Backups/BackupEndpoints.cs`：上传、`latest`、下载与恢复响应都输出 `serverRevision` 与 `cursor`（字符串）。
- `server/src/Mosaic.Server/Sync/SyncService.cs`：注入 `ServerOptions`；`kind: "setting"` 白名单（`400 sync.invalid`）；`PushAsync` 在写入事务内调用新增的 `PruneHistoryAsync`（按保留期删除 `sync_changes` 并推进 `sync_floor`）；`PullAsync` 在 `cursor < floor` 时返回 `410 sync.cursor_invalid`（`details.requiresSnapshot=true`、`details.floor`）。
- `server/src/Mosaic.Server/Sync/SyncEndpoints.cs`：拉取默认 `limit` 改为 200；非法 `cursor` 的 `400 sync.invalid` 附带 `details.requiresSnapshot=true`。
- `server/src/Mosaic.Server/Http/ApiPipeline.cs`：错误信封在保留根级兼容字段的同时增加 `details` 对象。
- `server/Directory.Build.props`：版本 `0.1.1`。
- `server/README.md`：更新认证/设备/备份/同步表、`MOSAIC_SYNC_HISTORY_DAYS`、白名单、游标 410 语义、会话对象附加字段与账户删除后的错误码说明。
- 测试：新增 `server/tests/Mosaic.Server.Tests/SessionManagementTests.cs`（会话列表/撤销/保留当前、账户删除密码/验证码/重新认证/级联清理/审计去标识、未知验证码用途）；扩展 `DeviceFlowTests.cs`（`verificationUriComplete`、设备信息、`slow_down`）、`BackupTests.cs`（`serverRevision`/`cursor`、设置白名单）、`SyncTests.cs`（设置白名单、历史清理后的 410）、`LoggingAndMigrationTests.cs`（迁移数为 2、`sync_floor` 存在）；`BackupTests` 的示例设置键改为白名单内键。

### 错误码总表（0.1.1 冻结）

完整表见 `server/README.md` 的《错误码》。与本次裁决相关的三个码：

| 状态 | code | 触发条件 |
| --- | --- | --- |
| 401 | `auth.reauth_required` | `POST /v1/auth/account/delete` 的密码不正确，或 `delete-account` 验证码无效/过期 |
| 401 | `auth.session_revoked` | 令牌有效但会话已撤销（单会话撤销、`revoke-all`、登出全部设备、改密/重置、刷新令牌重用导致 family 撤销） |
| 410 | `sync.cursor_invalid` | 拉取游标早于 `sync_floor` 水位；`details.requiresSnapshot=true`、`details.floor` 给出水位；格式非法则为 `400 sync.invalid`，同样带 `details.requiresSnapshot=true` |
| 404 | `auth.session_not_found` | `POST /v1/auth/sessions/{id}/revoke` 的会话 ID 不属于当前用户或不存在（`id` 为 `/v1/auth/sessions` 返回的会话家族 ID） |

补充说明：**删除账户是真删**——`users` 行删除后 `sessions`/`refresh_tokens` 等随外键级联清空，没有墓碑；因此删号后的旧令牌返回 `401 auth.required`，而不是 `auth.session_revoked`。客户端（桌面与手机）已按裁决把两者都映射为“重新登录”。

### 可移植设置白名单（14 键，备份与同步共用）

`server/src/Mosaic.Server/Sync/PortableSettings.cs` 与 `server/README.md` 的《可移植设置白名单（14 键）》一致：

`ui.language`、`appearance.theme`、`tasks.sortOrder`、`tasks.defaultFilter`、`tasks.archive.autoEnabled`、`tasks.archive.delayDays`、`tasks.title.maxCharacters`、`tasks.notes.maxCharacters`、`tasks.title.required`、`tasks.defaultCompleted`、`tasks.confirmDelete`、`tasks.confirmUnsaved`、`tasks.showCharacterCount`、`tasks.autoSwitchToAllOnHidden`。

- 备份上传中 `kind: "setting"` 的非白名单键 → `400 backup.invalid`；同步推送中 → `400 sync.invalid`；服务端不保存、不转发。
- `kind: "task"` 不受限制；旧快照不因白名单变化被删除或改写。

### 协议决策与偏差（已回报“计划”会话）

- `ChangeDto` 采用冻结文档的 `{cursor,serverRevision,kind,key,deleted,payload,deviceId,createdAt}`（协调消息中的 `operation`/`updatedAtUtc` 与文档冲突，以文档为准）。
- 游标过期采用 `410 sync.cursor_invalid` + `details.requiresSnapshot=true`；格式非法为 `400 sync.invalid` + `details.requiresSnapshot=true`。
- `slow_down` 按“计划”会话最新约定以 `200 {status:"slow_down", interval}` 返回，并保留 `429 device.rate_limited` 作为设备码级兜底。
- 会话响应是超集：保留桌面 `HttpAuthTransport` 已读取的 `user`/`accessTokenExpiresAt`，同时提供 `expiresIn` 与 `account`。
- 新增 `delete-account` 验证码用途（冻结文档只写了“重新认证”，具体用途名由服务端定义并已写入 README）。
- 删除账户后旧令牌返回 `401 auth.required` 而非 `auth.session_revoked`：账户行随级联删除，令牌已不可解析；要保持 `auth.session_revoked` 需要保留墓碑，与“删除账户即删除数据”冲突。
- 错误细节既平铺根级（0.1.0 兼容）又放入 `details`（冻结信封）。

### 迁移与升级影响

- 0.1.0 → 0.1.1 为纯追加迁移（`Migration2`），现有用户、会话、备份与同步数据保留；旧会话平台为空字符串、`last_seen_at=0`（首次请求即补写）。
- 备份响应新增字段属兼容变更；旧客户端忽略即可。
- 设置白名单会使此前上传过的非白名单设置键无法再推送/备份（旧快照仍可下载与恢复，不删除历史数据）。若目标部署已有此类数据，客户端应过滤后重新上传。

已有安装的迁移验收项（0.1.0 → 0.1.1）：

1. 用 0.1.0 的数据目录启动 0.1.1，进程正常监听，`schema_migrations` 出现 `(2, session_management_and_sync_floor)` 且 `schema_version` 不降级。
2. `SELECT platform, last_seen_at FROM sessions` 对旧会话返回 `''` 与 `0`，登录一次后该行被补写；`GET /v1/auth/sessions` 返回旧会话（`platform` 为空字符串）并能撤销。
3. `SELECT server_revision, sync_cursor FROM backups` 对旧快照返回 `0, 0`；旧快照仍可下载与恢复，新上传携带当前同步位置。
4. `sync_floor` 表存在；旧 `sync_changes` 行不受迁移影响，`cursor=0` 仍可拉取。
5. `MOSAIC_SYNC_HISTORY_DAYS` 未设置时默认 90 天；设置非法值（0/负数/非数字）时启动失败并给出可读错误。
6. 回滚验证：保留迁移前的数据目录副本；0.1.0 程序遇到版本 2 会拒绝启动，因此回滚只能恢复数据目录副本，不能直接降级运行。

### 验证与未验证

已验证（Windows 11，.NET SDK 10.0.300）：

- `dotnet build server/Mosaic.Server.sln`：0 警告 0 错误。
- `dotnet test server/Mosaic.Server.sln`：81 个用例全部通过，新增覆盖会话列表与 `current` 标记、单会话撤销后 `auth.session_revoked`、`revoke-all` 的 `keepCurrent` 分支、账户删除的密码与验证码路径、错误密码/验证码 `auth.reauth_required`、删除后 `users`/`backups`/`backup_items`/`sessions`/`refresh_tokens`/`device_auth`/`sync_changes`/`sync_cursors`/`sync_items`/`email_codes` 计数为 0 且审计不含邮箱、设备 `verificationUriComplete` 与信息查询、`slow_down` 与时钟推进后的恢复、备份 `serverRevision`/`cursor`、备份与同步的设置白名单、历史清理后的 `410 sync.cursor_invalid`、非法游标的 `400 sync.invalid`（两者 `details.requiresSnapshot=true`）以及 `cursor=floor` 可继续拉取。

未验证：

- 真实桌面客户端对新增 `auth.session_revoked`/`auth.reauth_required`/`slow_down`/410 的实际处理（服务端行为已测，客户端映射需桌面会话确认）。
- 长时间运行下 `sync_floor` 水位推进与客户端“先恢复快照再增量”的完整链路（仅在小时间窗内用假时钟验证）。
- 0.1.0 数据库升级到 0.1.1 的真实二进制启动验证（迁移由测试的幂等迁移用例覆盖，但未用 0.1.0 生成的库文件实测）。

## 0.1.2（尚未发布、尚未提交）：浏览器登录页补齐注册/重置与共用授权码

用户裁决：桌面登录页不再保留邮箱+密码表单，「在浏览器中登录」直接用系统浏览器打开服务端 `/device`，注册、验证码、密码、忘记密码全部在网站完成。本机版本号仍为 0.1.1（未改 Directory.Build.props），线上 0.1.1 不动。

### 根因位置

- 旧 `/device` 页面只有「登录 / 邮箱验证码登录 / 创建账号」三个表单，没有任何密码重置入口（`server/src/Mosaic.Server/Pages/DevicePage.cs` 重写前的 authView 区块），用户忘记密码时无法在这个唯一授权的浏览器页面自助恢复。
- 授权码输入框位于 `authView` 内部（重写前的 `DevicePage.cs` 中 `id="codeInput"` 段落），登录成功后 `showView("approve")` 会隐藏整个 `authView`，确认页只有只读文本，导致裸 `/device`（无 `code` 参数）先登录后无法补填授权码，只能点「使用其他账号」退回且丢失登录态。
- 页面不读取 `GET /v1/auth/policy`，注册关闭时仍显示「创建账号」；确认页也不展示 `GET /v1/device/info` 返回的设备信息，用户无法核对是哪台设备在申请授权。
- `MOSAIC_PUBLIC_URL` 默认 `http://localhost:5080`（`server/src/Mosaic.Server/ServerOptions.cs:23`），浏览器登录链接会指向服务器本机；此前没有任何提示。

### 改动位置

- `server/src/Mosaic.Server/Pages/DevicePage.cs`（`DevicePage.Html`）：
  - 结构：新增共用区块 `#codeBlock`（置于 `#authView` 之前，登录前后及确认页都可见）；新增 `#resetView`（邮箱 → `#sendResetCode` → 6 位验证码 → 新密码 + 确认 → `#resetButton`）；确认页新增「应用」行 `#deviceInfo` 与「剩余时间」行 `#approveExpiry`；登录表单新增 `#forgotPassword` 与 `#policyHint`；CSS 新增 `.code-block`/`.policy`，`.link` 改为块级避免两个链接挤在一行。
  - 脚本函数：`setCodeValue`（统一授权码格式化，供预填、输入、查询复用）、`currentCode`、`showView`（新增 reset 视图并单独控制 `#codeBlock` 显隐）、`setMessage`/`showMessage`/`showResetMessage`/`showApproveMessage`、`request`（支持 GET/POST，读取 `details` 前的 `message`）、`sendCode`（按用途切换防枚举文案，重置用途使用「如果该邮箱已注册…」）、`loadPolicy`（注册关闭隐藏「创建账号」，展示服务器名与邮件可用性）、`updateDeviceInfo`（Bearer 调用 `/v1/device/info?userCode=`，300ms 防抖）、`enterApprove`（登录/注册/验证码登录共用，无授权码时提示回到上方填写）、`renderExpiry`/`startExpiry`/`stopExpiry`（按 `expiresAt` 逐秒倒计时，到期禁用批准/拒绝）、`switchTab`。
  - 事件：登录、验证码登录、注册、重置（校验两次密码一致；成功后清空 `state.token`/`state.email` 并回登录态）、批准/拒绝、使用其他账号（保留授权码、清除令牌）、授权码输入同步 `#approveCode` 与设备信息查询。
  - 初始化：`?code=` 预填；`?mode=reset` 直达重置面板；调用 `loadPolicy()`。
- `server/src/Mosaic.Server/ServerApplication.cs`（`CreateAsync`）：`PublicUrl` 为回环地址时通过 `app.Logger.LogWarning` 提示公网部署需设置对外 HTTPS 地址；仅日志，不改变启动与协议行为。
- `server/tests/Mosaic.Server.Tests/DeviceFlowTests.cs`：新增 `Device_page_hosts_password_reset_and_shared_code_entry`，断言重置入口/表单/端点、`mode=reset` 处理、策略与设备信息调用，以及 `#codeBlock` 位于 `#authView` 之外且在其之前。
- `server/README.md`：新增《浏览器登录（`/device` 页面）》小节（`verificationUriComplete` 打开方式、`?code=`/`?mode=reset`、策略与设备信息、`MOSAIC_PUBLIC_URL` 必需配置与启动警告），测试计数更新为 82。
- `docs/server-implementation-status.md`：本节。

无 API、schema、迁移变化；既有安装升级不需要数据迁移。

### 验证与未验证

已验证：

- `dotnet build server/Mosaic.Server.sln`：0 警告 0 错误；`dotnet test`：82/82 通过。
- 真实进程 + 浏览器（本地 5080）：`/device?mode=reset&code=` 直达重置面板并预填授权码；`/device` 裸页登录/注册/忘记密码切换正常，授权码区块保持可见；策略行显示「服务器：Mosaic · 邮件验证码暂不可用，请使用密码登录」；空表单登录显示服务端错误文案；未配置 SMTP 时「获取验证码」显示统一 503 文案；两次密码不一致被前端拦截；浏览器控制台无脚本错误。

未验证：

- 真实 SMTP 下的重置邮件投递与整条重置链路（端点本身已有测试，页面仅走同一 API）。
- 倒计时的真实到期禁用（只做了 HTML/脚本断言与代码核对；需要有效设备码 + 登录态才能端到端观察，未用真实账号走完）。
- 桌面端「忘记密码」按钮实际打开 `?mode=reset` 的行为（桌面侧尚未接入）。
- 生产 HTTPS 与真实域名下的 `MOSAIC_PUBLIC_URL` 配置。

### 参考实现核对

对照用户提供的 `D:\Code\jiaojianbox-latest`（`internal/api/auth.go:178-239`）：其 device/start（8 位 XXXX-XXXX、600s、interval=2）、poll 状态机、网页展示应用名/用户码/剩余时间、POST approve 需登录、deviceCode 归一化（去 -/空格、转大写、校验 8 位）均已由 Mosaic 现有实现覆盖；本次仅补齐页面上的「应用名 + 剩余时间」展示与保留「使用其他账号」。未照搬其存储方式：Mosaic 仍只存令牌哈希、设备授权以 AES-GCM 加密一次性交付、`slow_down` 返回 200 状态字段，响应形状与安全增强均按本协议，不采用 Go 版的明文令牌落库与自定义响应。

### 状态

按协调会话要求：未提交、未打 tag、未发布；当前版本号仍为 0.1.1，线上 0.1.1 不变。
