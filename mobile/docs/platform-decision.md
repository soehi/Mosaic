# 阶段 0：流体云平台决策记录

日期：2026-11-26
状态：**阻塞（流体云形态不可实现）**，桌面 AppWidget 不受影响
对应计划：`docs/mobile-refactor-plan.md` §2

## 1. 候选评估

| 候选 | 结论 | 证据 |
|---|---|---|
| OPPO ColorOS 流体云 | 不可接入 | OPPO 文档中心“流体云”栏目对公开访问显示“您暂无权限查看该文档”；接入需要 OPPO 开放平台资质与 `ClientId`/`ClientSecret`，并绑定 OPPO 厂商推送通道；要求 ColorOS 15+。本轮没有这些凭证，也没有可用真机。 |
| Android 16 Live Updates | 不选作“流体云” | 它是 promoted ongoing notification，需要目标设备 Android 16（API 36）、系统资格判定与通知运行时权限。本应用无 Activity，无法走系统权限弹窗；且用户术语指向 OPPO 流体云，用 Live Updates 顶替属于换概念。 |
| HarmonyOS 实况窗 / 服务卡片 | 不选 | HarmonyOS 与 .NET Android 工具链、签名和上架渠道不同；官方术语也不是“流体云”。只有用户明确要求 HarmonyOS 时才应立项。 |

## 2. 决策

1. 本轮**不实现**流体云形态；在 `IFluidCloudAdapter` 边界后绑定 `AndroidFluidCloudAdapter`，状态为 `Blocked("fluidcloud.oppo_credentials_missing")`。
2. 卡片底部显示明确文案“流体云不可用：未接入 OPPO 开放平台”，不崩溃、不影响 AppWidget。
3. **禁止用 AppWidget 冒充流体云**：两者是独立形态，桌面小组件只作为桌面小组件注册与渲染。
4. 未来解除阻塞时只需实现 `IFluidCloudAdapter`：把 `CardModel`（标题、数量、状态、行）映射到厂商卡片，并把完成/归档回调到同一个 `MobileKernel.ExecuteAsync`，服务端契约不变。

## 3. 版本下限与工具链

| 项 | 取值 |
|---|---|
| AppWidget 形态 | `minSdk 26`（Android 8.0）、`targetSdk 36`、`compileSdk 36` |
| 构建工具 | `Microsoft.Android.Sdk.Windows 36.1.69`、Android platform `android-36`、JDK 21（Microsoft OpenJDK 21.0.13） |
| 流体云 | 未接入；ColorOS 15+ 的说法只作为调研结论，不作为已验证事实 |

## 4. 凭证与权限

- APK 内没有任何厂商 `ClientId`/`ClientSecret`，也没有 OPPO 推送 SDK 依赖。
- 清单只声明 `android.permission.INTERNET`；无通知、无前台服务、无精确闹钟、无后台定位。
- 网络安全配置：`cleartextTrafficPermitted="false"`，仅回环地址例外（本地开发）。

## 5. 动作回传机制

- 桌面小组件：`PendingIntent.getBroadcast` + `FLAG_IMMUTABLE`，request code 由 `(taskId, action)` 派生；`WidgetActionReceiver` 声明 `android:exported="false"`，只接受 `complete`/`archive`/`refresh` 三个动作。
- 流体云：无回传实现（平台阻塞），因此也没有“展示支持、动作不支持”的假象。

## 6. 可用真机

`adb devices` 返回空列表，本轮没有任何 Android 真机或模拟器。因此：

- 已做：宿主工程构建、合并后的 `AndroidManifest.xml` 检查（无 Activity、两个非导出接收器、widget meta-data 正确）。
- 未做（全部标记“未验证”）：真机添加小组件、点击回传、深色/字体放大可读性、流体云降级观感、覆盖安装与卸载重装。

## 7. 解除条件

接续实现前需要用户提供：OPPO 开放平台资质与 `ClientId`/`ClientSecret`、ColorOS 15+ 真机、官方接入文档访问权限；若要改用 Android 16 Live Updates，还需要 Android 16 真机并接受“通知权限需要用户手动开启”的约束。