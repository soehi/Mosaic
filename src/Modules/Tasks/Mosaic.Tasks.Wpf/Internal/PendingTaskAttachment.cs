namespace Mosaic.Tasks.Wpf.Internal;

// 新建任务时尚未落库的附件：只记录来源路径与展示信息，保存草稿后统一落库。
internal sealed record PendingTaskAttachment(string SourcePath, string FileName, long SizeBytes);
