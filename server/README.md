# Mosaic 服务端（0.1.0）

Mosaic 的可选自建账号与同步服务端：邮箱验证码注册/登录、密码与令牌、Device Flow 浏览器授权、任务与可移植设置的备份/恢复、增量同步。桌面客户端未登录时仍可完全离线使用。

- 运行时：.NET 10 / ASP.NET Core，单进程 + SQLite（WAL）。
- 部署：Linux `linux-x64` 自包含包，或 Docker 镜像。
- 协议：`/v1/auth/*`、`/v1/device/*`、`/v1/backups/*`、`/v1/sync/changes*`，字段定义见下文。
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
| `MOSAIC_MAX_BODY_BYTES` | `16777216` | 请求体上限（Kestrel） |
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

## 数据、密钥与迁移

- 数据库：`<数据目录>/mosaic-server.sqlite3`（WAL 模式，`busy_timeout` 15s）。
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
| POST | `/v1/auth/email-code` | `{email, purpose}`，`purpose` 为 `register`/`login`/`reset`；恒 `202 {status,resendAfterSeconds}`，不发送时也返回相同响应 |
| POST | `/v1/auth/register` | `{email, password, code, deviceLabel?}`，`201` + 会话 |
| POST | `/v1/auth/login` | `{email, password, deviceLabel?}`，`200` + 会话 |
| POST | `/v1/auth/login/code` | `{email, code, deviceLabel?}`，`200` + 会话 |
| POST | `/v1/auth/refresh` | `{refreshToken}`，轮换后返回新会话 |
| POST | `/v1/auth/logout` | Bearer + `{refreshToken, allDevices?}`，撤销该 family（或全部） |
| POST | `/v1/auth/forgot-password` | `{email}`，恒 `202`，发重置验证码 |
| POST | `/v1/auth/reset-password` | `{email, code, newPassword}`，成功后撤销全部令牌、验证码与已绑定的设备授权 |
| GET | `/v1/auth/me` | Bearer，`{user}` |

密码规则：10–256 个 Unicode 字符；PBKDF2-HMAC-SHA256，310,000 次迭代，16 字节盐，32 字节密钥，常量时间比较。刷新令牌每次使用都轮换；已轮换的令牌再次出现视为泄露，撤销整个 family。

### Device Flow

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/v1/device/start` | 无鉴权，请求体可省略或 `{deviceLabel}`；返回 `{deviceCode,userCode,verificationUri,expiresIn,interval}` |
| POST | `/v1/device/poll` | `{deviceCode}`；`{status}` 为 `pending`/`denied`/`expired`/`consumed`，或 `{status:"approved", session}` |
| POST | `/v1/device/approve` | Bearer + `{userCode}`，确认授权 |
| POST | `/v1/device/deny` | Bearer + `{userCode}`，拒绝授权 |
| GET | `/device?code=XXXX-XXXX` | 服务端内置的浏览器授权页（无外部资源） |

授权码为 8 位（`BCDFGHJKLMNPQRSTVWXZ23456789`），显示为 `XXXX-XXXX`，10 分钟有效。`interval` 为 2 秒的轮询建议；`approved` 后的会话只能领取一次，之后轮询返回 `consumed`。`verificationUri` 即 `{MOSAIC_PUBLIC_URL}/device?code=...`。

### 备份

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| PUT | `/v1/backups` | `{deviceId, baseSnapshotId?, contentSha256?, items:[{kind,key,revision,payload}]}`；`201` 新建，`200` 内容去重；`413 backup.quota_exceeded` |
| GET | `/v1/backups/latest` | 最新快照元数据 + 清单（无 payload） |
| GET | `/v1/backups/{snapshotId}` | 完整快照（含 payload） |
| POST | `/v1/backups/{snapshotId}/restore` | 与下载相同，并记录恢复审计 |

- `kind` 仅 `task`（任务）与 `setting`（可移植用户设置）；宿主相关设置由客户端过滤，不上传。
- `payload` 必须是 JSON 对象；单条 ≤ 1 MiB；每份 ≤ 20,000 条；每用户累计 ≤ `MOSAIC_BACKUP_QUOTA_BYTES`。
- 相同内容（不受 `deviceId` 影响）返回相同快照且不新增记录。
- 快照不可变；恢复由客户端显式触发。
- `contentSha256` 可选，用于客户端自查；服务端总会自行计算并在读取时重新校验，不匹配返回 `500 backup.corrupt`。

内容哈希算法（客户端如需自算）：

1. 每条 `{kind, key, revision, payload}` 序列化为：对象属性按名称 Ordinal 排序、数组保持顺序、数字保留原始字面量、不转义非 ASCII；
2. 条目按 `kind`、`key` Ordinal 排序，拼成 `{"items":[...]}`；
3. 对 UTF-8 字节求 SHA-256，小写十六进制。

### 增量同步

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/v1/sync/changes` | `{deviceId, kind, key, payload, deleted, baseServerRevision, clientRevision, idempotencyKey}` |
| GET | `/v1/sync/changes?cursor=0&limit=100&deviceId=...` | 按 `seq` 增量拉取 |

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
- 拉取响应 `{changes:[...], nextCursor:"12", hasMore:false}`；`cursor` 为上一页 `nextCursor`，`limit` 1–500。附带 `deviceId` 时服务器记录该设备游标。

### 管理与健康

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET/PUT | `/v1/admin/mail` | 需 `X-Admin-Token`；读取只返回 `hasPassword`，写入时空字符串表示清除密码，省略字段表示保持 |
| POST | `/v1/admin/mail/test` | `{email}`，发送测试邮件，`204` |
| GET | `/health` | `200 {status:"ok", version, timeUtc}`；数据库不可用 `503` |

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

69 个测试用例（含 Theory 展开）覆盖：密码参数与常量时间校验、邮箱规范化、验证码过期/重放/五次锁定/重发节流/IP 与地址限流、账号枚举一致性、并发注册只成功一次、刷新令牌轮换与重用检测、登出/重置撤销、Device Flow 全流程与一次性领取、备份往返/去重/隔离/配额/哈希校验、同步游标/冲突/tombstone/幂等/用户隔离、管理接口加密存储、日志不含秘密、迁移幂等、重启后数据与会话可用。

已验证：Windows 上 `dotnet build`/`dotnet test` 全部通过；`dotnet publish -r linux-x64 --self-contained` 产物生成；本地 Kestrel 冒烟（健康检查、策略、设备页、未配置 SMTP 的统一 503）。

未验证：真实 Linux 主机运行自包含包、Docker 构建与运行（本机无 Docker）、真实 SMTP 提供商（TLS 连接/投递）、反向代理后的 HTTPS 与 `X-Forwarded-*`、手机端、多设备并发冲突的真实网络条件。上线前请在目标环境补测。

## 版本

服务端独立版本号（当前 `0.1.0`），与桌面客户端版本无强绑定；协议版本为 `/v1`。新增字段属兼容变更，删除或改义字段需升 `/v2`。
