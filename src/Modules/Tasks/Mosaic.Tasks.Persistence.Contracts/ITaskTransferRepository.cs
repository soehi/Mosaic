using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Persistence.Contracts;

// The replacement is committed as a single transaction: either every supplied row is present
// afterwards or the previous table is untouched. Creation receipts are never deleted here.
public interface ITaskTransferRepository
{
    Task<OperationResult> ReplaceAllAsync(IReadOnlyList<TaskItemDto> tasks, CancellationToken cancellationToken = default);
}
