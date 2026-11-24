# Mosaic 服务端（0.1.2）

Mosaic 的可选自建账号与同步服务端：邮箱验证码注册/登录、密码与令牌、Device Flow 浏览器授权、任务与可移植设置的备份/恢复、增量同步。桌面客户端未登录时仍可完全离线使用。

- 运行时：.NET 10 / ASP.NET Core，单进程 + SQLite（WAL）。
- 部署：Linux `linux-x64` 自包含包，或 Docker 镜像。
- 协议：`/v1/auth/*`、`/v1/device/*`、`/v1/backups/*`、`/v1/attachments/*`、`/v1/sync/changes*`，字段定义见下文。
- 规划文档：`../docs/auth-sync-server-plan.md`；实施记录：`../docs/server-implementation-status.md`。

```
server/
  src/Mosaic.Server/          服务端源码
  tests/Mosaic.Server.Tests/  端到端测试（真实 Kestrel + 临时 SQLite + 内存邮件）
  Dockerfile                  多阶段构建，非 root 运行，内置健康检查
  publish-linux.ps1 / .sh     linux-x64 自包含发布
  Mosaic.Server.sln
```

## 快速开始

```powershell
# 本地开发（数据默认写到 bin 输出目录下的 data/）
dotnet run --project src/Mosaic.Server --urls http://127.0.0.1:5080
```

```powershell
# 运行全部服务端测试
dotnet test Mosaic.Server.sln
```

未配置 SMTP 时，`/v1/auth/email-code` 会统一返回 `503 email.not_configured`，健康检查、策略与设备授权页仍可使用。

## 配置

所有配置来自环境变量（Docker secrets 可挂载为环境变量）与同目录 `appsettings.json`；环境变量优先。

| 变量 | 默认值 | 说明 |
| --- | --- | --- |
| `ASPNETCORE_URLS` | `http://0.0.0.0:5080`（appsettings） | 监听地址；Docker 镜像默认 `http://0.0.0.0:8080` |
| `MOSAIC_DATA_DIR` | `<程序目录>/data` | 数据目录，SQLite、`master.key` 都在这里 |
| `MOSAIC_DATABASE_PATH` | `<数据目录>/mosaic-server.sqlite3` | 可单独指定数据库文件 |
| `MOSAIC_MASTER_KEY` | 自动生成 `master.key` | 32 字节，base64 或 base64url；用于令牌哈希之外的所有密钥派生 |
| `MOSAIC_PUBLIC_URL` | `http://localhost:5080` | 对外地址，用于设备授权页链接；反向代理后请填公网 HTTPS 地址 |
| `MOSAIC_SERVER_NAME` | `Mosaic` | 邮件与页面显示名 |
| `MOSAIC_REGISTRATION_MODE` | `open` | `open` / `closed`；关闭后注册返回 `403`，注册验证码不再发送 |
| `MOSAIC_ADMIN_TOKEN` | 未设置 | 设置后启用 `/v1/admin/mail`；未设置时该路径返回 404 |
| `MOSAIC_TRUSTED_PROXIES` | 空 | 逗号分隔的反向代理 IP；设置后启用 `X-Forwarded-For/Proto` |
| `MOSAIC_BACKUP_QUOTA_BYTES` | `52428800` | 每用户备份总配额（50 MiB） |
| `MOSAIC_BACKUP_RETENTION` | `20` | 每用户保留快照数，超出删除最旧的 |
| `MOSAIC_MAX_BODY_BYTES` | `16777216` | 请求体上限（Kestrel，JSON 接口）；附件上传按请求单独放宽到 `MOSAIC_ATTACHMENT_MAX_BYTES` |
| `MOSAIC_ATTACHMENT_QUOTA_BYTES` | `1073741824` | 每用户任务附件总配额（1 GiB） |
| `MOSAIC_ATTACHMENT_MAX_BYTES` | `52428800` | 单个附件上限（50 MiB），必须 ≤ 配额 |
| `MOSAIC_ACCESS_TOKEN_SECONDS` | `900` | 访问令牌有效期（15 分钟） |
| `MOSAIC_REFRESH_TOKEN_SECONDS` | `1209600` | 刷新令牌有效期（14 天） |
| `MOSAIC_EMAIL_CODE_TTL_SECONDS` | `600` | 验证码有效期（10 分钟） |
| `MOSAIC_EMAIL_MAX_ATTEMPTS` | `5` | 单个验证码最大尝试次数 |
| `MOSAIC_EMAIL_RESEND_SECONDS` | `60` | 同邮箱同用途重发间隔 |
| `MOSAIC_EMAIL_ADDRESS_LIMIT_PER_HOUR` | `6` | 同邮箱同用途每小时上限 |
| `MOSAIC_EMAIL_IP_LIMIT` / `..._WINDOW_SECONDS` | `12` / `600` | 验证码接口按 IP 限流 |
| `MOSAIC_LOGIN_IP_LIMIT` / `..._WINDOW_SECONDS` | `30` / `600` | 登录接口按 IP 限流 |
| `MOSAIC_LOGIN_FAILURE_LIMIT` / `..._WINDOW_SECONDS` | `10` / `900` | 同邮箱登录失败上限 |
| `MOSAIC_DEVICE_TTL_SECONDS` | `600` | 设备授权码有效期 |
| `MOSAIC_SYNC_HISTORY_DAYS` | `90` | 增量变更日志保留天数；更早的历史在推送时清理，过期游标返回 `410 sync.cursor_invalid` |
| `MOSAIC_SMTP_HOST` / `_PORT` | 空 / `587` | 邮件服务器；未设置 `HOST` 即禁用邮箱功能 |
| `MOSAIC_SMTP_SECURITY` | `starttls` | `starttls` 或 `ssl`（隐式 TLS）；两者都强制 TLS 1.2+ |
| `MOSAIC_SMTP_USERNAME` / `_PASSWORD` | 空 | SMTP 认证；支持 AUTH PLAIN / LOGIN |
| `MOSAIC_SMTP_FROM` / `_FROM_NAME` | 空 / `Mosaic` | 发件人地址与显示名 |

通过 `/v1/admin/mail` 保存的配置会覆盖上述 SMTP 环境变量；密码以 AES-256-GCM 加密存于 SQLite，读取接口只返回 `hasPassword`。

## 部署

### Docker

```powershell
cd server
docker build -t mosaic-server .
```

```powershell
docker run -d --name mosaic-server -p 8080:8080 `
  -v mosaic-data:/data `
  -e MOSAIC_PUBLIC_URL=https://mosaic.example.com `
  -e MOSAIC_MASTER_KEY=<32 字节 base64> `
  -e MOSAIC_SMTP_HOST=smtp.example.com -e MOSAIC_SMTP_PORT=587 `
  -e MOSAIC_SMTP_SECURITY=starttls `
  -e MOSAIC_SMTP_USERNAME=... -e MOSAIC_SMTP_PASSWORD=... `
  -e MOSAIC_SMTP_FROM=noreply@example.com `
  mosaic-server
```

镜像以非 root（`app`）运行，数据卷挂在 `/data`，`HEALTHCHECK` 调用 `Mosaic.Server.dll --healthcheck`（默认探测 `http://127.0.0.1:8080/health`，可用 `MOSAIC_HEALTHCHECK_URL` 覆盖）。

### Coolify（线上实际部署方式）

线上 `https://mosaic.bansonic.top` 由 Coolify 4.5.3 托管，应用名 `mosaic-server`，构建包为 `dockercompose`：Coolify 从源仓库拉取仓库根的 `docker-compose.yaml`，按 `./server` 的 `Dockerfile` 构建镜像，容器监听 `8080`，数据卷 `mosaic-data:/data` 跨重建保留。

- 触发构建：Coolify 面板「Deploy」，或 `POST /api/v1/deploy?uuid=<APP_UUID>&force=false`（`Authorization: Bearer <API_TOKEN>`）。
- 构建的是源仓库 **`main` 分支最新提交**；版本号取自该提交里的 `server/Directory.Build.props`。代码没进 `main` 时，面板怎么点都只会重建旧版本。
- 版本核验：`curl -fsS https://mosaic.bansonic.top/health` 的 `version` 必须等于 `server/Directory.Build.props` 的 `Version`。
- 回滚：在面板里对上一份成功部署重新 Deploy（重建镜像），数据卷不动。迁移只追加，0.1.2 的迁移 3 是纯增量 DDL，用 0.1.1 镜像读取含 `attachments` 表的库不会失败。
- 反向代理上传上限：实测 51 MiB 请求体可达应用（`PUT /v1/attachments/<guid>` 返回 404/401 而非 413），满足 `MOSAIC_ATTACHMENT_MAX_BYTES=50m` 的默认值。

**上线前置：提交必须在 Gitea `main` 上。** Coolify 只拉 `https://git.bansonic.top/azzz/Mosaic.git`；本仓库 checkout 只配了 `origin`（`git@github.com:soehi/Mosaic.git`），所以裸 `git push` 或 `git push origin` 只更新 GitHub，Gitea `main` 不会变——此时点 Deploy 只会重建旧版本（曾因此连续两次"已推送"却仍在构建 `b5d7d60`）。推送要显式写到 Gitea：

```powershell
git push https://git.bansonic.top/azzz/Mosaic.git <本地分支>:main
```

或在本地加一个远端后复用：

```powershell
git remote add gitea https://git.bansonic.top/azzz/Mosaic.git
git push gitea <本地分支>:main
```

推完先核验再构建，哈希没变就不要点 Deploy：

```powershell
git ls-remote https://git.bansonic.top/azzz/Mosaic.git refs/heads/main
```

下面两个小节（自包含包 + systemd）适用于自建主机安装，不适用于 Coolify 部署。

### Linux 自包含包

```powershell
./publish-linux.ps1 -Output artifacts/linux-x64
# 或在 Linux 上：
./publish-linux.sh
```

把输出目录拷到服务器后：

```bash
MOSAIC_DATA_DIR=/var/lib/mosaic-server \
MOSAIC_PUBLIC_URL=https://mosaic.example.com \
./Mosaic.Server --urls http://127.0.0.1:5080
```

systemd 示例：

```ini
[Unit]
Description=Mosaic server
After=network-online.target

[Service]
WorkingDirectory=/opt/mosaic-server
ExecStart=/opt/mosaic-server/Mosaic.Server --urls http://127.0.0.1:5080
EnvironmentFile=/etc/mosaic-server.env
Restart=always
User=mosaic

[Install]
WantedBy=multi-user.target
```

反向代理（Caddy/Nginx）负责 HTTPS；设置 `MOSAIC_PUBLIC_URL=https://...` 与 `MOSAIC_TRUSTED_PROXIES=<代理 IP>`，这样设备授权页链接与按 IP 限流都正确。

### 升级（原地替换，仅自建 systemd 主机）

Coolify 部署不走本小节，见上文「Coolify（线上实际部署方式）」。

发布包按 `<版本>` 打成单个 tar.gz（341 个文件，约 47 MB）。在服务器上以 root 执行：

```bash
sudo ./upgrade-linux.sh Mosaic.Server-0.1.2-linux-x64.tar.gz SHA256SUMS
```

脚本做六件事，全部可回滚：

1. `sha256sum -c` 校验发布包。
2. 从 `systemctl show` 解析 `ExecStart` 程序目录、`EnvironmentFiles` 里的 `MOSAIC_DATA_DIR`（未设置时回落 `<程序目录>/data` 或 `/var/lib/mosaic-server`）与 `User`，检查磁盘余量（解压体积 + 数据目录体积）。
3. `systemctl stop` 后复制数据库（含 `-wal`/`-shm`）、`master.key`、`attachments/` 到 `/var/backups/mosaic-server/<UTC 时间戳>/`。
4. 旧目录整体改名为 `<程序目录>.previous-<时间戳>`，解压新包到原路径，`chmod +x Mosaic.Server`，按服务用户 `chown -R`（保留最近 2 个旧版本目录）。
5. `systemctl start` 后轮询 `/health` 最多 60 秒，并要求返回的 `version` 与包名版本一致；任一步失败自动停服、换回旧目录、重启，新目录留在 `<程序目录>.failed-<时间戳>`。
6. 打印 `schema_migrations` 全部版本、`attachments` 表是否存在、附件文件数，以及 nginx 的 `client_max_body_size`（若装了 nginx）。

默认配置可用环境变量覆盖：`MOSAIC_SERVICE`、`MOSAIC_APP_DIR`、`MOSAIC_BACKUP_ROOT`、`MOSAIC_HEALTH_URL`、`MOSAIC_DATA_DIR`、`MOSAIC_SKIP_ATTACHMENTS_BACKUP=1`（磁盘紧张时跳过附件备份）、`MOSAIC_KEEP_PREVIOUS`。

数据回滚（仅在需要回退到 0.1.1 时）：停服务 → 覆盖回备份的 `mosaic-server.sqlite3`、删除 `-wal`/`-shm` → `DROP TABLE attachments; DELETE FROM schema_migrations WHERE version = 3;` → 启动。0.1.2 → 0.1.2 的内部回滚只换目录，库不动。

## 数据、密钥与迁移

- 数据库：`<数据目录>/mosaic-server.sqlite3`（WAL 模式，`busy_timeout` 15s）。
- 任务附件：`<数据目录>/attachments/<用户 ID（32 位无连字符）>/<附件 ID（32 位无连字符）><扩展名>`；`attachments` 表是权威清单。删除账户时行级联删除，目录在事务提交后清理（失败只浪费磁盘，不影响一致性）。
- 主密钥：`<数据目录>/master.key`（Unix 权限 0600）。**丢失主密钥后现有数据库中的 SMTP 密码与设备授权密文无法恢复，但用户密码哈希不受影响。** 现有数据库缺少 `master.key` 时服务端拒绝启动，避免写出不兼容数据。
- 备份数据目录即可完成完整备份；恢复时请连同 `master.key` 一起恢复。
- 迁移在启动时自动执行，记录于 `schema_migrations`。升级只追加新迁移；数据库版本高于服务端支持版本时拒绝启动。

## API

统一错误格式（所有非 2xx）：

```json
{ "code": "auth.invalid_code", "message": "验证码无效或已过期。", "traceId": "0HN..." }
```

`traceId` 同时出现在响应头 `X-Trace-Id`。认证接口统一 `Cache-Control: no-store`。时间均为 UTC ISO-8601（`2026-01-01T00:00:00Z`）。

### 会话对象

```json
{
  "accessToken": "...",
  "refreshToken": "...",
  "accessTokenExpiresAt": "2026-01-01T00:15:00Z",
  "refreshTokenExpiresAt": "2026-01-15T00:00:00Z",
  "user": { "id": "...", "email": "a@example.com", "createdAt": "...", "emailVerifiedAt": "..." }
}
```

受保护接口使用 `Authorization: Bearer <accessToken>`。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/v1/auth/policy` | 注册开关、邮件可用性、验证码/密码参数 |
| POST | `/v1/auth/email-code` | `{email, purpose}`，`purpose` 为 `register`/`login`/`reset`/`delete-account`；恒 `202 {status,resendAfterSeconds}`，不发送时也返回相同响应 |
| POST | `/v1/auth/register` | `{email, password, code, deviceLabel?, platform?}`，`201` + 会话 |
| POST | `/v1/auth/login` | `{email, password, deviceLabel?, platform?}`，`200` + 会话 |
| POST | `/v1/auth/login/code` | `{email, code, deviceLabel?, platform?}`，`200` + 会话 |
| POST | `/v1/auth/refresh` | `{refreshToken}`，轮换后返回新会话 |
| POST | `/v1/auth/logout` | Bearer + `{refreshToken, allDevices?}`，撤销该 family（或全部） |
| POST | `/v1/auth/forgot-password` | `{email}`，恒 `202`，发重置验证码 |
| POST | `/v1/auth/reset-password` | `{email, code, newPassword}`（兼容 `password`），成功后撤销全部令牌、验证码与已绑定的设备授权 |
| GET | `/v1/auth/me` | Bearer，账户字段（`userId`、`email`、`createdAt`、`emailVerifiedAt`）+ `user` |
| GET | `/v1/auth/sessions` | Bearer，`{sessions:[{id,deviceLabel,platform,createdAt,lastSeenAt,expiresAt,current}]}`，`id` 为会话家族 ID |
| POST | `/v1/auth/sessions/{id}/revoke` | Bearer，撤销指定会话（该设备的全部刷新令牌）；不存在 `404 auth.session_not_found` |
| POST | `/v1/auth/sessions/revoke-all` | Bearer，可选 `{keepCurrent:true}`；无请求体时撤销全部，包括当前会话 |
| POST | `/v1/auth/account/delete` | Bearer + `{password}` 或 `{code}`（`delete-account` 验证码）；重新认证失败 `401 auth.reauth_required`，成功后级联删除 `sessions`、`refresh_tokens`、`email_codes`、`device_auth`、`backups`、`backup_items`、`sync_items`、`sync_changes`、`sync_cursors`、`sync_counters`、`sync_floor` 并返回 `204`；`settings_audit` 以 `deleted:<emailHash 前 16 位>` 去标识保留 |

密码规则：10–256 个 Unicode 字符；PBKDF2-HMAC-SHA256，310,000 次迭代，16 字节盐，32 字节密钥，常量时间比较。刷新令牌每次使用都轮换；已轮换的令牌再次出现视为泄露，撤销整个 family。

会话对象在 `accessToken`、`refreshToken`、`accessTokenExpiresAt`、`refreshTokenExpiresAt`、`user` 之外还返回 `expiresIn`（访问令牌剩余秒数）与 `account{userId,email,displayName}`，两者均为兼容性附加字段。被撤销的会话访问时返回 `401 auth.session_revoked`，过期返回 `401 auth.session_expired`。删除账户后令牌所属行已随账户级联删除，因此返回 `401 auth.required`（与未知令牌不可区分），而非 `auth.session_revoked`。

### Device Flow

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/v1/device/start` | 无鉴权，请求体可省略或 `{deviceLabel?, platform?, appVersion?}`；返回 `{deviceCode,userCode,verificationUri,verificationUriComplete,expiresIn,interval}` |
| POST | `/v1/device/poll` | `{deviceCode}`；`{status,interval}` 为 `pending`/`denied`/`expired`/`consumed`/`slow_down`，或 `{status:"approved", session}`；轮询快于 `interval` 返回 `200 {status:"slow_down", interval}` |
| POST | `/v1/device/approve` | Bearer + `{userCode}`，确认授权 |
| POST | `/v1/device/deny` | Bearer + `{userCode}`，拒绝授权 |
| GET | `/v1/device/info?userCode=...` | Bearer，`{deviceLabel,platform,appVersion,state,expiresAt,approvedAt}`，用于确认页展示设备信息；不存在或已过期 `404 device.invalid_code` |
| GET | `/device?code=XXXX-XXXX` | 服务端内置的浏览器授权页（无外部资源） |

授权码为 8 位（`BCDFGHJKLMNPQRSTVWXZ23456789`），显示为 `XXXX-XXXX`，10 分钟有效。`interval` 固定为 2 秒；同一设备码两次 `pending` 轮询间隔小于 `interval` 时返回 `200 {status:"slow_down", interval:2}`，客户端应递增自己的轮询间隔而不是把它当作错误或重试风暴（设备码本身的限流仍由 `429 device.rate_limited` 兜底）。`approved` 后的会话只能领取一次，之后轮询返回 `consumed`。`verificationUri` 为 `{MOSAIC_PUBLIC_URL}/device`，`verificationUriComplete` 带上 `?code=...` 供二维码/链接直接打开。

### 浏览器登录（`/device` 页面）

桌面端「在浏览器中登录」只需调用系统默认浏览器打开服务端返回的 `verificationUriComplete`，窗口里显示授权码并轮询 `/v1/device/poll`；注册、邮箱验证码、密码与忘记密码全部在服务端页面完成，桌面端不再渲染这些表单。

- `GET /device?code=XXXX-XXXX`：预填授权码并停留在登录/注册页；无 `code` 时同样可以先登录或创建账号，页面上方的授权码输入在登录前后都可见，授权码填好后再确认。
- `GET /device?mode=reset`：直接打开「重置密码」面板（邮箱 → 获取重置验证码 → 新密码 + 确认），供桌面端「忘记密码」按钮打开。重置成功会撤销该账号的全部会话与令牌，页面切回登录态，用户用新密码重新登录。
- 页面载入时调用 `GET /v1/auth/policy`：展示 `serverName`，注册关闭时隐藏「创建账号」，邮件不可用时提示改用密码登录。
- 登录后进入确认页，页面用 `GET /v1/device/info?userCode=` 展示发起授权的应用名（`deviceLabel`）、`platform`/`appVersion`、规范化后的授权码与剩余时间倒计时（按 `expiresAt`，最后几十秒逐秒更新，到期禁用批准/拒绝），再批准或拒绝；「使用其他账号」保留已填授权码、只清除登录态。
- 页面无任何外部资源，沿用 `/device` 的严格 CSP（`default-src 'none'`）与 `no-store`；令牌只保存在页面内存中，刷新即丢失。
- **`MOSAIC_PUBLIC_URL` 必须设置为该服务器对外可访问的地址**（例如 `https://mosaic.bansonic.top`）。它决定 `verificationUri`/`verificationUriComplete`；若仍是默认 `http://localhost:5080`，浏览器登录只会打开服务器本机地址。服务端启动时会为此打印警告（仅日志，对既有安装与协议无行为影响）。

### 备份

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| PUT | `/v1/backups` | `{deviceId, baseSnapshotId?, contentSha256?, items:[{kind,key,revision,payload}]}`；`201` 新建，`200` 内容去重；`413 backup.quota_exceeded` |
| GET | `/v1/backups/latest` | 最新快照元数据 + 清单（无 payload） |
| GET | `/v1/backups/{snapshotId}` | 完整快照（含 payload） |
| POST | `/v1/backups/{snapshotId}/restore` | 与下载相同，并记录恢复审计 |

- `kind` 仅 `task`（任务）与 `setting`（可移植用户设置）；宿主相关设置由客户端过滤，不上传。
- `kind: "setting"` 的 `key` 必须属于下文《可移植设置白名单（14 键）》，否则 `400 backup.invalid`；备份与同步共用同一份 `Sync.PortableSettings`。
- 上传、`latest`、下载与恢复的响应都带 `serverRevision` 与 `cursor`（字符串，与 `/v1/sync/changes` 的游标同一体系），表示该快照覆盖到的同步位置；恢复快照后客户端可据此决定继续增量还是重新对齐。去重命中时返回首次记录的这两个值。
- `payload` 必须是 JSON 对象；单条 ≤ 1 MiB；每份 ≤ 20,000 条；每用户累计 ≤ `MOSAIC_BACKUP_QUOTA_BYTES`。
- 相同内容（不受 `deviceId` 影响）返回相同快照且不新增记录。
- 快照不可变；恢复由客户端显式触发。
- `contentSha256` 可选，用于客户端自查；服务端总会自行计算并在读取时重新校验，不匹配返回 `500 backup.corrupt`。

内容哈希算法（客户端如需自算）：

1. 每条 `{kind, key, revision, payload}` 序列化为：对象属性按名称 Ordinal 排序、数组保持顺序、数字保留原始字面量、不转义非 ASCII；
2. 条目按 `kind`、`key` Ordinal 排序，拼成 `{"items":[...]}`；
3. 对 UTF-8 字节求 SHA-256，小写十六进制。

### 任务附件

附件由客户端**主动上传**（不随增量同步自动推送）；服务端只保存二进制内容与元数据清单，任务的 revision 仍由任务同步通道管理。

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/v1/attachments` | 清单 `{items:[{id,taskId,fileName,sizeBytes,sha256,createdAt,updatedAt}], usageBytes, quotaBytes, maxBytes}` |
| PUT | `/v1/attachments/{attachmentId}?taskId=<guid>&fileName=<name>` | 请求体为原始字节（`application/octet-stream`）；`201` 新建，`200` 内容相同重放；`X-Mosaic-Sha256` 可选 |
| GET | `/v1/attachments/{attachmentId}` | 下载原始字节，`Content-Disposition` 使用清单中的文件名 |
| DELETE | `/v1/attachments/{attachmentId}` | `204`；不存在同样 `204`（幂等） |

- `attachmentId`、`taskId` 必须是 Guid；`fileName` 会去掉首尾空白并拒绝控制字符、`Path.GetInvalidFileNameChars()`、`.`/`..` 与超过 260 个字符，否则 `400 attachment.invalid`。
- 磁盘文件名只由 `attachmentId` 与白名单校验后的扩展名（2–17 位字母数字）组成，客户端文件名永远不会进入路径。
- 上传先流式写入临时文件并同时计算 SHA-256：`Content-Length` 或实际字节超过 `MOSAIC_ATTACHMENT_MAX_BYTES` 返回 `413 attachment.too_large`；带了 `X-Mosaic-Sha256` 但与小写十六进制不符返回 `400 attachment.hash_mismatch`，临时文件随即删除。
- 同一用户、同一 `attachmentId`、相同 `taskId`/`fileName`/大小/哈希且磁盘文件仍在 → `200 {duplicate:true}`，不重写。相同 ID 但内容不同 → 覆盖并删除旧扩展名文件（客户端 ID 只生成一次，正常不会发生）。
- 不同用户使用相同 `attachmentId` → `409 attachment.conflict`，不泄露对方是否持有该附件。
- 配额按用户累计（`SUM(size_bytes)`，覆盖前先扣除自身旧值），超出返回 `413 attachment.quota_exceeded`。
- 写入顺序：临时文件 → 数据库行提交 → 原子 `File.Move` 到最终路径。`File.Move` 失败返回 `500 attachment.storage_failed`，此时行在、文件不在；客户端按原内容重传即可修复（清单下载会因文件缺失返回 `404 attachment.not_found`）。
- 全局 `MOSAIC_MAX_BODY_BYTES`（16 MiB）不适用于附件 PUT：该请求在执行时把 `IHttpMaxRequestBodySizeFeature` 提升到 `MOSAIC_ATTACHMENT_MAX_BYTES`，因此 50 MiB 附件可正常上传；反向代理（Nginx `client_max_body_size`、Caddy 默认无限制）仍需同步放宽。
- 下载当前为整体响应，不支持 `Range`；客户端导入后自行校验 SHA-256。

### 增量同步

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/v1/sync/changes` | `{deviceId, kind, key, payload, deleted, baseServerRevision, clientRevision, idempotencyKey}` |
| GET | `/v1/sync/changes?cursor=0&limit=200&deviceId=...` | 按 `seq` 增量拉取；空 `cursor` 视为 `0` |

推送成功：

```json
{
  "change": {
    "cursor": "12",
    "serverRevision": 4,
    "kind": "task",
    "key": "task-1",
    "deleted": false,
    "payload": { "title": "..." },
    "deviceId": "desktop-1",
    "createdAt": "2026-01-01T00:00:00Z"
  },
  "replayed": false
}
```

- `baseServerRevision` 必须等于服务器当前版本（不存在视为 `0`）；否则 `409 sync.conflict`，响应附带 `current`（服务器当前条目，可能为 `null`）。
- `serverRevision` 由服务器单调递增，客户端本地 revision 只作为 `clientRevision` 记录，不被信任。
- `deleted: true` 写入 tombstone，拉取端收到 `deleted: true` 与 `payload: null`。
- `idempotencyKey` 必填；重复提交返回首次结果并带 `replayed: true`。
- `kind: "setting"` 的 `key` 同样受白名单约束，未知设置键返回 `400 sync.invalid`。
- 拉取响应 `{changes:[...], nextCursor:"12", hasMore:false}`；`cursor` 为上一页 `nextCursor`，`limit` 1–500（默认 200）。附带 `deviceId` 时服务器记录该设备游标。
- 变更日志按 `MOSAIC_SYNC_HISTORY_DAYS` 保留；推送时清理更早的行并记录清理水位。游标早于水位时返回 `410 sync.cursor_invalid`，错误信封附带 `details.requiresSnapshot=true` 与 `details.floor`，客户端应先下载最新备份快照再继续增量；`cursor` 非法（非数字/负数）返回 `400 sync.invalid`，同样带 `details.requiresSnapshot=true`。

### 管理与健康

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET/PUT | `/v1/admin/mail` | 需 `X-Admin-Token`；读取只返回 `hasPassword`，写入时空字符串表示清除密码，省略字段表示保持 |
| POST | `/v1/admin/mail/test` | `{email}`，发送测试邮件，`204` |
| GET | `/health` | `200 {status:"ok", version, timeUtc}`；数据库不可用 `503` |

### 可移植设置白名单（14 键）

备份与增量同步对 `kind: "setting"` 强制同一份白名单（代码：`server/src/Mosaic.Server/Sync/PortableSettings.cs`），方便客户端与验收逐项对照：

| # | 键 | 说明 |
| --- | --- | --- |
| 1 | `ui.language` | 界面语言 |
| 2 | `appearance.theme` | 主题 |
| 3 | `tasks.sortOrder` | 任务排序 |
| 4 | `tasks.defaultFilter` | 默认筛选 |
| 5 | `tasks.archive.autoEnabled` | 自动归档开关 |
| 6 | `tasks.archive.delayDays` | 自动归档延迟天数 |
| 7 | `tasks.title.maxCharacters` | 标题长度上限 |
| 8 | `tasks.notes.maxCharacters` | 备注长度上限 |
| 9 | `tasks.title.required` | 标题必填 |
| 10 | `tasks.defaultCompleted` | 新建任务默认完成状态 |
| 11 | `tasks.confirmDelete` | 删除前确认 |
| 12 | `tasks.confirmUnsaved` | 未保存时确认 |
| 13 | `tasks.showCharacterCount` | 显示字数统计 |
| 14 | `tasks.autoSwitchToAllOnHidden` | 隐藏时自动切到"全部" |

- 白名单外的 `setting` 键：备份上传 `400 backup.invalid`，同步推送 `400 sync.invalid`；服务端不保存、不转发。
- `kind: "task"` 不受此限制。
- 已存在的旧快照不因白名单变化被删除或改写，仍可下载与恢复；但客户端不应再把非白名单设置写回。

### 错误码

所有错误响应为 `{code, message, traceId}`，必要时附带 `details` 对象（同一组字段也平铺在根级以兼容 0.1.0 客户端）。`message` 仅供展示，客户端逻辑一律按 `code` 判断。

| 状态 | code | 触发条件 |
| --- | --- | --- |
| 401 | `auth.required` | 缺少或无法解析 Bearer 令牌；也用于**账户已删除**后的旧令牌（账户行已级联删除，无墓碑，因此无法区分"从未存在"与"已删除"） |
| 401 | `auth.session_revoked` | 令牌有效但会话已被撤销（单会话撤销、`revoke-all`、登出全部设备、改密/重置、刷新令牌重用导致 family 撤销） |
| 401 | `auth.session_expired` | 访问令牌超过 15 分钟有效期 |
| 401 | `auth.reauth_required` | 敏感操作重新认证失败：`account/delete` 的密码不正确，或 `delete-account` 验证码无效/过期 |
| 401 | `auth.invalid_credentials` | 邮箱或密码不正确（含未注册邮箱，返回一致的错误与耗时） |
| 400 | `auth.invalid_code` | 验证码错误（未达尝试上限） |
| 400 | `auth.code_expired` | 验证码已过期 |
| 429 | `auth.code_attempts_exceeded` | 同一验证码尝试次数达到 5 次 |
| 429 | `auth.rate_limited` | 登录/验证码请求超过 IP 或地址窗口限制 |
| 403 | `auth.registration_closed` | 服务端关闭注册 |
| 422 | `auth.invalid_email` / `auth.weak_password` | 邮箱格式不合法 / 密码不满足 10–256 字符 |
| 404 | `auth.session_not_found` | 撤销的会话 ID 不属于当前用户或不存在 |
| 401 | `auth.invalid_refresh` / `auth.refresh_reused` | 刷新令牌不存在/已失效 / 检测到已轮换令牌重用（撤销整个 family） |
| 400 | `device.invalid_code` | 设备码或用户码无效 |
| 404 | `device.invalid_code` | 用户码不存在、已过期或已属于其他用户（`/v1/device/info`、`approve`、`deny`） |
| 409 | `device.already_handled` | 授权码已被批准、拒绝或过期，不能再次处理 |
| 429 | `device.rate_limited` | 设备授权启动/轮询超过窗口限制 |
| 400 | `backup.invalid` | 结构校验失败，含非白名单 `setting` 键 |
| 400 | `backup.hash_mismatch` | 客户端声明的 `contentSha256` 与服务端计算不一致 |
| 404 | `backup.not_found` | 快照不存在或不属于当前用户 |
| 409 | `backup.conflict` | 备份写入遇到并发冲突，客户端可原样重试（内容相同会去重） |
| 413 | `backup.quota_exceeded` | 超过每用户备份配额 |
| 500 | `backup.corrupt` | 读取时重新校验哈希失败 |
| 400 | `attachment.invalid` | 附件 ID/`taskId` 不是 Guid、`fileName` 非法或 `X-Mosaic-Sha256` 不是 64 位十六进制 |
| 400 | `attachment.hash_mismatch` | 客户端声明的 `X-Mosaic-Sha256` 与实际内容不一致 |
| 404 | `attachment.not_found` | 附件不存在、不属于当前用户，或清单中的行缺少磁盘文件 |
| 409 | `attachment.conflict` | 附件 ID 已被其他用户占用，或同一 ID 与已存在内容的归属冲突 |
| 413 | `attachment.too_large` | 单个附件超过 `MOSAIC_ATTACHMENT_MAX_BYTES` |
| 413 | `attachment.quota_exceeded` | 超过每用户附件配额 `MOSAIC_ATTACHMENT_QUOTA_BYTES` |
| 500 | `attachment.storage_failed` | 数据库行已提交但落盘失败，客户端原样重传即可修复 |
| 400 | `sync.invalid` | 推送/拉取参数非法，含非白名单 `setting` 键与非法游标（后者带 `details.requiresSnapshot=true`） |
| 409 | `sync.conflict` | `baseServerRevision` 与服务器当前版本不符，响应附带 `details.current` |
| 410 | `sync.cursor_invalid` | 游标早于已清理的历史水位，响应附带 `details.requiresSnapshot=true` 与 `details.floor`；客户端应先恢复最新备份快照 |
| 400 | `request.invalid` | 请求体无法解析或缺少必填字段 |
| 503 | `email.not_configured` | SMTP 未配置；对所有地址统一返回，不泄露上游错误 |
| 503 | `email.delivery_failed` | SMTP 已配置但投递失败；同样对所有地址统一返回 |
| 401 | `admin.forbidden` | 管理接口的 `X-Admin-Token` 缺失或不正确 |
| 404 | `server.not_found` | 未启用管理接口时按不存在处理（隐藏管理面） |
| 500 | `server.error` | 未预期错误；只记录类型，不返回内部细节 |

## 安全设计

- 访问/刷新令牌、设备码、验证码均为高熵随机值；数据库只保存 SHA-256 或带 pepper 的 HMAC。
- 邮箱地址在存储层使用 HMAC 标识；日志只记录 `userId`、IP 的 HMAC 前 12 位、结果与耗时，**不记录邮箱、密码、验证码、令牌或邮件正文**。
- 发送验证码对已注册/未注册地址返回完全一致的响应；SMTP 不可用时对所有地址统一 503。
- 验证码一次性：消费使用条件更新，并发注册/登录只有一个成功；5 次失败后锁定，需重新获取。
- 刷新令牌重用检测会撤销整个 family（含已签发的访问令牌）。
- 重置密码撤销全部会话、刷新令牌、未消费验证码，以及已绑定用户的设备授权。
- SMTP 仅 `starttls`/`ssl`，强制 TLS 1.2+；AUTH 凭据只经加密连接发送；SMTP 响应文本不回传客户端。
- 反向代理场景必须配置 `MOSAIC_TRUSTED_PROXIES`，否则不会信任 `X-Forwarded-For`。
- 服务端限制请求体大小（默认 16 MiB），备份与同步另有条目/内容上限。
- 管理接口在未设置 `MOSAIC_ADMIN_TOKEN` 时不存在；令牌比较为常量时间。

已知边界（v1 有意为之）：

- 单实例部署：限流计数在进程内，SQLite 不适合多副本共享写入。多副本需要外置限流与数据库。
- 未实现独立的“修改密码”接口；改密走“忘记密码 → 重置密码”，同样撤销全部凭据。
- 未批准的设备授权码尚未绑定用户，重置密码无法定向撤销；批准动作本身需要有效登录。
- 不提供邮件退回/退信处理，验证码邮件可能进入垃圾箱。

## 测试与验证

```powershell
dotnet test Mosaic.Server.sln
```

91 个测试用例（含 Theory 展开）覆盖：密码参数与常量时间校验、邮箱规范化、验证码过期/重放/五次锁定/重发节流/IP 与地址限流、账号枚举一致性、并发注册只成功一次、刷新令牌轮换与重用检测、登出/重置撤销、Device Flow 全流程与一次性领取、`slow_down`、`verificationUriComplete`、设备信息查询、会话列表/单会话撤销/全部撤销（含保留当前）、账户删除的密码与验证码两条路径及审计去标识、备份往返/去重/隔离/配额/哈希校验/白名单/serverRevision 与 cursor、同步游标/冲突/tombstone/幂等/用户隔离/设置白名单/历史清理后的 410 与非法游标的 400（均带 requiresSnapshot）、任务附件清单/PUT 写序与重复重放/哈希校验/非法 ID/单件超限/配额/下载/幂等删除/跨用户冲突/账户删除清理目录、管理接口加密存储、日志不含秘密、迁移幂等（含迁移 2 与 3）、重启后数据与会话可用、`/device` 页面的重置入口与共用授权码区块。

已验证：Windows 上 `dotnet build`/`dotnet test` 全部通过；`dotnet publish -r linux-x64 --self-contained` 产物生成；本地 Kestrel 冒烟（健康检查、策略、设备页、未配置 SMTP 的统一 503）。

未验证：真实 Linux 主机运行自包含包、Docker 构建与运行（本机无 Docker）、真实 SMTP 提供商（TLS 连接/投递）、反向代理后的 HTTPS 与 `X-Forwarded-*`、手机端、多设备并发冲突的真实网络条件。上线前请在目标环境补测。

## 版本

服务端独立版本号（当前 `0.1.2`），与桌面客户端版本无强绑定；协议版本为 `/v1`。新增字段属兼容变更，删除或改义字段需升 `/v2`。
