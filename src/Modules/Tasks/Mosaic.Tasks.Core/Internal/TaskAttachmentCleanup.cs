using Mosaic.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Core.Internal;

internal sealed class TaskAttachmentCleanup(ITaskAttachmentRepository repository, ITaskAttachmentBlobStore blobs, TimeProvider clock, IDiagnosticSink diagnostics)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _policyGate = new(1, 1);
    private int _days;
    private CancellationTokenSource? _cancellation;
    private Task _loop = Task.CompletedTask;

    internal Task ApplyAsync(IReadOnlyDictionary<string, object?> values) =>
        SetDaysAsync(values.GetValueOrDefault("tasks.attachments.archiveCleanupDays") is int days and >= 1 and <= 365 ? days : 0);

    internal async Task SetDaysAsync(int days)
    {
        await _policyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (days == _days) return;
            if (_cancellation is not null)
            {
                await _cancellation.CancelAsync().ConfigureAwait(false);
                await _loop.ConfigureAwait(false);
                _cancellation.Dispose();
                _cancellation = null;
            }
            _days = days;
            if (days == 0) return;
            _cancellation = new();
            var token = _cancellation.Token;
            _loop = Task.Run(() => RunAsync(TimeSpan.FromDays(days), token));
        }
        finally { _policyGate.Release(); }
    }

    private async Task RunAsync(TimeSpan delay, CancellationToken token)
    {
        using var timer = new PeriodicTimer(PollInterval, clock);
        try
        {
            do
            {
                try
                {
                    var now = clock.GetUtcNow();
                    var cutoff = now - DateTimeOffset.MinValue < delay ? DateTimeOffset.MinValue : now - delay;
                    await CleanupDueAsync(cutoff, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    diagnostics.Report("tasks.attachments.cleanup", exception);
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task CleanupDueAsync(DateTimeOffset cutoff, CancellationToken token)
    {
        var candidates = await repository.ListExpiredArchivedAsync(cutoff, token).ConfigureAwait(false);
        if (!candidates.IsSuccess)
        {
            diagnostics.Report("tasks.attachments.cleanup.query", new InvalidOperationException(candidates.Error!.Code));
            return;
        }
        foreach (var attachment in candidates.Value)
        {
            token.ThrowIfCancellationRequested();
            var path = await repository.FindStoredPathAsync(attachment.Id, token).ConfigureAwait(false);
            var deleted = await repository.DeleteAsync(attachment.Id, token).ConfigureAwait(false);
            if (!deleted.IsSuccess)
            {
                diagnostics.Report("tasks.attachments.cleanup.delete", new InvalidOperationException(deleted.Error!.Code));
                continue;
            }
            var removed = await blobs.DeleteAsync(attachment.TaskId, attachment.Id, attachment.FileName,
                path.IsSuccess ? path.Value : null, token).ConfigureAwait(false);
            if (!removed.IsSuccess)
                diagnostics.Report("tasks.attachments.cleanup.file", new InvalidOperationException(removed.Error!.Code));
        }
    }
}
