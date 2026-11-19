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
- 设备授权：`verificationUri = {MOSAIC_PUBLIC_URL}/device?code=XXXX-XXXX`，`expiresIn=600`，`interval=2`；状态 `pending|approved|denied|expired|consumed`，`approved` 仅能领取一次。
- 备份/同步 wire DTO 见 `server/README.md` 的 API 章节。`kind` 取值为 `task`、`setting`，与 `Mosaic.Sync.Contracts.SyncItemKinds` 一致。
- 未实现 `slow_down`；`interval` 为建议值，轮询过密由 `device.rate_limited` 兜底。

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
