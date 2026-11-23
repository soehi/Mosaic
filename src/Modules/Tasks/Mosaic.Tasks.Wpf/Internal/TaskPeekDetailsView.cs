using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Mosaic.Presentation.Wpf;
using Mosaic.Tasks.Contracts;
namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TaskPeekDetailsView : Grid
{
    internal Action? Back { get; set; }
    internal Func<Task>? Retry { get; set; }
    internal Button BackButton { get; } = new() { HorizontalAlignment = HorizontalAlignment.Left };
    internal Border Card { get; } = new();
    internal TextBox NotesBox { get; }
    internal TaskAttachmentPanel? Attachments { get; private set; }
    internal TextBlock TitleText { get; } = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    internal TextBlock StatusText { get; } = new() { FontSize = 12 };
    internal TextBlock TimeText { get; } = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _body = new();
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _cardBody = new();
    private readonly TextBlock _emptyNotes = new() { FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
    private double _gap = 12;
    private bool _compact;
    internal void ConfigureDensity(double gap)
    {
        _compact = true; _gap = gap; _scroll.Margin = new Thickness(0, gap, 0, 0);
        Card.Margin = new Thickness(0, 0, 0, gap);
        TimeText.Margin = new Thickness(0, 0, 0, gap);
    }
    internal TaskPeekDetailsView(ITaskAttachmentApi? attachmentApi = null)
    {
        if (attachmentApi is not null) Attachments = new TaskAttachmentPanel(attachmentApi);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Mosaic.Tasks.Wpf;component/Themes/Peek.xaml", UriKind.Relative) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        BackButton.SetResourceReference(StyleProperty, MosaicTheme.QuietButtonKey);
        BackButton.Click += (_, _) => Back?.Invoke();
        Children.Add(BackButton);

        Card.SetResourceReference(StyleProperty, "TaskPeekCardStyle");
        TitleText.SetResourceReference(TextBlock.ForegroundProperty, MosaicTheme.TextPrimaryBrushKey);
        AutomationProperties.SetAutomationId(TitleText, "PeekDetailsTitle");
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, MosaicTheme.TextSecondaryBrushKey);
        StatusText.Margin = new Thickness(0, 2, 0, 10);
        AutomationProperties.SetAutomationId(StatusText, "PeekDetailsStatus");
        NotesBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        NotesBox.SetResourceReference(StyleProperty, "TaskPeekNotesStyle");
        AutomationProperties.SetAutomationId(NotesBox, "PeekDetailsNotes");
        _emptyNotes.SetResourceReference(TextBlock.ForegroundProperty, MosaicTheme.TextSecondaryBrushKey);
        _cardBody.Children.Add(TitleText);
        _cardBody.Children.Add(StatusText);
        _cardBody.Children.Add(NotesBox);
        _cardBody.Children.Add(_emptyNotes);
        Card.Child = _cardBody;

        TimeText.SetResourceReference(TextBlock.ForegroundProperty, MosaicTheme.TextSecondaryBrushKey);
        AutomationProperties.SetAutomationId(TimeText, "PeekDetailsTimes");

        var scroll = _scroll = new ScrollViewer
        {
            Content = _body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 8, 0, 0)
        };
        SetRow(scroll, 1);
        Children.Add(scroll);
        AutomationProperties.SetAutomationId(this, "TaskPeekDetails");
    }
    private static string Text(string key, string fallback) => TaskLocalization.Localize(key, fallback);
    private static TextBlock BodyText(string value)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        text.SetResourceReference(TextBlock.ForegroundProperty, MosaicTheme.TextPrimaryBrushKey);
        return text;
    }
    internal void Render(TaskItemDto? task, bool loading, bool failed, bool missing, bool showHeading = true)
    {
        BackButton.Content = Text("Tasks.Peek.Back", "← 返回任务列表");
        _body.Children.Clear();
        if (loading) { _body.Children.Add(BodyText(Text("Tasks.Loading", "正在读取任务…"))); return; }
        if (failed)
        {
            _body.Children.Add(BodyText(missing ? Text("Tasks.Peek.Deleted", "任务已删除或不存在") : Text("Tasks.Peek.DetailError", "无法读取任务详情")));
            var retry = new Button { Content = Text("Tasks.Retry", "重试"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            retry.Click += async (_, _) => { if (Retry is { } action) await action(); };
            _body.Children.Add(retry);
            return;
        }
        if (task is null) return;
        var notes = task.Notes ?? "";
        TitleText.Visibility = StatusText.Visibility = showHeading ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = task.Title;
        StatusText.Text = (task.IsCompleted ? "✓ " : "○ ") + Text(task.IsCompleted ? "Tasks.Peek.Completed" : "Tasks.Peek.Open", task.IsCompleted ? "已完成" : "未完成");
        NotesBox.Text = notes;
        NotesBox.Visibility = notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _emptyNotes.Text = Text("Tasks.Peek.NoNotes", "暂无备注");
        _emptyNotes.Visibility = notes.Length == 0 && !_compact ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(NotesBox, TitleText.Text + "，" + Text("Tasks.Peek.Notes", "任务备注"));
        var culture = MosaicLocalizationManager.Current == MosaicLanguage.English ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("zh-CN");
        TimeText.Text = Text("Tasks.Peek.CreatedAt", "创建时间") + "：" + task.CreatedAtUtc.ToLocalTime().ToString("g", culture)
            + Environment.NewLine + Text("Tasks.Peek.UpdatedAt", "更新时间") + "：" + task.UpdatedAtUtc.ToLocalTime().ToString("g", culture);
        AutomationProperties.SetName(TimeText, TimeText.Text.Replace(Environment.NewLine, "，"));
        Card.Margin = new Thickness(0, 0, 0, _gap);
        TimeText.Margin = new Thickness(0, 0, 0, _gap);
        _body.Children.Add(Card);
        _body.Children.Add(TimeText);
        if (Attachments is { } attachments)
        {
            attachments.Margin = new Thickness(0, 0, 0, _gap);
            _body.Children.Add(attachments);
        }
    }
}
