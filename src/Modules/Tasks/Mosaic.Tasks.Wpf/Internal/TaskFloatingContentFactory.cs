using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Mosaic.Contracts;
using Mosaic.FloatingWindows.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Tasks.Contracts;
namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TaskFloatingContentFactory(TasksUiDependencies dependencies,Func<bool> active) : IFloatingContentFactory
{
    public IFloatingContentSession Create(FloatingContentReference reference)
    {
        if(!active())throw new InvalidOperationException("Floating content capability is inactive.");
        if(reference.ProviderId!="tasks-ui" || !Guid.TryParseExact(reference.ItemKey,"D",out var id)) throw new ArgumentException("Unsupported floating content reference.");
        return new TaskFloatingContentSession(dependencies,id);
    }
}
internal sealed class TaskFloatingContentSession : IFloatingContentSession
{
    private readonly TasksUiDependencies _dependencies;
    private readonly Guid _id;
    private readonly Grid _view=new();
    private readonly Grid _heading=new();
    private readonly StackPanel _feedbackPanel=new();
    private readonly ToggleButton _state=new(){MinWidth=42,MinHeight=36,Margin=new Thickness(0,0,8,0)};
    private readonly Button _expand=new(){HorizontalContentAlignment=HorizontalAlignment.Stretch,MinWidth=0,Padding=new Thickness(4)};
    private readonly TextBlock _title=new(){TextWrapping=TextWrapping.Wrap,TextTrimming=TextTrimming.CharacterEllipsis,FontWeight=FontWeights.SemiBold,FontSize=14,MinWidth=0};
    private readonly TextBlock _status=new(){FontSize=12};
    private readonly TextBlock _feedback=new(){TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,4,0,0)};
    private readonly Button _retry=new(){HorizontalAlignment=HorizontalAlignment.Left};
    private readonly TaskPeekDetailsView _details=new();
    private readonly CancellationTokenSource _lifetime=new();
    private readonly IDisposable? _subscription;
    private Task _pending=Task.CompletedTask;
    private TaskItemDto? _current;
    private bool _closed,_expanded,_busy;
    private int _generation;
    private string _error="";
    public TaskFloatingContentSession(TasksUiDependencies dependencies,Guid id)
    {
        _dependencies=dependencies;_id=id;
        _view.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("/Mosaic.Tasks.Wpf;component/Themes/Floating.xaml",UriKind.Relative)});
        _state.SetResourceReference(FrameworkElement.StyleProperty,"TaskFloatingStateStyle");
        _view.RowDefinitions.Add(new(){Height=GridLength.Auto});_view.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});_view.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var heading=_heading;heading.ColumnDefinitions.Add(new(){Width=GridLength.Auto});heading.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        heading.Children.Add(_state);var label=new StackPanel();label.Children.Add(_title);label.Children.Add(_status);_expand.Content=label;Grid.SetColumn(_expand,1);heading.Children.Add(_expand);_view.Children.Add(heading);
        _details.BackButton.Visibility=Visibility.Collapsed;_details.Visibility=Visibility.Collapsed;Grid.SetRow(_details,1);_view.Children.Add(_details);
        var feedback=_feedbackPanel;feedback.Children.Add(_feedback);feedback.Children.Add(_retry);Grid.SetRow(feedback,2);_view.Children.Add(feedback);
        _expand.SetResourceReference(FrameworkElement.StyleProperty,MosaicTheme.QuietButtonKey);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_expand,"FloatingTaskExpand");
        _expand.Click+=(_,_)=>ModeRequested?.Invoke(this,EventArgs.Empty);
        _state.Click+=(_,_)=>Track(ToggleAsync());
        _retry.Click+=(_,_)=>Track(ReloadAsync());
        _details.Retry=ReloadAsync;
        _subscription=dependencies.Changes?.Subscribe(change=> {if(change.TaskId==_id) _=dependencies.Dispatcher.InvokeAsync(()=>{if(!_closed)Track(ReloadAsync());});});
        MosaicLocalizationManager.LanguageChanged+=LanguageChanged;
        Render();
    }
    public FrameworkElement View=>_view;
    public FrameworkElement Header=>_heading;
    public void ConfigureLayout(FloatingContentLayoutOptions options)
    {
        _heading.Margin=new Thickness(0,0,options.HeaderTrailingSpace,0);
        _state.Margin=new Thickness(0,0,options.ContentGap,0);
        _status.Margin=new Thickness(0,options.ContentGap,0,0);
        _feedbackPanel.Margin=new Thickness(0,options.ContentGap,0,0);
        _details.ConfigureDensity(options.ContentGap);
    }
    public event EventHandler? LayoutInvalidated;
    public event EventHandler? Missing;
    public event EventHandler? ModeRequested;
    public Task<OperationResult> InitializeAsync(CancellationToken cancellationToken=default)
    {
        var query=LoadAsync(cancellationToken);Track(query);return query;
    }
    private void Track(Task task)=>_pending=Task.WhenAll(_pending,task);
    private async Task ReloadAsync()=>await LoadAsync(_lifetime.Token);
    private async Task<OperationResult> LoadAsync(CancellationToken ct)
    {
        var generation=++_generation;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,_lifetime.Token);
        var result=await _dependencies.Api.GetAsync(_id,linked.Token);
        await _dependencies.Dispatcher.InvokeAsync(()=>
        {
            if(_closed || linked.IsCancellationRequested || generation!=_generation)return;
            if(result.IsSuccess){if(_current is null || result.Value.Revision>=_current.Revision)_current=result.Value;_error="";}
            else if(result.Error!.Code==ErrorCodes.NotFound){_current=null;_error=TaskLocalization.Localize("Tasks.Peek.Deleted","任务已删除或不存在");Missing?.Invoke(this,EventArgs.Empty);}
            else _error=TaskErrorLocalizer.Localize(result.Error!);
            Render();
        });
        return result.IsSuccess?OperationResult.Success():OperationResult.Failure(result.Error!);
    }
    private async Task ToggleAsync()
    {
        if(_closed || _busy || _current is not {} current){Render();return;}
        _busy=true;Render();
        try
        {
            var result=await _dependencies.Api.SetCompletedAsync(new(_id,!current.IsCompleted,current.Revision),_lifetime.Token);
            if(_closed)return;
            if(result.IsSuccess){if(_current is null || result.Value.Revision>=_current.Revision)_current=result.Value;_error="";}
            else
            {
                var message=TaskErrorLocalizer.Localize(result.Error!);
                if(result.Error!.Code==ErrorCodes.Conflict) await ReloadAsync();
                _error=message;
            }
        }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested){}
        finally{_busy=false;if(!_closed)Render();}
    }
    private void LanguageChanged(object? sender,EventArgs e){if(!_closed)Render();}
    private void Render()
    {
        _title.Text=_current?.Title??TaskLocalization.Localize("Tasks.Peek.Snapshot","任务内容");
        _title.ToolTip=_title.Text;
        var status=TaskLocalization.Localize(_current?.IsCompleted==true?"Tasks.Peek.Completed":"Tasks.Peek.Open",_current?.IsCompleted==true?"已完成":"未完成");
        var mark=new System.Windows.Shapes.Path {Data=Geometry.Parse(_current?.IsCompleted==true?"M 2,9 L 7,14 L 17,3":"M 18,9 A 8,8 0 1 1 2,9 A 8,8 0 1 1 18,9"),Width=18,Height=18,Stretch=Stretch.Uniform,StrokeThickness=1.7};
        mark.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,MosaicTheme.AccentTextBrushKey);
        _status.Text=status+(_expanded?"  ▴":"  ▾");
        _title.FontWeight=_current?.IsCompleted==true?FontWeights.Normal:FontWeights.SemiBold;
        _state.Content=mark;_state.ToolTip=status;_state.IsChecked=_current?.IsCompleted??false;_state.IsEnabled=!_busy && _current is not null;
        System.Windows.Automation.AutomationProperties.SetName(_state,status);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_state,"FloatingTaskState");
        System.Windows.Automation.AutomationProperties.SetName(_expand,_title.Text+" · "+TaskLocalization.Localize(_expanded?"Tasks.Floating.Collapse":"Tasks.Floating.Expand",_expanded?"收起":"展开"));
        _details.Render(_current,false,false,false,showHeading:false);
        _feedbackPanel.Visibility=_error.Length>0?Visibility.Visible:Visibility.Collapsed;
        _feedback.Text=_error;_feedback.Visibility=_error.Length>0?Visibility.Visible:Visibility.Collapsed;
        _retry.Content=TaskLocalization.Localize("Tasks.Retry","重试");_retry.Visibility=_error.Length>0?Visibility.Visible:Visibility.Collapsed;
        LayoutInvalidated?.Invoke(this,EventArgs.Empty);
    }
    public void SetExpanded(bool expanded){_expanded=expanded;_details.Visibility=expanded?Visibility.Visible:Visibility.Collapsed;Render();}
    public Task FlushAsync()=>_pending;
    public async ValueTask DisposeAsync()
    {
        if(_closed)return;_closed=true;++_generation;_subscription?.Dispose();_lifetime.Cancel();MosaicLocalizationManager.LanguageChanged-=LanguageChanged;
        try{await _pending;}finally{_lifetime.Dispose();LayoutInvalidated=null;Missing=null;ModeRequested=null;}
    }
}
