namespace Mosaic.Tasks.Contracts;
// Replaced 在整表恢复后只发布一次，TaskId 为空、Revision 为 0；观察者应把它当成整表重载。
public enum TaskChangeKind { Created, Updated, Deleted, Replaced }
public sealed record TaskChange(Guid TaskId,long Revision,TaskChangeKind Kind);
public interface ITaskChangeSource { IDisposable Subscribe(Action<TaskChange> observer); }
