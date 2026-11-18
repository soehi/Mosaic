using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Mosaic.Contracts;
using Mosaic.FloatingWindows.Contracts;
using Mosaic.FloatingWindows.Persistence.Contracts;
using Mosaic.FloatingWindows.Wpf;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using System.Windows.Media.Imaging;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Core;
using Mosaic.Shell.Wpf;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Wpf;
namespace Mosaic.Wpf.Tests;
public sealed class FloatingWindowsTests
{
    private sealed class Diagnostics:IDiagnosticSink{public void Report(string id,Exception exception){}}
    private sealed class Dialogs:IUserDialogService
    {
        internal UnsavedChoice Choice = UnsavedChoice.Cancel;
        public Task<bool> ConfirmDeleteAsync(string title)=>Task.FromResult(false);
        public Task<bool> ConfirmAsync(string title,string message)=>Task.FromResult(false);
        public Task<UnsavedChoice> AskUnsavedChangesAsync()=>Task.FromResult(Choice);
        public Task ShowErrorAsync(string message)=>Task.CompletedTask;
    }
    private sealed class SettingsRepository:ISettingRepository
    {
        internal Dictionary<string,object?> Values=[];
        public Task<OperationResult<IReadOnlyDictionary<string,object?>>> LoadAsync(CancellationToken cancellationToken=default)=>Task.FromResult(OperationResult<IReadOnlyDictionary<string,object?>>.Success(new Dictionary<string,object?>(Values)));
        public Task<OperationResult> SaveAsync(IReadOnlyDictionary<string,object?> values,CancellationToken cancellationToken=default){foreach(var pair in values)Values[pair.Key]=pair.Value;return Task.FromResult(OperationResult.Success());}
        public Task<OperationResult> DeleteAsync(IReadOnlyList<string> ids,CancellationToken cancellationToken=default)=>Task.FromResult(OperationResult.Success());
    }
    private sealed class Repository:IFloatingWindowRepository
    {
        internal Dictionary<FloatingContentReference,FloatingWindowRecord> Records=[];
        internal bool Fail;
        public Task<OperationResult<IReadOnlyList<FloatingWindowRecord>>> LoadAsync(CancellationToken cancellationToken=default)=>Task.FromResult(OperationResult<IReadOnlyList<FloatingWindowRecord>>.Success(Records.Values.ToArray()));
        public Task<OperationResult> SaveAsync(FloatingWindowRecord record,long expectedRevision,CancellationToken cancellationToken=default)
        {if(Fail)return Task.FromResult(OperationResult.Failure(new(ErrorCodes.StorageUnavailable,"full")));Records[record.Reference]=record;return Task.FromResult(OperationResult.Success());}
    }
    private sealed class Api:ITasksApi
    {
        internal Dictionary<Guid,TaskItemDto> Items=[];internal int Writes;
        public Task<OperationResult<TaskItemDto>> GetAsync(Guid id,CancellationToken cancellationToken=default)=>Task.FromResult(Items.TryGetValue(id,out var item)?OperationResult<TaskItemDto>.Success(item):OperationResult<TaskItemDto>.Failure(new(ErrorCodes.NotFound,"deleted")));
        public Task<OperationResult<TaskItemDto>> SetCompletedAsync(SetTaskCompletedRequest request,CancellationToken cancellationToken=default)
        {Writes++;var next=Items[request.Id] with{IsCompleted=request.IsCompleted,Revision=Items[request.Id].Revision+1};Items[request.Id]=next;return Task.FromResult(OperationResult<TaskItemDto>.Success(next));}
        public Task<OperationResult<IReadOnlyList<TaskItemDto>>> ListAsync(TaskQuery query,CancellationToken cancellationToken=default)=>Task.FromResult(OperationResult<IReadOnlyList<TaskItemDto>>.Success(Items.Values.ToArray()));
        public Task<OperationResult<TaskItemDto>> CreateAsync(CreateTaskRequest request,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request,CancellationToken cancellationToken=default)
        {
            Writes++;
            if (!Items.TryGetValue(request.Id, out var item)) return Task.FromResult(OperationResult<TaskItemDto>.Failure(new(ErrorCodes.NotFound, "deleted")));
            if (item.Revision != request.ExpectedRevision) return Task.FromResult(OperationResult<TaskItemDto>.Failure(new(ErrorCodes.Conflict, "conflict")));
            var updated = item with { Title = request.Title, Notes = request.Notes, Revision = item.Revision + 1 };
            Items[request.Id] = updated;
            return Task.FromResult(OperationResult<TaskItemDto>.Success(updated));
        }
        public Task<OperationResult> DeleteAsync(DeleteTaskRequest request,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<OperationResult<TaskConstraintsDto>> GetConstraintsAsync(CancellationToken cancellationToken=default)=>
            Task.FromResult(OperationResult<TaskConstraintsDto>.Success(new(200, 20000, true, false, 1)));
    }
    private static TaskItemDto Item()=>new(Guid.NewGuid(),"可复制任务 Long title",string.Concat(Enumerable.Repeat("长备注 long notes ",100)),false,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,1);
    private static Window[] Windows(FloatingWindowsModule module)
    {
        var surface=typeof(FloatingWindowsModule).GetField("_surface",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(module)!;
        var values=((IDictionary)surface.GetType().GetField("_windows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(surface)!).Values;
        return values.Cast<object>().Select(entry=>(Window)entry.GetType().GetField("Window",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(entry)!).ToArray();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;for(var i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(node,i)))yield return child;
    }
    [Fact]
    public Task Floating_notes_preview_conflict_and_close_protect_drafts() => Sta.Run(async () =>
    {
        const string key = "tasks.floating.allowNotesEditing";
        var api = new Api();
        var task = Item();
        api.Items.Add(task.Id, task);
        var reference = new FloatingContentReference("tasks-ui", task.Id.ToString("D"));
        var dispatcher = new UiDispatcher(Dispatcher.CurrentDispatcher);
        var dialogs = new Dialogs();
        var repository = new Repository();
        var stored = new SettingsRepository();
        await using (var settings = new SettingsModule(new(stored)))
        {
            await settings.ActivateAsync();
            await using var tasks = new TasksUiModule(new(api, dispatcher, dialogs, new Diagnostics(), settings.Api));
            await tasks.ActivateAsync();
            await using var module = new FloatingWindowsModule(new(repository, tasks.FloatingFactory, tasks.DetachSource, settings.Api, dispatcher, new Diagnostics(), () => Task.CompletedTask));
            await module.ActivateAsync();
            Assert.Equal(false, (await settings.Api.GetValuesAsync()).Value[key]);
            Assert.True((await module.Api.OpenAsync(reference)).IsSuccess);
            var window = Assert.Single(Windows(module));
            Button Find(string id) => Descendants(window).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == id);
            Find("FloatingTaskExpand").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await dispatcher.InvokeAsync(window.UpdateLayout);
            var notes = Assert.Single(Descendants(window).OfType<TextBox>());
            Assert.True(notes.IsReadOnly);
            Assert.False(Find("FloatingNotesEdit").IsVisible);
            await using (var edit = (await settings.Api.BeginEditSessionAsync()).Value)
            {
                Assert.True((await edit.PreviewAsync(new Dictionary<string, object?> { [key] = true }, 1)).IsSuccess);
                Find("FloatingNotesEdit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(notes.IsReadOnly);
                notes.Text = "浮窗草稿\n第二行";
                Assert.True((await edit.DiscardAsync()).IsSuccess);
            }
            Assert.True(notes.IsReadOnly);
            Assert.Equal("浮窗草稿\n第二行", notes.Text);
            Assert.Equal(0, api.Writes);
            Find("FloatingClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(window, Assert.Single(Windows(module)));
            Assert.True(repository.Records[reference].IsOpen);
            Assert.Equal("浮窗草稿\n第二行", notes.Text);
            Assert.Equal(ErrorCodes.Canceled, (await module.Api.CloseAsync(reference)).Error?.Code);
            await using (var edit = (await settings.Api.BeginEditSessionAsync()).Value)
            {
                Assert.True((await edit.PreviewAsync(new Dictionary<string, object?> { [key] = true }, 1)).IsSuccess);
                Assert.True((await edit.CommitAsync(1)).IsSuccess);
            }
            Assert.False(notes.IsReadOnly);
            api.Items[task.Id] = task with { Notes = "外部备注", Revision = 2 };
            dialogs.Choice = UnsavedChoice.Save;
            Assert.Equal(ErrorCodes.Conflict, (await module.Api.CloseAsync(reference)).Error?.Code);
            Assert.Equal("浮窗草稿\n第二行", notes.Text);
            Assert.Equal("外部备注", api.Items[task.Id].Notes);
            Assert.True(repository.Records[reference].IsOpen);
            dialogs.Choice = UnsavedChoice.Discard;
            repository.Fail = true;
            Assert.Equal(ErrorCodes.StorageUnavailable, (await module.Api.CloseAsync(reference)).Error?.Code);
            Assert.Equal("浮窗草稿\n第二行", notes.Text);
            repository.Fail = false;
            Assert.True((await module.Api.CloseAsync(reference)).IsSuccess);
            Assert.False(repository.Records[reference].IsOpen);
        }
        await using var restartedSettings = new SettingsModule(new(stored));
        await restartedSettings.ActivateAsync();
        await using var restartedTasks = new TasksUiModule(new(api, dispatcher, dialogs, new Diagnostics(), restartedSettings.Api));
        await restartedTasks.ActivateAsync();
        await using var session = restartedTasks.FloatingFactory.Create(reference);
        Assert.True((await session.InitializeAsync()).IsSuccess);
        session.SetExpanded(true);
        var view = session.View;
        view.Measure(new Size(400, 400));
        view.Arrange(new Rect(0, 0, 400, 400));
        var editor = Assert.Single(Descendants(view).OfType<TextBox>());
        var editButton = Descendants(view).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "FloatingNotesEdit");
        editButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(editor.IsReadOnly);
        editor.Text = "重启后保存";
        dialogs.Choice = UnsavedChoice.Save;
        Assert.True((await session.PrepareCloseAsync(CancellationToken.None)).IsSuccess);
        Assert.Equal("重启后保存", api.Items[task.Id].Notes);
        Assert.Equal(3, api.Items[task.Id].Revision);
    });

    [Fact]
    public Task Toolbar_icons_have_independent_hit_targets_and_pin_follows_saved_state() => Sta.Run(async () =>
    {
        string[] ids = ["FloatingRestore", "FloatingPin", "FloatingClose"];
        var api = new Api();
        var first = Item() with { Title = "任务一", Notes = "" };
        var second = Item() with { Title = "任务二", Notes = "" };
        api.Items[first.Id] = first;
        api.Items[second.Id] = second;
        var firstReference = new FloatingContentReference("tasks-ui", first.Id.ToString("D"));
        var secondReference = new FloatingContentReference("tasks-ui", second.Id.ToString("D"));
        var repo = new Repository();
        repo.Records[firstReference] = new(firstReference, true, FloatingDisplayMode.Compact, 220, 220, 360, "primary", 24, 24, false, 1);
        repo.Records[secondReference] = new(secondReference, true, FloatingDisplayMode.Expanded, 320, 320, 360, "primary", 24, 130, true, 1);
        var dispatcher = new UiDispatcher(Dispatcher.CurrentDispatcher);
        await using var settings = new SettingsModule(new(new SettingsRepository()));
        await settings.ActivateAsync();
        await using var tasks = new TasksUiModule(new(api, dispatcher, new Dialogs(), new Diagnostics()));
        await tasks.ActivateAsync();
        await using var module = new FloatingWindowsModule(new(repo, tasks.FloatingFactory, tasks.DetachSource, settings.Api, dispatcher, new Diagnostics(), () => Task.CompletedTask));
        await module.ActivateAsync();
        await module.RestoreAsync();

        static string Id(Button button) => System.Windows.Automation.AutomationProperties.GetAutomationId(button);
        Button[] Tools(Window window) => Descendants(window).OfType<Button>().Where(button => ids.Contains(Id(button))).ToArray();
        static System.Windows.Shapes.Path Icon(Button button) => Assert.Single(Descendants(button).OfType<System.Windows.Shapes.Path>());
        static string Name(Button button) => System.Windows.Automation.AutomationProperties.GetName(button);
        static async Task Click(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        var windows = Windows(module);
        Assert.Equal(2, windows.Length);
        var compact = windows.Single(window => !window.Topmost);
        var expanded = windows.Single(window => window.Topmost);
        foreach (var window in windows)
        {
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative)
            });
            window.UpdateLayout();
            var buttons = Tools(window);
            Assert.Equal(ids, buttons.Select(Id));
            foreach (var button in buttons)
            {
                var icon = Icon(button);
                Assert.False(icon.IsHitTestVisible);
                Assert.Equal(16, icon.ActualWidth);
                Assert.Equal(16, icon.ActualHeight);
                Assert.True(button.ActualWidth >= 28 && button.ActualHeight >= 28);
                Assert.Equal(1.7, icon.StrokeThickness);
                Assert.NotNull(icon.Stroke);
            }
            var expand = Descendants(window).OfType<Button>().Single(button => Id(button) == "FloatingTaskExpand");
            Assert.True(expand.TranslatePoint(new Point(expand.ActualWidth, 0), window).X <= buttons[0].TranslatePoint(new Point(), window).X);
        }
        Assert.InRange(compact.Height, 64, 76);
        Assert.NotSame(Icon(Tools(compact)[0]), Icon(Tools(expanded)[0]));
        var pin = Tools(compact)[1];
        var pinned = Tools(expanded)[1];
        Assert.Null(Icon(pin).Fill);
        Assert.NotNull(Icon(pinned).Fill);
        Assert.NotEqual(Name(pin), Name(pinned));
        Assert.Equal(Name(pin), pin.ToolTip);
        Assert.Equal(Name(pinned), pinned.ToolTip);
        var oldLanguage = MosaicLocalizationManager.Current;
        try
        {
            foreach (var (language, pinLabel, unpinLabel) in new[]
            {
                (MosaicLanguage.Chinese, "置顶", "取消置顶"),
                (MosaicLanguage.English, "Keep on top", "Stop keeping on top")
            })
            {
                MosaicLocalizationManager.SetLanguage(language);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(pinLabel, Name(pin));
                Assert.Equal(unpinLabel, Name(pinned));
                Assert.Equal(pinLabel, pin.ToolTip);
                Assert.Equal(unpinLabel, pinned.ToolTip);
            }
        }
        finally
        {
            MosaicLocalizationManager.SetLanguage(oldLanguage);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        var originalName = Name(pin);
        var originalRecord = repo.Records[firstReference];

        repo.Fail = true;
        await Click(pin);
        Assert.Equal(originalRecord, repo.Records[firstReference]);
        Assert.False(compact.Topmost);
        Assert.Null(Icon(pin).Fill);
        Assert.Equal(originalName, Name(pin));

        repo.Fail = false;
        await Click(pin);
        Assert.True(repo.Records[firstReference].IsTopmost);
        Assert.True(compact.Topmost);
        Assert.NotNull(Icon(pin).Fill);
        Assert.Equal(Name(pinned), Name(pin));
        await Click(pin);
        Assert.False(repo.Records[firstReference].IsTopmost);
        Assert.False(compact.Topmost);
        Assert.Null(Icon(pin).Fill);
        Assert.Equal(originalName, Name(pin));
        Assert.True(expanded.Topmost);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Mosaic.sln"))) root = root.Parent;
        var directory = Path.Combine(root!.FullName, "artifacts/ui-preview");
        Directory.CreateDirectory(directory);
        var oldTheme = MosaicThemeManager.Current;
        try
        {
            foreach (var theme in new[] { MosaicThemeVariant.Light, MosaicThemeVariant.Dark, MosaicThemeVariant.HighContrast })
            {
                MosaicThemeManager.Apply(compact.Resources, theme);
                compact.UpdateLayout();
                var content = (FrameworkElement)compact.Content;
                // WPF render checks at 100%, 150%, and 200% without altering the desktop display settings.
                foreach (var dpi in new[] { 96, 144, 192 })
                {
                    var scale = dpi / 96d;
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale),
                        (int)Math.Ceiling(content.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, $"floating-toolbar-{theme}-{dpi}.png"));
                    encoder.Save(stream);
                }
            }
        }
        finally { MosaicThemeManager.Apply(compact.Resources, oldTheme); }
    });

    [Fact]
    public Task Independent_windows_deduplicate_toggle_modes_complete_close_and_restore() => Sta.Run(async()=>
    {
        var api=new Api();var a=Item();var b=Item();api.Items[a.Id]=a;api.Items[b.Id]=b;
        var repo=new Repository();await using var settings=new SettingsModule(new(new SettingsRepository()));await settings.ActivateAsync();
        await using var tasks=new TasksUiModule(new(api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Dialogs(),new Diagnostics()));await tasks.ActivateAsync();
        await using(var module=new FloatingWindowsModule(new(repo,tasks.FloatingFactory,tasks.DetachSource,settings.Api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Diagnostics(),()=>Task.CompletedTask)))
        {
            await module.ActivateAsync();Assert.True(tasks.DetachSource.IsAvailable);
            var ra=new FloatingContentReference("tasks-ui",a.Id.ToString("D"));var rb=new FloatingContentReference("tasks-ui",b.Id.ToString("D"));
            Assert.True((await tasks.DetachSource.RequestDetachAsync(ra)).IsSuccess);await module.Api.OpenAsync(ra);await module.Api.OpenAsync(rb);
            Assert.Equal(2,Windows(module).Length);var window=Windows(module)[0];Assert.True(window.Topmost);Assert.False(window.ShowInTaskbar);
            var expand=Descendants(window).OfType<Button>().Single(button=>button.Content is StackPanel);
            expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(FloatingDisplayMode.Expanded,repo.Records[ra].Mode);Assert.True(window.Height>=180);
            var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Mosaic.sln")))root=root.Parent;
            var directory=Path.Combine(root!.FullName,"artifacts/ui-preview");Directory.CreateDirectory(directory);
            window.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml",UriKind.Relative)});
            var oldTheme=MosaicThemeManager.Current;
            foreach(var theme in new[]{MosaicThemeVariant.Light,MosaicThemeVariant.Dark,MosaicThemeVariant.HighContrast})
            {
                MosaicThemeManager.Apply(window.Resources,theme);window.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render((FrameworkElement)window.Content);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(directory,"floating-expanded-"+theme+".png"));encoder.Save(stream);
            }
            MosaicThemeManager.Apply(window.Resources,oldTheme);
            var fields=Descendants(window).OfType<TextBox>().ToArray();Assert.NotEmpty(fields);Assert.All(fields,field=>Assert.True(field.IsReadOnly));
            var toggle=Descendants(window).OfType<ToggleButton>().Single();toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Assert.True(api.Items[a.Id].IsCompleted);Assert.Equal(1,api.Writes);
            var pin=Descendants(window).OfType<Button>().Single(button=>System.Windows.Automation.AutomationProperties.GetAutomationId(button)=="FloatingPin");pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Assert.False(window.Topmost);
            Assert.True((await module.Api.CloseAsync(ra)).IsSuccess);Assert.Single(Windows(module));Assert.False(repo.Records[ra].IsOpen);
            Assert.True((await module.PrepareExitAsync()).IsSuccess);Assert.False(tasks.DetachSource.IsAvailable);
            await module.StopAsync();Assert.Empty(Windows(module));Assert.True(repo.Records[rb].IsOpen);
        }
        await using(var restored=new FloatingWindowsModule(new(repo,tasks.FloatingFactory,tasks.DetachSource,settings.Api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Diagnostics(),()=>Task.CompletedTask)))
        {await restored.ActivateAsync();await restored.RestoreAsync();Assert.Single(Windows(restored));}
    });
    [Fact]
    public Task Header_and_toolbar_buttons_are_drag_candidates_but_body_controls_are_not() => Sta.Run(async()=>
    {
        var api=new Api();var item=Item();api.Items[item.Id]=item;var reference=new FloatingContentReference("tasks-ui",item.Id.ToString("D"));
        var repo=new Repository();await using var settings=new SettingsModule(new(new SettingsRepository()));await settings.ActivateAsync();
        await using var tasks=new TasksUiModule(new(api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Dialogs(),new Diagnostics()));await tasks.ActivateAsync();
        await using var module=new FloatingWindowsModule(new(repo,tasks.FloatingFactory,tasks.DetachSource,settings.Api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Diagnostics(),()=>Task.CompletedTask));
        await module.ActivateAsync();await module.Api.OpenAsync(reference);
        var window=Windows(module).Single();
        var surface=typeof(FloatingWindowsModule).GetField("_surface",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(module)!;
        var entries=(IDictionary)surface.GetType().GetField("_windows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(surface)!;
        var entry=entries.Values.Cast<object>().Single();
        var input=entry.GetType().GetField("Input",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(entry)!;
        var can=input.GetType().GetMethod("CanStartHeaderDrag",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)!;
        var expand=Descendants(window).OfType<Button>().Single(button=>System.Windows.Automation.AutomationProperties.GetAutomationId(button)=="FloatingTaskExpand");
        var close=Descendants(window).OfType<Button>().Single(button=>System.Windows.Automation.AutomationProperties.GetAutomationId(button)=="FloatingClose");
        var state=Descendants(window).OfType<ToggleButton>().Single();
        Assert.True((bool)can.Invoke(input,[expand])!);Assert.True((bool)can.Invoke(input,[close])!);Assert.True((bool)can.Invoke(input,[state])!);
        expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
        var bodyButton=Descendants(window).OfType<Button>().FirstOrDefault(button=>button.IsVisible&&button.Name is "DeleteButton" or "ReloadButton" or "BackToTaskListButton");
        var notes=Descendants(window).OfType<TextBox>().First();
        if(bodyButton is not null)Assert.False((bool)can.Invoke(input,[bodyButton])!);
        Assert.False((bool)can.Invoke(input,[notes])!);
    });
    [Fact]
    public Task Placement_preview_ghost_tracks_the_hint_and_ends_without_a_window() => Sta.Run(async()=>
    {
        var api=new Api();var a=Item();var b=Item();api.Items[a.Id]=a;api.Items[b.Id]=b;
        var repo=new Repository();await using var settings=new SettingsModule(new(new SettingsRepository()));await settings.ActivateAsync();
        await using var tasks=new TasksUiModule(new(api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Dialogs(),new Diagnostics()));await tasks.ActivateAsync();
        await using var module=new FloatingWindowsModule(new(repo,tasks.FloatingFactory,tasks.DetachSource,settings.Api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Diagnostics(),()=>Task.CompletedTask));
        await module.ActivateAsync();await module.Api.OpenAsync(new("tasks-ui",a.Id.ToString("D")));
        var window=Windows(module).Single();var origin=window.PointToScreen(new Point(20,20));
        var target=new FloatingContentReference("tasks-ui",b.Id.ToString("D"));
        module.Api.BeginPlacementPreview(target,new FloatingPlacementHint(origin.X,origin.Y,10,10));
        var surface=typeof(FloatingWindowsModule).GetField("_surface",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(module)!;
        var preview=(Window?)surface.GetType().GetField("_previewWindow",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(surface);
        Assert.NotNull(preview);Assert.True(preview!.IsVisible);Assert.InRange(preview.Width,220,640);
        var left=preview.Left;
        module.Api.UpdatePlacementPreview(new FloatingPlacementHint(origin.X+40,origin.Y+20,10,10));
        Assert.NotEqual(left,preview.Left);
        preview.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml",UriKind.Relative)});
        preview.UpdateLayout();
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Mosaic.sln")))root=root.Parent;
        var directory=Path.Combine(root!.FullName,"artifacts/ui-preview");Directory.CreateDirectory(directory);
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(preview.Width),(int)Math.Ceiling(preview.Height),96,96,PixelFormats.Pbgra32);bitmap.Render((FrameworkElement)preview.Content);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(directory,"floating-placement-preview.png")))encoder.Save(stream);
        module.Api.EndPlacementPreview();Assert.False(preview.IsVisible);
        Assert.Single(Windows(module));
    });

    [Fact]
    public Task Preview_clamps_without_saving_preference_and_failed_close_keeps_window() => Sta.Run(async()=>
    {
        var api=new Api();var item=Item();api.Items[item.Id]=item;var reference=new FloatingContentReference("tasks-ui",item.Id.ToString("D"));
        var repo=new Repository();repo.Records[reference]=new(reference,false,FloatingDisplayMode.Compact,240,320,360,"missing monitor",0,0,true,1);
        await using var settings=new SettingsModule(new(new SettingsRepository()));await settings.ActivateAsync();
        await using var tasks=new TasksUiModule(new(api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Dialogs(),new Diagnostics()));await tasks.ActivateAsync();
        await using var module=new FloatingWindowsModule(new(repo,tasks.FloatingFactory,tasks.DetachSource,settings.Api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Diagnostics(),()=>Task.CompletedTask));await module.ActivateAsync();await module.Api.OpenAsync(reference);
        var definitions=(await settings.Api.ListDefinitionsAsync()).Value.Where(def=>def.ModuleId=="floating-windows").ToArray();Assert.Equal(12,definitions.Length);Assert.Equal(2,definitions.Count(def=>def.Category==SettingCategory.Normal));
        await using(var edit=(await settings.Api.BeginEditSessionAsync()).Value)
        {
            var preview=await edit.PreviewAsync(new Dictionary<string,object?>{["floating.minWidth"]=280},1);Assert.True(preview.IsSuccess);
            Assert.True(Windows(module)[0].Width>=280);Assert.Equal(240,repo.Records[reference].CompactWidth);
            var surface=typeof(FloatingWindowsModule).GetField("_surface",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(module)!;
            var entries=(IDictionary)surface.GetType().GetField("_windows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(surface)!;
            var entry=entries.Values.Cast<object>().Single();
            surface.GetType().GetMethod("QueueBounds",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(surface,[entry,false,false]);
            await (Task)surface.GetType().GetMethod("SaveBoundsAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(surface,[entry])!;
            Assert.Equal(240,repo.Records[reference].CompactWidth);
            await edit.DiscardAsync();Assert.Equal(240,Windows(module)[0].Width);
        }
        repo.Fail=true;Assert.False((await module.Api.CloseAsync(reference)).IsSuccess);Assert.Single(Windows(module));Assert.True(repo.Records[reference].IsOpen);
        Assert.False((await module.PrepareExitAsync()).IsSuccess);Assert.True(tasks.DetachSource.IsAvailable);
        repo.Fail=false;Assert.True((await module.Api.CloseAsync(reference)).IsSuccess);
    });
    [Theory]
    [InlineData(8,4,0)]
    [InlineData(12,8,4)]
    [InlineData(20,16,12)]
    public Task Unified_hit_regions_exclude_tools_controls_and_transparent_corners(int band,int padding,int gap) => Sta.Run(async()=>
    {
        var api=new Api();var item=Item() with {Title="短标题",Notes=""};api.Items[item.Id]=item;
        var reference=new FloatingContentReference("tasks-ui",item.Id.ToString("D"));var repo=new Repository();
        var stored=new SettingsRepository{Values={["floating.dragBandHeight"]=band,["floating.cardPadding"]=padding,["floating.contentGap"]=gap}};
        await using var settings=new SettingsModule(new(stored));await settings.ActivateAsync();
        await using var tasks=new TasksUiModule(new(api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Dialogs(),new Diagnostics()));await tasks.ActivateAsync();
        await using var module=new FloatingWindowsModule(new(repo,tasks.FloatingFactory,tasks.DetachSource,settings.Api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Diagnostics(),()=>Task.CompletedTask));await module.ActivateAsync();await module.Api.OpenAsync(reference);
        var window=Windows(module).Single();window.UpdateLayout();
        var surface=typeof(FloatingWindowsModule).GetField("_surface",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(module)!;
        var entry=((IDictionary)surface.GetType().GetField("_windows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(surface)!).Values.Cast<object>().Single();
        var input=entry.GetType().GetField("Input",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(entry)!;
        int Hit(double x,double y)=>(int)input.GetType().GetMethod("HitTest",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(input,[new Point(x,y)])!;
        Assert.Equal(-1,Hit(2,2));Assert.Equal(-1,Hit(4,4));
        Assert.Equal(2,Hit(window.Width/2,4));Assert.Equal(2,Hit(10,7));
        Assert.Equal(10,Hit(5,window.Height/2));Assert.Equal(11,Hit(window.Width-5,window.Height/2));Assert.Equal(15,Hit(window.Width/2,window.Height-5));
        foreach(var button in Descendants(window).OfType<ButtonBase>().Where(button=>button.IsVisible))
        {
            var rect=button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
            Assert.Equal(1,Hit(rect.Left+1,rect.Top+1));Assert.Equal(1,Hit(rect.Right-1,rect.Bottom-1));
            button.IsEnabled=false;Assert.Equal(1,Hit(rect.Left+rect.Width/2,rect.Top+rect.Height/2));button.IsEnabled=true;
        }
        if(padding==8)Assert.InRange(window.Height,64,76);
        var expand=Descendants(window).OfType<Button>().Single(button=>button.Content is StackPanel);
        var tools=Descendants(window).OfType<Button>().Single(button=>System.Windows.Automation.AutomationProperties.GetAutomationId(button)=="FloatingRestore");
        Assert.True(expand.TransformToAncestor(window).TransformBounds(new Rect(expand.RenderSize)).Right<=tools.TranslatePoint(new Point(),window).X);
        expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();
        Assert.Equal(2,Hit(window.Width/2,4));
        foreach(var text in Descendants(window).OfType<TextBox>().Where(text=>text.IsVisible))
        {var point=text.TranslatePoint(new Point(2,2),window);Assert.Equal(1,Hit(point.X,point.Y));}
        window.IsEnabled=false;Assert.Equal(1,Hit(window.Width/2,4));window.IsEnabled=true;
        await using var edit=(await settings.Api.BeginEditSessionAsync()).Value;
        var before=repo.Records[reference];
        Assert.True((await edit.PreviewAsync(new Dictionary<string,object?>{["floating.cardPadding"]=4,["floating.dragBandHeight"]=8,["floating.contentGap"]=0},1)).IsSuccess);
        Assert.Equal(before,repo.Records[reference]);await edit.DiscardAsync();Assert.Equal(before,repo.Records[reference]);
    });

    [Fact]
    public Task Long_floating_titles_grow_without_empty_feedback_or_notes_placeholders() => Sta.Run(async()=>
    {
        var api=new Api();var item=Item() with {Title=string.Concat(Enumerable.Repeat("较长标题 ",12)),Notes=""};api.Items[item.Id]=item;
        await using var settings=new SettingsModule(new(new SettingsRepository()));await settings.ActivateAsync();
        await using var tasks=new TasksUiModule(new(api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Dialogs(),new Diagnostics()));await tasks.ActivateAsync();
        await using var module=new FloatingWindowsModule(new(new Repository(),tasks.FloatingFactory,tasks.DetachSource,settings.Api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Diagnostics(),()=>Task.CompletedTask));await module.ActivateAsync();await module.Api.OpenAsync(new("tasks-ui",item.Id.ToString("D")));
        var window=Windows(module).Single();window.UpdateLayout();Assert.True(window.Height>76);
        Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(),text=>text.IsVisible && string.IsNullOrEmpty(text.Text));
        var expand=Descendants(window).OfType<Button>().Single(button=>button.Content is StackPanel);expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.DoesNotContain(Descendants(window).OfType<TextBox>(),text=>text.Text=="暂无备注");
    });

    [Fact]
    public Task Restore_disabled_retains_records_and_does_not_create_windows() => Sta.Run(async()=>
    {
        var api=new Api();var item=Item();api.Items[item.Id]=item;var reference=new FloatingContentReference("tasks-ui",item.Id.ToString("D"));
        var repo=new Repository();repo.Records[reference]=new(reference,true,FloatingDisplayMode.Compact,320,320,360,"primary",24,24,true,1);
        var stored=new SettingsRepository{Values={["floating.restoreOnStartup"]=false}};
        await using var settings=new SettingsModule(new(stored));await settings.ActivateAsync();await using var tasks=new TasksUiModule(new(api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Dialogs(),new Diagnostics()));await tasks.ActivateAsync();
        await using var module=new FloatingWindowsModule(new(repo,tasks.FloatingFactory,tasks.DetachSource,settings.Api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Diagnostics(),()=>Task.CompletedTask));await module.ActivateAsync();await module.RestoreAsync();Assert.Empty(Windows(module));Assert.True(repo.Records[reference].IsOpen);
    });

    [Fact]
    public Task Missing_task_does_not_open_or_keep_restore_eligibility() => Sta.Run(async()=>
    {
        var api=new Api();var reference=new FloatingContentReference("tasks-ui",Guid.NewGuid().ToString("D"));var repo=new Repository();repo.Records[reference]=new(reference,true,FloatingDisplayMode.Compact,320,320,360,"primary",0,0,true,1);
        await using var settings=new SettingsModule(new(new SettingsRepository()));await settings.ActivateAsync();await using var tasks=new TasksUiModule(new(api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Dialogs(),new Diagnostics()));await tasks.ActivateAsync();
        await using var module=new FloatingWindowsModule(new(repo,tasks.FloatingFactory,tasks.DetachSource,settings.Api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Diagnostics(),()=>Task.CompletedTask));await module.ActivateAsync();await module.RestoreAsync();Assert.Empty(Windows(module));Assert.False(repo.Records[reference].IsOpen);
    });
}
