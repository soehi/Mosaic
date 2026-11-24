using Mosaic.Contracts;
using Mosaic.Storage.Sqlite;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Persistence.Contracts;
using Mosaic.Tasks.Sqlite;
using Mosaic.TestSupport;

namespace Mosaic.Tasks.Contract.Tests;

public sealed class RepositoryContractTests
{
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string id, Exception ex) { } }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repository_contract_pages_projects_and_counts_rows(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        await using var storage = new SqliteModule(new(path), new Diagnostics());
        ITaskRepository repo;
        TasksSqliteModule? adapter = null;
        if (sqlite) { await storage.ActivateAsync(); adapter = new(storage.Session); await adapter.ActivateAsync(); repo = adapter.Repository; }
        else repo = new MemoryTaskRepository();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var items = Enumerable.Range(0, 5)
            .Select(index => new TaskItemDto(Guid.NewGuid(), $"任务 {index}", $"备注 {index}", index == 2, start.AddMinutes(index), start.AddMinutes(index), 1))
            .ToArray();
        foreach (var item in items) Assert.True((await repo.InsertAsync(item)).IsSuccess, item.Title);

        var page = await repo.QueryAsync(new(TaskFilter.All, TaskSortOrder.CreatedAsc) { Limit = 2, Offset = 1, IncludeNotes = false });
        Assert.Equal(new[] { items[1].Id, items[2].Id }, page.Value.Select(task => task.Id).ToArray());
        Assert.All(page.Value, task => Assert.Equal("", task.Notes));

        var all = await repo.QueryAsync(new(TaskFilter.All, TaskSortOrder.CreatedAsc) { IncludeNotes = false });
        Assert.Equal(5, all.Value.Count);
        Assert.All(all.Value, task => Assert.Equal("", task.Notes));

        Assert.Equal(new TaskCountsDto(5, 4), (await repo.QueryCountsAsync(new())).Value);
        Assert.Equal(new TaskCountsDto(1, 0), (await repo.QueryCountsAsync(new(TaskFilter.Completed))).Value);
        if (adapter is not null) await adapter.DisposeAsync();
        await storage.StopAsync();
        if (sqlite) File.Delete(path);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repository_contract_supports_sort_filter_atomic_version_and_delete(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), "Mosaic-tests", Guid.NewGuid() + ".sqlite");
        await using var storage = new SqliteModule(new(path), new Diagnostics());
        ITaskRepository repo;
        TasksSqliteModule? adapter = null;
        if (sqlite) { await storage.ActivateAsync(); adapter = new(storage.Session); await adapter.ActivateAsync(); repo = adapter.Repository; }
        else repo = new MemoryTaskRepository();
        var now = DateTimeOffset.UtcNow;
        var a = new TaskItemDto(Guid.Parse("00000000-0000-0000-0000-000000000002"), "中文 ' ; DROP TABLE tasks;", "notes", false, now, now, 1);
        var b = a with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), IsCompleted = true };
        Assert.True((await repo.InsertAsync(a)).IsSuccess); Assert.True((await repo.InsertAsync(b)).IsSuccess);
        Assert.Null((await repo.FindAsync(Guid.NewGuid())).Value);
        Assert.Equal(a, (await repo.FindAsync(a.Id)).Value);
        Assert.Equal(new[] { b.Id, a.Id }, (await repo.QueryAsync(new())).Value.Select(t => t.Id));
        Assert.Single((await repo.QueryAsync(new(TaskFilter.Open))).Value);
        Assert.Single((await repo.QueryAsync(new(TaskFilter.Completed))).Value);
        var outcomes = await Task.WhenAll(repo.ReplaceAsync(a with { Title = "first", Revision = 2 }, 1), repo.ReplaceAsync(a with { Title = "second", Revision = 2 }, 1));
        Assert.Single(outcomes, r => r.IsSuccess); Assert.Single(outcomes, r => r.Error?.Code == ErrorCodes.Conflict);
        Assert.Equal(ErrorCodes.Conflict, (await repo.DeleteAsync(a.Id, 1)).Error?.Code);
        Assert.True((await repo.DeleteAsync(a.Id, 2)).IsSuccess);
        Assert.Equal(ErrorCodes.Conflict, (await repo.DeleteAsync(a.Id, 2)).Error?.Code);
        if (adapter is not null) await adapter.DisposeAsync();
        await storage.StopAsync();
        if (sqlite)
        {
            await using var reopened = new SqliteModule(new(path), new Diagnostics());
            await reopened.ActivateAsync();
            await using var mapped = new TasksSqliteModule(reopened.Session); await mapped.ActivateAsync();
            Assert.Equal(b, (await mapped.Repository.FindAsync(b.Id)).Value);
            await reopened.StopAsync();
            File.Delete(path);
        }
    }
}
