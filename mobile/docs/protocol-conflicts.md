# 手机端发现的协议冲突与缺失端点

本文件记录手机端实施时（2026-11-19）与共享协议 [docs/auth-sync-server-plan.md](../../docs/auth-sync-server-plan.md) 和 [手机端规划](../../docs/mobile-auth-sync-plan.md) 不一致之处。手机端**不单方面修改**桌面或服务端定义，只做双向兼容并在下方标注；请服务端/桌面会话确认后更新共享协议。

## 1. 同步变更 DTO 字段

| 项目 | 冻结协议 | 当前服务端实现 |
| --- | --- | --- |
| 删除标记 | `operation: "upsert" \| "delete"` | `deleted: true/false` |
| 时间 | `updatedAtUtc` | `createdAt`（另有 `seq`、逐行 `cursor`） |

手机端处理：`ChangeDto` 同时读取 `operation`/`deleted` 与 `updatedAtUtc`/`createdAt`；显式但无法识别的 `operation` 直接跳过，不猜测。测试覆盖两种形状。

建议：服务端按冻结协议补 `operation` 与 `updatedAtUtc`；在此之前双方都可运行。

## 2. 错误码

| 场景 | 冻结码 | 服务端实际码 |
| --- | --- | --- |
| 验证码错误 | `auth.code_invalid` | `auth.invalid_code` |
| 验证码尝试过多 | `auth.code_attempts_exceeded` | `auth.code_locked` |
| 密码策略 | `auth.password_policy` | `auth.weak_password` |
| 邮箱格式 | `auth.email_invalid` | `auth.invalid_email` |
| 会话失效 | `auth.session_revoked` | `auth.session_expired` / `auth.required` / `auth.invalid_refresh` |

手机端处理：`WireErrorCodes` 同时保留冻结码与服务端别名，`AuthError` 分类器两种都映射到同一行为。测试覆盖。

建议：服务端统一到冻结码；在完成前，任何新客户端都必须同时识别两套。

## 3. `sync.cursor_invalid` 未实现

服务端当前没有返回该错误（也没有历史清理逻辑）。手机端按规划实现：收到 `410` 或 `sync.cursor_invalid` 时清空投影并从 `/v1/backups/latest` 重建；同一次同步内第二次失效则停止并提示手动重建，避免循环。

## 4. 缺失的冻结端点（当前服务端返回 404）

- `GET /v1/auth/sessions`
- `POST /v1/auth/sessions/{id}/revoke`
- `POST /v1/auth/sessions/revoke-all`
- `POST /v1/auth/account/delete`
- `GET /v1/device/info?userCode=`

手机端已按冻结协议实现调用；服务端补齐前，账户页的会话管理、账号删除与桌面授权页的设备信息会显示通用失败（不泄露原始响应）。设备批准/拒绝端点（`/v1/device/approve`、`/v1/device/deny`）服务端已存在。

## 5. `slow_down` 与限流

冻结协议用轮询状态 `slow_down`；服务端实际返回 `429 device.rate_limited` + `Retry-After`。手机端两路都处理：状态 `slow_down` 或 429 都把轮询间隔增加 5 秒并采用服务端 `Retry-After`。

## 6. 授权码字母表与文档示例

服务端字母表为 `BCDFGHJKLMNPQRSTVWXZ23456789`，不含 A/E/I/O/U。规划文档中的示例 `BAAD-C832` 按此规则是非法码。手机端完全镜像服务端字母表；文档示例应改为例如 `BCDF-GHJK`。

## 7. 备份快照字段

规划文档提到 `/v1/backups/latest` 返回 `serverRevision` 与 `cursor`；当前服务端不返回这两个字段。手机端将二者设为可选，恢复后若无 `cursor` 则从空游标继续拉增量。

## 8. 设备标识

登录/注册响应不返回服务端 deviceId；手机端自行生成并加密保存本地 deviceId，仅把 `deviceLabel`/`platform`/`appVersion` 发给服务端。写入阶段（第二阶段）需要服务端确认 deviceId 的传递方式。

## 9. 设置白名单与桌面发布范围

规划 §7.2 列出 14 个可移植设置键；桌面同步实现当前只发布 `appearance.theme` 与 `ui.language`。手机端按规划白名单接收，未知键忽略并记录诊断，因此两种状态都能工作。本文件所指“4.3 白名单”应为规划 §7.2；共享协议中未单独编号。
