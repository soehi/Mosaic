using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TaskListItemViewModel(TaskItemDto item, string untitledTitle)
{
    public TaskItemDto Item { get; } = item;
    public Guid Id => Item.Id;
    public string Title => string.IsNullOrWhiteSpace(Item.Title) ? untitledTitle : Item.Title;
    public bool IsCompleted => Item.IsCompleted;
    public string CompletionGlyph => IsCompleted ? "✓" : "○";
    public bool HasAttachments => Item.AttachmentCount > 0;
    public string AttachmentCountLabel => HasAttachments
        ? TaskLocalization.Format("Tasks.Attachments.Count", "📎 {0}", Item.AttachmentCount)
        : "";
    public string CreatedLabel => TaskLocalization.Format(
        "Tasks.List.Created",
        "创建于 {0}",
        Item.CreatedAtUtc.LocalDateTime.ToString("MM-dd HH:mm"));
    public string CompletionLabel => TaskLocalization.Localize(
        IsCompleted ? "Tasks.FilterCompleted" : "Tasks.FilterOpen",
        IsCompleted ? "已完成" : "未完成");
    public string AccessibilityLabel => TaskLocalization.Format(
        "Tasks.List.Accessibility",
        "{0}，{1}，{2}",
        Title,
        CompletionLabel,
        HasAttachments ? TaskLocalization.Format("Tasks.Attachments.Accessibility", "{0} 个附件", Item.AttachmentCount) : CreatedLabel);
}
