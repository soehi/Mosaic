using Mosaic.Contracts;
using Mosaic.FloatingWindows.Contracts;
using Mosaic.FloatingWindows.Core;
using Mosaic.FloatingWindows.Persistence.Contracts;
using Mosaic.FloatingWindows.Sqlite;
using Mosaic.Storage.Sqlite;
namespace Mosaic.FloatingWindows.Tests;
public sealed class FloatingTests
{
    private sealed class Diagnostics:IDiagnosticSink {public void Report(string id,Exception exception){}}
    private sealed class Repository:IFloatingWindowRepository
    {
        internal Dictionary<FloatingContentReference,FloatingWindowRecord> Records=[];
        internal bool Fail;
        public Task<OperationResult<IReadOnlyList<FloatingWindowRecord>>> LoadAsync(CancellationToken cancellationToken=default)=>Task.FromResult(OperationResult<IReadOnlyList<FloatingWindowRecord>>.Success(Records.Values.ToArray()));
        public Task<OperationResult> SaveAsync(FloatingWindowRecord record,long expectedRevision,CancellationToken cancellationToken=default)
        {
            if(Fail)return Task.FromResult(OperationResult.Failure(new(ErrorCodes.StorageUnavailable,"disk full")));
            if((Records.GetValueOrDefault(record.Reference)?.Revision??0)!=expectedRevision)return Task.FromResult(OperationResult.Failure(new(ErrorCodes.Conflict,"stale")));
            Records[record.Reference]=record;return Task.FromResult(OperationResult.Success());
        }
    }
    private sealed class Surface:IFloatingWindowSurface
    {
        internal int Created,Activated,Destroyed,PreviewBegins,PreviewUpdates,PreviewEnds;
        internal string? Error;
        internal Func<Task>? Wait;
        internal Func<FloatingContentReference, Task<OperationResult>>? Prepare;
        internal int Preparations, Releases;
        public Task<OperationResult> PrepareCloseAsync(FloatingContentReference reference, CancellationToken ct)
        {
            Preparations++;
            return Prepare?.Invoke(reference) ?? Task.FromResult(OperationResult.Success());
        }
        public Task CancelClosePreparationAsync(FloatingContentReference reference)
        {
            Releases++;
            return Task.CompletedTask;
        }
        public FloatingWindowRecord CreateRecord(FloatingContentReference reference,FloatingPlacementHint? hint)=>
            hint is null?Record(reference):Record(reference) with{MonitorId="hint-monitor",X=hint.ScreenX,Y=hint.ScreenY};
        public async Task<OperationResult> ShowAsync(FloatingWindowRecord record,CancellationToken cancellationToken){Created++;if(Wait is not null)await Wait();return Error is null?OperationResult.Success():OperationResult.Failure(new(Error,"read failure"));}
        public void Activate(FloatingContentReference reference)=>Activated++;
        public Task DestroyAsync(FloatingContentReference reference){Destroyed++;return Task.CompletedTask;}
        public FloatingWindowRecord Snapshot(FloatingWindowRecord record)=>record;
        public void Apply(FloatingWindowRecord record){}
        public void BeginPreview(FloatingContentReference reference,FloatingPlacementHint hint)=>PreviewBegins++;
        public void UpdatePreview(FloatingPlacementHint hint)=>PreviewUpdates++;
        public void EndPreview()=>PreviewEnds++;
    }
    private static FloatingContentReference Reference(string key="a")=>new("provider",key);
    private static FloatingWindowRecord Record(FloatingContentReference? reference=null)=>new(reference??Reference(),true,FloatingDisplayMode.Compact,320,400,360,"primary",24,24,true);
    [Fact]
    public async Task Concurrent_requests_merge_while_opening_and_limit_does_not_discard_restoration()
    {
        var repo=new Repository();var surface=new Surface();var ready=new TaskCompletionSource();surface.Wait=()=>ready.Task;
        var core=new FloatingWindowCoordinator(repo,surface,()=>1,new Diagnostics());
        var first=core.OpenAsync(Reference());var second=core.OpenAsync(Reference());ready.SetResult();
        var results=await Task.WhenAll(first,second);Assert.All(results,result=>Assert.True(result.IsSuccess));
        Assert.Equal(1,surface.Created);Assert.Equal(1,surface.Activated);Assert.Single(repo.Records);
        Assert.False((await core.OpenAsync(Reference("b"))).IsSuccess);
        Assert.True((await core.PrepareExitAsync()).IsSuccess);
        Assert.False((await core.OpenAsync(Reference("b"))).IsSuccess);
        await core.StopAsync();Assert.True(repo.Records[Reference()].IsOpen);
        var restored=new Surface();var next=new FloatingWindowCoordinator(repo,restored,()=>1,new Diagnostics());await next.RestoreAsync();Assert.Equal(1,restored.Created);
        Assert.True((await next.CloseAsync(Reference())).IsSuccess);Assert.False(repo.Records[Reference()].IsOpen);
        var afterClose=new Surface();await new FloatingWindowCoordinator(repo,afterClose,()=>1,new Diagnostics()).RestoreAsync();Assert.Equal(0,afterClose.Created);
    }
    [Fact]
    public async Task Close_confirmation_is_shared_and_does_not_block_other_windows()
    {
        var repository = new Repository();
        var surface = new Surface();
        var core = new FloatingWindowCoordinator(repository, surface, () => 8, new Diagnostics());
        await core.OpenAsync(Reference());
        var choice = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        surface.Prepare = _ => choice.Task;
        var first = core.CloseAsync(Reference());
        var duplicate = core.CloseAsync(Reference());
        try
        {
            Assert.False(first.IsCompleted);
            Assert.True((await core.OpenAsync(Reference("b")).WaitAsync(TimeSpan.FromSeconds(2))).IsSuccess);
            Assert.Equal(1, surface.Preparations);
        }
        finally { choice.SetResult(OperationResult.Failure(new(ErrorCodes.Canceled, "cancel"))); }
        Assert.All(await Task.WhenAll(first, duplicate), result => Assert.Equal(ErrorCodes.Canceled, result.Error?.Code));
        Assert.True(repository.Records[Reference()].IsOpen);
        Assert.Equal(1, repository.Records[Reference()].Revision);
        Assert.Equal(0, surface.Destroyed);
        surface.Prepare = null;
        Assert.True((await core.CloseAsync(Reference())).IsSuccess);
        Assert.False(repository.Records[Reference()].IsOpen);
    }

    [Fact]
    public async Task Exit_cancellation_releases_prepared_content_and_allows_new_windows()
    {
        var repository = new Repository();
        var surface = new Surface { Prepare = reference => Task.FromResult(reference == Reference("b")
            ? OperationResult.Failure(new(ErrorCodes.Canceled, "cancel")) : OperationResult.Success()) };
        var core = new FloatingWindowCoordinator(repository, surface, () => 8, new Diagnostics());
        await core.OpenAsync(Reference());
        await core.OpenAsync(Reference("b"));
        Assert.Equal(ErrorCodes.Canceled, (await core.PrepareExitAsync()).Error?.Code);
        Assert.Equal(2, surface.Preparations);
        Assert.Equal(2, surface.Releases);
        Assert.All(repository.Records.Values, record => Assert.Equal(1, record.Revision));
        Assert.True((await core.OpenAsync(Reference("c"))).IsSuccess);
    }

    [Fact]
    public async Task Open_close_and_exit_failures_preserve_visible_window_and_allow_retry()
    {
        var repo=new Repository{Fail=true};var surface=new Surface();var core=new FloatingWindowCoordinator(repo,surface,()=>8,new Diagnostics());
        Assert.False((await core.OpenAsync(Reference())).IsSuccess);Assert.Equal(0,surface.Created);
        repo.Fail=false;Assert.True((await core.OpenAsync(Reference())).IsSuccess);
        repo.Fail=true;Assert.False((await core.CloseAsync(Reference())).IsSuccess);Assert.Equal(0,surface.Destroyed);
        Assert.False((await core.PrepareExitAsync()).IsSuccess);
        repo.Fail=false;Assert.True((await core.OpenAsync(Reference())).IsSuccess);Assert.Equal(1,surface.Activated);
        Assert.True((await core.CloseAsync(Reference())).IsSuccess);Assert.Equal(1,surface.Destroyed);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Confirmed_missing_clears_restore_read_failure_keeps_it(bool missing)
    {
        var repo=new Repository();repo.Records[Reference()]=Record() with{Revision=1};
        var surface=new Surface{Error=missing?ErrorCodes.NotFound:ErrorCodes.StorageUnavailable};
        await new FloatingWindowCoordinator(repo,surface,()=>8,new Diagnostics()).RestoreAsync();
        Assert.Equal(!missing,repo.Records[Reference()].IsOpen);Assert.Equal(1,surface.Destroyed);
    }
    [Fact]
    public async Task Mode_dimensions_and_pin_are_separate_and_failed_save_does_not_apply()
    {
        var repo=new Repository();var surface=new Surface();var core=new FloatingWindowCoordinator(repo,surface,()=>8,new Diagnostics());await core.OpenAsync(Reference());
        await core.ChangeAsync(Reference(),record=>record with{Mode=FloatingDisplayMode.Expanded,ExpandedWidth=500,ExpandedHeight=600,IsTopmost=false});
        var expanded=repo.Records[Reference()];Assert.Equal(320,expanded.CompactWidth);Assert.Equal(500,expanded.ExpandedWidth);Assert.False(expanded.IsTopmost);
        repo.Fail=true;Assert.False((await core.ChangeAsync(Reference(),record=>record with{CompactWidth=600})).IsSuccess);
        Assert.Equal(expanded,repo.Records[Reference()]);
        repo.Fail=false;await core.ChangeAsync(Reference(),record=>record with{Mode=FloatingDisplayMode.Compact});Assert.Equal(600,repo.Records[Reference()].ExpandedHeight);
    }
    [Fact]
    public async Task Restore_limit_keeps_excess_records_and_controlled_exit_can_skip_failed_save()
    {
        var repo=new Repository();repo.Records[Reference()]=Record() with{Revision=1};repo.Records[Reference("b")]=Record(Reference("b")) with{Revision=1};
        var surface=new Surface();var core=new FloatingWindowCoordinator(repo,surface,()=>1,new Diagnostics());await core.RestoreAsync();Assert.Equal(1,surface.Created);Assert.Equal(2,repo.Records.Count);
        Assert.All(repo.Records.Values,record=>Assert.True(record.IsOpen));repo.Fail=true;await core.StopWithoutSavingAsync();Assert.Equal(1,surface.Destroyed);
    }
    [Fact]
    public async Task Sqlite_records_survive_reopen_reject_stale_write_and_do_not_touch_tasks()
    {
        var path=Path.Combine(Path.GetTempPath(),"Mosaic-tests",Guid.NewGuid()+".sqlite");
        await using(var storage=new SqliteModule(new(path),new Diagnostics()))
        {
            await storage.ActivateAsync();await using var module=new FloatingWindowsSqliteModule(storage.Session);await module.ActivateAsync();
            var record=Record() with{Revision=1};Assert.True((await module.Repository.SaveAsync(record,0)).IsSuccess);
            Assert.Equal(ErrorCodes.Conflict,(await module.Repository.SaveAsync(record,0)).Error?.Code);
            Assert.True((await module.Repository.SaveAsync(record with{IsOpen=false,Revision=2},1)).IsSuccess);
            var tables=(await storage.Session.QueryAsync(new("SELECT name FROM sqlite_master WHERE type='table' AND name='tasks'"))).Value;Assert.Empty(tables);
        }
        await using(var storage=new SqliteModule(new(path),new Diagnostics()))
        {
            await storage.ActivateAsync();await using var module=new FloatingWindowsSqliteModule(storage.Session);await module.ActivateAsync();
            var record=Assert.Single((await module.Repository.LoadAsync()).Value);Assert.False(record.IsOpen);Assert.Equal(2,record.Revision);
            await storage.Session.ExecuteAsync(new("UPDATE floating_windows SET schema_version=999"));
            Assert.False((await module.Repository.LoadAsync()).IsSuccess);
        }
        File.Delete(path);
    }
    [Fact]
    public async Task Window_migration_failure_preserves_existing_data_and_can_retry()
    {
        var path=Path.Combine(Path.GetTempPath(),"Mosaic-tests",Guid.NewGuid()+".sqlite");
        await using(var storage=new SqliteModule(new(path),new Diagnostics()))
        {
            await storage.ActivateAsync();
            await storage.Session.ExecuteAsync(new("CREATE TABLE floating_windows(sentinel TEXT NOT NULL)"));
            await storage.Session.ExecuteAsync(new("INSERT INTO floating_windows VALUES('keep')"));
            await using var module=new FloatingWindowsSqliteModule(storage.Session);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>module.ActivateAsync());
            Assert.Equal("keep",Assert.Single((await storage.Session.QueryAsync(new("SELECT sentinel FROM floating_windows"))).Value).String("sentinel"));
            Assert.Empty((await storage.Session.QueryAsync(new("SELECT version FROM mosaic_migrations WHERE module_id='floating-windows-sqlite'"))).Value);
            Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!,Path.GetFileName(path)+".backup-*.sqlite"));
            await storage.Session.ExecuteAsync(new("DROP TABLE floating_windows"));await module.ActivateAsync();Assert.Empty((await module.Repository.LoadAsync()).Value);
        }
        File.Delete(path);
    }

    [Fact]
    public async Task Detach_hint_overrides_saved_position_but_keeps_existing_sizes_and_forwards_preview()
    {
        var repo=new Repository();
        var saved=Record() with{CompactWidth=240,X=24,Y=24,Revision=1,IsOpen=false};
        repo.Records[Reference()]=saved;
        var surface=new Surface();var core=new FloatingWindowCoordinator(repo,surface,()=>8,new Diagnostics());
        var hint=new FloatingPlacementHint(500,400,10,10);
        core.BeginPlacementPreview(Reference(),hint);core.UpdatePlacementPreview(hint with{ScreenX=520});
        Assert.Equal(1,surface.PreviewBegins);Assert.Equal(1,surface.PreviewUpdates);
        Assert.True((await core.OpenAsync(Reference(),hint)).IsSuccess);
        var opened=repo.Records[Reference()];
        Assert.Equal(500,opened.X);Assert.Equal(400,opened.Y);Assert.Equal("hint-monitor",opened.MonitorId);
        Assert.Equal(240,opened.CompactWidth);Assert.Equal(400,opened.ExpandedWidth);
        core.EndPlacementPreview();Assert.Equal(1,surface.PreviewEnds);
        Assert.True((await core.PrepareExitAsync()).IsSuccess);
        core.BeginPlacementPreview(Reference(),hint);Assert.Equal(1,surface.PreviewBegins);
    }

    [Fact]
    public async Task Cancellation_while_opening_cleans_window_and_restore_eligibility()
    {
        using var cancellation=new CancellationTokenSource();var repo=new Repository();var surface=new Surface{Wait=()=>Task.Delay(Timeout.Infinite,cancellation.Token)};
        var core=new FloatingWindowCoordinator(repo,surface,()=>8,new Diagnostics());var pending=core.OpenAsync(Reference(),cancellationToken:cancellation.Token);
        cancellation.Cancel();var result=await pending;Assert.False(result.IsSuccess);Assert.Equal(ErrorCodes.Canceled,result.Error?.Code);Assert.Equal(1,surface.Destroyed);Assert.False(repo.Records[Reference()].IsOpen);
    }

}
