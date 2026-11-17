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
        public Task<bool> ConfirmDeleteAsync(string title)=>Task.FromResult(false);
        public Task<bool> ConfirmAsync(string title,string message)=>Task.FromResult(false);
        public Task<UnsavedChoice> AskUnsavedChangesAsync()=>Task.FromResult(UnsavedChoice.Cancel);
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
        public Task<OperationResult<TaskItemDto>> UpdateAsync(UpdateTaskRequest request,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<OperationResult> DeleteAsync(DeleteTaskRequest request,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<OperationResult<TaskConstraintsDto>> GetConstraintsAsync(CancellationToken cancellationToken=default)=>throw new NotSupportedException();
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
    public Task Preview_clamps_without_saving_preference_and_failed_close_keeps_window() => Sta.Run(async()=>
    {
        var api=new Api();var item=Item();api.Items[item.Id]=item;var reference=new FloatingContentReference("tasks-ui",item.Id.ToString("D"));
        var repo=new Repository();repo.Records[reference]=new(reference,false,FloatingDisplayMode.Compact,240,320,360,"missing monitor",0,0,true,1);
        await using var settings=new SettingsModule(new(new SettingsRepository()));await settings.ActivateAsync();
        await using var tasks=new TasksUiModule(new(api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Dialogs(),new Diagnostics()));await tasks.ActivateAsync();
        await using var module=new FloatingWindowsModule(new(repo,tasks.FloatingFactory,tasks.DetachSource,settings.Api,new UiDispatcher(Dispatcher.CurrentDispatcher),new Diagnostics(),()=>Task.CompletedTask));await module.ActivateAsync();await module.Api.OpenAsync(reference);
        var definitions=(await settings.Api.ListDefinitionsAsync()).Value.Where(def=>def.ModuleId=="floating-windows").ToArray();Assert.Equal(9,definitions.Length);Assert.Equal(2,definitions.Count(def=>def.Category==SettingCategory.Normal));
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
