# Mosaic 账号、邮箱验证码注册、服务端备份与手机同步规划

状态：架构方案；本会话只负责规划与交接。服务端由“服务端”会话实施，桌面客户端由“当前项目架构梳理”会话实施，手机端另起新会话规划。

## 1. 已确认的产品决策

1. 服务端采用 ASP.NET Core/.NET 10，可部署到 Linux：发布 `linux-x64` 自包含包或 Docker 镜像。
2. 客户端登录是可选的：未登录继续本地离线使用；登录入口按参考项目截图呈现“在浏览器中登录 / 连接其他自建服务器 / 不连接服务器，离线使用 / 忘记密码”。
3. 注册时使用邮箱验证码和自设密码；验证码与密码都不能进入设置持久化或日志。
4. 第一版备份同时覆盖任务和可移植的用户设置；不同步机器相关的宿主设置。
5. 手机端本次不实施，但服务端协议需要预留 Device Flow 和同步 wire DTO。

## 2. 参考项目学习结论

参考项目 `D:\Code\jiaojianbox-latest` 当前真正运行的是 Go 服务端；`server/` 下 Python/FastAPI 是历史兼容夹具，不能照搬。

可借鉴：

- 邮箱/用户名规范化、10–256 位密码、PBKDF2-HMAC-SHA256 310000 次、16 字节盐、32 字节输出、常量时间比较。
- 256-bit 一次性邮件令牌，只存 SHA-256，30 分钟有效。
- 数据库保存 opaque Bearer 会话令牌哈希，令牌不写数据库明文；30 天固定过期；退出只撤销当前会话。
- SMTP 凭据加密存储、读接口不返回密码；邮件链接使用配置的 `public_url`，不信任请求 Host。
- 认证接口按 IP 限流；发送邮件失败统一 503；忘记密码无论账号是否存在返回相同文案。
- Windows 桌面令牌使用 DPAPI，Android 使用 Keystore；截图中的桌面登录采用 Device Flow：桌面显示短授权码并轮询，浏览器登录并确认授权。

必须改进，不能照搬的缺陷：

- 参考项目没有数字邮箱验证码，只有邮件链接；本需求必须实现 6 位验证码。
- 注册接口会暴露账号是否已注册；新服务端的发送验证码和注册确认应对外返回一致响应。
- 缺少按邮箱限流和 60 秒重发间隔；新服务端必须同时按 IP 和邮箱限制。
- 修改密码后仍可用未失效的重置令牌；新服务端必须撤销全部邮件码和会话。
- 反向代理下 `RemoteAddr` 限流会误伤；新服务端只在可信代理配置存在时解析转发头。
- WebView 的 localStorage 不适合保存令牌；桌面使用 DPAPI/Credential Manager。

截图明确的交互语义：

- 桌面先展示品牌和“登录文件箱”卡片，主按钮“在浏览器中登录”。
- 提供“连接其他自建服务器”和“不连接服务器，离线使用”。
- Device Flow 第二步在浏览器显示“授权桌面客户端”、当前账号和类似 `BAAD-C832` 的授权码，用户点“允许登录/取消”。
- 手机端复用同一账号体系和 Device Flow；桌面显示授权码，手机或浏览器确认授权。

## 3. 服务端规划（交给“服务端”会话）

### 3.1 项目与部署

- 新建独立 `server/`，不要让 Desktop 引用服务端程序集；`src` 的架构边界测试继续只覆盖客户端。
- ASP.NET Core 10，SQLite 作为默认轻量部署库；连接串、SMTP、公开 URL、注册模式和主密钥通过环境变量或 Docker secrets 提供。
- 提供 `Dockerfile`、Linux `linux-x64` 发布脚本、健康检查 `/health` 和本地开发配置；不要提交任何真实 SMTP 密码、主密钥或用户数据。
- 数据库文件默认在服务端数据目录，迁移由服务端启动时执行；升级只能新增迁移，不破坏已有账号和备份。

### 3.2 数据库

建议表：

- `users(id, email UNIQUE COLLATE NOCASE, password_salt, password_hash, created_at, email_verified_at)`。
- `sessions(id, user_id, token_hash UNIQUE, created_at, expires_at, revoked_at, device_label)`；只保存 token hash。
- `refresh_tokens(id, user_id, token_hash UNIQUE, family_id, expires_at, revoked_at, replaced_by)`；支持轮换和重用检测。
- `email_codes(id, email_hash, purpose, code_hash, payload_json, expires_at, attempts, consumed_at, last_sent_at, created_at)`；`purpose` 为 register/login/reset；只存哈希和尝试次数。
- `device_auth(id, user_code_hash UNIQUE, device_code_hash UNIQUE, expires_at, state, user_id, session_id, created_at)`；state 为 pending/approved/denied/consumed。
- `backups(id, user_id, device_id, base_snapshot_id, content_sha256, created_at, size_bytes)`。
- `backup_items(id, backup_id, kind, item_key, payload_json, revision)`；kind 为 task 或 setting。
- `sync_cursors(user_id, device_id, cursor, updated_at)`。
- `sync_changes(seq INTEGER PRIMARY KEY AUTOINCREMENT, user_id, kind, item_key, server_revision, payload_json, created_at, device_id)`。
- `settings_audit(id, user_id, action, created_at)`；不要存令牌或验证码。

服务端从 access/refresh token 推导 `user_id`，任何端点的请求体都不能携带他人的 `user_id` 作为权威身份。

### 3.3 密码、验证码与会话

- 密码：PBKDF2-HMAC-SHA256，310000 次，16 字节随机盐，32 字节输出，常量时间比较；Unicode 字符长度 10–256。
- 邮箱：去首尾空格、转小写、长度 ≤254、基本地址格式校验；不做 Gmail 点号/plus 规范化。
- 邮箱验证码：6 位数字，10 分钟有效，最多 5 次校验；按 email+purpose 的 60 秒重发间隔；按 IP 和邮箱双重限流；数据库只存带 pepper 的 SHA-256 或 HMAC；验证成功立即消费；并发消费使用条件 UPDATE 并检查影响行数。
- 发送验证码接口对未注册邮箱和已注册邮箱返回统一文案；注册确认和登录验证码校验不得暴露账号枚举。
- access token 15 分钟，refresh token 14 天且轮换；refresh 重用检测撤销整个 token family；数据库只存 token hash。
- 退出撤销当前 refresh family；改密、重置密码撤销全部会话、refresh token、未消费验证码和设备授权。
- SMTP 使用 TLS 1.2+，支持 SSL/STARTTLS；邮件密码加密存储；读配置只返回 `hasPassword`；发送失败返回 503，不把 SMTP 错误原文回传客户端。
- 所有认证接口记录不含秘密的相关 ID、IP hash、结果和耗时；绝不记录验证码、密码、令牌或完整邮件正文。

### 3.4 API

统一前缀 `/v1`，错误响应 `{ "code": "...", "message": "...", "traceId": "..." }`，不用中文 `detail` 文本承载机器逻辑。

认证：

- `GET /v1/auth/policy` → 注册是否开放、是否要求邮箱验证、服务端公开名称。
- `POST /v1/auth/email-code` → `{email, purpose}`；发送验证码，统一 202 响应。
- `POST /v1/auth/register` → `{email, password, code}`；校验 register 码，创建账号并直接返回 session。
- `POST /v1/auth/login` → `{email, password}`；返回 session；可选支持 login 验证码作为二次验证。
- `POST /v1/auth/login/code` → `{email, code}`；使用 login 码登录或作为 MFA。
- `POST /v1/auth/refresh` → `{refreshToken}`；轮换 refresh，返回新 session。
- `POST /v1/auth/logout` → 撤销当前 family。
- `POST /v1/auth/forgot-password` / `POST /v1/auth/reset-password`。
- `GET /v1/auth/me`。
- `GET /v1/auth/sessions`：返回当前账号的会话/设备列表（id、deviceLabel、platform、createdAt、lastSeenAt、expiresAt、current），不返回 token。
- `POST /v1/auth/sessions/{id}/revoke`：撤销指定会话；`POST /v1/auth/sessions/revoke-all` 撤销全部会话（可带 `keepCurrent=true`）。
- `POST /v1/auth/account/delete`：账号删除，要求当前密码或新鲜的邮箱验证码（服务端定义用途名，见 server/README.md）；失败 401 `auth.reauth_required`；删除账号、会话、refresh token、邮件码、设备授权、备份、同步变更和游标；审计日志匿名化为 `deleted:<emailHash16>` 并写 `account.delete`，成功 204。手机商店要求应用内可达，桌面账户页也提供入口。
- 错误包统一为 `{code,message,traceId,details?}`；`details` 为可选 JSON 对象，旧客户端必须忽略未知字段，且其中不得含令牌、验证码或密码。

Device Flow（截图交互）：

- `POST /v1/device/start` → `deviceCode`、`userCode`（8 位短码，服务端字母表为 `BCDFGHJKLMNPQRSTVWXZ23456789`，排除 A/E/I/O/U 等易混淆字符；`BAAD-C832` 仅为参考截图示意）、`verificationUri`、`verificationUriComplete`、`expiresIn=600`、`interval=2`；请求可带 `deviceLabel`、`platform`、`appVersion`。`verificationUri` 是不带码的 `{PUBLIC_URL}/device`，授权码只出现在 `verificationUriComplete`；客户端要打开带码链接必须读 `verificationUriComplete`，不能把 `verificationUri` 当带码链接用。
- `POST /v1/device/poll` → `{status:"pending|approved|denied|expired|consumed", session?}`；approved 时 session 放在响应内 `session` 键下一次交付；轮询快于 interval 时返回 200 `{status:"slow_down", interval:2}`（不是 429，429 仅保留 `device.rate_limited` 兜底），客户端收到后按返回的 `interval` 递增轮询间隔。
- `POST /v1/device/approve` / `POST /v1/device/deny` → 已登录用户在浏览器/手机确认；`GET /v1/device/info?userCode=...` 需要登录，返回发起设备的 label/platform/state/expiresAt，供手机批准页展示。
- 桌面只显示 userCode；deviceCode 和令牌不放 URL、不显示在 UI、不写日志。

备份：

- `PUT /v1/backups` → `{deviceId, baseSnapshotId?, items:[{kind,key,payload,revision}]}`；服务端校验配额、去重、计算内容哈希，返回 `snapshotId`、`serverRevision`、`cursor`。服务端 0.1.1 起 `kind=setting` 条目强制 14 键白名单（见第 4.3 节），非白名单键返回 `400 backup.invalid`；task 条目不受限，旧快照不改写。完整错误码总表见 `server/README.md`。
- `GET /v1/backups/latest` → 快照元数据、`serverRevision`、`cursor` 和下载清单；手机首次引导用它拿一致基线。
- `GET /v1/backups/{snapshotId}` → 完整快照；下载需要再次验证当前账号权限。
- `POST /v1/backups/{snapshotId}/restore` 可由客户端显式触发下载；不要在服务端直接改写客户端数据库。

增量同步（供手机端预留，第一版手机只读）：

- `GET /v1/sync/changes?cursor=&limit=200` → `{changes, nextCursor, hasMore}`；空 cursor 从 0 开始。cursor 格式非法返回 `400 sync.invalid`；cursor 早于服务端历史下限（`MOSAIC_SYNC_HISTORY_DAYS`，默认 90 天）返回 `410 sync.cursor_invalid`，`details.requiresSnapshot=true` 时客户端改走 `/v1/backups/latest`。
- `POST /v1/sync/changes` → **单条** `{deviceId,kind,key,payload,deleted,baseServerRevision,clientRevision,idempotencyKey}`；响应 `{change:{cursor,serverRevision,kind,key,deleted,payload,deviceId,createdAt},replayed}`；服务端按 `user_id + idempotencyKey` 去重，冲突返回 `409 sync.conflict`，`details.current` 为服务器当前条目。
- ChangeDto 冻结为：`{cursor,serverRevision,kind,key,deleted,payload,deviceId,createdAt}`；`kind` 为 `task|setting`，`deleted` 为布尔值；删除时 `payload=null` 但保留 key/version；任务 payload 是完整 `TaskItemDto` 形状；设置 payload 固定为 `{value:<json 标量>}`。第一批同步使用空 cursor；不提供批量 POST，客户端逐条调用并用 idempotencyKey 重试。
- 删除通过 tombstone 传播；创建使用幂等键；服务端分配单调递增 `serverRevision`，`cursor` 是不透明字符串；不把客户端本地 `Revision` 当全局版本。
- 服务端 0.1.1 实际 canonical 错误码：`auth.invalid_credentials`、`auth.invalid_code`、`auth.code_expired`、`auth.code_attempts_exceeded`、`auth.weak_password`、`auth.rate_limited`、`auth.registration_closed`、`auth.required`、`auth.session_revoked`、`auth.session_expired`、`auth.session_not_found`（撤销不存在的会话）、`auth.invalid_refresh`、`auth.refresh_reused`、`auth.reauth_required`（账号删除/敏感操作重新认证失败）、`device.invalid_code`、`device.rate_limited`、`backup.*`、`sync.invalid`、`sync.cursor_invalid`、`sync.conflict`。注意：账号删除是级联真删，不保留墓碑，旧令牌因此解析不到账户，返回 `auth.required` 而非 `auth.session_revoked`——客户端把两者都映射到"重新登录"即可。兼容别名 `auth.code_invalid`、`auth.password_policy`、`sync.invalid`（服务端 0.1.0）已由客户端映射层兼容。

### 3.5 测试

服务端至少覆盖：验证码重发/过期/尝试次数/并发消费/重放；密码哈希参数；账号枚举防护；登录和 refresh 轮换/重用检测；改密撤销；Device Flow 状态机；按 IP 和邮箱限流；备份隔离、配额、sha256、恢复校验；同步游标、冲突、tombstone、幂等；Linux 容器启动和迁移；日志中不出现秘密。

## 4. 桌面客户端规划（交给“当前项目架构梳理”会话）

### 4.1 能力模块

按 Mosaic 的“一项职责一个所有者”新增独立模块族，不把网络/密码塞进 Shell、Settings 或 Tasks：

- `Mosaic.Auth.Contracts` → `auth.api@1`：登录、注册、验证码、会话、退出、设备流状态。
- `Mosaic.Auth.Core` → 认证状态和错误映射；依赖 transport/secrets。
- `Mosaic.Auth.Http` → `auth.transport@1`，唯一拥有 Auth `HttpClient`。
- `Mosaic.Auth.Windows` → `auth.secrets@1`，DPAPI 或 Credential Manager；只保存 refresh token、device id 和账户标识，密码/验证码绝不落盘。
- `Mosaic.Auth.Wpf` → `auth.ui@1`，登录/注册/验证码/账户页、Device Flow 展示与轮询。

- `Mosaic.Sync.Contracts` → `sync.api@1`、wire DTO 与备份策略。
- `Mosaic.Sync.Core` → 上传、下载、重试、冲突策略；依赖 `sync.transport@1`、`auth.api@1` 和 Tasks 的传输能力。
- `Mosaic.Sync.Http` → `sync.transport@1`，唯一拥有 Sync `HttpClient`。
- `Mosaic.Sync.Windows` 或 `Mosaic.Sync.Sqlite` → 本地游标、设备 ID、outbox；只访问自己的表。
- 可选 `Mosaic.Sync.Wpf` → 同步状态和备份按钮；也可合并到账户页。

Tasks 只增加明确的新能力，不改现有 `ITasksApi` 签名：

- `tasks.transfer@1`：全量导出和显式恢复；导出复用 `ITasksApi.ListAsync(All, ArchiveScope.All)`。
- 恢复需要新的 `ITaskTransferRepository`，在一个事务中按原始 ID/Revision/时间戳替换，避免丢失 ID、Revision、归档时间和幂等回执。
- 增量同步第二阶段再增加 `tasks.sync@1`、change log、tombstone 和同步游标；第一阶段不直接复制 `mosaic.sqlite`。

### 4.2 UI 与本地优先

- 登录是可选能力：应用启动仍直接进入现有 Shell；新增“账户/同步”导航页，或按截图提供登录卡片和离线按钮。
- 不在启动时强制登录，避免破坏离线使用、桌面驻留和现有 Desktop Smoke。
- “在浏览器中登录”采用 Device Flow：桌面生成 userCode 并轮询，浏览器完成邮箱验证码注册/登录并授权；桌面收到 approved 后保存会话。
- “连接其他自建服务器”允许修改服务端 URL；URL 必须 HTTPS，开发例外只能显式配置且不能用于正式发布。
- “不连接服务器，离线使用”保持当前全部本地能力。
- 注册页字段：邮箱、6 位验证码、密码、确认密码、发送验证码/重新发送倒计时；密码强度与错误反馈明确。
- 设置页只放非秘密项：`auth.serverUrl`、`sync.enabled`、`sync.backupOnExit`、`sync.intervalMinutes`、可移植设置白名单。密码、验证码、access/refresh token 不进入 `settings` 表、`appsettings.json`、`settings.pending.json` 或日志。

### 4.3 备份范围

用户要求“任务与设置都同步”，但必须区分：

- 可备份：Tasks 的全部任务字段（含 ID、Revision、完成/归档/恢复时间）、用户设置白名单（任务筛选、显示、主题、文案、已明确可移植的任务 UI 参数）。
- 设置白名单由桌面端拥有并版本化；手机端硬编码“已知键 + 类型”，未知键忽略。第一版白名单为：`ui.language`、`appearance.theme`、`tasks.sortOrder`、`tasks.defaultFilter`、`tasks.archive.autoEnabled`、`tasks.archive.delayDays`、`tasks.title.maxCharacters`、`tasks.notes.maxCharacters`、`tasks.title.required`、`tasks.defaultCompleted`、`tasks.confirmDelete`、`tasks.confirmUnsaved`、`tasks.showCharacterCount`、`tasks.autoSwitchToAllOnHidden`。
- 不备份：HostConfig、存储路径、窗口尺寸和设备相关设置、开机自启、桌面驻留状态、`tasks.revisionStart`、`tasks.floating.allowNotesEditing`、`shell.*`、`storage.*`、`startup.*`、`desktop-presence.*`、`floating-windows.*`、`auth.*`、`sync.*`、日志、`settings.pending.json`、令牌、SMTP 配置和任何机器路径。
- 第一版用显式“上传备份/下载并覆盖恢复”，不做静默双向覆盖；用户必须确认覆盖本地数据。
- 后续手机同步再启用游标和字段级冲突处理；本地 Revision 不是全局版本，不能“谁大谁覆盖”。

### 4.4 宿主与架构同步

新增模块必须同步：

- `src/Mosaic.Desktop/BuiltInModuleCatalog.cs` 的 Entry 与 `ModuleRuntimeContext`。
- `src/Mosaic.Desktop/appsettings.json` 的模块和 provider 绑定；可选功能默认装配但功能关闭，避免 stale binding。
- `AppConfiguration.cs` 的一次性配置迁移标记、`HostSettingsCatalog.cs` 的非秘密默认值。
- `build/project-boundaries.json`、`build/public-api.txt`。
- `tests/Mosaic.Architecture.Tests/BoundaryTests.cs` 的 WPF 项目列表、SQL 白名单、设置归属。
- Settings 的 `SettingsGroup`/`SettingsSection` 若要新增分区必须同步本地化和映射；更简洁的做法是把非秘密同步设置放入现有“存储与系统”分区。
- Desktop Smoke 覆盖：离线默认流程不回归、账户页打开、表单校验、假服务端/本地测试服务器完成注册与登录、Device Flow、上传备份、恢复确认、退出后令牌不泄漏。

## 5. 手机端规划（另起新会话）

手机端第一版只预留，不实施：

- Android/iOS 复用同一 `/v1` wire DTO 和 Device Flow；手机既可作为授权确认端，也可作为同步客户端。
- 令牌使用 Android Keystore / iOS Keychain；不写 localStorage 或普通文件。
- 第一版能力：邮箱验证码注册/登录、授权桌面、读取同步变更、任务与可移植设置拉取；写入和冲突 UI 可放第二阶段。
- 需要独立会话先确定 Android/iOS 技术栈、最低系统版本、离线策略、推送通知与应用商店隐私要求，再开始实施。

## 6. 交接边界

- 服务端会话拥有 `server/` 全部文件、独立解决方案、服务端测试、Docker/Linux 发布脚本和服务端文档；不得修改 `src` 与 MSI。
- 桌面会话拥有 `src/`、`tests/`、`build/`、桌面文档和 MSI；不得修改 `server/`。
- 两个会话共同遵守本文件的 `/v1` 协议与 DTO 字段；任何字段变更必须先在本文件更新并通知对方。
- 发布必须分两条线：服务端可独立发布 Linux 包/镜像版本号；桌面端沿用 Mosaic Release/MSI 链，不能修改已发布历史条目。
- 手机端只消费 `/v1` 协议，不读取 Mosaic 本地 SQLite schema。

## 7. 根因/改动位置记录要求

按 `AGENTS.md`，两个实施会话的最终答复和 `docs/implementation-status.md`（服务端文档可另建但需相互引用）必须写明：

- 根因位置：当前没有认证/网络/服务端/凭据保护能力；设置持久化是明文；Tasks 只有本地 Revision 且物理删除，无法安全跨设备同步。具体文件与行号见第 4 节调查结论。
- 改动位置：每个新增/修改文件及其中类型、方法或行号，新增文件同样列出。
- 明确列出已验证与未验证的 Linux 服务器、真实 SMTP、手机端、TLS、多设备冲突场景。
