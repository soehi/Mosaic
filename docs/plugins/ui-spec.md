# UI 规范

插件对界面的全部影响通过 `Mosaic.Presentation.Contracts` 声明，由 Shell 统一渲染。插件不直接操作主窗口、导航控件或状态栏布局。

## 1. 贡献模型

```csharp
public interface IUiModule
{
    IReadOnlyList<NavigationItem> Navigation { get; }
    IReadOnlyList<ViewContribution> Views { get; }
    IReadOnlyList<IStatusContribution> Status => [];
}
```

注册流程：

1. 模块 `ActivateAsync` 完成后，宿主触发 `ModuleStarted`。
2. 宿主读取该模块的 `Navigation`、`Views`、`Status`，调用 `ContributionRegistry.Register` **一次**。
3. `ModuleStopping` 时撤销注册，页面与导航一起移除。

约束：

- 三个列表在注册时被**采样**；之后再改变列表不会反映到界面。动态内容用 `IStatusContribution` 的 `INotifyPropertyChanged`。
- 列表可以为空；没有 `Navigation` 的模块可以只提供 `Views`，通过 `TargetViewId` 或代码导航打开。
- 注册失败会抛 `InvalidOperationException`，启动失败。消息逐字为（`src/Mosaic.Shell.Wpf/ContributionRegistry.cs:39,42,45`）：
  - `Duplicate or invalid view contribution.`：视图 ID 为空、重复或工厂为空。
  - `Duplicate navigation or missing view contribution.`：导航 ID 为空或重复，或 `ViewId` 在合并后的视图表中找不到。
  - `Duplicate or invalid status contribution.`：状态项为空、ID 为空或重复。

## 2. 导航

```csharp
public sealed record NavigationItem(
    string Id,          // 导航项唯一 ID，也用于选中高亮
    string Label,       // 无本地化键时的回退文本
    string IconKey,     // 内置图标键，未知值回退为文档图标
    string ViewId,      // 必须对应一个已注册视图（本模块或更早注册的其他模块视图）
    int Order,          // 升序排列
    string? LabelKey = null,
    bool IsDefault = false);
```

规则：

| 项 | 规则 |
| --- | --- |
| `Id` | 全局唯一；同一模块内也不得重复 |
| `ViewId` | 必须对应一个已注册视图；校验在**合并后的视图表**里进行，因此可以引用更早注册的其他模块视图。找不到时抛 `Duplicate navigation or missing view contribution.`（`ContributionRegistry.cs:41-42`） |
| `Order` | 升序排列，同值按 `Id`（`StringComparer.Ordinal`）排序（`ContributionRegistry.cs:82`）。当前内置值：任务 100、已归档 110、设置 900；账户不是导航项，而是 `AccountsAndBackup` 分组的设置页（`AuthUiModule.cs:44-45`，其 `Navigation`/`Views`/`Status` 均为 `[]`，`:34-36`）。新增业务页面建议 200–499，账户类 800 |
| `LabelKey` | 优先使用；由 `MosaicLocalizationManager` 解析，缺失时用 `Label` |
| `IsDefault` | 运行时取第一个 `IsDefault`，不校验唯一性；启动页选 `IsDefault`，无则选第一个导航项（`ShellViewModel.cs:86`，贡献变化后的回退逻辑在 `:154`） |
| `IconKey` | `check-square`、`folder`、`calendar`、`settings`、`account`；其他值回退 `\uE8A5`（Document，`NavigationConverters.cs:10`） |

新增图标必须先扩展 `src/Mosaic.Shell.Wpf/NavigationConverters.cs` 的映射，不要在页面里自己画图标。

## 3. 视图与页面生命周期

```csharp
public sealed record ViewContribution(string Id, IViewFactory Factory);
public sealed record ViewPage(FrameworkElement View, IPageController Controller);

public interface IViewFactory { ViewPage Create(); }
public interface INavigationGuard { Task<bool> CanLeaveAsync(); }
public interface IPageController : INavigationGuard, IAsyncDisposable { Task InitializeAsync(); }
```

Shell 的调用顺序（`src/Mosaic.Shell.Wpf/NavigationService.cs`）：

```text
导航请求
  → 当前页面 CanLeaveAsync()      返回 false 则取消切换（用于未保存草稿）
  → 新页面 Factory.Create()
  → 新页面 Controller.InitializeAsync()
  → 旧页面 Controller.DisposeAsync()
  → 切换 Content
```

生命周期规则：

- **切换到不同视图时新建页面**，Shell 不缓存 `ViewPage`；导航到当前视图 ID 时直接返回，不重建（`NavigationService.cs:11`）。页面不要假设自己常驻。
- 视图 ID 找不到工厂时，弹“该页面暂不可用。”并返回 `false`，不切换页面（`NavigationService.cs:13`）。
- `InitializeAsync` 抛异常时，`NavigationService` 先 `DisposeAsync` 新页再重新抛出（`NavigationService.cs:16-17`）；此时旧页仍是 `Current`，界面保留旧页。`ReleaseCurrentAsync` 抛异常时同样先释放新页（`:18-19`），但 `Current` 已在 `:27` 清空，旧的控制器已释放、`NavigationService.Current` 为 `null`。两种情况下 `ShellViewModel` 都会设 `PageError` 并弹“页面加载失败，请重新启动应用。”（`ShellViewModel.cs:107-110`）。
- `CanLeaveAsync` 只处理“是否允许离开”，不要在里做耗时超过 200 ms 的工作。
- `DisposeAsync` 必须幂等，取消订阅、停止计时器、释放图片资源。
- 视图必须是 `FrameworkElement`，不要返回 `Window`；不要自己 `Show()` 子窗口，浮窗走浮窗模块。
- 所有 UI 操作在 UI 线程执行；后台结果用 `IUiDispatcher.InvokeAsync` 回到 UI 线程。

页面实现骨架——仓库里的四个真实页面（任务、设置、账户、维护）都把 `IPageController` 实现在 ViewModel 上，视图本身只是 `UserControl`，新建页面照这个形状写：

```csharp
internal sealed class NotesViewModel : ObservableObject, IPageController
{
    public async Task InitializeAsync() => await LoadAsync();
    public Task<bool> CanLeaveAsync() => Task.FromResult(!HasUnsavedChanges);
    public ValueTask DisposeAsync() { Clear(); return ValueTask.CompletedTask; }
}

internal sealed partial class NotesPage : UserControl
{
    public NotesPage() => InitializeComponent();
}

public sealed class NotesViewFactory(Func<NotesViewModel> createViewModel, IUiDispatcher dispatcher) : IViewFactory
{
    public ViewPage Create()
    {
        if (!dispatcher.CheckAccess()) throw new InvalidOperationException("Views must be created on the UI thread.");
        var model = createViewModel();
        return new(new NotesPage { DataContext = model }, model);   // 视图与控制器分开
    }
}
```

对应实现：`TasksViewModel : ObservableObject, IPageController`、`TaskViewFactory.Create()` 先做 `Dispatcher.CheckAccess()` 守卫（`src/Modules/Tasks/Mosaic.Tasks.Wpf/Internal/TaskViewFactory.cs:8-13`）。工厂由 Shell 在导航时调用，此时必在 UI 线程；守卫是防御性断言，失败即启动/导航错误，不要静默切线程。

## 4. 设置页

```csharp
public sealed record SettingsPageContribution(string GroupId, string Id, IViewFactory Factory);
public sealed class SettingsPageRegistry
{
    public string? InitialGroupId { get; set; }
    public IReadOnlyList<SettingsPageContribution> Pages { get; }
    public IDisposable Register(SettingsPageContribution page);
}
```

- `GroupId` 必须是设置分区名，大小写精确：`Tasks`、`Appearance`、`Storage`、`Text`、`DesktopPresence`、`FloatingWindows`、`AccountsAndBackup`、`Maintenance`。这些名称来自 `Mosaic.Settings.Wpf/Internal/SettingsViewModel.cs:11-21` 的 `internal enum SettingsSection`，插件不能引用该类型，只能写字面量；宿主在打开设置窗口时用 `Enum.Parse<SettingsSection>(groupId, ignoreCase: false)` 解析（`SettingsViewModel.cs:359`），非法值会抛异常。
- `Register` 校验 `GroupId`、`Id` 非空与 `Factory` 非空（`ArgumentException` / `ArgumentNullException`，`SettingsPageRegistry.cs:16-18`），再按 `Id` 去重；重复时抛 `InvalidOperationException`，消息为 `Settings page '{Id}' is already registered.`（`SettingsPageRegistry.cs:20`）。同一 `GroupId` 注册两个页面会在设置窗口按分组建索引时抛重复键异常（`SettingsViewModel.cs:359` 的 `ToDictionary`），因此每个分组实际只能有一个页面。
- `Pages` 按 `GroupId`、`Id` 排序见上一条；`Registration.Dispose` 是幂等的一次性移除，重复释放不再生效（`SettingsPageRegistry.cs:30-33`）。
- 页面在设置窗口**首次打开该分组时创建并缓存**，设置窗口关闭时统一 `DisposeAsync`。`SettingsPageRegistry.Pages` 按 `GroupId`、`Id` 排序（`SettingsPageRegistry.cs:10-13`），与注册先后无关。
- 注册返回的 `IDisposable` 必须保存，在 `StopAsync` 中释放；不要把注册表本身存进单例。
- 设置页只负责展示与编辑，读写一律走 `ISettingsApi`；字段定义属于设置拥有模块。

```csharp
public Task ActivateAsync(CancellationToken ct = default)
{
    _pageRegistration ??= settingsPages.Register(new SettingsPageContribution(
        "Appearance", "notes", new NotesSettingsViewFactory(...)));
    return Task.CompletedTask;
}
```

`ISettingsPageModule` 目前只有契约定义（`PresentationContracts.cs:17-20`），宿主没有读取它；注册一律走 `SettingsPageRegistry.Register`。

## 5. 状态栏与侧栏条目

```csharp
public interface IStatusContribution : INotifyPropertyChanged
{
    string Id { get; }
    string Glyph { get; }
    string Text { get; }
    string? Detail { get; }
    string? ToolTip { get; }
    string? TargetViewId { get; }      // 点击后导航到的视图（可空）
    int Order { get; }
    StatusPlacement SidebarPlacement => StatusPlacement.None; // Bottom = 同时出现在侧栏底部
    bool CanAct => false;
    string? ActionGlyph => null;
    string? ActionLabel => null;
    string? ActionToolTip => null;
    event EventHandler? ActionRequested { add { } remove { } }
    void RequestAction() { }
}
```

- Shell 只按字段通用渲染，不理解语义；文本、图标、可用性由模块自己更新并触发 `PropertyChanged`。
- `Glyph` 使用 Segoe MDL2 Assets 字符（Unicode 私用区，如 `\uE73E`），与导航图标保持同一视觉语言；不要使用位图或 emoji。
- `TargetViewId` 指向已注册视图；点击时 Shell 直接导航，不要求有导航项。
- `ActionRequested` 用于“退出登录”这类单一动作；Shell 不命名动作，只转发。
- 条目数量控制在 3 个以内；状态栏不是日志区。
- 可选成员是接口默认实现（`PresentationContracts.cs:54-73`）：只覆盖需要的成员即可，已有贡献者无需重编译。`ICompanionViewSession.FilterChanged` 同理有默认实现，宿主优先通过 `ICompanionFilterSelection` 订阅、未实现时回退（`PresentationContracts.cs:38-43`）。

## 6. 伴生视图（浮窗/速览）

```csharp
public sealed record CompanionViewOptions(
    string Filter, int MaxItems, string OpenColor, string CompletedColor,
    int RowGap = 0, int RowPaddingY = 12, int TitleFontSize = 14, int TitleMaxLines = 2,
    bool ShowCompletionToggle = true, bool ShowFilterSwitch = true);

public interface ICompanionViewFactory { ICompanionViewSession Create(CompanionViewOptions options); }
public interface ICompanionViewSession : IAsyncDisposable
{
    FrameworkElement View { get; }
    event EventHandler? LayoutInvalidated;
    event EventHandler? DismissRequested;
    event EventHandler<string>? FilterChanged;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task CloseAsync();
}
```

- 伴生视图由桌面驻留模块的 `PresenceSurface` 创建并托管（`src/Modules/DesktopPresence/Mosaic.DesktopPresence.Wpf/PresenceSurface.cs:220-237`）：它调用 `ICompanionViewFactory.Create`，订阅 `LayoutInvalidated`/`DismissRequested`，并仅在 `desktop.popup.filterSwitch` 打开时订阅 `FilterChanged`；关闭弹窗时逐个退订（`:301-306`），同时负责测量与收起。插件只提供工厂、内容视图与会话，不要自行创建 `Window`。
- `DismissRequested` 表示一次交互完成，是否收起由宿主策略决定（用户设置）。
- `FilterChanged` 把用户在伴生视图中的筛选选择回报给拥有模块。
- `LayoutInvalidated` 在内容尺寸变化时触发，宿主据此重新测量。
- 颜色参数由调用方传入主题色，不要硬编码十六进制。

## 7. 本地化

实现位于 `src/Mosaic.Presentation.Wpf/MosaicLocalizationManager.cs`；`MosaicLanguage` 的取值是 `FollowSystem`、`Chinese`、`English`（`:6-11`）。

```csharp
public static class MosaicLocalizationManager
{
    public static MosaicLanguage Current { get; }
    public static event EventHandler? LanguageChanged;
    public static string? TryFindResource(string key);
    public static IDisposable Register(string owner, Func<MosaicLanguage, ResourceDictionary> dictionaryFactory);
    public static void SetLanguage(MosaicLanguage language);
}
```

规则：

- 模块在 `ActivateAsync` 中 `Register("notes-ui", language => new ResourceDictionary { Source = ... })`，保存返回的 `IDisposable`，`StopAsync` 释放。
- `owner` 使用模块 ID 或 UI 工程名，全局唯一。
- 资源键使用 `[Plugin].Page.Title` 这类前缀，避免与 Shell 冲突。
- 所有用户可见文本必须有 `x:Key`；`NavigationItem.LabelKey`、`SettingDefinition.DisplayNameKey/DescriptionKey` 指向这些键。
- 语言切换事件触发后，已加载页面通过 `TryFindResource` 重新取文本；绑定到资源字典的文本会自动刷新。
- 中文为源语言，`en.xaml` 必须与 `zh.xaml` 键集一致：XAML 里 `{StaticResource 键}` 缺键会在加载时直接抛异常；只有代码路径经 `TryFindResource(...) ?? fallback` 才有回退（`ShellLocalization.cs:13-14`、`TaskLocalization.cs:13-14`）。不要指望“缺英文自动显示中文”。

## 8. 主题与视觉

主题的样式字典 `Mosaic.Presentation.Wpf/Themes/Theme.xaml` 由宿主静态合并（`src/Mosaic.Desktop/App.xaml:8`），`MosaicThemeManager.Attach` 在 `App.xaml.cs:26` 只在启动时绑定资源与系统跟随。之后每次切换（`Apply` 固定，`:41-51`；系统变化触发 `Refresh`，`:61-77`）`ApplyCore`（`:79-95`）只做一件事：从 `MergedDictionaries` 移除上一个调色板、以相对 URI（`UriKind.Relative`）加入 `Themes/Palettes/{Light,Dark,HighContrast}.xaml`（`:88-92`）。插件只能使用主题键：

| 类别 | 键 |
| --- | --- |
| 背景 | `CanvasBrushKey`、`SurfaceBrushKey`、`SurfaceMutedBrushKey`、`SidebarBrushKey`、`DisabledSurfaceBrushKey` |
| 文本 | `TextPrimaryBrushKey`、`TextSecondaryBrushKey`、`TextMutedBrushKey`、`DisabledTextBrushKey` |
| 强调 | `AccentBrushKey`、`AccentTextBrushKey`、`AccentHoverBrushKey`、`AccentSoftBrushKey`、`AccentButtonBackgroundBrushKey`、`AccentButtonTextBrushKey` |
| 危险 | `DangerBrushKey`、`DangerSoftBrushKey`、`DangerBorderBrushKey` |
| 边框/焦点 | `BorderBrushKey`、`FocusBrushKey`、`ControlHoverBorderBrushKey`、`SelectionBrushKey` |
| 滚动条 | `ScrollThumbBrushKey`、`ScrollThumbHoverBrushKey` |
| 按钮样式 | `AccentButtonKey`、`DangerButtonKey`、`QuietButtonKey` |
| 文本样式 | `HeaderTextKey`、`BodyTextKey`、`CaptionTextKey` |

用法：

```xml
<Button Style="{DynamicResource {x:Static theme:MosaicTheme.AccentButtonKey}}" Content="{DynamicResource Notes.Save}" />
<Border Background="{DynamicResource {x:Static theme:MosaicTheme.SurfaceBrushKey}}"
        BorderBrush="{DynamicResource {x:Static theme:MosaicTheme.BorderBrushKey}}" />
```

视觉规则：

- 禁止写死 `#RRGGBB`、`White`、`Black`；否则深色/高对比模式不可用。
- 主操作使用 `AccentButtonKey`，破坏性操作使用 `DangerButtonKey`，次要操作使用 `QuietButtonKey`；不要自建按钮模板。
- 状态不能只靠颜色区分：同时提供图标或文字（例如完成态用圆角徽标 + 勾选）。
- 图标使用 Segoe MDL2 Assets 或矢量 `Path`，不使用位图字体图标。
- 间距沿用现有页面：卡片圆角 8–12 px，控件间距以 4/8/12 为步进，页面主内边距 16–24 px。
- 高对比模式下必须保留 3:1 以上的文本对比度与可见焦点框；用主题按钮样式自带的焦点视觉，不要设置 `FocusVisualStyle="{x:Null}"` 或自行隐藏焦点。

## 9. 对话框与线程

```csharp
public interface IUserDialogService
{
    Task<bool> ConfirmDeleteAsync(string title);
    Task<bool> ConfirmAsync(string title, string message);
    Task<UnsavedChoice> AskUnsavedChangesAsync();
    Task ShowErrorAsync(string message);
}

public interface IUiDispatcher
{
    bool CheckAccess();
    Task InvokeAsync(Action action);
}
```

- 删除确认、未保存草稿、错误提示必须走 `IUserDialogService`；不要直接 `MessageBox.Show`。
- 后台任务访问 UI 前先 `CheckAccess()`，不满足则 `InvokeAsync`。
- 对话框文案本地化后传入；`ShowErrorAsync` 只用于可恢复错误，启动失败由宿主统一呈现。

## 10. 可访问性

- 所有可交互控件设置 `AutomationProperties.Name`；列表项读屏文本包含完整语义（例如“完成 3 个附件”）。
- 键盘可达：Tab 顺序与视觉顺序一致；焦点框使用主题焦点画刷。
- 图片/图标若无语义，设置 `AutomationProperties.IsOffscreenBehavior` 或空名称；有语义的图标提供名称。
- 动画尊重系统“减少动态效果”设置；不要用持续闪烁提示状态。

## 11. UI 测试

- 视图模型与页面控制器测试放在 `tests/Mosaic.Wpf.Tests`（需要 WPF 线程时用 `Sta.Run`，实现在 `tests/Mosaic.Wpf.Tests/Sta.cs:5`）。
- 关键交互就在这里覆盖，不在冒烟测试里：保存中离开先等待（`PresentationTests.cs:150`）、保存失败后输入保留且守卫继续拦截（`:168`）、冲突时保留编辑器（`:181`）；设置页的并发离开共用一个提示、取消后保留预览（`EditingPresentationTests.cs:97`），回滚失败时拒绝离开并保留草稿（`:126`）。即时应用失败但保存成功的分支在 `tests/Mosaic.Settings.Core.Tests/EditingTests.cs:184`，断言 `SettingResults.ApplyWarningId`（`SettingsContracts.cs:67`）。
- `tests/Mosaic.Desktop.SmokeTests` 负责发布产物的端到端行为（启动、配置迁移、浮窗与速览往返），需要真实窗口与进程；`build/test.ps1` 逐工程串行执行全部测试：

```powershell
build/test.ps1 -Configuration Release
```

- 新增页面至少验证：首次加载、离开守卫、释放后无事件泄漏、深色/浅色主题下无写死颜色。

## 12. 提交前检查表

- [ ] 导航项 `Id`、`ViewId` 唯一且引用存在；需要默认页时只标一个 `IsDefault`（运行时不校验唯一性，多标时取按 `Order`、`Id` 排序后的第一个）。
- [ ] 页面实现 `CanLeaveAsync` 与幂等 `DisposeAsync`。
- [ ] 设置页 `GroupId` 是合法枚举名，注册句柄已释放。
- [ ] 状态条目使用 `INotifyPropertyChanged`，`TargetViewId` 指向真实视图。
- [ ] 本地化资源已注册并在停止时释放；没有硬编码用户文本。
- [ ] 颜色、字体、按钮样式全部来自 `MosaicTheme` 键。
- [ ] 键盘焦点、读屏名称、高对比模式已检查。

---

相关文档：[插件规范](plugin-spec.md) · [边界声明](boundary-declaration.md) · [框架规范](framework-spec.md) · [插件制作教程](README.md)