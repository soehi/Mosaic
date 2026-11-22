using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TaskEditorViewModel : ObservableObject
{
    private readonly ITaskAttachmentApi? _attachmentApi;
    private string _title = "";
    private string _notes = "";
    private string _titleError = "";
    private string _notesError = "";
    private TaskItemDto? _current;
    private TaskConstraintsDto _constraints = new(
        TaskConstraints.TitleMaxCharacters,
        TaskConstraints.NotesMaxCharacters,
        TitleRequired: true,
        DefaultCompleted: false,
        RevisionStart: 1,
        AttachmentMaxFileMegabytes: TaskAttachmentConstraints.MaxFileMegabytes,
        AttachmentMaxPerTask: TaskAttachmentConstraints.MaxPerTask);
    internal TaskConstraintsDto Constraints => _constraints;
    private bool _validationErrors;

    internal TaskEditorViewModel(ITaskAttachmentApi? attachmentApi = null)
    {
        _attachmentApi = attachmentApi;
    }

    public TaskItemDto? Current => _current;
    public ObservableCollection<TaskAttachmentDto> Attachments { get; } = new();
    public string Title
    {
        get => _title;
        set
        {
            if (!SetProperty(ref _title, value)) return;
            TitleError = "";
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(TitleCountLabel));
            OnPropertyChanged(nameof(TitleExceedsLimit));
        }
    }
    public string Notes
    {
        get => _notes;
        set
        {
            if (!SetProperty(ref _notes, value)) return;
            NotesError = "";
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(NotesCountLabel));
            OnPropertyChanged(nameof(NotesExceedsLimit));
        }
    }
    public string TitleError { get => _titleError; private set { if (SetProperty(ref _titleError, value)) OnPropertyChanged(nameof(HasTitleError)); } }
    public string NotesError { get => _notesError; private set { if (SetProperty(ref _notesError, value)) OnPropertyChanged(nameof(HasNotesError)); } }
    public bool HasTitleError => !string.IsNullOrEmpty(TitleError);
    public bool HasNotesError => !string.IsNullOrEmpty(NotesError);
    public bool IsDirty => Title != (_current?.Title ?? "") || Notes != (_current?.Notes ?? "");
    public bool HasTask => _current is not null;
    public bool IsArchived => _current?.IsArchived == true;
    public bool CanManageAttachments => HasTask && !IsArchived && _attachmentApi is not null;
    public string AttachmentLimitLabel => TaskLocalization.Format(
        "Tasks.Attachments.Limit", "最多 {0} 个 · 单个 ≤ {1} MB",
        _constraints.AttachmentMaxPerTask, _constraints.AttachmentMaxFileMegabytes);
    public string Heading => TaskLocalization.Localize(
        HasTask ? "Tasks.Editor.Details" : "Tasks.Editor.New",
        HasTask ? "任务详情" : "新建任务");
    public string StateLabel => TaskLocalization.Localize(
        _current?.IsCompleted == true ? "Tasks.FilterCompleted" : "Tasks.FilterOpen",
        _current?.IsCompleted == true ? "已完成" : "未完成");
    public string ToggleLabel => TaskLocalization.Localize(
        _current?.IsCompleted == true ? "Tasks.Editor.MarkOpen" : "Tasks.Editor.MarkCompleted",
        _current?.IsCompleted == true ? "恢复未完成" : "标记完成");
    public string Metadata => _current is null
        ? TaskLocalization.Localize("Tasks.Editor.EmptyMetadata", "记录一个想法，开始下一步。")
        : TaskLocalization.Format(
            "Tasks.Editor.Created",
            "创建于 {0}  ·  版本 {1}",
            _current.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm"),
            _current.Revision);
    public string TitleHelpText => TaskLocalization.Format(
        "Tasks.Editor.Limit",
        "{0} · 最多 {1:N0} 个字符",
        _constraints.TitleRequired
            ? TaskLocalization.Localize("Tasks.Required", "必填")
            : TaskLocalization.Localize("Tasks.Optional", "可选"),
        _constraints.TitleMaxCharacters);
    public string NotesHelpText => TaskLocalization.Format(
        "Tasks.Editor.Limit",
        "{0} · 最多 {1:N0} 个字符",
        TaskLocalization.Localize("Tasks.Optional", "可选"),
        _constraints.NotesMaxCharacters);
    public string TitleCountLabel => $"{CountCharacters(Title):N0} / {_constraints.TitleMaxCharacters:N0}";
    public string NotesCountLabel => $"{CountCharacters(Notes):N0} / {_constraints.NotesMaxCharacters:N0}";
    public bool TitleExceedsLimit => CountCharacters(Title) > _constraints.TitleMaxCharacters;
    public bool NotesExceedsLimit => CountCharacters(Notes) > _constraints.NotesMaxCharacters;

    internal void ApplyConstraints(TaskConstraintsDto constraints)
    {
        _constraints = constraints;
        foreach (var name in new[]
        {
            nameof(TitleHelpText),
            nameof(NotesHelpText),
            nameof(TitleCountLabel),
            nameof(NotesCountLabel),
            nameof(TitleExceedsLimit),
            nameof(NotesExceedsLimit),
            nameof(AttachmentLimitLabel)
        })
            OnPropertyChanged(name);
    }

    internal void RefreshLocalizedText()
    {
        foreach (var name in new[]
        {
            nameof(Heading),
            nameof(StateLabel),
            nameof(ToggleLabel),
            nameof(Metadata),
            nameof(TitleHelpText),
            nameof(NotesHelpText),
            nameof(AttachmentLimitLabel),
            nameof(CanManageAttachments)
        })
            OnPropertyChanged(name);
        if (_validationErrors) Validate();
    }

    internal void Load(TaskItemDto? task)
    {
        _current = task;
        Title = task?.Title ?? "";
        Notes = task?.Notes ?? "";
        TitleError = "";
        NotesError = "";
        foreach (var name in new[]
        {
            nameof(Current),
            nameof(HasTask),
            nameof(Heading),
            nameof(StateLabel),
            nameof(ToggleLabel),
            nameof(Metadata),
            nameof(IsDirty),
            nameof(IsArchived),
            nameof(CanManageAttachments)
        })
            OnPropertyChanged(name);
        _ = LoadAttachmentsAsync();
    }

    internal async Task LoadAttachmentsAsync()
    {
        Attachments.Clear();
        if (_current is null || _attachmentApi is null) return;
        // 捕获 UI SynchronizationContext：回来后仍在 UI 线程改 ObservableCollection。
        var result = await _attachmentApi.ListAsync(_current.Id);
        if (result.IsSuccess)
            foreach (var item in result.Value) Attachments.Add(item);
    }

    internal async Task<TaskItemDto?> AddAttachmentsAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        if (_current is null || _attachmentApi is null) return null;
        TaskItemDto? latest = null;
        foreach (var path in paths)
        {
            var result = await _attachmentApi.AddAsync(new(_current.Id, path, _current.Revision), ct);
            if (result.IsSuccess) { latest = result.Value; _current = result.Value; }
            // 失败时继续处理其他文件
        }
        if (latest is not null) { _ = LoadAttachmentsAsync(); }
        return latest;
    }

    internal async Task<bool> RemoveAttachmentAsync(Guid attachmentId, CancellationToken ct = default)
    {
        if (_current is null || _attachmentApi is null) return false;
        var result = await _attachmentApi.RemoveAsync(new(_current.Id, attachmentId, _current.Revision), ct);
        if (!result.IsSuccess) return false;
        _current = result.Value;
        _ = LoadAttachmentsAsync();
        return true;
    }

    internal async Task<TaskItemDto?> ExportAttachmentAsync(Guid attachmentId, string targetDir, TaskAttachmentTransferMode mode, CancellationToken ct = default)
    {
        if (_current is null || _attachmentApi is null) return null;
        var result = await _attachmentApi.ExportAsync(new(_current.Id, attachmentId, targetDir, mode, _current.Revision), ct);
        if (!result.IsSuccess) return null;
        _current = result.Value;
        _ = LoadAttachmentsAsync();
        return result.Value;
    }

    internal bool Validate()
    {
        _validationErrors = false;
        TitleError = _constraints.TitleRequired && CountCharacters(Title.Trim()) == 0
            ? TaskLocalization.Localize("Tasks.Editor.TitleRequiredError", "请输入任务标题。")
            : TitleExceedsLimit
                ? TaskLocalization.Format("Tasks.Editor.TitleMaxError", "标题最多 {0:N0} 个字符。", _constraints.TitleMaxCharacters)
                : "";
        NotesError = NotesExceedsLimit
            ? TaskLocalization.Format("Tasks.Editor.NotesMaxError", "备注最多 {0:N0} 个字符。", _constraints.NotesMaxCharacters)
            : "";
        _validationErrors = HasTitleError || HasNotesError;
        return !HasTitleError && !HasNotesError;
    }

    internal bool SetFieldError(OperationError error)
    {
        _validationErrors = false;
        TitleError = "";
        NotesError = "";
        if (error.Field == "Title")
        {
            TitleError = TaskErrorLocalizer.Localize(error, _constraints);
            return true;
        }
        if (error.Field == "Notes")
        {
            NotesError = TaskErrorLocalizer.Localize(error, _constraints);
            return true;
        }
        return false;
    }

    private static int CountCharacters(string value) => value.EnumerateRunes().Count();
}
