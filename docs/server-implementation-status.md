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

## 0.1.2（其一）：浏览器登录页补齐注册/重置与共用授权码（2026-11-19）

用户裁决：桌面登录页不再保留邮箱+密码表单，「在浏览器中登录」直接用系统浏览器打开服务端 `/device`，注册、验证码、密码、忘记密码全部在网站完成。本条目提交于 `76b63c3`（2026-11-19），当时未单独提升版本号；随本次任务附件一并发布为 `0.1.2`。

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

已由 `76b63c3`（2026-11-19）提交；提交时按协调会话要求未提升版本号（`server/Directory.Build.props` 仍为 0.1.1）、未单独打 tag。本次任务附件改动把版本统一提升为 0.1.2 并一并发布，本条的版本号现状以此为准。

## 0.1.2（其二）：任务附件存储与 `/v1/attachments`（2026-11-24）

为桌面端「账号备份」提供附件通道：新增 `attachments` 表（迁移 3）与四个端点，附件二进制按用户与附件 ID 落盘，数据库表为权威清单。附件只由客户端显式上传/下载触发，不参与增量同步，任务的 revision 仍由任务通道管理。配套桌面侧实现与验收见 `docs/implementation-status.md` 的 0.19.0 条目。

### 根因位置

- 服务端只有 `users`/`sessions`/`device_auth`/`backups` 等表，没有附件表与目录约定（`server/src/Mosaic.Server/Data/Schema.cs` 迁移 1–2），客户端上传附件时没有任何可信落点，也没有清单可对齐。
- 全局请求体上限 `MOSAIC_MAX_BODY_BYTES = 16777216` 施加在整条管道（`ServerOptions.cs` 的 Kestrel 配置），单个 50 MiB 附件会在读 body 前被 413 拒绝。
- 备份表只覆盖设置与任务 JSON，不含附件元数据；删除账户的清理逻辑只处理数据库行（`SessionService.cs` 删除账户事务），磁盘侧若新增目录则无人回收。
- 缺少每用户配额与单件上限的服务端约束，客户端单方面限制无法阻止多客户端绕过。

### 改动位置

- `server/src/Mosaic.Server/Attachments/AttachmentPaths.cs`（新增）：`AttachmentPaths:9`、`MaxFileNameCharacters = 260:11`、`Root:13`、`UserDirectory:16`、`FileFor:19`、`NormalizeUserId:23`、`NormalizeAttachmentId:34`、`Extension:48`、`TryDeleteUserDirectory:71`。
- `server/src/Mosaic.Server/Attachments/AttachmentService.cs`（新增）：`AttachmentService:31`、`ListAsync:39`、`SaveAsync:61`、`OpenAsync:246`、`DeleteAsync:274`、`CopyAndHashAsync:313`、`MapItem:354`、`NormalizeAttachmentId:364`、`NormalizeTaskId:374`、`ValidateFileName:384`、`ValidateHash:409`、`TryDelete:425`。
- `server/src/Mosaic.Server/Attachments/AttachmentEndpoints.cs`（新增）：`AttachmentEndpoints:7`、`MapAttachmentEndpoints:9`（四个端点；PUT 提升 `IHttpMaxRequestBodySizeFeature` 到 `AttachmentsMaxBytes` 后再读流）。
- `server/src/Mosaic.Server/Data/Schema.cs:168-182`：`Migration3` 建 `attachments(id,user_id,task_id,file_name,size_bytes,sha256,created_at,updated_at)` 与索引 `ix_attachments_user(user_id, task_id)`。
- `server/src/Mosaic.Server/Data/DatabaseMigrator.cs:12`：迁移清单加入 `(3, "attachments", Schema.Migration3)`；`schema_migrations` 记录版本 3。
- `server/src/Mosaic.Server/ServerApplication.cs:4`、`:90`（`AddSingleton<AttachmentService>()`）、`:128`（`app.MapAttachmentEndpoints()`）。
- `server/src/Mosaic.Server/ServerOptions.cs:44`、`:47`、`:102-103`、`:188`（配额 1 GiB、单件 50 MiB、对应环境变量、`quota >= max` 校验）。
- `server/src/Mosaic.Server/Auth/SessionService.cs:2`、`:431-433`：账户删除事务提交后调用 `AttachmentPaths.TryDeleteUserDirectory`，失败只记日志。
- `server/tests/Mosaic.Server.Tests/AttachmentTests.cs`（新增，275 行）；`server/tests/Mosaic.Server.Tests/LoggingAndMigrationTests.cs` 迁移断言由 2 改为 3。
- `server/README.md`：`:1` 版本、`:50-52` 环境变量、`:133` 磁盘布局、`:230-248`《任务附件》、`:346-352` 错误码、`:389` 测试计数（91）。
- `server/Directory.Build.props:12`：`0.1.1` → `0.1.2`。

### 端点与行为

| 方法 | 路径 | 行为 |
| --- | --- | --- |
| GET | `/v1/attachments` | `{items:[{id,taskId,fileName,sizeBytes,sha256,createdAt,updatedAt}], usageBytes, quotaBytes, maxBytes}` |
| PUT | `/v1/attachments/{attachmentId}?taskId=&fileName=` | 原始流 body；新建 `201`；同内容重放 `200 {duplicate:true}` |
| GET | `/v1/attachments/{attachmentId}` | 原始字节，`Content-Disposition` 用清单文件名；行存在但文件缺失返回 `404 attachment.not_found` |
| DELETE | `/v1/attachments/{attachmentId}` | `204`；不存在同样 `204`（幂等） |

- 前置校验：`attachmentId`/`taskId` 必须 Guid（归一化为 32 位无连字符小写）；`fileName` 去空白后拒绝控制字符、`Path.GetInvalidFileNameChars()`、`.`/`..`、超过 260 字符；`X-Mosaic-Sha256` 若存在必须是 64 位十六进制。任一不过 → `400 attachment.invalid`（哈希格式除外为 `attachment.hash_mismatch`）。
- 路径：磁盘名只由 `attachmentId:N` 与白名单扩展名（2–17 位字母数字）组成，客户端文件名永不进入路径。
- 写序：流式写临时文件并同时算 SHA-256（`Content-Length` 或实际字节超 `MOSAIC_ATTACHMENT_MAX_BYTES` → `413 attachment.too_large`）→ 校验 `X-Mosaic-Sha256` → 配额检查（`SUM(size_bytes)` 覆盖前扣除自身旧值，超 `MOSAIC_ATTACHMENT_QUOTA_BYTES` → `413 attachment.quota_exceeded`）→ 事务写行 → 提交后原子 `File.Move`；`File.Move` 失败回滚行并删临时文件，返回 `500 attachment.storage_failed`。
- 去重：同用户、同 ID、同 `taskId`/`fileName`/大小/哈希且文件仍在 → `200 {duplicate:true}`，不重写；同 ID 不同内容 → 覆盖并删旧扩展名文件；不同用户同 ID → `409 attachment.conflict`（不泄露对方是否持有）。
- 错误码：`attachment.invalid`、`attachment.hash_mismatch`、`attachment.not_found`、`attachment.conflict`、`attachment.too_large`、`attachment.quota_exceeded`、`attachment.storage_failed`。
- 下载当前为整体响应，不支持 `Range`；客户端自行校验 SHA-256。

### 迁移与升级影响

- 迁移 3 为纯增量 DDL（`CREATE TABLE IF NOT EXISTS` + `CREATE INDEX IF NOT EXISTS`），无数据回填；迁移 1–2 的库启动后 `schema_migrations` 出现版本 3，备份与用户数据不变。
- `attachments` 目录首次上传时按需创建（`<数据目录>/attachments/<userId:N>/`），旧安装不存在该目录不影响启动，无需新增配置项。
- 删除账户：行随 `users` 级联删除（外键在 `Migration3` 中声明），目录在事务提交后清理；目录删除失败仅浪费磁盘，不影响删除结果。
- 新增环境变量均有默认值，旧部署的 `appsettings`/容器环境无需修改；反向代理需把 `client_max_body_size` 放到 ≥ `MOSAIC_ATTACHMENT_MAX_BYTES`，否则大附件在代理层被拒。

### 验收项

1. 用迁移 2 的旧数据目录启动 → 日志出现迁移 3 应用记录，`attachments` 表存在且为空，`/v1/backups` 仍可正常往返。
2. 上传一件附件 → 目录出现 `<attachmentId:N><ext>`，`GET /v1/attachments` 的 `usageBytes` 增加对应大小；重复上传返回 `200 {duplicate:true}` 且文件 mtime 不变。
3. 上传超过 `MOSAIC_ATTACHMENT_MAX_BYTES` 的 body → `413 attachment.too_large`，目录内无残留临时文件。
4. 上传直到 `usageBytes` 逼近 `MOSAIC_ATTACHMENT_QUOTA_BYTES` → 下一件返回 `413 attachment.quota_exceeded`。
5. `X-Mosaic-Sha256` 填错 → `400 attachment.hash_mismatch`；`fileName=../x` 或非 Guid ID → `400 attachment.invalid`。
6. 下载 → 内容与上传字节一致、SHA-256 相同；删除 → `204`，再删仍 `204`，清单项消失。
7. 删除账户 → `attachments` 行清空、用户目录被删；重新注册同邮箱不受影响。
8. 两台设备并发写同一 ID 不同内容 → 一方成功、另一方内容被覆盖后清单只有一个哈希，无残留临时文件。

### 验证与未验证

已验证：`dotnet test server/tests/Mosaic.Server.Tests/Mosaic.Server.Tests.csproj` → 91/91 通过（含新增 9 组附件用例与迁移 3 幂等用例）；`dotnet build Mosaic.Server.sln` 0 警告 0 错误；本机 Kestrel 冒烟覆盖清单/PUT/GET/DELETE 与错误码。

未验证：真实 Linux 自包含包与 Docker 镜像下的附件目录权限与磁盘配额；反向代理（Nginx/Caddy）后的 50 MiB 上传；多实例共享同一数据目录（当前设计为单进程 + 本地 SQLite，不支持多实例并发写）；`Range` 断点续传（未实现）。

### 状态

随桌面 0.19.0 一并提交与发布；版本号 `0.1.2`，协议仍为 `/v1`。

### 回滚方式

`git revert` 本次提交并回退 `server/Directory.Build.props:12` 至 `0.1.1`。已升级的库可继续以 0.1.1 运行（旧代码不读取 `attachments` 表）；如需彻底清理：停服务 → 备份数据目录 → `DROP TABLE attachments; DELETE FROM schema_migrations WHERE version = 3;` → 删除 `<数据目录>/attachments/`。

## 0.1.2（其三）：Linux 原地升级脚本与发布包（2026-11-24）

### 根因位置

线上实例 `https://mosaic.bansonic.top`（DNS → `47.112.187.172`，nginx 反代 → `127.0.0.1:5080`）在 2026-11-24T13:01Z 的 `/health` 返回 `"version":"0.1.1"`，即线上仍在运行 0.1.2（其二）之前的构建，`attachments` 表与 `/v1/attachments` 端点不存在。

升级通道缺失：`server/README.md` 的部署章节（原「把输出目录拷到服务器后」段落）与 `server/publish-linux.ps1:31-37` 只打印启动命令，没有任何停服、备份、目录替换、健康检查与回滚步骤；仓库没有 `.github/` 工作流（`gh workflow list` 为空），也没有 Coolify/脚本化部署资产。按现有文档升级只能人工 `rsync`，容易漏掉 `<数据目录>/master.key` 与 `<数据目录>/attachments/` 的备份，也没有「版本号与迁移版本是否到位」的判定标准。

### 改动位置

- `server/deploy/upgrade-linux.sh`（新增）：root 权限执行的原地升级脚本。`die`/`read_env` 为辅助函数；`PKG_VERSION` 从包名解析版本；步骤依次为校验（`sha256sum -c`）→ 探测（`systemctl show -p ExecStart/EnvironmentFiles/User` 得到程序目录、`MOSAIC_DATA_DIR`、`MOSAIC_DATABASE_PATH`、服务用户；未配置时回落 `<程序目录>/data` → `/var/lib/mosaic-server`）→ 磁盘余量检查（包体积 + 解压体积 + 数据目录体积）→ `systemctl stop` 后备份（数据库与 `-wal`/`-shm`、`master.key`、`attachments/`）→ 目录整体替换（旧目录改名 `<APP_DIR>.previous-<STAMP>`，新目录解压后 `chmod +x` 与 `chown -R`）→ `systemctl start` 并轮询 `/health` 60 秒且比对版本 → 打印 `schema_migrations`/`attachments` 表/附件文件数/nginx `client_max_body_size`。健康检查失败调用 `rollback()`：停服、新目录改名 `<APP_DIR>.failed-<STAMP>`、旧目录换回、重启。
- `server/README.md`：新增「### 升级（原地替换）」小节，写明单包发布形态、脚本执行命令、六步流程、可覆盖的环境变量与数据回滚语句。
- `server/artifacts/Mosaic.Server-0.1.2-linux-x64.tar.gz`（派生产物，`artifacts/` 已在 `.gitignore:3`）：`tar -C linux-x64 -czf` 打包 341 个文件，48,849,064 字节，SHA-256 `67fda52481a9a62855c0a5327896dae4c5fb3bcf3edaf65a362e52db431611ad`；同目录 `SHA256SUMS` 记录该行。

### 本机验证

1. `dotnet publish server/src/Mosaic.Server/Mosaic.Server.csproj -c Release -r linux-x64 --self-contained true -o server/artifacts/linux-x64` → 退出码 0，108 MB / 341 文件；`Mosaic.Server.dll` 内含 `0.1.2`；包内 `Mosaic.Server` 为 78,256 字节的 linux-x64 单文件宿主。
2. 全新数据目录（`MOSAIC_DATA_DIR=server/artifacts/smoke-data`）从源码启动 → `GET /health` 返回 `"version":"0.1.2+2c60018a..."`；SQLite 内 `schema_migrations` = `1 initial, 2 session_management_and_sync_floor, 3 attachments`，`attachments` 表存在，`master.key` 自动生成；`GET /v1/attachments/<guid>` 返回 `401`（未携带访问令牌）。
3. 升级路径模拟：停服 → 删除 `schema_migrations` 中 `version=3` 的行并 `DROP TABLE attachments`（还原成 0.1.1 的库形态）→ 用 0.1.2 启动 → 自动补回迁移 3 与 `attachments` 表，`master.key` 复用未报错，`/health` 仍为 0.1.2。迁移 3 是纯增量 DDL（`Schema.cs:172-183`），不触碰既有行。
4. `dotnet test server/tests/Mosaic.Server.Tests/Mosaic.Server.Tests.csproj -c Release --filter "FullyQualifiedName~Attachment"` → 9/9 通过（约 6 秒）。未跑全量（`Mosaic.Server.Tests` 全量 91 个用例的上一轮结果见 0.1.2（其二））。
5. `sh -n server/deploy/upgrade-linux.sh` 通过；本机无 WSL 发行版，无法用 `dash` 复验，也未在真实 systemd 主机上执行过脚本本身。

### 未验证

- `upgrade-linux.sh` 在真实 Linux + systemd 上的完整执行（目录改名、`chown`、`/health` 轮询、失败回滚分支）。
- 线上 47.112.187.172 的 nginx `client_max_body_size` 是否 ≥ 50 MiB；脚本只在装了 nginx 时打印该项，不会修改配置。
- 反向代理之后的 50 MiB 附件上传、真实两设备附件往返、`attachments/` 目录在 `User=mosaic` 下的写权限与磁盘配额。

### 线上替换的访问前提

替换线上实例需要对 `bansonic.top`（`47.112.187.172`）的 SSH 访问。本次探测：`ssh -o BatchMode=yes root@bansonic.top` 返回 `Permission denied (publickey,gssapi-keyex,gssapi-with-mic,password)`，默认 `~/.ssh/id_rsa` 未被接受；仓库与本地均无部署脚本、CI 或凭据。在拿到可用登录方式（用户名 + 端口 + 密钥或口令）或由操作者在面板上执行脚本之前，线上保持 0.1.1。

### 线上替换步骤（拿到访问后执行）

1. 上传 `Mosaic.Server-0.1.2-linux-x64.tar.gz` 与 `SHA256SUMS` 到服务器任意目录（例如 `/root/mosaic-upgrade/`）。
2. `sudo sh upgrade-linux.sh Mosaic.Server-0.1.2-linux-x64.tar.gz SHA256SUMS`（脚本在仓库 `server/deploy/upgrade-linux.sh`，并随 v0.19.0 发布附件提供）。
3. 核验：`curl -fsS https://mosaic.bansonic.top/health` 应含 `"version":"0.1.2`；`sqlite3 <数据目录>/mosaic-server.sqlite3 "select * from schema_migrations"` 应含 `3|attachments`；`nginx -T | grep client_max_body_size` 应 ≥ `50m`。
4. 失败回滚：脚本自动完成；手工回滚按脚本结尾打印的三步（停服 → 换回 `<程序目录>.previous-<STAMP>` → 启动），数据回滚见「升级（原地替换）」小节。
