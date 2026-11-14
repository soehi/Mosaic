using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TaskEditorViewModel : ObservableObject
{
    private string _title = "";
    private string _notes = "";
    private string _titleError = "";
    private string _notesError = "";
    private TaskItemDto? _current;

    public TaskItemDto? Current => _current;
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
    public string Heading => HasTask ? "任务详情" : "新建任务";
    public string StateLabel => _current?.IsCompleted == true ? "已完成" : "未完成";
    public string ToggleLabel => _current?.IsCompleted == true ? "恢复未完成" : "标记完成";
    public string Metadata => _current is null ? "记录一个想法，开始下一步。" : $"创建于 {_current.CreatedAtUtc.ToLocalTime():MM月dd日 HH:mm}  ·  版本 {_current.Revision}";
    public string TitleHelpText => $"必填 · 最多 {TaskConstraints.TitleMaxCharacters:N0} 个字符";
    public string NotesHelpText => $"可选 · 最多 {TaskConstraints.NotesMaxCharacters:N0} 个字符";
    public string TitleCountLabel => $"{CountCharacters(Title):N0} / {TaskConstraints.TitleMaxCharacters:N0}";
    public string NotesCountLabel => $"{CountCharacters(Notes):N0} / {TaskConstraints.NotesMaxCharacters:N0}";
    public bool TitleExceedsLimit => CountCharacters(Title) > TaskConstraints.TitleMaxCharacters;
    public bool NotesExceedsLimit => CountCharacters(Notes) > TaskConstraints.NotesMaxCharacters;

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
            nameof(IsDirty)
        })
            OnPropertyChanged(name);
    }

    internal bool Validate()
    {
        TitleError = CountCharacters(Title.Trim()) == 0
            ? "请输入任务标题。"
            : TitleExceedsLimit
                ? $"标题最多 {TaskConstraints.TitleMaxCharacters:N0} 个字符。"
                : "";
        NotesError = NotesExceedsLimit
            ? $"备注最多 {TaskConstraints.NotesMaxCharacters:N0} 个字符。"
            : "";
        return !HasTitleError && !HasNotesError;
    }

    internal bool SetFieldError(OperationError error)
    {
        TitleError = "";
        NotesError = "";
        if (error.Field == "Title")
        {
            TitleError = error.Message;
            return true;
        }
        if (error.Field == "Notes")
        {
            NotesError = error.Message;
            return true;
        }
        return false;
    }

    private static int CountCharacters(string value) => value.EnumerateRunes().Count();
}
