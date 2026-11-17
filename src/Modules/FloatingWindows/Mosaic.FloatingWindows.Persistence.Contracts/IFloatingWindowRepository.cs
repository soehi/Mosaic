using Mosaic.Contracts;
using Mosaic.FloatingWindows.Contracts;
namespace Mosaic.FloatingWindows.Persistence.Contracts;
public interface IFloatingWindowRepository
{
    Task<OperationResult<IReadOnlyList<FloatingWindowRecord>>> LoadAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> SaveAsync(FloatingWindowRecord record, long expectedRevision, CancellationToken cancellationToken = default);
}
