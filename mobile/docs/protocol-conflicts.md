# 手机端协议对齐记录（服务端 0.1.1）

本文件记录手机端与服务端 `/v1` wire 的对齐情况。基线为服务端 0.1.1 提交 `4a223be` 与共享协议 [docs/auth-sync-server-plan.md](../../docs/auth-sync-server-plan.md)；手机端只消费协议，不单方面修改服务端或桌面定义。

## 1. 已对齐（不再有冲突）

| 事项 | 服务端 0.1.1 | 手机端实现 |
| --- | --- | --- |
| 同步写 | 单条 `POST /v1/sync/changes`，无批量；响应 `{change,replayed}` | `ChangePushRequestDto`/`SyncPushResponseDto`/`SyncWriteClient` |
| 同步读 | `GET /v1/sync/changes?cursor=&limit=200`；`Change {cursor,serverRevision,kind,key,deleted,payload,deviceId,createdAt}` | `ChangeDto`（`cursor` 优先；仍容忍旧 `operation`/`updatedAtUtc`） |
| 删除 | `deleted=true`，`payload` 省略 | 墓碑投影、`ChangeDto.ResolvedOperation` |
| 设置 payload | `{value:<标量>}`，14 个白名单键 | `PortableSettings` |
| 冲突 | `409 sync.conflict`，`details.current {kind,key,serverRevision,deleted,payload}` | `WireErrorDto.DetailsAs<SyncConflictDetailsDto>` + `SyncWriteClient.Conflict` |
| 游标错误 | `400 sync.invalid`（可带 `details.requiresSnapshot=true`）；`410 sync.cursor_invalid` | `SyncEngine.IsCursorInvalid`：前者仅带标志时重建；后者重建；普通 400 不清空 |
| 会话响应 | 根级 token + `expiresIn`，可带 `user` 与 `account` | `SessionDto.Normalize`：`account` 回填 `user`，`expiresIn` 计算绝对过期 |
| Device Flow | `verificationUriComplete` 返回；`slow_down` 为 200 + `interval`；429 仅 `device.rate_limited` | `AuthorizationUri` 直接用完整链接；`SlowDown` 取“当前 +5 秒”和返回 `interval` 的较大值 |
| `device/info` | `deviceLabel/platform/appVersion/state/expiresAt/approvedAt` | `DeviceInfoDto` 全量解析 |
| 会话管理 | `GET /v1/auth/sessions` → `{sessions:[]}`；revoke 404 `auth.session_not_found`；revoke-all 支持 `{keepCurrent:true}` | `SessionListParser`、`RevokeAllSessionsAsync(keepCurrent)` |
| 账号删除 | `{password}` 或 `{code}`；凭据无效 `401 auth.reauth_required` | `DeleteAccountAsync` + 账户页入口 |
| 错误码 | 规范集 18 个（见规划 §4.5/§16.1） | `WireErrorCodes` 规范常量 + 旧别名，`AuthError.Classify` 统一分类 |

## 2. 保留的兼容处理

| 项目 | 原因 | 处理 |
| --- | --- | --- |
| 旧 `operation` / `updatedAtUtc` | 旧构建或旧文档样例仍可能出现 | `ChangeDto` 兼容读取；显式未知 `operation` 仍跳过，不猜测 |
| 别名错误码 | 服务端旧构建发的 `auth.code_invalid`、`auth.password_policy`、`sync.idempotency_mismatch` | 与规范码映射到同一 `AuthErrorKind`，行为一致 |
| `auth.invalid_email` | 不在规范集中 | 仅作旧别名识别；正常流程由客户端输入规则拦截，未知码按协议错误处理 |

## 3. 仍需服务端/产品确认

1. **`auth.session_not_found`**：服务端 revoke 不存在的会话返回该码，但未列入规范错误码表；建议补入共享协议，手机端当前按普通失败并允许刷新列表。
2. **用户码字母表**：服务端为 `BCDFGHJKLMNPQRSTVWXZ23456789`（不含 `A`）。共享协议早期样例 `BAAD-C832` 不合法，文档应改用不含 `A` 的样例。
3. **deviceId**：登录/注册响应仍不返回服务端 deviceId；手机端本地生成并加密保存，`device/start` 与同步写请求都携带本地 deviceId。若服务端后续改为下发，需先更新协议。
4. **写入上线范围**：手机端 0.2.1 仅上线 Android 小组件的三个窄写命令（标记完成、归档、可选取消归档），复用共享 `POST /v1/sync/changes` 并提交完整 `TaskItemDto`；没有 outbox、离线编辑或冲突选择 UI。服务端仍只做 opaque payload swap，不校验完成/归档字段一致性，无单任务 GET，不阻止并发覆盖；强语义服务端改动不在本轮范围。

## 4. 验证

契约测试位于 `mobile/tests/Mosaic.Mobile.Wire.Tests`、`Mosaic.Mobile.Auth.Tests`、`Mosaic.Mobile.Sync.Tests`，覆盖上述两种变更形状、会话超集、`slow_down` 间隔、单条写入、409 `details.current`、400/410 游标恢复。用本地 fake HTTP 驱动，未连接真实服务端。
