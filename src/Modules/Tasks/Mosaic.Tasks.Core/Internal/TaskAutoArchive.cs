using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;

namespace Mosaic.Tasks.Core.Internal;

internal sealed class TaskAutoArchive(ITaskRepository repository, ITasksApi api, TimeProvider clock, IDiagnosticSink diagnostics)
{
    private const int BatchSize = 64;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _policyGate = new(1, 1);
    private int _delayDays;
    private CancellationTokenSource? _cancellation;
    private Task _loop = Task.CompletedTask;

    internal Task ApplyAsync(IReadOnlyDictionary<string, object?> values) =>
        SetDelayAsync(values.GetValueOrDefault("tasks.archive.autoEnabled") is true
            ? values.GetValueOrDefault("tasks.archive.delayDays") is int days and >= 1 and <= 365 ? days : 7
            : 0);

    internal async Task SetDelayAsync(int days)
    {
        await _policyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (days == _delayDays) return;
            if (_cancellation is not null)
            {
                await _cancellation.CancelAsync().ConfigureAwait(false);
                await _loop.ConfigureAwait(false);
                _cancellation.Dispose();
                _cancellation = null;
            }
            _delayDays = days;
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
                    await ArchiveDueAsync(cutoff, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    diagnostics.Report("tasks.archive", exception);
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ArchiveDueAsync(DateTimeOffset cutoff, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var candidates = await repository.QueryArchiveCandidatesAsync(cutoff, BatchSize, token).ConfigureAwait(false);
            if (!candidates.IsSuccess)
            {
                diagnostics.Report("tasks.archive.query", new InvalidOperationException(candidates.Error!.Code));
                return;
            }
            var retry = false;
            foreach (var task in candidates.Value)
            {
                token.ThrowIfCancellationRequested();
                var result = await api.SetArchivedAsync(new(task.Id, true, task.Revision), token).ConfigureAwait(false);
                if (result.IsSuccess) continue;
                retry = true;
                if (result.Error!.Code != ErrorCodes.Conflict)
                    diagnostics.Report("tasks.archive.write", new InvalidOperationException(result.Error.Code));
            }
            if (retry || candidates.Value.Count < BatchSize) return;
        }
    }
}
