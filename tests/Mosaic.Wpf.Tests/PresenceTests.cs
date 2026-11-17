using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Reflection;
using System.Windows.Media.Imaging;
using Mosaic.Contracts;
using Mosaic.DesktopPresence.Contracts;
using Mosaic.DesktopPresence.Wpf;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Core;
using Mosaic.Settings.Wpf;
using Mosaic.Shell.Wpf;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Wpf;

namespace Mosaic.Wpf.Tests;

public sealed class PresenceTests
{
    private sealed class Diagnostics : IDiagnosticSink
    {
        public void Report(string correlationId, Exception exception) { }
    }
    private sealed class Dialogs : IUserDialogService
    {
        public Task<bool> ConfirmDeleteAsync(string title) => Task.FromResult(false);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<UnsavedChoice> AskUnsavedChangesAsync() => Task.FromResult(UnsavedChoice.Cancel);
        public Task ShowErrorAsync(string message) => Task.CompletedTask;
    }
    private sealed class Host : IDesktopWindowHost
    {
        public Task HideMainWindowAsync() => Task.CompletedTask;
        public Task RestoreMainWindowAsync() => Task.CompletedTask;
        public Task<ExitRequestResult> RequestApplicationExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(ExitRequestResult.Cancelled);
    }
    private sealed class Repository : ISettingRepository
    {
        private readonly Dictionary<string, object?> _values = [];
        public Task<OperationResult<IReadOnlyDictionary<string, object?>>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, object?>>.Success(new Dictionary<string, object?>(_values)));
        public Task<OperationResult> SaveAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
        { foreach (var item in values) _values[item.Key] = item.Value; return Task.FromResult(OperationResult.Success()); }
        public Task<OperationResult> DeleteAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
        { foreach (var id in ids) _values.Remove(id); return Task.FromResult(OperationResult.Success()); }
    }
    private sealed class EmptyFactory : ICompanionViewFactory
    {
        public ICompanionViewSession Create(CompanionViewOptions options) => throw new InvalidOperationException("Not opened by this test");
    }
    private sealed class Factory : ICompanionViewFactory
    {
        internal List<Session> Sessions = [];
        public ICompanionViewSession Create(CompanionViewOptions options)
        {
            var session = new Session(options); Sessions.Add(session); return session;
        }
    }
    private sealed class Session(CompanionViewOptions options) : ICompanionViewSession
    {
        internal CompanionViewOptions Options => options;
        internal bool Closed;
        internal bool Disposed;
        public FrameworkElement View { get; } = new TextBlock { Text = "只读任务摘要 · 未完成 / 已完成", TextWrapping = TextWrapping.Wrap };
        public event EventHandler? LayoutInvalidated;
        internal void UpdateText(string text) { ((TextBlock)View).Text = text; LayoutInvalidated?.Invoke(this, EventArgs.Empty); }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CloseAsync() { Closed = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Api : ITasksApi
    {
        internal Func<TaskQuery, CancellationToken, Task<OperationResult<IReadOnlyList<TaskItemDto>>>> Query = (_, _) =>
            Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([]));
        public Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(TaskQuery query, CancellationToken cancellationToken = default) => Query(query, cancellationToken);
        public Task<OperationResult<TaskItemDto>> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        internal Func<Guid, CancellationToken, Task<OperationResult<TaskItemDto>>> Get = (_, _) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> GetAsync(Guid id, CancellationToken cancellationToken = default) => Get(id, cancellationToken);
        public Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> SetCompletedAsync(SetTaskCompletedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult> DeleteAsync(DeleteTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<TaskConstraintsDto>> GetConstraintsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private static TaskItemDto Item(string title, bool completed = false) => new(Guid.NewGuid(), title, "", completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
    private static string[] Texts(FrameworkElement view)
    {
        view.Measure(new Size(380, 560)); view.Arrange(new Rect(0, 0, 380, 560)); view.UpdateLayout();
        return Descendants(view).OfType<TextBlock>().Select(text => text.Text).ToArray();
    }

    [Fact]
    public Task Details_use_stable_identity_ignore_late_results_and_return_without_edits() => Sta.Run(async () =>
    {
        var a = Item("同名任务"); var b = Item("同名任务") with { Notes = "B full notes" };
        var late = new TaskCompletionSource<OperationResult<TaskItemDto>>();
        CancellationToken aToken = default;
        var api = new Api
        {
            Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([a,b])),
            Get = (id, ct) => { if (id == a.Id) { aToken = ct; return late.Task; } Assert.Equal(b.Id,id); return Task.FromResult(OperationResult<TaskItemDto>.Success(b)); }
        };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("All",12,"Theme","Theme"));
        await session.InitializeAsync(); Texts(session.View);
        var rows = Descendants(session.View).OfType<Button>().Where(button => button.DataContext?.GetType().Name == "TaskPeekRow").ToArray();
        Assert.Equal(2, rows.Length);
        rows[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        rows[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(aToken.IsCancellationRequested);
        late.SetResult(OperationResult<TaskItemDto>.Success(a with { Notes = "stale A notes" }));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Texts(session.View);
        var fields = Descendants(session.View).OfType<TextBox>().ToArray();
        Assert.Contains(fields, field => field.Text == "B full notes");
        Assert.All(fields, field => Assert.True(field.IsReadOnly));
        Assert.DoesNotContain(fields, field => field.Text == "stale A notes");
        Assert.True(((ICompanionNavigation)session).TryGoBack());
        Assert.False(((ICompanionNavigation)session).TryGoBack());
        await session.CloseAsync();
    });

    [Fact]
    public Task Deleted_details_can_retry_and_close_while_query_is_pending() => Sta.Run(async () =>
    {
        var item = Item("Deleted"); var pending = new TaskCompletionSource<OperationResult<TaskItemDto>>();
        var calls = 0; CancellationToken token = default;
        var api = new Api { Query = (_,_) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([item])),
            Get = (_, ct) => { if (++calls == 1) return Task.FromResult(OperationResult<TaskItemDto>.Failure(new(ErrorCodes.NotFound,"gone"))); token = ct; return pending.Task; } };
        await using var module = new TasksUiModule(new(api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Dialogs(),new Diagnostics()));
        await module.ActivateAsync(); await using var session = module.CompanionFactory.Create(new("All",12,"Theme","Theme"));
        await session.InitializeAsync(); Texts(session.View);
        Descendants(session.View).OfType<Button>().Single(button => button.DataContext?.GetType().Name == "TaskPeekRow").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Texts(session.View);
        Assert.Contains(Descendants(session.View).OfType<TextBox>(), field => field.Text.Contains("删除"));
        var retry = Descendants(session.View).OfType<Button>().Last(button => button.Content as string == "重试");
        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await session.CloseAsync(); Assert.True(token.IsCancellationRequested);
        pending.SetResult(OperationResult<TaskItemDto>.Success(item));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(((ICompanionNavigation)session).TryGoBack());
    });

    [Fact]
    public Task Settings_register_26_rows_project_3_normal_validate_save_and_revoke() => Sta.Run(async () =>
    {
        await using var settings = new SettingsModule(new(new Repository())); await settings.ActivateAsync();
        await using var module = new DesktopPresenceModule(new(new Host(), new EmptyFactory(), settings.Api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Diagnostics()));
        await module.ActivateAsync();
        var definitions = (await settings.Api.ListDefinitionsAsync()).Value;
        Assert.Equal(26, definitions.Count);
        Assert.Equal(3, definitions.Count(item => item.Category == SettingCategory.Normal));
        Assert.All(definitions, item => Assert.Equal(SettingsGroup.DesktopPresence, item.Group));
        Assert.All(definitions.Where(item => item.Id is "desktop.popup.fitContent" or "desktop.popup.padding" or
            "desktop.popup.cornerRadius" or "desktop.popup.rowGap" or "desktop.popup.rowPaddingY" or
            "desktop.popup.titleFontSize" or "desktop.popup.titleMaxLines" or "desktop.popup.showStatusText" or "desktop.popup.showHints"),
            item => { Assert.Equal(SettingCategory.Advanced, item.Category); Assert.Equal(SettingPreviewMode.Reversible, item.PreviewMode); });
        var invalid = await settings.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["desktop.edge.visibleDepth"] = 64 });
        Assert.Contains("desktop.edge.visibleDepth", invalid.Value.Keys);
        invalid = await settings.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["desktop.popup.openColor"] = "red" });
        Assert.Contains("desktop.popup.openColor", invalid.Value.Keys);
        var saved = await settings.Api.ProposeChangesAsync(new Dictionary<string, object?> { ["desktop.closeBehavior"] = "Exit" });
        Assert.Empty(saved.Value);
        Assert.Equal(DesktopCloseBehavior.Exit, module.Api.CloseBehavior);
        var ui = new SettingsUiModule(new(settings.Api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await ui.ActivateAsync(); var page = Assert.Single(ui.Views).Factory.Create();
        await page.Controller.InitializeAsync();
        var model = page.View.DataContext;
        Assert.Equal(3, ((IList)model.GetType().GetProperty("VisibleRows")!.GetValue(model)!).Count);
        page.View.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative) });
        page.View.Measure(new Size(1200, 800)); page.View.Arrange(new Rect(0, 0, 1200, 800)); page.View.UpdateLayout();
        var activation = Descendants(page.View).OfType<ComboBox>().Single(combo =>
            combo.DataContext.GetType().GetProperty("Id")!.GetValue(combo.DataContext) as string == "desktop.edge.activation");
        Assert.Equal("SingleClick", activation.SelectedValue);
        model.GetType().GetProperty("ShowAdvanced")!.SetValue(model, true);
        Assert.Equal(26, ((IList)model.GetType().GetProperty("VisibleRows")!.GetValue(model)!).Count);
        await page.Controller.DisposeAsync(); await ui.DisposeAsync();
        await module.StopAsync();
        Assert.Empty((await settings.Api.ListDefinitionsAsync()).Value);
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    [Fact]
    public Task Surface_creates_refreshes_and_releases_edge_popup_and_sessions() => Sta.Run(async () =>
    {
        await using var settings = new SettingsModule(new(new Repository())); await settings.ActivateAsync();
        var factory = new Factory();
        await using var module = new DesktopPresenceModule(new(new Host(), factory, settings.Api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Diagnostics()));
        await module.ActivateAsync();
        var surface = typeof(DesktopPresenceModule).GetField("_surface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(module)!;
        Window? WindowField(string name) => surface.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface) as Window;
        Assert.True((await module.Api.RequestParkAsync()).IsSuccess);
        var edge = Assert.IsType<Window>(WindowField("_edge"));
        Assert.True(edge.IsVisible); Assert.False(edge.ShowInTaskbar); Assert.True(edge.Topmost);
        Assert.True((await module.Api.RequestPopupAsync()).IsSuccess);
        var popup = Assert.IsType<Window>(WindowField("_popup"));
        Assert.True(popup.IsVisible); Assert.False(popup.ShowInTaskbar);
        Assert.Equal(380, popup.Width); Assert.InRange(popup.Height, 1, 560);
        Assert.Single(factory.Sessions);
        Assert.True((await module.Api.RequestRefreshAsync()).IsSuccess);
        Assert.True(factory.Sessions[0].Closed); Assert.True(factory.Sessions[0].Disposed);
        Assert.Equal(2, factory.Sessions.Count);
        Assert.False(popup.IsVisible);
        await module.Api.RequestRestoreAsync();
        Assert.False(edge.IsVisible); Assert.Null(WindowField("_popup"));
        Assert.All(factory.Sessions, session => Assert.True(session.Disposed));
        await module.StopAsync();
        Assert.Null(WindowField("_edge"));
    });

    [Fact]
    public Task Popup_measures_generic_content_clamps_height_and_ignores_closed_session_notifications() => Sta.Run(async () =>
    {
        await using var settings = new SettingsModule(new(new Repository())); await settings.ActivateAsync();
        var factory = new Factory();
        await using var module = new DesktopPresenceModule(new(new Host(), factory, settings.Api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Diagnostics()));
        await module.ActivateAsync();
        var surface = typeof(DesktopPresenceModule).GetField("_surface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(module)!;
        Window Popup() => (Window)surface.GetType().GetField("_popup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        await module.Api.RequestParkAsync(); await module.Api.RequestPopupAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var shortHeight = Popup().Height;
        Assert.InRange(shortHeight, 80, 300);
        factory.Sessions[0].UpdateText(string.Concat(Enumerable.Repeat("一段足够长的通用内容，用于验证容器测量。\n", 100)));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.InRange(Popup().Height, shortHeight + 1, 560);
        await module.Api.RequestRefreshAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var replacement = Popup(); var replacementHeight = replacement.Height;
        factory.Sessions[0].UpdateText("旧会话");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Same(replacement, Popup()); Assert.Equal(replacementHeight, Popup().Height);
        await module.StopAsync();
    });

    [Fact]
    public Task Popup_more_menu_belongs_to_interaction_region_and_escape_closes_it_first() => Sta.Run(async () =>
    {
        await using var settings = new SettingsModule(new(new Repository())); await settings.ActivateAsync();
        await using var module = new DesktopPresenceModule(new(new Host(), new Factory(), settings.Api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Diagnostics()));
        await module.ActivateAsync(); await module.Api.RequestParkAsync(); await module.Api.RequestPopupAsync();
        var surface = typeof(DesktopPresenceModule).GetField("_surface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(module)!;
        var popup = (Window)surface.GetType().GetField("_popup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        var view = (FrameworkElement)popup.Content;
        var more = Descendants(view).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "PresenceMore");
        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var menu = Assert.IsType<ContextMenu>(more.ContextMenu);
        Assert.True(menu.IsOpen);
        Assert.True((bool)surface.GetType().GetMethod("Inside", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, [true])!);
        Assert.True((bool)view.GetType().GetMethod("CloseMenuForEscape", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!);
        Assert.False(menu.IsOpen); Assert.True(popup.IsVisible);
        Assert.False((bool)view.GetType().GetMethod("CloseMenuForEscape", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!);
        await module.StopAsync();
    });

    [Fact]
    public Task Popup_chrome_renders_in_both_themes_and_compacts_at_small_heights() => Sta.Run(async () =>
    {
        var oldLanguage = MosaicLocalizationManager.Current;
        MosaicLocalizationManager.SetLanguage(MosaicLanguage.Chinese);
        var api = new Api { Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([
            Item("整理本周计划"), Item("完成模块边界检查", true), Item("继续下一步")])) };
        await using var tasks = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics())); await tasks.ActivateAsync();
        await using var settings = new SettingsModule(new(new Repository()));
        await using var presence = new DesktopPresenceModule(new(new Host(), tasks.CompanionFactory, settings.Api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Diagnostics()));
        await presence.ActivateAsync();
        var autoHideChange = await settings.Api.ProposeChangesAsync(new Dictionary<string, object?>
        {
            ["desktop.popup.autoHide"] = false
        });
        Assert.True(autoHideChange.IsSuccess);
        await presence.Api.RequestParkAsync(); await presence.Api.RequestPopupAsync();
        var surface = typeof(DesktopPresenceModule).GetField("_surface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presence)!;
        var popup = (Window)surface.GetType().GetField("_popup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        popup.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative) });
        foreach (var key in new[] { "Presence.Title", "Presence.Subtitle", "Presence.Dismiss", "Presence.Restore", "Presence.Exit", "Presence.ExitShort", "Presence.AutoHideHint" })
            popup.Resources[key] = MosaicLocalizationManager.TryFindResource(key)!;
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "Mosaic.sln"))) root = root.Parent;
        var directory = Path.Combine(root!.FullName, "artifacts/ui-preview"); Directory.CreateDirectory(directory);
        try
        {
            foreach (var (name, theme, width, height) in new[]
            {
                ("presence-popup-light", MosaicThemeVariant.Light, 380, 560),
                ("presence-popup-dark", MosaicThemeVariant.Dark, 380, 560),
                ("presence-popup-compact", MosaicThemeVariant.Light, 280, 300)
            })
            {
                MosaicThemeManager.Apply(popup.Resources, theme); popup.Width = width; popup.Height = height;
                popup.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var view = (FrameworkElement)popup.Content; view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
                var restore = Descendants(view).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "PresenceRestore");
                var point = restore.TranslatePoint(new Point(), view);
                Assert.InRange(point.X + restore.ActualWidth, 1, width); Assert.InRange(point.Y + restore.ActualHeight, 1, height);
            }
        }
        finally
        {
            MosaicThemeManager.Apply(popup.Resources, MosaicThemeVariant.Light);
            MosaicLocalizationManager.SetLanguage(oldLanguage); await presence.Api.StopAsync();
        }
    });

    [Fact]
    public Task Task_companion_preview_can_render_as_independent_read_only_view() => Sta.Run(async () =>
    {
        var api = new Api { Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([
            Item("整理本周计划"), Item("完成模块边界检查", true), Item("继续下一步")])) };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme"));
        await session.InitializeAsync();
        session.View.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative) });
        session.View.Measure(new Size(380, 420)); session.View.Arrange(new Rect(0, 0, 380, 420)); session.View.UpdateLayout();
        var image = new RenderTargetBitmap(380, 420, 96, 96, PixelFormats.Pbgra32); image.Render(session.View);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Mosaic.sln"))) root = root.Parent;
        var directory = Path.Combine(root!.FullName, "artifacts/ui-preview"); Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(directory, "presence-tasks-readonly.png")); encoder.Save(stream);
        Assert.DoesNotContain(Descendants(session.View), child => child is TextBox);
    });

    [Fact]
    public Task Companion_renders_status_and_limit_without_exposing_editors_and_requeries_each_open() => Sta.Run(async () =>
    {
        var queries = 0;
        var api = new Api { Query = (query, _) =>
        {
            queries++; Assert.Equal(TaskFilter.All, query.Filter);
            return Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([Item("Open title"), Item("Done title", true), Item("More")]));
        } };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        await using (var session = module.CompanionFactory.Create(new("All", 2, "Theme", "Theme")))
        {
            await session.InitializeAsync(); var texts = Texts(session.View);
            Assert.Contains("Open title", texts); Assert.Contains("Done title", texts); Assert.DoesNotContain("More", texts);
            Assert.Equal(2, Descendants(session.View).OfType<System.Windows.Shapes.Path>().Count());
            Assert.Contains("未完成", texts); Assert.Contains("已完成", texts);
            Assert.DoesNotContain(Descendants(session.View), element => element is CheckBox or TextBox);
            Assert.Contains(texts, text => text.Contains('3'));
        }
        await using (var second = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme"))) await second.InitializeAsync();
        Assert.Equal(2, queries);
        await module.StopAsync();
        Assert.Throws<InvalidOperationException>(() => module.CompanionFactory.Create(new("All", 12, "Theme", "Theme")));
    });

    [Fact]
    public Task Companion_ignores_old_and_closed_query_results_and_cancels_lifetime() => Sta.Run(async () =>
    {
        var first = new TaskCompletionSource<OperationResult<IReadOnlyList<TaskItemDto>>>();
        CancellationToken token = default; var calls = 0;
        var api = new Api { Query = (_, ct) => { if (++calls == 1) { token = ct; return first.Task; } return Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([Item("Newest")])); } };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("Open", 12, "Theme", "Theme"));
        var old = session.InitializeAsync(); await session.InitializeAsync();
        Assert.Contains("Newest", Texts(session.View));
        await session.CloseAsync(); Assert.True(token.IsCancellationRequested);
        first.SetResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([Item("Stale")])); await old;
        Assert.DoesNotContain("Stale", Texts(session.View));
    });

    [Fact]
    public Task Companion_visual_options_keep_full_accessible_status_and_bound_title_height() => Sta.Run(async () =>
    {
        var title = string.Concat(Enumerable.Repeat("非常长的中英文标题 Long task title ", 12));
        var api = new Api { Query = (_, _) => Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success([Item(title, true)])) };
        await using var module = new TasksUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), new Dialogs(), new Diagnostics()));
        await module.ActivateAsync();
        await using var session = module.CompanionFactory.Create(new("All", 12, "Theme", "Theme",
            RowGap: 4, RowPaddingY: 6, TitleFontSize: 20, TitleMaxLines: 1, ShowStatusText: false));
        var layoutChanges = 0; session.LayoutInvalidated += (_, _) => layoutChanges++;
        await session.InitializeAsync(); Texts(session.View);
        var titleText = Descendants(session.View).OfType<TextBlock>().Single(text => text.Text == title);
        Assert.Equal(20, titleText.FontSize); Assert.Equal(28, titleText.MaxHeight);
        Assert.Contains("已完成", System.Windows.Automation.AutomationProperties.GetName(titleText));
        Assert.Contains(title, System.Windows.Automation.AutomationProperties.GetName(titleText));
        Assert.Equal(Visibility.Collapsed, Descendants(session.View).OfType<TextBlock>().Single(text => text.Text == "已完成").Visibility);
        Assert.True(layoutChanges > 0);
        Assert.Single(Descendants(session.View).OfType<ScrollViewer>());
        Assert.DoesNotContain(Descendants(session.View), element => element is CheckBox or TextBox);
    });
}
