namespace Mosaic.Tasks.Contracts;
public enum TaskChangeKind { Created, Updated, Deleted }
public sealed record TaskChange(Guid TaskId,long Revision,TaskChangeKind Kind);
public interface ITaskChangeSource { IDisposable Subscribe(Action<TaskChange> observer); }
