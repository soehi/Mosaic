using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mosaic.Presentation.Wpf;
using Mosaic.Tasks.Contracts;
namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TaskPeekDetailsView : Grid
{
    internal Action? Back { get; set; }
    internal Func<Task>? Retry { get; set; }
    internal Button BackButton { get; } = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _body = new();
    private readonly ScrollViewer _scroll;
    private double _gap=12;
    private bool _compact;
    internal void ConfigureDensity(double gap)
    {
        _compact=true;_gap=gap;_scroll.Margin=new Thickness(0,gap,0,0);
        foreach(var child in _body.Children.OfType<FrameworkElement>())child.Margin=new Thickness(0,0,0,gap);
    }
    internal TaskPeekDetailsView()
    {
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        BackButton.SetResourceReference(StyleProperty, MosaicTheme.QuietButtonKey);
        BackButton.Click += (_, _) => Back?.Invoke();
        Children.Add(BackButton);
        var scroll = _scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 8, 0, 0) };
        SetRow(scroll, 1); Children.Add(scroll);
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "TaskPeekDetails");
    }
    private static string Text(string key, string fallback) => TaskLocalization.Localize(key, fallback);
    private void AddText(string value, bool heading = false)
    {
        var text = new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, _gap), FontSize = heading ? 17 : 13, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
        text.SetResourceReference(Control.ForegroundProperty, MosaicTheme.TextPrimaryBrushKey);
        System.Windows.Automation.AutomationProperties.SetName(text, value);
        _body.Children.Add(text);
    }
    internal void Render(TaskItemDto? task, bool loading, bool failed, bool missing, bool showHeading = true)
    {
        BackButton.Content = Text("Tasks.Peek.Back", "← 返回任务列表");
        _body.Children.Clear();
        if (loading) { AddText(Text("Tasks.Loading", "正在读取任务…")); return; }
        if (failed)
        {
            AddText(missing ? Text("Tasks.Peek.Deleted", "任务已删除或不存在") : Text("Tasks.Peek.DetailError", "无法读取任务详情"));
            var retry = new Button { Content = Text("Tasks.Retry", "重试"), HorizontalAlignment = HorizontalAlignment.Left };
            retry.Click += async (_, _) => { if (Retry is { } action) await action(); };
            _body.Children.Add(retry); return;
        }
        if (task is null) return;
        if(showHeading) {
        AddText(task.Title, true);
        AddText((task.IsCompleted ? "✓ " : "○ ") + Text(task.IsCompleted ? "Tasks.Peek.Completed" : "Tasks.Peek.Open", task.IsCompleted ? "已完成" : "未完成"));
        }
        if(!_compact || !string.IsNullOrEmpty(task.Notes)) AddText(string.IsNullOrEmpty(task.Notes) ? Text("Tasks.Peek.NoNotes", "暂无备注") : task.Notes);
        var culture = MosaicLocalizationManager.Current == MosaicLanguage.English ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("zh-CN");
        AddText(Text("Tasks.Peek.CreatedAt", "创建时间") + "：" + task.CreatedAtUtc.ToLocalTime().ToString("g", culture));
        AddText(Text("Tasks.Peek.UpdatedAt", "更新时间") + "：" + task.UpdatedAtUtc.ToLocalTime().ToString("g", culture));
    }
}
