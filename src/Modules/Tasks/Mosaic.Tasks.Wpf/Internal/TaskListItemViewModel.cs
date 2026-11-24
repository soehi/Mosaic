using System.ComponentModel;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

// 列表刷新按 Id 复用同一个行对象：容器、滚动位置与选择因此能保留。
// 已存在的行只在数据或界面语言真的变化时更新。
internal sealed class TaskListItemViewModel : INotifyPropertyChanged
{
    private TaskItemDto _item;
    private string _untitledTitle;
    internal TaskListItemViewModel(TaskItemDto item, string untitledTitle)
    {
        _item = item;
        _untitledTitle = untitledTitle;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal TaskItemDto Item => _item;
    public Guid Id => _item.Id;
    public string Title => string.IsNullOrWhiteSpace(_item.Title) ? _untitledTitle : _item.Title;
    public bool IsCompleted => _item.IsCompleted;
    public bool HasAttachments => _item.HasAttachments;
    public string CreatedLabel => TaskLocalization.Format(
        "Tasks.List.Created",
        "创建于 {0}",
        _item.CreatedAtUtc.LocalDateTime.ToString("MM-dd HH:mm"));
    public string CompletionLabel => TaskLocalization.Localize(
        IsCompleted ? "Tasks.FilterCompleted" : "Tasks.FilterOpen",
        IsCompleted ? "已完成" : "未完成");
    public string AttachmentLabel => TaskLocalization.AttachmentCountLabel(_item.AttachmentCount);
    public string AccessibilityLabel => TaskLocalization.Format(
        "Tasks.List.Accessibility",
        "{0}，{1}，{2}",
        Title,
        CompletionLabel,
        HasAttachments ? AttachmentLabel : CreatedLabel);

    internal void Update(TaskItemDto item, string untitledTitle)
    {
        if (ReferenceEquals(_item, item) && string.Equals(_untitledTitle, untitledTitle, StringComparison.Ordinal)) return;
        if (_item == item && string.Equals(_untitledTitle, untitledTitle, StringComparison.Ordinal)) return;
        _item = item;
        _untitledTitle = untitledTitle;
        NotifyText();
    }

    // 语言切换不会改变数据，只会改变展示串，因此单独刷新文本而不触碰集合。
    internal void RefreshLocalizedText(string untitledTitle)
    {
        _untitledTitle = untitledTitle;
        NotifyText();
    }

    private void NotifyText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(CreatedLabel));
        OnPropertyChanged(nameof(CompletionLabel));
        OnPropertyChanged(nameof(AttachmentLabel));
        OnPropertyChanged(nameof(AccessibilityLabel));
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}