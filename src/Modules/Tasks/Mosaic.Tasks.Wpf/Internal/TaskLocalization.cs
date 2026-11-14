using System.Windows;

namespace Mosaic.Tasks.Wpf.Internal;

internal static class TaskLocalization
{
    public static ResourceDictionary Dictionary(bool english) => new()
    {
        ["Tasks.Header"] = english ? "Today, finish one thing" : "今天，先完成一件事",
        ["Tasks.Subheader"] = english ? "Clear your mind and focus on the next step." : "收好杂念，把下一步放在眼前。",
        ["Tasks.NewTask"] = english ? "＋  New task" : "＋  新建任务",
        ["Tasks.ListTitle"] = english ? "Task list" : "任务清单",
        ["Tasks.FilterAll"] = english ? "All tasks" : "全部任务",
        ["Tasks.FilterOpen"] = english ? "Open" : "未完成",
        ["Tasks.FilterCompleted"] = english ? "Completed" : "已完成",
        ["Tasks.Refresh"] = english ? "Refresh" : "刷新",
        ["Tasks.Loading"] = english ? "Loading tasks…" : "正在读取任务…",
        ["Tasks.LoadError"] = english ? "Could not load the task list" : "任务列表加载失败",
        ["Tasks.Retry"] = english ? "Retry" : "重试",
        ["Tasks.BackToList"] = english ? "‹  Back to tasks" : "‹  返回任务列表",
        ["Tasks.NotSaved"] = english ? "Not saved" : "尚未保存",
        ["Tasks.ConflictTitle"] = english ? "This task changed elsewhere" : "这项任务已在别处更新",
        ["Tasks.ConflictDescription"] = english ? "Your input is preserved. Reload the latest version before editing." : "当前输入会保留。重新加载最新版本后再修改。",
        ["Tasks.ReloadLatest"] = english ? "Reload latest" : "重新加载最新版本",
        ["Tasks.TitleLabel"] = english ? "Task title" : "任务标题",
        ["Tasks.NotesLabel"] = english ? "Notes" : "备注",
        ["Tasks.Save"] = english ? "Save task" : "保存任务",
        ["Tasks.SaveHint"] = english ? "Edit the content to enable saving" : "填写或修改内容后即可保存",
        ["Tasks.Reload"] = english ? "Reload" : "重新加载",
        ["Tasks.Delete"] = english ? "Delete" : "删除",
        ["Tasks.Navigation"] = english ? "Tasks" : "任务"
    };
}
