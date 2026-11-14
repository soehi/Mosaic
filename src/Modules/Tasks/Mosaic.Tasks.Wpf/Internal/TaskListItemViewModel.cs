using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TaskListItemViewModel(TaskItemDto item, string untitledTitle)
{
    public TaskItemDto Item { get; } = item;
    public Guid Id => Item.Id;
    public string Title => string.IsNullOrWhiteSpace(Item.Title) ? untitledTitle : Item.Title;
    public bool IsCompleted => Item.IsCompleted;
    public string CompletionGlyph => IsCompleted ? "✓" : "○";
    public string CreatedLabel => $"创建于 {Item.CreatedAtUtc.LocalDateTime:MM-dd HH:mm}";
    public string CompletionLabel => IsCompleted ? "已完成" : "未完成";
    public string AccessibilityLabel => $"{Title}，{CompletionLabel}，{CreatedLabel}";
}
