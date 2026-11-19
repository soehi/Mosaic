using Mosaic.Contracts;

namespace Mosaic.Tasks.Contracts;

// Explicit cross-device transfer capability. Export reuses the same listing rules as the UI;
// ReplaceAllAsync overwrites the local task table in one transaction with the supplied rows,
// preserving identifiers, revisions, archive timestamps and creation receipts.
public interface ITaskTransferApi
{
    Task<OperationResult<IReadOnlyList<TaskItemDto>>> ExportAllAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> ReplaceAllAsync(IReadOnlyList<TaskItemDto> tasks, CancellationToken cancellationToken = default);
}
