# Mosaic Mobile

Android/iOS 第一版：只消费 Mosaic 服务端 `/v1` wire 协议，查看同步到账号的任务与设置。

## 目录

| 项目 | 目标框架 | 作用 |
| --- | --- | --- |
| `src/Mosaic.Mobile.Wire` | net10.0 | `/v1` DTO、错误码、JSON 选项与样例解析；不引用桌面或服务端程序集 |
| `src/Mosaic.Mobile.Auth` | net10.0 | 唯一 HTTP 客户端、登录/注册/验证码/忘记密码、令牌生命周期、Device Flow、密钥存储抽象 |
| `src/Mosaic.Mobile.Storage` | net10.0 | 自有 SQLite 投影（任务、设置、tombstone、游标）与预留 outbox |
| `src/Mosaic.Mobile.Sync` | net10.0 | 只读增量同步、设置白名单、`sync.cursor_invalid`/`sync.invalid` 快照恢复、单条写入客户端 |
| `src/Mosaic.Mobile.Presentation` | net10.0 | 视图模型、中英文案、浅深主题状态 |
| `src/Mosaic.Mobile.App` | net10.0-android / net10.0-ios | MAUI 壳、页面、Android Keystore / iOS Keychain |
| `tests/*` | net10.0 | 契约、认证状态机、Device Flow、同步/存储、视图模型测试 |

## 构建与测试

无 MAUI 工作负载的机器（CI、桌面）：

```powershell
dotnet build mobile/Mosaic.Mobile.Core.slnf
```

```powershell
dotnet test mobile/tests/Mosaic.Mobile.Wire.Tests/Mosaic.Mobile.Wire.Tests.csproj
```

```powershell
dotnet test mobile/tests/Mosaic.Mobile.Auth.Tests/Mosaic.Mobile.Auth.Tests.csproj
```

```powershell
dotnet test mobile/tests/Mosaic.Mobile.Sync.Tests/Mosaic.Mobile.Sync.Tests.csproj
```

```powershell
dotnet test mobile/tests/Mosaic.Mobile.Presentation.Tests/Mosaic.Mobile.Presentation.Tests.csproj
```

构建 App 头需要 MAUI 工作负载与 Android SDK：

```powershell
dotnet workload install maui
```

```powershell
dotnet build mobile/src/Mosaic.Mobile.App/Mosaic.Mobile.App.csproj -f net10.0-android
```

iOS 构建需要 macOS + Xcode 签名环境。

## 边界与规则

- 只调用 `/v1`。不读取桌面 `mosaic.sqlite`，不引用桌面 Shell/Tasks 程序集。
- 网络只有一个所有者：`MosaicApiClient`（`Mosaic.Mobile.Auth`）。Sync 通过 `IMosaicApi` 使用它，自己不发请求。
- 默认离线可用；未登录也能打开任务缓存。登录是可选的。
- 只有 refresh token、deviceId、serverUrl、账号标识落盘，且只经 `ISecretStorage`（Android Keystore AES-GCM / iOS Keychain）。access token 只存在于内存。
- 密码、邮箱验证码、授权码、令牌不写日志、不入普通文件、不进系统备份（Android 备份规则显式排除）。
- 第一版只读同步：`SyncWriteClient` 已按服务端 0.1.1 单条写入实现，outbox 排空与冲突选择 UI 属于第二阶段。

## 已知限制

- `mobile/src/Mosaic.Mobile.App` 需要 MAUI 工作负载；本仓库的开发机未安装，未做编译与真机/模拟器验证。
- 服务端 0.1.1 已提供会话列表/撤销、账号删除、`/v1/device/info` 与单条同步写入；手机端已按最终 wire 对齐，剩余确认项见 [协议对齐](docs/protocol-conflicts.md)。
- 应用包名已确认：`app.mosaic.tasks`（Android `applicationId` / iOS bundle identifier）；上架后不可更改。上架渠道与合规材料仍待准备。
- 主题与语言在本机即时生效并持久化；云端的可移植设置是只读同步，本地修改会在下一次同步时被服务器值覆盖。

## 文档

- [实施记录](docs/implementation-status.md)
- [协议对齐与待确认项](docs/protocol-conflicts.md)
- 共享协议：[docs/auth-sync-server-plan.md](../docs/auth-sync-server-plan.md)
- 手机端规划：[docs/mobile-auth-sync-plan.md](../docs/mobile-auth-sync-plan.md)
