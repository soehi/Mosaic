using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TaskEditorViewModel : ObservableObject
{
    private readonly ITaskAttachmentApi? _attachmentApi;
    private readonly Mosaic.Presentation.Contracts.IUiDispatcher? _dispatcher;
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

    internal TaskEditorViewModel(ITaskAttachmentApi? attachmentApi = null, Mosaic.Presentation.Contracts.IUiDispatcher? dispatcher = null)
    {
        _attachmentApi = attachmentApi;
        _dispatcher = dispatcher;
    }

    /// <summary>把后台线程的界面操作切回 UI 线程执行。</summary>
    internal Task DispatcherInvoke(Action action) =>
        _dispatcher is null ? Task.Run(action) : _dispatcher.InvokeAsync(action);

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
    private string _attachmentStatus = "";
    public string AttachmentStatus
    {
        get => _attachmentStatus;
        private set { if (SetProperty(ref _attachmentStatus, value)) OnPropertyChanged(nameof(HasAttachmentStatus)); }
    }
    public bool HasAttachmentStatus => !string.IsNullOrEmpty(AttachmentStatus);
    public bool IsDirty => Title != (_current?.Title ?? "") || Notes != (_current?.Notes ?? "");
    public bool HasTask => _current is not null;
    public bool IsArchived => _current?.IsArchived == true;
    public bool CanManageAttachments => !IsArchived && _attachmentApi is not null;
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
        PendingAttachments.Clear();
        OnPropertyChanged(nameof(HasAttachments));
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

    /// <summary>附件操作会推进任务 Revision；只重通知依赖当前记录的属性，不重置标题/备注输入。</summary>
    private void OnCurrentChanged()
    {
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Metadata));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsArchived));
        OnPropertyChanged(nameof(CanManageAttachments));
    }

    internal async Task LoadAttachmentsAsync()
    {
        Attachments.Clear();
        if (_current is null || _attachmentApi is null) return;
        // 捕获 UI SynchronizationContext：回来后仍在 UI 线程改 ObservableCollection。
        var result = await _attachmentApi.ListAsync(_current.Id);
        if (!result.IsSuccess)
        {
            AttachmentStatus = TaskErrorLocalizer.Localize(result.Error!, _constraints);
            return;
        }
        foreach (var item in result.Value) Attachments.Add(item);
    }

    // 新建模式：任务尚未保存，先加入暂存清单，保存草稿后随任务一起落库。
    public ObservableCollection<PendingTaskAttachment> PendingAttachments { get; } = new();
    public bool HasAttachments => Attachments.Count > 0 || PendingAttachments.Count > 0;

    internal async Task<TaskItemDto?> AddAttachmentsAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        if (_attachmentApi is null) return null;
        if (_current is null)
        {
            // 草稿模式：先暂存，保存任务后由 FlushPendingAttachmentsAsync 落库。
            foreach (var path in paths)
            {
                if (PendingAttachments.Count >= _constraints.AttachmentMaxPerTask) break;
                if (!File.Exists(path)) continue;
                var info = new FileInfo(path);
                if (info.Length > _constraints.AttachmentMaxFileMegabytes * 1024L * 1024L) continue;
                if (PendingAttachments.Any(pending => string.Equals(pending.SourcePath, path, StringComparison.OrdinalIgnoreCase))) continue;
                PendingAttachments.Add(new PendingTaskAttachment(path, Path.GetFileName(path), info.Length));
            }
            OnPropertyChanged(nameof(HasAttachments));
            return null;
        }
        AttachmentStatus = "";
        TaskItemDto? latest = null;
        var failed = 0;
        OperationError? lastError = null;
        try
        {
            foreach (var path in paths)
            {
                // 取消后继续逐个调用只会刷一串 CANCELED：直接停下。
                if (ct.IsCancellationRequested) { lastError ??= new(ErrorCodes.Canceled, "操作已取消。"); failed++; break; }
                var result = await _attachmentApi.AddAsync(new(_current.Id, path, _current.Revision), ct);
                if (result.IsSuccess) { latest = result.Value; _current = result.Value; }
                else { failed++; lastError = result.Error; }
            }
        }
        finally
        {
            if (failed > 0 && lastError is not null) ReportAddFailure(failed, latest is not null, lastError);
            if (latest is not null) { OnCurrentChanged(); _ = LoadAttachmentsAsync(); }
        }
        OnPropertyChanged(nameof(HasAttachments));
        return latest;
    }

    // 草稿保存成功后由 TasksViewModel 调用：逐个落库暂存附件并清空清单。
    internal async Task FlushPendingAttachmentsAsync(TaskItemDto created, CancellationToken ct = default)
    {
        if (PendingAttachments.Count == 0) return;
        var pending = PendingAttachments.ToArray();
        PendingAttachments.Clear();
        OnPropertyChanged(nameof(HasAttachments));
        if (_attachmentApi is null) return;
        AttachmentStatus = "";
        var current = created;
        var failed = 0;
        OperationError? lastError = null;
        var succeeded = 0;
        foreach (var item in pending)
        {
            if (ct.IsCancellationRequested) { lastError ??= new(ErrorCodes.Canceled, "操作已取消。"); failed++; break; }
            var result = await _attachmentApi.AddAsync(new(created.Id, item.SourcePath, current.Revision), ct);
            if (!result.IsSuccess) { failed++; lastError = result.Error; continue; }
            current = result.Value;
            succeeded++;
        }
        _current = current;
        if (failed > 0 && lastError is not null) ReportAddFailure(failed, succeeded > 0, lastError);
        OnCurrentChanged();
        await LoadAttachmentsAsync();
        OnPropertyChanged(nameof(HasAttachments));
    }

    private void ReportAddFailure(int failed, bool partiallySucceeded, OperationError lastError)
    {
        var detail = TaskErrorLocalizer.Localize(lastError, _constraints);
        var count = failed > 1
            ? " " + TaskLocalization.Format("Tasks.Attachments.Error.WithCount", "（本次失败 {0} 项）", failed)
            : "";
        AttachmentStatus = partiallySucceeded || failed > 1
            ? $"{TaskLocalization.Localize("Tasks.Attachments.AddFailed", "部分附件添加失败")}：{detail}{count}"
            : $"{detail}{count}";
    }

    internal async Task<bool> RemoveAttachmentAsync(Guid attachmentId, CancellationToken ct = default)
    {
        if (_current is null || _attachmentApi is null) return false;
        var result = await _attachmentApi.RemoveAsync(new(_current.Id, attachmentId, _current.Revision), ct);
        if (!result.IsSuccess)
        {
            AttachmentStatus = TaskErrorLocalizer.Localize(result.Error!, _constraints);
            return false;
        }
        AttachmentStatus = "";
        _current = result.Value;
        OnCurrentChanged();
        _ = LoadAttachmentsAsync();
        return true;
    }

    /// <summary>把一次附件操作失败转成已本地化的界面提示。</summary>
    internal void ReportAttachmentFailure(OperationError error) =>
        AttachmentStatus = TaskErrorLocalizer.Localize(error, _constraints);

    /// <summary>打开附件只读流；调用方负责释放。路径解析完全在附件模块内部完成。</summary>
    internal async Task<OperationResult<Stream>> OpenAttachmentAsync(Guid attachmentId, CancellationToken ct = default)
    {
        if (_current is null || _attachmentApi is null)
            return OperationResult<Stream>.Failure(new(ErrorCodes.CapabilityUnavailable, "任务功能尚未启用或正在关闭。"));
        return await _attachmentApi.OpenReadAsync(_current.Id, attachmentId, ct);
    }

    internal async Task<TaskItemDto?> ExportAttachmentAsync(Guid attachmentId, string targetDir, TaskAttachmentTransferMode mode, CancellationToken ct = default)
    {
        if (_current is null || _attachmentApi is null) return null;
        var result = await _attachmentApi.ExportAsync(new(_current.Id, attachmentId, targetDir, mode, _current.Revision), ct);
        if (!result.IsSuccess)
        {
            var detail = TaskErrorLocalizer.Localize(result.Error!, _constraints);
            AttachmentStatus = $"{TaskLocalization.Localize("Tasks.Attachments.ExportFailed", "附件导出失败")}：{detail}";
            return null;
        }
        AttachmentStatus = "";
        _current = result.Value;
        OnCurrentChanged();
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
