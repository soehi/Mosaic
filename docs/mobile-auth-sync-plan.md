# Mosaic 手机端账号、授权与同步预留规划

状态：规划文档；本会话只做规划，不实施代码。基线协议见 [账号、邮箱验证码注册、服务端备份与手机同步规划](auth-sync-server-plan.md)（下称“共享协议文档”），基线版本为 Mosaic 0.15.3（2026-11-19）。

适用范围：Android/iOS 第一版预留。手机端**只消费服务端 `/v1` wire 协议**，不读取 Mosaic 桌面本地 SQLite schema，不引用桌面 Shell/Tasks 的内部实现。服务端由“服务端”会话实施，桌面端由“电脑客户端”会话实施；本文不修改 `server/`、`src/`、`tests/`、`build/`。

---

## 1. 第一版范围与非目标

### 1.1 第一版必须覆盖

| 能力 | 第一版 | 说明 |
| --- | --- | --- |
| 邮箱验证码注册 | 是 | 邮箱 + 6 位验证码 + 自设密码 |
| 邮箱密码登录 | 是 | 可选登录验证码作为二次验证 |
| 忘记密码 | 是 | 重置后撤销全部会话，手机端必须重新登录 |
| 授权桌面登录（Device Flow） | 是 | 手机作为“批准端” |
| 手机自身作为同步客户端 | 是 | 直接登录为主；Device Flow 作为无密码入口 |
| 读取任务 | 是 | 包含活动、已完成、已归档和删除墓碑 |
| 读取可移植设置 | 是 | 只应用白名单键 |
| 写入任务/设置 | 第二阶段 | 第一版只拉取；outbox 与冲突结构预留 |
| 后台同步、推送 | 第二阶段 | 第一版仅前台/回到前台时同步 |
| 附件、图片、富文本 | 非目标 | 桌面协议当前也没有附件 |

### 1.2 明确不做

- 不把桌面 `mosaic.sqlite` 复制到手机，也不解析其表结构、迁移历史或列名。
- 不使用 WebView `localStorage` 保存会话或缓存权威数据。
- 不在手机端保存密码、邮箱验证码、access/refresh token 明文到普通文件、日志或系统备份。
- 不实现“谁 Revision 大谁覆盖”的同步；手机端本地版本号不是全局版本。
- 不要求登录才能启动应用；离线仍可进入本地界面并查看上次同步的缓存。

### 1.3 交付边界

本文只新增本文件。手机端实施时新增独立 `mobile/` 目录与独立解决方案，不改动桌面模块边界测试的既有项目列表；服务端如需新增端点或字段，必须先更新共享协议文档并通知桌面端，手机端不得单方面假设。

---

## 2. 技术栈与最低系统版本

### 2.1 推荐：.NET MAUI 10（C#）

| 维度 | 结论 | 理由 |
| --- | --- | --- |
| 语言/框架 | .NET MAUI 10，`net10.0-android`、`net10.0-ios` | 团队现有技术栈是 C#/.NET；与服务端同为 .NET 10；wire DTO 可用同一套 `System.Text.Json` 记录与契约测试 |
| 共享代码 | 新建中立的 `Mosaic.Mobile.Wire`（纯 DTO、错误码、JSON 选项） | 桌面、手机、服务端可以各自引用同一份“协议镜像”，但不引用彼此的内部程序集 |
| 安全存储 | `SecureStorage` 抽象 + 平台实现 | Android 落到 Keystore 包装密钥，iOS 落到 Keychain；业务层不直接接触平台 API |
| 本地缓存 | SQLite（`sqlite-net-pcl` 或 EF Core SQLite） | 手机自己的库，表结构由手机端拥有，与桌面库无关 |
| 最低 Android | Android 8.0（API 26） | 系统默认 TLS 1.2、Keystore AES-GCM、通知/后台限制语义稳定；2026 年覆盖足够，且避免 API 21–25 的存储与加密兼容分支 |
| 最低 iOS | iOS 16.0 | 仍在安全更新窗口内，Keychain/ATS/Swift Concurrency 行为一致；覆盖 iPhone 8 及之后机型 |
| 构建链 | Android 本机/CI 构建；iOS 必须 macOS 构建机或云 CI | 所有跨平台方案都一样，iOS 签名与上传需要 Apple 工具链 |

备选方案与取舍：

- **Flutter**：UI 迭代和视觉一致性更强，适合设计驱动；代价是与 C# 生态割裂，wire DTO 需要代码生成或手工镜像。协议层不受影响，若最终选 Flutter，本文第 4–12 节仍然成立。
- **Kotlin Multiplatform + Compose**：共享业务逻辑、原生 UI 体验好；需要 Kotlin/Swift 双栈维护，团队成本最高。
- **原生 Kotlin + Swift**：体验最佳、成本最高，不建议第一版。

**决策建议**：第一版选 .NET MAUI，前提是团队能维护 macOS/iOS 构建链；如果 UI 交付速度是首要约束，则选 Flutter。该决策不阻塞协议与服务端实施。

### 2.2 版本与渠道

- 手机端使用独立语义版本（建议首版 `0.1.0`），不跟随桌面 `0.15.x`。
- 每个请求带 `User-Agent: Mosaic-Mobile/<version> (<platform> <osVersion>)`，便于服务端排障与最小版本策略；不得包含邮箱、设备唯一硬件标识。
- 应用包名已确认：`app.mosaic.tasks`（Android applicationId / iOS bundle identifier）；上架后不可更改。

---

## 3. 架构边界与项目布局

```text
mobile/
  Mosaic.Mobile.sln
  src/
    Mosaic.Mobile.Wire/        # /v1 DTO、错误码、JSON 选项、契约样例
    Mosaic.Mobile.Auth/        # 登录状态机、令牌生命周期、Device Flow 状态机
    Mosaic.Mobile.Sync/        # cursor、变更应用、outbox（第二阶段）
    Mosaic.Mobile.Storage/     # 手机本地 SQLite/偏好；只拥有自己的表
    Mosaic.Mobile.App/         # MAUI 页面、导航、本地化、平台入口
  tests/
    Mosaic.Mobile.Wire.Tests/      # 契约样例与兼容性
    Mosaic.Mobile.Auth.Tests/      # 状态机与错误映射
    Mosaic.Mobile.Sync.Tests/      # cursor/tombstone/幂等/冲突
```

约束：

1. **Wire 层无平台依赖**：`Mosaic.Mobile.Wire` 不引用 MAUI、不引用桌面程序集、不引用 `server/`。
2. **网络只有一个所有者**：所有 `/v1` 调用经 `Auth` 或 `Sync` 的 HTTP 客户端；页面不得自行拼 URL、自行读令牌。
3. **令牌只有一个所有者**：`Mosaic.Mobile.Auth` 的 `TokenStore` 是唯一读写会话的位置；其他模块通过接口请求带令牌的请求。
4. **缓存是投影不是权威**：本地表只保存服务端 payload 的映射与同步元数据；不得推断桌面数据库的语义。
5. **未知字段忽略、未知枚举不崩溃**：服务端新增字段或枚举值时，旧版手机端必须降级显示而不是解析失败。
6. **平台能力隔离**：Keystore/Keychain、生物识别、网络状态、后台任务各自封装在 `Platform/`，业务层用接口注入。

---

## 4. `/v1` wire 契约（手机端消费基线）

### 4.1 通用规则

- Base URL 由用户输入或配置下发，统一以 `/v1` 结尾拼接；正式环境必须 HTTPS。
- 请求/响应 JSON 使用 camelCase；时间使用 ISO 8601 UTC（`2026-11-19T08:00:00Z`）。
- 错误统一为 `{ "code": "...", "message": "...", "traceId": "..." }`；手机端以 `code` 驱动逻辑，`message` 只作为兜底展示，不能用于判断。
- 分页/游标均为不透明字符串；手机端不解析、不比较、不自行递增。
- 请求超时建议：连接 10 秒、总 30 秒；上传/下载备份单独放宽到 120 秒。
- 所有重试的请求必须可安全重放：GET 可直接重试；写请求必须带幂等键。

### 4.2 认证端点（手机端使用方式）

| 端点 | 手机端用途 | 关键约束 |
| --- | --- | --- |
| `GET /v1/auth/policy` | 启动时读取注册是否开放、是否要求邮箱验证、服务端名称 | 失败不阻塞离线启动；显示“无法连接服务器” |
| `POST /v1/auth/email-code` | 注册/登录/重置发送 6 位码 | 统一 202；60 秒重发间隔；不暴露邮箱是否注册 |
| `POST /v1/auth/register` | 邮箱 + 密码 + 验证码 | 成功直接返回 session；失败不区分账号是否存在 |
| `POST /v1/auth/login` | 邮箱 + 密码 | 可选要求 login 验证码作为二次验证 |
| `POST /v1/auth/login/code` | 邮箱 + 验证码登录或 MFA | 与密码登录返回同一 session 结构 |
| `POST /v1/auth/refresh` | 刷新会话 | refresh token 轮换；旧 token 立即失效 |
| `POST /v1/auth/logout` | 退出当前设备 | 只撤销当前 refresh family |
| `POST /v1/auth/forgot-password` / `reset-password` | 忘记密码 | 重置后撤销全部会话；手机端清空令牌并回到登录 |
| `GET /v1/auth/me` | 显示当前账号、服务端名称 | 401 时进入会话失效处理 |

手机端需要、服务端 0.1.1 已实现的稳定错误码：`auth.invalid_credentials`、`auth.invalid_code`、`auth.code_expired`、`auth.code_attempts_exceeded`、`auth.weak_password`、`auth.rate_limited`、`auth.registration_closed`、`auth.required`、`auth.session_revoked`、`auth.session_expired`、`auth.invalid_refresh`、`auth.refresh_reused`、`auth.reauth_required`、`device.invalid_code`、`device.rate_limited`、`sync.invalid`、`sync.cursor_invalid`、`sync.conflict`；另外兼容旧别名 `auth.code_invalid`、`auth.password_policy`。这些是机器码，不是展示文案。

### 4.3 Device Flow：两种场景

共享协议已定义：

- `POST /v1/device/start` → `deviceCode`、`userCode`、`verificationUri`、`expiresIn=600`、`interval=2`
- `POST /v1/device/poll` → `pending / approved / denied / expired / consumed`
- `POST /v1/device/approve` / `POST /v1/device/deny`

#### 场景 A：手机批准桌面登录（桌面是发起端）

1. 桌面展示 `BAAD-C832` 形式的 `userCode`，并给出“在浏览器中登录”入口；桌面只轮询，不显示 `deviceCode`。
2. 手机已登录时，在“授权新设备”页输入或扫码得到 `userCode`；手机先展示“即将授权哪台设备、哪个账号”，用户确认后调用 `POST /v1/device/approve`。
3. 手机未登录时，用系统浏览器打开 `verificationUri`（Android Custom Tabs / iOS `ASWebAuthenticationSession`），完成登录后再确认授权；不得在应用内嵌 WebView 收集密码。
4. 手机显示结果（已允许/已取消/已过期），并提供“重新输入”。授权成功后桌面轮询拿到 session，手机不保存桌面的任何令牌。

安全要求：

- 授权动作前要求一次本地确认（建议生物识别/设备凭据；至少是独立确认按钮），防止手机解锁状态下被他人代批。
- 深链只允许**预填**授权码，不允许自动批准；`userCode` 输入不区分大小写、自动去连字符，限定 8 位字母数字。
- 授权码不是秘密但一次性使用；失败次数限流；过期后明确提示重新在桌面发起。
- 手机不得记录 `deviceCode`、桌面会话或授权码到日志/剪贴板历史。

#### 场景 B：手机作为独立同步客户端发起 Device Flow

适用于手机不便直接输入密码、或用户希望沿用浏览器登录结果的场景：

1. 手机调用 `POST /v1/device/start`，携带 `deviceLabel`（如“Pixel 9 · Android 16”）、`platform`（固定小写 `android` | `ios`）与 `appVersion`；获得 `userCode`、`verificationUri`、`verificationUriComplete`、`expiresIn`、`interval`。
2. 手机展示授权码与倒计时，并提供“在浏览器中打开”按钮（系统浏览器，不在 WebView 内登录）。打开链接一律使用服务端返回的 `verificationUriComplete`，手机**不得**自行拼接 `?code=`；仅在旧服务端缺少该字段时退回 `verificationUri` 并提示手工输入。
3. 手机在前台按 `interval` 轮询 `POST /v1/device/poll`。轮询过快时服务端返回 **HTTP 200** `{"status":"slow_down","interval":2}`，手机按返回的 `interval` 递增轮询间隔（不低于当前间隔 + 5 秒）；HTTP 429 只用于 `device.rate_limited`，按 `Retry-After` 等待。退到后台立即停止轮询，回到前台按剩余有效期恢复。
4. `approved` 时一次性交付 session（会话字段位于响应根）：手机原子写入 Keychain/Keystore，再调用 `/v1/auth/me` 校验；`denied/expired` 提示重新发起；`consumed` 且本地没有 session 时视为过期，不重试。
5. 普通邮箱密码登录仍是主路径；Device Flow 是补充入口，不默认展示，避免用户困惑。

已在共享协议文档 §3.4 冻结的 Device Flow 字段：

- `device/start` 请求体接受 `deviceLabel`、`platform`、`appVersion`。
- `verificationUriComplete` 由服务端返回并优先使用；手机不构造授权链接。
- `GET /v1/device/info?userCode=...` 需要登录，返回 `deviceLabel`、`platform`、`appVersion`、`state`、`expiresAt`、`approvedAt`，供手机批准页展示。

### 4.4 备份读取（第一版可选）

- `GET /v1/backups/latest` 获取快照元数据；`GET /v1/backups/{snapshotId}` 下载完整快照。
- 手机第一版只在“首次登录且本地为空”时提供“从云端恢复”作为引导，且必须二次确认覆盖本地缓存；不提供静默覆盖。
- 恢复只映射到手机自己的缓存表，不写桌面数据库，不假设快照内部结构超出 `items[{kind,key,payload,revision}]`。

### 4.5 增量同步 DTO（服务端 0.1.1 最终 wire）

以下字段与语义以共享协议文档 §3.4（[auth-sync-server-plan.md](auth-sync-server-plan.md)）和服务端 0.1.1 实现为准，手机端按此实现；任何字段变更必须先更新共享协议并通知手机端。

- `GET /v1/sync/changes?cursor=&limit=200` → `{changes, nextCursor, hasMore}`；空 cursor 等价于从 0 开始。
- `POST /v1/sync/changes` **每次只写一条变更**，没有批量端点。请求体 `{deviceId,kind,key,payload,deleted,baseServerRevision,clientRevision,idempotencyKey}`，响应 `{change,replayed}`；服务端按 `user_id + idempotencyKey` 去重，重复提交返回已存储的 `change` 且 `replayed=true`。
- 读响应中的 `Change` 统一为 `{cursor,serverRevision,kind,key,deleted,payload,deviceId,createdAt}`；`cursor` 是逐行不透明游标，历史清理后不可再用。
- 删除通过 tombstone 传播：`deleted=true`、`payload` 省略/`null`，但保留 `key` 与 `serverRevision`；服务端分配单调递增 `serverRevision`，客户端本地 `Revision` 不是全局版本。

```json
{
  "cursor": "eyJzZXEiOjEwNDJ9",
  "serverRevision": 1042,
  "kind": "task",
  "key": "6f1c0d0e-6c3a-4a1e-9f5d-1b2c3d4e5f60",
  "deleted": false,
  "payload": {
    "id": "6f1c0d0e-6c3a-4a1e-9f5d-1b2c3d4e5f60",
    "title": "买牛奶",
    "notes": "",
    "isCompleted": false,
    "createdAtUtc": "2026-11-19T08:00:00Z",
    "updatedAtUtc": "2026-11-19T08:05:00Z",
    "revision": 7,
    "completedAtUtc": null,
    "archivedAtUtc": null,
    "restoredAtUtc": null
  },
  "deviceId": "b1a2c3d4-...",
  "createdAt": "2026-11-19T08:05:01Z"
}
```

单条写入请求与响应：

```json
{
  "deviceId": "b1a2c3d4-...",
  "kind": "task",
  "key": "6f1c0d0e-6c3a-4a1e-9f5d-1b2c3d4e5f60",
  "payload": { "id": "6f1c0d0e-...", "title": "买牛奶", "...": "..." },
  "deleted": false,
  "baseServerRevision": 1041,
  "clientRevision": 8,
  "idempotencyKey": "9b0f4d0e-7f0e-4b8e-9f4e-6e6f5c4b3a21"
}
```

```json
{ "change": { "cursor": "eyJzZXEiOjEwNDJ9", "serverRevision": 1042, "kind": "task", "key": "6f1c0d0e-...", "deleted": false, "payload": { "...": "..." }, "deviceId": "b1a2c3d4-...", "createdAt": "2026-11-19T08:05:01Z" }, "replayed": false }
```

- `kind`：`task` | `setting`。
- `key`：任务为 GUID 字符串；设置为设置键（如 `tasks.sortOrder`）。
- `payload`：任务为完整 `TaskItemDto` 形状；设置固定为 `{ "value": <JSON 标量> }`，且只允许 §7.2 的 14 个可移植键；未知键/未知类型忽略并记录诊断。
- `baseServerRevision` 是写入所基于的服务端版本，新对象为 0；`clientRevision` 是本地递增版本。
- 顶层不重复 `revision`；任务版本在 `payload.revision`，设置没有客户端版本。
- 任务 `Revision`、`serverRevision`、`cursor` 三者互不替代，手机端不得跨设备比较 `Revision`。

冲突与游标失效：

- 冲突返回 `409 sync.conflict`，错误包 `{code,message,traceId,details?}` 的 `details.current = {kind,key,serverRevision,deleted,payload}` 携带服务端当前记录；`details` 不得含令牌、验证码或密码。手机端以 `serverRevision` 做 LWW 判定，冲突留给用户选择或第二阶段冲突 UI。
- 游标格式无法识别返回 `400 sync.invalid`；若 `details.requiresSnapshot=true`，手机端清空投影并改走 `GET /v1/backups/latest` 重建基线。没有该标志的 `sync.invalid` 属于请求错误，**不得**清空本地投影。
- 历史被清理返回 `410 sync.cursor_invalid`（同样可带 `details.requiresSnapshot=true`）；手机端清空投影并重建基线。
- 规范错误码（服务端 0.1.1）：`auth.invalid_credentials`、`auth.invalid_code`、`auth.code_expired`、`auth.code_attempts_exceeded`、`auth.weak_password`、`auth.rate_limited`、`auth.registration_closed`、`auth.required`、`auth.session_revoked`、`auth.session_expired`、`auth.invalid_refresh`、`auth.refresh_reused`、`auth.reauth_required`、`device.invalid_code`、`device.rate_limited`、`sync.invalid`、`sync.cursor_invalid`、`sync.conflict`。
- 兼容别名（服务端旧构建，客户端仍接受）：`auth.code_invalid`、`auth.password_policy`、`sync.idempotency_mismatch`；客户端按同一分类处理，不依赖错误文案。

### 4.6 契约兼容规则

- 手机端只依赖本文列出的字段；其余字段忽略。
- 新增枚举值按“未知值 = 只读展示/原样保留”处理，不删除本地数据。
- 服务端升级不得改变既有字段含义；破坏性变更走 `/v1` 之外的新前缀。
- 契约样例（请求/响应 JSON）纳入 `Mosaic.Mobile.Wire.Tests`，作为服务端与桌面端的共同验收材料。

---

## 5. 注册、登录与密码策略

### 5.1 邮箱验证码

- 6 位数字，10 分钟有效，最多 5 次校验；服务端按 `email+purpose` 60 秒重发间隔，并按 IP 与邮箱双重限流。
- 手机端倒计时以服务端响应为准（`Retry-After` 或统一错误码），本地仅做展示，退到后台再回来不重置倒计时。
- 输入体验：6 格数字框，支持系统一次性验证码自动填充（iOS `textContentType=oneTimeCode`；Android Autofill hint），支持整段粘贴；不自动读取剪贴板。
- 发送失败统一提示“如果该邮箱可用，验证码已发送”，不区分未注册/已注册。
- 校验失败不提示剩余次数之外的信息；达到上限后引导重新发送。
- 注册成功直接建立会话；若服务端要求邮箱验证，注册页不进入主界面前不得跳过。

### 5.2 密码策略

与共享协议一致，服务端是唯一权威：

- Unicode 字符长度 10–256；不强制大小写/数字/符号组合；不做 Gmail 点号或 `+` 规范化。
- 密码不 trim；允许空格；不得在客户端做比服务端更严格的规则，避免“客户端能过、服务端拒绝”或相反。
- 客户端只做长度与“两次输入一致”的即时校验；强度提示是建议，不是拦截条件。
- 支持密码管理器与粘贴；提供显示/隐藏切换；不把密码写入剪贴板、日志、崩溃上报或屏幕截图缓存。
- 登录失败统一提示“邮箱或密码错误”，不区分账号不存在与密码错误；验证码相关错误区分“错误/过期/尝试过多”，网络失败单独提示。

### 5.3 会话与刷新

- access token 15 分钟、refresh token 14 天且轮换；手机端只持久化 refresh token（access token 可只驻留内存）。
- 401 只尝试一次刷新；刷新失败（`auth.refresh_reused`、`session_revoked`、过期）立即进入“需要重新登录”，清空令牌，保留本地缓存并明确标注只读。
- 并发请求共享同一个刷新任务，避免多个请求同时轮换导致 refresh 重用检测误伤。
- 刷新成功后重放原请求一次；仍失败则展示该请求的真实错误，不循环重试。

### 5.4 设备标识与标签

- `deviceId` 为客户端生成的 UUID，首次启动生成并保存在 Keychain/Keystore；卸载重装后视为新设备。
- 设备标签由用户可见信息组成（机型 + 系统版本，可编辑为“我的手机”），不作为唯一硬件标识，不上报 IMEI/MAC/广告 ID。
- 授权桌面与手机自身同步都使用各自独立的 `deviceId` 与 session，不共享令牌。

---

## 6. 令牌存储

| 平台 | 存储位置 | 保护方式 | 备份行为 |
| --- | --- | --- | --- |
| Android | 应用私有目录中的密文（DataStore/文件），密钥在 Android Keystore | Keystore 生成 AES-256-GCM 密钥，别名按“服务器 + 账号”隔离；每次写入新 IV，AAD 绑定 `deviceId + serverUrl`；优先硬件支持，StrongBox 可选 | 在 Auto Backup / `allowBackup` 中排除密钥与密文；恢复后解密失败必须清除并要求重新登录 |
| iOS | Keychain（`kSecClassGenericPassword`） | `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`；不参与 iCloud Keychain 同步；按 `serverUrl + accountId` 分项；可选 `SecAccessControl` 生物识别保护“查看会话/撤销设备”操作，但不能阻塞后台刷新 | 设备迁移/恢复后 Keychain 项不会随普通备份恢复；缺失时视为未登录 |

统一规则：

- 持久化内容仅限：refresh token、`deviceId`、`serverUrl`、账号标识（邮箱/用户 ID）、令牌过期时间；access token 若持久化也必须使用同一存储，不得落到 `SharedPreferences`/`UserDefaults` 明文。
- 禁止进入存储：密码、邮箱验证码、桌面 `deviceCode`、SMTP/服务器管理密钥、任务正文缓存以外的敏感配置。
- 读取失败或解密失败（Keystore key invalidated、设备恢复、生物识别变更）统一按“会话不可用”处理：清除坏项，提示重新登录，不崩溃。
- 本地缓存中的任务/备注若包含用户隐私，使用应用沙箱即可；第一版不额外加密，但在“退出/设备撤销”时清空。后续可对缓存库启用 SQLCipher，作为独立决策。
- 日志与崩溃上报统一脱敏：过滤 `Authorization`、`refreshToken`、`accessToken`、`deviceCode`、验证码、邮箱完整地址；错误只上报 `code` 与 `traceId`。

---

## 7. 任务与可移植设置的读取范围

### 7.1 任务

字段与桌面 `TaskItemDto` 的 wire 形状一致：`Id`、`Title`、`Notes`、`IsCompleted`、`CreatedAtUtc`、`UpdatedAtUtc`、`Revision`、`CompletedAtUtc`、`ArchivedAtUtc`、`RestoredAtUtc`。

- 第一版读取活动、已完成、已归档任务，以及删除墓碑；不用 `Revision` 做跨设备排序。
- 手机本地模型保留 `serverRevision`（该条变更的服务端序号）与 `lastSyncedCursor`；服务端序号只用于增量判断。
- 删除任务后本地立即从列表移除，并保留 tombstone 记录直到游标越过它，防止旧快照把任务“复活”。
- 归档/恢复是任务字段变化，不是删除；手机端按 `ArchivedAtUtc` 是否为空展示。
- 手机端不解析桌面数据库的 `tasks` 表列名、迁移版本或 CAS 语义；`Revision` 只作为服务端 payload 中的不透明版本值原样保存，写入时回传。

### 7.2 可移植设置白名单

以下键为桌面 `SettingPersistence.UserSettings` 中的候选。第一版手机只**读取并应用**，不上传：

| 设置键 | 组 | 手机端处理 |
| --- | --- | --- |
| `ui.language` | Appearance | 应用（`FollowSystem/Chinese/English`） |
| `appearance.theme` | Appearance | 应用 `FollowSystem/Light/Dark`；`HighContrast` 降级为 `FollowSystem` 并提示 |
| `tasks.sortOrder` | Tasks | 应用（`CreatedDesc/CreatedAsc/TitleAsc/TitleDesc`） |
| `tasks.defaultFilter` | Tasks | 应用（`All/Open/Completed`；归档页由手机导航单独控制） |
| `tasks.archive.autoEnabled` | Tasks | 只读展示；自动归档由桌面/服务端执行，手机不本地执行 |
| `tasks.archive.delayDays` | Tasks | 只读展示 |
| `tasks.title.maxCharacters` | Tasks | 应用为输入上限 |
| `tasks.notes.maxCharacters` | Tasks | 应用为输入上限 |
| `tasks.title.required` | Tasks | 应用为校验 |
| `tasks.defaultCompleted` | Tasks | 应用为新任务默认值（第二阶段写入时） |
| `tasks.revisionStart` | Tasks | 忽略（桌面本地实现细节） |
| `tasks.confirmDelete` | Tasks | 应用 |
| `tasks.confirmUnsaved` | Tasks | 应用 |
| `tasks.showCharacterCount` | Tasks | 应用 |
| `tasks.autoSwitchToAllOnHidden` | Tasks | 应用 |
| `tasks.floating.allowNotesEditing`（浮窗） | FloatingWindows | 忽略（桌面宿主专属） |
| `shell.*`、`storage.*`、`startup.*`、`desktop-presence.*`、`floating-windows.*` | 其他 | 忽略（机器/宿主相关，不备份） |

规则：

- 白名单由桌面端拥有并版本化；手机端硬编码“已知键 + 类型”，未知键忽略，不因新增键失败。
- 手机端对同一键的类型不匹配（例如服务端返回字符串而期望布尔）时保留旧值并记录诊断。
- 第一版不把手机设置写回服务端；第二阶段写入时，设置冲突按单键处理，不做整包覆盖。
- 不读取、不备份：HostConfig、存储路径、窗口尺寸与位置、开机自启、桌面驻留、日志、`settings.pending.json`、SMTP 配置、任何机器路径与令牌。

### 7.3 首次登录与本地为空

1. 调用 `GET /v1/auth/policy` 与 `GET /v1/auth/me`。
2. 若本地缓存为空且服务端存在备份，提供“从云端恢复任务与设置”与“仅同步增量”两个选项；默认后者，避免无意覆盖。
3. 若本地已有离线数据且服务端也有数据，第一版必须让用户选择“保留本机并合并（只读展示）”或“以云端覆盖本机”，不得静默决定。

---

## 8. 同步机制

### 8.1 serverRevision 与 cursor

- `serverRevision` 是服务端为每次变更分配的单调递增序号，属于用户维度；不是任务 `Revision`，也不是时间戳。
- `cursor` 是服务端编码后的不透明游标；手机端只保存最近的 `nextCursor`，不解析内容。
- 应用顺序：在一个本地事务中“写入本页变更 + 更新 cursor”；先更新 cursor 再写数据会在崩溃时永久丢变更。
- 游标失效：历史被清理或游标过期返回 `410 sync.cursor_invalid`；游标格式无法识别返回 `400 sync.invalid`，其中 `details.requiresSnapshot=true` 才表示需要重建。手机端在这两种情况下清空本地投影并从快照重新同步，绝不从中间猜测；普通 `400 sync.invalid` 不清空数据。
- 拉取分页建议 `limit=200`；一次前台同步最多连续追 20 页，剩余留到下次，避免耗电与长时间占用。
- 所有变更以 `serverRevision` 排序应用；同一 `key` 出现多次时以最后一次为准。

### 8.2 tombstone

- 删除以 `deleted=true`、`payload` 省略/`null`、保留 `key/serverRevision` 的形式传播。
- 手机端维护 `deleted_keys` 表（键、serverRevision），直到游标稳定越过；避免旧备份或重复页把已删除任务写回。
- 如果本地有未上传修改而收到 tombstone（第二阶段），进入冲突流程，不静默删除。
- 设置删除（回到默认值）建议用 `upsert` 默认值而不是 tombstone，减少歧义；由服务端确认。

### 8.3 幂等键与 outbox（第二阶段预留）

- 每个写操作在本地 outbox 中先生成 `idempotencyKey`（UUIDv7）并持久化，再发请求；重试复用同一个键。
- 服务端按 `user_id + idempotencyKey` 去重，重复提交返回已存储的 `change` 且 `replayed=true`；客户端不得用同一个键发送不同内容。
- outbox 状态：`pending → sending → acked / failed / conflict`；应用被杀后 `sending` 回到 `pending`。
- 上传顺序按用户操作顺序串行；同一任务的多个操作可合并为最后一次状态，但 `idempotencyKey` 必须对应最终发送内容。
- 第一版只读，不启用 outbox；表结构与状态机可先建立，避免第二阶段迁移本地库。

### 8.4 冲突与 UI（第二阶段）

- 写入请求携带 `baseServerRevision`；服务端发现已变化返回 409 与当前版本（见 4.5）。
- 手机端**不自动合并、不按 Revision 覆盖**。冲突界面逐字段展示：标题、备注、完成状态、归档状态、时间。
- 用户可选择：以服务器版本为准 / 以我的修改为准（重新基于最新 `serverRevision` 提交）/ 手动合并（仅文本字段）。
- 冲突项进入“待解决”列表，任务卡片显示角标；未解决期间该任务不再自动重推，避免循环。
- 设置冲突按单键处理：本地未改则直接采用服务器值；双方都改则展示两个值让用户选择。批量冲突合并为一个页面，不逐条弹窗。
- 网络失败与冲突严格区分：失败可重试，冲突必须用户决策。

### 8.5 拉取流程（第一版）

```text
on app resume / pull-to-refresh / login:
  if no session: stop
  if syncing: join existing run
  loop up to N pages:
    GET /v1/sync/changes?cursor=<saved>&limit=200
    if 401 -> refresh once -> retry
    if cursor_invalid -> clear projection -> bootstrap
    apply all changes in one local transaction
    save nextCursor in same transaction
    if !hasMore: break
  update "last synced at" and UI
```

- 同一时刻只允许一个同步任务；手动下拉与自动同步共享任务。
- 同步中登出/设备被撤销：取消请求，清空队列，不把结果写回。
- 显示“上次同步时间”；失败时保留旧数据并给出可重试提示，不清空列表。

---

## 9. 弱网、离线与生命周期

### 9.1 弱网

- 超时与重试：网络错误/5xx/408 指数退避（1s、2s、4s……上限 60s，带抖动）；429 与 `Retry-After` 优先；4xx（除 408/429）不重试。
- 发送验证码、刷新令牌、Device Flow 轮询不共用激进重试；轮询按 `interval`，收到 `slow_down` 取“当前间隔 + 5 秒”与服务端返回 `interval` 的较大值。
- 所有请求可取消；页面退出、账号切换、登出时取消进行中的请求。
- 显示明确的网络状态（离线/服务器不可达/限流中），不把网络错误当作“密码错误”。

### 9.2 离线

- 启动不要求网络；未登录可本地浏览缓存（若曾登录过），登录页可跳过并“离线使用”。
- 离线时只读缓存，标注“离线数据 · 上次同步 …”；第二阶段允许离线编辑并进入 outbox。
- 令牌过期但离线：保留缓存与只读状态，服务端操作提示“联网后重新登录”，不立即清空数据。
- 缓存过期策略：任务与设置不自动过期；备份下载文件不长期保留，恢复完成后删除临时文件。

### 9.3 生命周期与后台

- 前台恢复、手动下拉、登录成功后触发同步；应用切后台时停止轮询与同步。
- 第一版不做后台同步与推送。第二阶段 Android 用 WorkManager（最小间隔 15 分钟、约束网络可用），iOS 用 `BGAppRefreshTask`（系统调度，不保证）；推送仅作为“有变更”的唤醒信号，正文为空，不含任务内容。
- 不使用精确闹钟、前台服务或常驻连接；不为了实时性牺牲电池与商店审核风险。

---

## 10. 登出、设备撤销与账号删除

### 10.1 登出

顺序固定，避免残留：

1. 取消同步、轮询、刷新任务。
2. 在线时调用 `POST /v1/auth/logout`；失败（网络）时给出两个选择：“仅本机退出（服务端会话到期前仍有效）”或“取消”。
3. 清除 Keychain/Keystore 中的 refresh token、access token、`deviceId` 派生密钥。
4. 清空本地任务/设置缓存与临时备份文件。
5. 重置内存状态，回到离线/登录入口。

不得只删 token 而保留可被下一个账号看到的任务缓存。

### 10.2 设备撤销

会话管理端点已在共享协议文档 §3.4 冻结：

- `GET /v1/auth/sessions`：返回 `id`、`deviceLabel`、`platform`、`createdAt`、`lastSeenAt`、`expiresAt`、`current`，不返回 token。
- `POST /v1/auth/sessions/{id}/revoke`：撤销指定会话；目标不存在返回 `404 auth.session_not_found`（幂等展示为“已失效”）。
- `POST /v1/auth/sessions/revoke-all`：撤销全部会话；可选请求体 `{keepCurrent:true}` 保留当前设备，无请求体表示全部撤销。保留当前设备时本地会话继续可用。
- 手机“账户”页提供“登录设备”列表、当前设备标记与撤销入口；撤销当前设备等同退出并清空本地数据。
- 被其他设备远程撤销后，任意请求返回 `auth.session_revoked`；令牌不可用时服务端也可能返回 `auth.required`、`auth.session_expired` 或 refresh 阶段的 `auth.invalid_refresh` / `auth.refresh_reused`。手机端把这些统一视为会话结束，立即清空令牌与本地缓存，展示“此设备已被移除，请重新登录”，不得反复自动重试。
- 改密/重置密码后全部会话失效，手机端回到登录页；需要重新验证的操作返回 `auth.reauth_required`，手机端引导再次输入密码或邮箱验证码。

### 10.3 账号删除

- 共享协议文档 §3.4 已冻结 `POST /v1/auth/account/delete`：请求体为 `{password}` 或 `{code}` 二选一（`code` 来自 `purpose=delete-account` 的新鲜验证码）；凭据无效或过期返回 `401 auth.reauth_required`。删除账号、会话、refresh token、邮件码、设备授权、备份、同步变更和游标；审计日志去标识化保留。
- Google Play 与 App Store 都要求：应用内可创建账号，就必须提供应用内可发起的账号删除入口。手机“账户”页提供“删除账号”，桌面账户页同样提供入口。
- 手机流程：说明不可恢复 → 本地重新验证（当前密码，或发送 `purpose=delete-account` 验证码）→ 调用端点 → 成功后清空 Keychain/Keystore、本地缓存和后台任务 → 回到离线/登录入口。
- 离线时不执行本地删除；请求失败保留数据并提示重试；遇到 `auth.reauth_required` 重新走验证，不得跳过。
- 收到成功响应后不得保留任何账号标识、设备标签或可关联的本地日志。

---

## 11. 安全、TLS 与隐私

### 11.1 传输安全

- 正式环境只允许 HTTPS，最低 TLS 1.2，建议 TLS 1.3；服务端启用 HSTS。
- Android：`network_security_config.xml` 禁止明文（`cleartextTrafficPermitted=false`），Release 只信任系统 CA；Debug 可对显式配置的本地开发地址放行，且不得进入正式包。
- iOS：保持 ATS 默认，不使用 `NSAllowsArbitraryLoads`；本地开发仅 Debug 配置 `NSAllowsLocalNetworking`。
- 证书错误（过期、域名不匹配、自签名）直接失败，不提供“忽略继续”。自建服务器用户需正确配置证书；文档给出排查指引。
- 第一版不做证书固定（自建服务器轮换成本高）；如后续提供，必须允许用户更新指纹并提供失效恢复路径。
- 登录、验证码、授权页禁用 WebView 内表单；浏览器授权必须使用系统浏览器/`ASWebAuthenticationSession`。

### 11.2 客户端安全

- Android：`FLAG_SECURE` 仅用于登录/授权/账户等敏感页面可选；不全局禁用截图，避免影响正常使用。
- iOS：不把令牌写入 `UserDefaults`；不启用 iCloud Keychain 同步；备份排除敏感文件（`isExcludedFromBackup`）。
- 根检测/越狱检测不做强制拦截（自托管场景误伤大），只作为诊断信息，不上报硬件标识。
- 不集成广告 SDK、不读取广告 ID、不做跨应用追踪；如加入崩溃上报，必须默认关闭或在隐私政策中说明并可关闭。

### 11.3 应用商店隐私

| 项目 | Google Play（Data safety） | App Store（App Privacy） |
| --- | --- | --- |
| 邮箱 | 收集，账号管理，传输加密 | Contact Info → Email，与用户关联，不用于追踪 |
| 任务/备注 | 收集，应用功能，用户内容，传输加密 | User Content，与用户关联 |
| 设备标识 | `deviceId` 仅用于会话管理；不使用广告 ID | Identifiers → Device ID（如声明） |
| 诊断 | 若启用崩溃上报，单独声明并可关闭 | Diagnostics（如启用） |
| 删除 | 提供应用内删除或网页入口 | 同左，且需在隐私政策中说明 |

其他要求：

- 提供隐私政策 URL、支持邮箱、账号删除说明、数据保留周期。
- 首次启动展示隐私政策与用户协议入口，不默认勾选同意；不因拒绝非必要权限而阻止核心功能。
- 权限最小化：仅网络；第二阶段推送才申请通知权限；不申请通讯录、定位、存储、电话状态。
- 中国大陆分发（App Store 中国区、华为/小米/OPPO/vivo/应用宝）另有要求：App 备案、软件著作权、隐私合规检测，以及服务器域名 ICP 备案；自建服务器若部署在境内也需备案。上线前单独确认，不影响协议设计。
- 年龄分级与内容说明：任务/备注属于用户生成内容，需在商店问卷中如实填写；第一版无社交、无公开内容，风险较低。

---

## 12. UI/UX、本地化与无障碍

- 导航建议：任务（全部/未完成/已完成/已归档）、账户与同步、设置。登录入口放在账户页，不阻塞启动。
- 登录页与桌面截图语义一致：主按钮“登录”、次入口“在浏览器/其他设备登录”、“连接其他自建服务器”、“不连接服务器，离线使用”、“忘记密码”。
- “连接其他自建服务器”要求输入完整 HTTPS Base URL，保存前调用 `GET /v1/auth/policy` 验证；失败不保存。地址存于普通设置（非秘密），切换服务器必须重新登录并清空旧账号缓存。
- 授权页：显示发起设备名/平台、当前账号、授权码、倒计时、允许/取消；授权成功给明确反馈。
- 状态可见：离线、同步中、上次同步时间、待解决冲突（第二阶段）、会话已失效。
- 本地化：跟随 `ui.language` 与系统语言，至少简体中文/英文；服务端 `message` 只作兜底，按 `code` 映射本地文案。
- 无障碍：44pt 最小触控区、动态字体 200% 不截断、TalkBack/VoiceOver 为验证码格、倒计时、冲突差异提供标签；深色模式与对比度满足 WCAG AA。
- 大文本与长备注：任务详情可滚动、可复制；编辑上限来自 `tasks.notes.maxCharacters`。

---

## 13. 测试与验收

### 13.1 契约与状态机

- 每个端点至少一份请求/响应 JSON 样例；严格解析、未知字段容忍、未知枚举降级。
- 注册/登录：验证码过期、错误、尝试上限、60 秒重发、429、统一文案、Unicode 密码边界（9/10/256/257 字符、emoji、空格）。
- 会话：401 刷新一次、并发刷新去重、refresh 重用、登出、改密后全部失效。
- Device Flow：pending→approved→consumed、denied、expired、`slow_down`、后台暂停/恢复、重复投递不重放。

### 13.2 存储与平台

- Android：Keystore 密钥失效（恢复备份、清除凭据）、AAD 不匹配、设备重启后解密、Auto Backup 排除生效。
- iOS：Keychain `AfterFirstUnlockThisDeviceOnly` 行为、设备迁移后缺失、生物识别变更后的降级。
- 日志与崩溃：令牌、验证码、邮箱不出现在任何输出；HTTP 拦截器脱敏用例。

### 13.3 同步

- cursor 分页、`hasMore`、事务性写入、崩溃恢复、`cursor_invalid` 重建。
- tombstone 不复活、重复页幂等、乱序 `serverRevision`。
- 第二阶段：409 冲突界面、幂等键重放、outbox 被杀恢复、设置单键冲突。

### 13.4 弱网与安全

- 飞行模式、断网重连、高延迟/丢包代理、429 与 `Retry-After`、服务器 5xx。
- 明文 HTTP 被拒、错误证书被拒、自建服务器地址校验、无 WebView 登录。
- 登出/撤销后本地数据与令牌不可恢复；应用备份中不含令牌。

### 13.5 验收口径

- 真机矩阵：Android 8/12/15 至少各一台（含低端机）；iOS 16/18 至少各一台。
- 商店预检：Data safety/App Privacy 填写与代码行为一致；账号删除可达；隐私政策可访问。
- 与桌面/服务端联合验收：同一账号在桌面注册、手机登录、手机授权第二台桌面、手机增量看到任务与设置变化、删除在手机显示为移除。

---

## 14. 实施分期

| 阶段 | 内容 | 退出条件 |
| --- | --- | --- |
| P0 协议对齐 | 复核第 16.1 节冻结项；落地契约样例与错误码映射 | 服务端/桌面/手机三方对 DTO 与错误码无歧义 |
| P1 本地骨架 | MAUI 工程、离线入口、本地缓存、设置与主题、契约样例 | 无网络可启动；契约测试通过 |
| P2 账号 | 注册、登录、忘记密码、令牌存储、刷新、登出 | 真机完成注册→登录→重启保持登录→登出清空 |
| P3 授权 | 手机批准桌面、手机 Device Flow 登录、系统浏览器授权 | 两种 Device Flow 真机全流程通过 |
| P4 只读同步 | 任务、设置、cursor、tombstone、首次恢复引导 | 桌面改动在手机可见，删除正确消失 |
| P5 写入与冲突 | outbox、幂等键、409 冲突 UI、设置写回 | 双设备并发编辑不丢数据、不静默覆盖 |
| P6 上架 | 隐私合规、商店材料、崩溃上报脱敏、发布 | 内测通过，商店审核材料齐备 |

第一版（P1–P4）即可满足共享协议中“手机端第一版只预留读取”的定位；P5 起才引入写同步。

---

## 15. 风险与取舍

| 风险 | 影响 | 缓解 |
| --- | --- | --- |
| 实现与服务端冻结字段不一致 | 手机端返工 | 以第 16.1 节冻结项与契约测试作为门禁 |
| 自建服务器证书配置错误 | 用户无法登录 | 连接页做可读诊断；文档给证书指引；不提供忽略证书 |
| 手机缓存含隐私数据 | 设备丢失泄露 | 沙箱 + 登出/撤销清空；后续评估 SQLCipher |
| 后台同步被系统限制 | 数据不同步及时 | 前台同步为主；UI 显示上次同步；推送只做唤醒 |
| MAUI iOS 构建链 | 发布受阻 | 提前准备 macOS/云 CI 与签名；必要时切 Flutter |
| 商店合规遗漏（账号删除、备案） | 无法上架 | 第 10.3、11.3 节列为发布门禁 |
| 设置白名单漂移 | 手机显示/行为不一致 | 白名单版本化；未知键忽略；联合验收 |

---

## 16. 已确认协议与仍需确认事项

### 16.1 已在共享协议中冻结

以下事项不再阻塞实施，手机端按共享协议文档（[auth-sync-server-plan.md](auth-sync-server-plan.md)）§3.4、§4.3 执行：

| 事项 | 定案 | 位置 |
| --- | --- | --- |
| 账号删除 | `POST /v1/auth/account/delete`，请求体 `{password}` 或 `{code}`（新鲜 `purpose=delete-account` 验证码）；凭据无效返回 `401 auth.reauth_required`；删除账号、会话、refresh token、邮件码、设备授权、备份、同步变更与游标，审计日志去标识化保留 | §3.4 |
| 会话列表/撤销 | `GET /v1/auth/sessions` → `{sessions:[...]}`；`POST /v1/auth/sessions/{id}/revoke`（不存在 `404 auth.session_not_found`）；`POST /v1/auth/sessions/revoke-all`（无 body 撤销全部，`{keepCurrent:true}` 保留当前）；错误码 `auth.session_revoked`、`auth.reauth_required` | §3.4 |
| 会话响应 | 根级 `accessToken/refreshToken/accessTokenExpiresAt/refreshTokenExpiresAt/expiresIn`，并同时可带 `user{id,email}` 与 `account{userId,email,displayName}`；手机端两种身份字段都解析，缺失绝对过期时间时用 `expiresIn` 计算 | §3.4 |
| 同步读 DTO | `ChangeDto {cursor,serverRevision,kind,key,deleted,payload,deviceId,createdAt}`；`GET /v1/sync/changes?cursor=&limit=200` → `{changes,nextCursor,hasMore}`；空 cursor 等价于 0 | §3.4 |
| 同步写 DTO | 单条写入 `POST /v1/sync/changes`，无批量；请求 `{deviceId,kind,key,payload,deleted,baseServerRevision,clientRevision,idempotencyKey}`，响应 `{change,replayed}`；任务 payload 为完整 `TaskItemDto` 形状；设置 payload 为 `{value:<JSON 标量>}`，仅 14 个白名单键；删除时 `payload` 省略/`null` 但保留 key/version | §3.4 |
| 冲突与错误包 | 错误包 `{code,message,traceId,details?}`；`409 sync.conflict` 的 `details.current={kind,key,serverRevision,deleted,payload}` 携带服务端当前记录；`details` 不含令牌、验证码或密码 | §3.4 |
| 游标失效 | 游标格式错误返回 `400 sync.invalid`（`details.requiresSnapshot=true` 时改走快照）；历史被清理返回 `410 sync.cursor_invalid`；两者重建基线均用 `GET /v1/backups/latest`；无 `requiresSnapshot` 的 400 不清空投影 | §3.4 |
| Device Flow 字段 | `device/start` 接受 `deviceLabel/platform=android\|ios/appVersion`，返回 `verificationUriComplete` 供直接打开；轮询过快返回 **200** `{status:"slow_down",interval}` 并按该间隔递增；429 仅 `device.rate_limited`；`GET /v1/device/info?userCode=` 返回 label/platform/appVersion/state/expiresAt/approvedAt | §3.4 |
| 设置白名单 | 由桌面端拥有并版本化；手机硬编码“已知键 + 类型”，未知键忽略；冻结 14 个键，见共享协议 §4.3 | §4.3 |
| 稳定错误码 | 规范集 18 个：`auth.invalid_credentials`、`auth.invalid_code`、`auth.code_expired`、`auth.code_attempts_exceeded`、`auth.weak_password`、`auth.rate_limited`、`auth.registration_closed`、`auth.required`、`auth.session_revoked`、`auth.session_expired`、`auth.invalid_refresh`、`auth.refresh_reused`、`auth.reauth_required`、`device.invalid_code`、`device.rate_limited`、`sync.invalid`、`sync.cursor_invalid`、`sync.conflict`；兼容别名 `auth.code_invalid`、`auth.password_policy`、`sync.idempotency_mismatch` | §3.4 |

### 16.2 仍需用户/产品确认

1. **技术栈与系统版本**：确认 .NET MAUI + Android 8.0/API 26 + iOS 16.0，还是 Flutter（应用包名已确认 `app.mosaic.tasks`）。
2. **分发渠道**：是否只做 Google Play/App Store，还是需要中国大陆安卓商店与 App Store 中国区（涉及备案与软著）。
3. **推送与后台**：第一版确认不做；第二阶段是否需要 FCM/APNs，以及是否接受“无正文唤醒”。
4. **最小客户端版本策略**：是否由服务端 `policy` 返回最低版本，手机端如何处理强制升级（当前协议未冻结该字段）。
5. **手机端写入时机**：是否按本文 P5 放在只读版之后，还是第一版就要求离线编辑与冲突 UI。

---

## 17. 对现有安装与仓库的影响

- 本文为纯规划文档，不修改 `server/`、`src/`、`tests/`、`build/`，不改变桌面数据目录、SQLite schema、设置持久化或安装升级行为。
- 手机端实施时新增独立 `mobile/` 目录；桌面现有安装无迁移，桌面端只在共享协议更新后增加账户/同步模块。
- 服务端新增账号删除、会话管理或同步字段属于新增端点/可选字段，不破坏既有账号与备份；发布时服务端与手机端版本独立，协议变更先更新共享协议文档。
- 上线手机端前，服务端与桌面端按第 16.1 节冻结项完成实现；第 16.2 节产品决策需用户确认。

---

## 18. 交接说明

- 本文只定义手机端如何消费 `/v1`，不替代共享协议文档；任何与服务端不一致处，以更新后的共享协议文档为准。
- 手机端实施会话拥有 `mobile/` 全部文件与测试；不得修改 `src/`、`tests/`、`build/`、`server/`。
- 服务端会话负责第 16.1 节冻结端点与字段的实现，并维护共享协议文档；桌面会话负责设置白名单与 `ChangeDto` 任务 payload 的实现。
- 手机端不读取 Mosaic 本地 SQLite schema，不引用桌面内部程序集；本地库表由手机端自己拥有，随时可重建。
