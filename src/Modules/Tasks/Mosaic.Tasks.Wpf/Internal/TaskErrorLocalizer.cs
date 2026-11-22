using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

internal static class TaskErrorLocalizer
{
    internal static string Localize(OperationError error, TaskConstraintsDto? constraints = null)
    {
        return error.MessageKey switch
        {
            "Tasks.Error.TitleRequired" => TaskLocalization.Localize(
                "Tasks.Editor.TitleRequiredError", "请输入任务标题。"),
            "Tasks.Error.TitleTooLong" when constraints is not null => TaskLocalization.Format(
                "Tasks.Editor.TitleMaxError", "标题最多 {0:N0} 个字符。", constraints.TitleMaxCharacters),
            "Tasks.Error.NotesTooLong" when constraints is not null => TaskLocalization.Format(
                "Tasks.Editor.NotesMaxError", "备注最多 {0:N0} 个字符。", constraints.NotesMaxCharacters),
            "Tasks.Error.NotFound" => TaskLocalization.Localize(
                "Tasks.Error.NotFound", "任务不存在，可能已被删除。"),
            "Tasks.Error.Conflict" => TaskLocalization.Localize(
                "Tasks.Error.Conflict", "任务已更改或删除。请重新加载后再保存。"),
            "Tasks.Attachments.Error.TooLarge" when constraints is not null => TaskLocalization.Format(
                "Tasks.Attachments.Error.TooLarge", "附件超过 {0} MB 大小限制。", constraints.AttachmentMaxFileMegabytes),
            "Tasks.Attachments.Error.TooMany" when constraints is not null => TaskLocalization.Format(
                "Tasks.Attachments.Error.TooMany", "每个任务最多 {0} 个附件。", constraints.AttachmentMaxPerTask),
            "Tasks.Attachments.Error.ContentMissing" => TaskLocalization.Localize(
                "Tasks.Attachments.Error.ContentMissing", "附件内容已丢失。"),
            "Tasks.Attachments.Error.TargetExists" => TaskLocalization.Localize(
                "Tasks.Attachments.Error.TargetExists", "目标文件夹已存在同名文件。"),
            "Tasks.Attachments.Error.WriteFailed" => TaskLocalization.Localize(
                "Tasks.Attachments.Error.WriteFailed", "无法写入目标文件夹，请检查权限后重试。"),
            "Tasks.Attachments.Error.NotFound" => TaskLocalization.Localize(
                "Tasks.Attachments.Error.NotFound", "附件不存在。"),
            "Tasks.Attachments.Error.StoreUnavailable" => TaskLocalization.Localize(
                "Tasks.Attachments.Error.StoreUnavailable", "附件存储不可用，请重启应用后重试。"),
            _ => ContainsHan(error.Message) ? Generic(error) : error.Message
        };
    }

    private static string Generic(OperationError error)
    {
        var key = error.Code switch
        {
            ErrorCodes.StorageUnavailable => "Tasks.Error.StorageUnavailable",
            ErrorCodes.CapabilityUnavailable => "Tasks.Error.CapabilityUnavailable",
            ErrorCodes.Canceled => "Tasks.Error.Canceled",
            ErrorCodes.InternalError => "Tasks.Error.Internal",
            _ => "Tasks.Error.Internal"
        };
        return TaskLocalization.Localize(key, error.Message);
    }

    private static bool ContainsHan(string text) =>
        text.Any(character => character is >= '\u3400' and <= '\u9fff');
}
