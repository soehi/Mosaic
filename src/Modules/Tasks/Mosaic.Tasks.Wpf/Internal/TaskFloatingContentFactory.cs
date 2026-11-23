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

internal sealed class TaskFloatingContentFactory(TasksUiDependencies dependencies, TaskUiSettingsTarget settings, Func<bool> active) : IFloatingContentFactory
{
    public IFloatingContentSession Create(FloatingContentReference reference)
    {
        if(!active())throw new InvalidOperationException("Floating content capability is inactive.");
        if(reference.ProviderId!="tasks-ui" || !Guid.TryParseExact(reference.ItemKey,"D",out var id)) throw new ArgumentException("Unsupported floating content reference.");
        return new TaskFloatingContentSession(dependencies, settings, id);
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
    private readonly TaskFloatingDetailsView _details;
    private readonly TaskFloatingNotesViewModel _notes;
    private readonly IDisposable _settingsSubscription;
    private readonly CancellationTokenSource _lifetime=new();
    private readonly IDisposable? _subscription;
    private Task _pending=Task.CompletedTask;
    private TaskItemDto? Current => _notes.Current;
    private bool _closed,_expanded,_busy;
    private int _generation;
    private string _error="";
    public TaskFloatingContentSession(TasksUiDependencies dependencies, TaskUiSettingsTarget settings, Guid id)
    {
        _dependencies=dependencies;_id=id;
        _notes = new(dependencies, _lifetime.Token);
        _details = new(_notes, () => Track(SaveNotesAsync()), () => Track(CancelNotesAsync()), dependencies.Attachments);
        _notes.Changed += RenderNotes;
        _view.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("/Mosaic.Tasks.Wpf;component/Themes/Floating.xaml",UriKind.Relative)});
        _state.SetResourceReference(FrameworkElement.StyleProperty,"TaskFloatingStateStyle");
        _view.RowDefinitions.Add(new(){Height=GridLength.Auto});_view.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});_view.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var heading=_heading;heading.ColumnDefinitions.Add(new(){Width=GridLength.Auto});heading.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        heading.Children.Add(_state);var label=new StackPanel();label.Children.Add(_title);label.Children.Add(_status);_expand.Content=label;Grid.SetColumn(_expand,1);heading.Children.Add(_expand);_view.Children.Add(heading);
        _details.Visibility=Visibility.Collapsed;Grid.SetRow(_details,1);_view.Children.Add(_details);
        var feedback=_feedbackPanel;feedback.Children.Add(_feedback);feedback.Children.Add(_retry);Grid.SetRow(feedback,2);_view.Children.Add(feedback);
        _expand.SetResourceReference(FrameworkElement.StyleProperty, "TaskFloatingButtonStyle");
        _retry.SetResourceReference(FrameworkElement.StyleProperty, "TaskFloatingButtonStyle");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_expand,"FloatingTaskExpand");
        _expand.Click+=(_,_)=>ModeRequested?.Invoke(this,EventArgs.Empty);
        _state.Click+=(_,_)=>Track(ToggleAsync());
        _retry.Click+=(_,_)=>Track(ReloadAsync());
        _settingsSubscription = settings.Subscribe(values =>
        {
            _notes.Allowed = values.GetValueOrDefault(TaskUiSettingsTarget.FloatingNotesEditing) is true;
            RenderNotes();
        });
        _subscription=dependencies.Changes?.Subscribe(change=> {if(change.TaskId==_id) _=dependencies.Dispatcher.InvokeAsync(()=>{if(!_closed)Track(ReloadAsync());});});
        MosaicLocalizationManager.LanguageChanged+=LanguageChanged;
        Render();
    }
    public FrameworkElement View=>_view;
    public FrameworkElement Header=>_heading;
    public FloatingContentDragKind QueryContentDrag(Point pointInView)
    {
        // The contract hands us a point in the root view's space; the details panel sits below the
        // heading, so convert before asking it. Collapsed details expose no glyphs: everything
        // outside the heading is blank and may drag the window.
        if (_details.Visibility != Visibility.Visible) return FloatingContentDragKind.Immediate;
        var kind = _details.QueryContentDrag(_details.TransformToAncestor(_view).Inverse.Transform(pointInView));
        // 附件行需要自行处理拖出，不让窗口层把手势当成移动窗口。
        var hit = _details.InputHitTest(_details.TransformToAncestor(_view).Inverse.Transform(pointInView)) as DependencyObject;
        for (var current = hit; current is not null && current != _details; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is FrameworkElement { Tag: TaskAttachmentDto }) return FloatingContentDragKind.None;
        return kind;
    }
    public void ConfigureLayout(FloatingContentLayoutOptions options)
    {
        _heading.Margin=new Thickness(0,0,options.HeaderTrailingSpace,0);
        _state.Margin=new Thickness(0,0,options.ContentGap,0);
        _status.Margin=new Thickness(0,options.ContentGap,0,0);
        _feedbackPanel.Margin=new Thickness(0,options.ContentGap,0,0);
        _details.Margin = new Thickness(0, options.ContentGap, 0, 0);
    }
    public event EventHandler? LayoutInvalidated;
    public event EventHandler? Missing;
    public event EventHandler? ModeRequested;
    public Task<OperationResult> InitializeAsync(CancellationToken cancellationToken=default)
    {
        var query=LoadAsync(cancellationToken, opening: true);Track(query);return query;
    }
    private void Track(Task task)=>_pending=Task.WhenAll(_pending,task);
    private async Task ReloadAsync()=>await LoadAsync(_lifetime.Token);
    private async Task<OperationResult> LoadAsync(CancellationToken ct, bool opening = false)
    {
        var generation=++_generation;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,_lifetime.Token);
        var result=await _dependencies.Api.GetAsync(_id,linked.Token);
        if (opening && result.IsSuccess && result.Value.IsArchived)
            return OperationResult.Failure(new(ErrorCodes.NotFound, "任务已归档。", MessageKey: "Tasks.Error.Archived"));
        await _dependencies.Dispatcher.InvokeAsync(()=>
        {
            if(_closed || linked.IsCancellationRequested || generation!=_generation)return;
            if(result.IsSuccess){_notes.Observe(result.Value);_error="";Track(_notes.RefreshAttachmentsAsync());}
            else if(result.Error!.Code==ErrorCodes.NotFound) MarkMissing();
            else _error=TaskErrorLocalizer.Localize(result.Error!);
            Render();
        });
        return result.IsSuccess?OperationResult.Success():OperationResult.Failure(result.Error!);
    }
    private async Task ToggleAsync()
    {
        if(_closed || _busy || _notes.IsSaving || _notes.Reserved || Current is not { IsArchived: false } current){Render();return;}
        _busy=true;Render();
        try
        {
            var result=await _dependencies.Api.SetCompletedAsync(new(_id,!current.IsCompleted,current.Revision),_lifetime.Token);
            if(_closed)return;
            if(result.IsSuccess){_notes.Observe(result.Value, current.Revision);_error="";}
            else if (result.Error!.Code == ErrorCodes.NotFound) MarkMissing();
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
    private void MarkMissing()
    {
        _notes.MarkMissing();
        if (!_notes.IsDirty) Missing?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveNotesAsync()
    {
        if (_busy || _closed) return;
        var result = await _notes.SaveAsync();
        if (result.Error?.Code == ErrorCodes.Conflict) await ReloadAsync();
        if (!_closed) Render();
    }

    private async Task CancelNotesAsync()
    {
        if (_busy || _notes.IsSaving || _notes.Reserved || !_notes.IsEditing) return;
        _notes.Reserved = true;
        RenderNotes();
        try
        {
            // Track this command before a modal dialog can dispatch another close request.
            await Task.Yield();
            if (_notes.IsDirty && !await _dependencies.Dialogs.ConfirmAsync(
                TaskFloatingNotesViewModel.Text("DiscardTitle", "放弃未保存的备注？"),
                TaskFloatingNotesViewModel.Text("DiscardDraft", "放弃未保存的备注，返回已保存内容。"))) return;
            var reload = _notes.HasConflict;
            _notes.Discard();
            if (_notes.IsMissing) Missing?.Invoke(this, EventArgs.Empty);
            else if (reload) await ReloadAsync();
        }
        finally { _notes.Reserved = false; if (!_closed) Render(); }
    }

    private void RenderNotes()
    {
        _details.Render(_notes, _busy);
        _state.IsEnabled = !_busy && !_notes.IsSaving && !_notes.Reserved && Current is { IsArchived: false };
    }

    public bool HandleEscape()
    {
        if (!_notes.IsEditing) return false;
        Track(CancelNotesAsync());
        return true;
    }

    public async Task<OperationResult> PrepareCloseAsync(CancellationToken cancellationToken)
    {
        // Prevent changes until the coordinator commits the close or rolls back the reservation.
        await _pending;
        cancellationToken.ThrowIfCancellationRequested();
        _notes.Reserved = true;
        RenderNotes();
        return await _notes.ConfirmCloseAsync(cancellationToken);
    }

    public void CancelClosePreparation()
    {
        _notes.Reserved = false;
        RenderNotes();
    }

    private void LanguageChanged(object? sender,EventArgs e){if(!_closed)Render();}
    private void Render()
    {
        _title.Text=Current?.Title??TaskLocalization.Localize("Tasks.Peek.Snapshot","任务内容");
        _title.ToolTip=_title.Text;
        var status = Current?.IsArchived == true ? TaskLocalization.Localize("Tasks.Archived", "已归档") : TaskLocalization.Localize(Current?.IsCompleted==true?"Tasks.Peek.Completed":"Tasks.Peek.Open",Current?.IsCompleted==true?"已完成":"未完成");
        var mark=new System.Windows.Shapes.Path {Data=Geometry.Parse(Current?.IsCompleted==true?"M 2,9 L 7,14 L 17,3":"M 18,9 A 8,8 0 1 1 2,9 A 8,8 0 1 1 18,9"),Width=18,Height=18,Stretch=Stretch.Uniform,StrokeThickness=1.7};
        mark.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,MosaicTheme.AccentTextBrushKey);
        _status.Text=status+(_expanded?"  ▴":"  ▾");
        _title.FontWeight=Current?.IsCompleted==true?FontWeights.Normal:FontWeights.SemiBold;
        _state.Content=mark;_state.ToolTip=status;_state.IsChecked=Current?.IsCompleted??false;
        System.Windows.Automation.AutomationProperties.SetName(_state,status);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_state,"FloatingTaskState");
        System.Windows.Automation.AutomationProperties.SetName(_expand,_title.Text+" · "+TaskLocalization.Localize(_expanded?"Tasks.Floating.Collapse":"Tasks.Floating.Expand",_expanded?"收起":"展开"));
        RenderNotes();
        _feedbackPanel.Visibility=_error.Length>0?Visibility.Visible:Visibility.Collapsed;
        _feedback.Text=_error;_feedback.Visibility=_error.Length>0?Visibility.Visible:Visibility.Collapsed;
        _retry.Content=TaskLocalization.Localize("Tasks.Retry","重试");_retry.Visibility=_error.Length>0?Visibility.Visible:Visibility.Collapsed;
        LayoutInvalidated?.Invoke(this,EventArgs.Empty);
    }
    public void SetExpanded(bool expanded){_expanded=expanded;_details.Visibility=expanded?Visibility.Visible:Visibility.Collapsed;Render();}
    public Task FlushAsync()=>_pending;
    public async ValueTask DisposeAsync()
    {
        if(_closed)return;_closed=true;++_generation;_settingsSubscription.Dispose();_subscription?.Dispose();_lifetime.Cancel();MosaicLocalizationManager.LanguageChanged-=LanguageChanged;
        try{await _pending;}finally{_lifetime.Dispose();LayoutInvalidated=null;Missing=null;ModeRequested=null;}
    }
}
