# Mosaic 手机端（0.3.0）

无界面的 Android 客户端：只有桌面小组件与系统级实时呈现边界，没有 Activity、没有页面、没有设置页。
任务数据来自服务端 `/v1` 备份与增量变更的只读投影；唯一写路径是单任务 `POST /v1/sync/changes`。

需求基线：`docs/mobile-refactor-plan.md`（本目录不改写该文件）。

## 组成

| 工程 | 作用 |
|---|---|
| `src/Mosaic.Mobile.Wire` | `/v1` wire 契约：DTO、JSON 配置、错误信封与错误码 |
| `src/Mosaic.Mobile.Core` | 无 UI 内核：配置边界、会话无 UI 恢复、只读投影、备份引导、增量拉取、单任务写入、卡片模型、流体云适配器接口 |
| `src/Mosaic.Mobile.App` | `net10.0-android` 宿主：`Application`、`AppWidgetProvider`、广播接收器、RemoteViews、Keystore 安全存储 |
| `tests/Mosaic.Mobile.Tests` | 窄写请求构造、409/401/离线语义、幂等重放、身份隔离、投影与卡片模型、备份哈希 |

## 数据与安全边界

- 投影按 `sha256(规范化服务器地址 + NUL + userId)` 隔离，文件名为该哈希；打开前校验 owner 与 schema，不匹配则移入 `quarantine/` 并拒绝展示。
- 投影目录为 `files/projections-v3/`，与 0.2.x 的 `files/projections/` 不同名；旧目录在首次启动时整体改名为 `retired-projections-v2-<时间戳>`，**不迁移、不读取**。
- 令牌只写 Android Keystore AES/GCM 加密后的私有偏好（`mosaic.mobile.v3.secure`），`android:allowBackup="false"` 且排除云备份/设备迁移；旧 `mosaic_secure` 偏好与旧 Keystore 别名在首次启动时删除。
- 只允许 HTTPS；仅回环地址允许 http 供本地开发。没有忽略证书错误的开关，客户端不内嵌 WebView。
- 日志与崩溃信息不写令牌、验证码、设备码或邮箱；卡片布局只出现数量、标题与状态文案。

## 三个动作

| 动作 | 行为 |
|---|---|
| 标记完成 | payload 置 `isCompleted=true`、`completedAtUtc=now`、`restoredAtUtc=null`、`updatedAtUtc=now`、`revision=旧+1`，`archivedAtUtc` 不变，`deleted=false` |
| 归档 | payload 置 `archivedAtUtc=now`、`updatedAtUtc=now`、`revision=旧+1`，保留完成状态，`deleted=false`（绝不写 tombstone） |
| 刷新 | 会话恢复 → 备份引导（首次）→ 增量拉取 → 重绘 |

写入请求带 `baseServerRevision` 与 `clientRevision=revision+1`，每次动作生成新的 `idempotencyKey`。409 时用 `details.current` 覆盖本地行并提示“任务已被其他设备修改”；401 清除令牌并显示会话失效；网络错误显示“离线，操作未提交”。没有 outbox、没有离线排队、没有本地乐观写入。

## 配置与登录（后置）

按计划，真实连接方式后置。当前实现：

- 服务器地址由 `IServerConfigProvider` 注入；Android 实现读取私有偏好 `mosaic.mobile.v3.config` 的 `serverUrl` / `deviceLabel`。
- 会话由 `SessionManager` 无 UI 恢复：读取加密记录 → 必要时刷新 → 用 `/v1/auth/me` 校验身份。没有 Activity 参与。
- 未配置服务器时卡片显示“未配置服务器”；没有有效会话时显示“等待登录”。

## 流体云

阶段 0 结论：OPPO ColorOS 流体云在本轮不可接入（无 OPPO 开放平台凭证、文档受权限控制、无 ColorOS 15+ 真机）。`IFluidCloudAdapter` 边界保留，Android 绑定 `AndroidFluidCloudAdapter` 返回明确的 Blocked 状态，卡片底部显示“流体云不可用”；**不使用 AppWidget 冒充流体云**。

## 构建

```powershell
dotnet build mobile/Mosaic.Mobile.Core.slnf -c Release
dotnet test mobile/tests/Mosaic.Mobile.Tests/Mosaic.Mobile.Tests.csproj -c Release
```

Android 宿主需要 Android SDK 与 JDK 21：

```powershell
dotnet build mobile/src/Mosaic.Mobile.App/Mosaic.Mobile.App.csproj -c Release -p:AndroidSdkDirectory="D:\Android\Sdk" -p:JavaSdkDirectory="C:\Program Files\Microsoft\jdk-21.0.13.102-hotspot"
```

打包见 `build/package-android.ps1`（默认 `apk`，可 `-RunTests`）。