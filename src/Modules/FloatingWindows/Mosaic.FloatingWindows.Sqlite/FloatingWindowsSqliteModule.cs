using System.Globalization;
using Mosaic.Contracts;
using Mosaic.FloatingWindows.Contracts;
using Mosaic.FloatingWindows.Persistence.Contracts;
using Mosaic.Storage.Contracts;
namespace Mosaic.FloatingWindows.Sqlite;
public sealed class FloatingWindowsSqliteModule(IDatabaseSession database) : IModuleActivation
{
    private bool _active;
    private readonly IFloatingWindowRepository _repository = new FloatingWindowRepository(database);
    public static ModuleManifest Definition { get; } = new("floating-windows-sqlite",[new("floating.repository",1)],[new("sqlite.session",1)],"Owns floating window state SQL and migrations; never reads tasks.");
    public ModuleManifest Manifest=>Definition;
    public IFloatingWindowRepository Repository=>_active?_repository:throw new InvalidOperationException("Floating storage inactive.");
    public async Task ActivateAsync(CancellationToken cancellationToken=default)
    {
        var migrated=await database.MigrateAsync(Definition.Id,[new(1,"CREATE TABLE floating_windows(provider_id TEXT NOT NULL,item_key TEXT NOT NULL,is_open INTEGER NOT NULL,mode INTEGER NOT NULL,compact_width REAL NOT NULL,expanded_width REAL NOT NULL,expanded_height REAL NOT NULL,monitor_id TEXT NOT NULL,x REAL NOT NULL,y REAL NOT NULL,topmost INTEGER NOT NULL,revision INTEGER NOT NULL,schema_version INTEGER NOT NULL,PRIMARY KEY(provider_id,item_key));")],cancellationToken);
        if(!migrated.IsSuccess) throw new InvalidOperationException("悬浮窗状态迁移失败："+migrated.Error!.Message);
        _active=true;
    }
    public Task StopAsync(){_active=false;return Task.CompletedTask;}
    public ValueTask DisposeAsync(){_active=false;return ValueTask.CompletedTask;}
}
internal sealed class FloatingWindowRepository(IDatabaseSession database) : IFloatingWindowRepository
{
    public async Task<OperationResult<IReadOnlyList<FloatingWindowRecord>>> LoadAsync(CancellationToken cancellationToken=default)
    {
        var result=await database.QueryAsync(new("SELECT * FROM floating_windows ORDER BY provider_id,item_key"),cancellationToken);
        if(!result.IsSuccess) return OperationResult<IReadOnlyList<FloatingWindowRecord>>.Failure(result.Error!);
        try
        {
            var records=result.Value.Select(row=>new FloatingWindowRecord(new(row.String("provider_id"),row.String("item_key")),row.Int64("is_open")==1,(FloatingDisplayMode)row.Int64("mode"),Number(row,"compact_width"),Number(row,"expanded_width"),Number(row,"expanded_height"),row.String("monitor_id"),Number(row,"x"),Number(row,"y"),row.Int64("topmost")==1,row.Int64("revision"),(int)row.Int64("schema_version"))).ToArray();
            if(records.Any(record=>!Valid(record))) return OperationResult<IReadOnlyList<FloatingWindowRecord>>.Failure(new(ErrorCodes.StorageUnavailable,"悬浮窗记录损坏，已保留原始数据。"));
            return OperationResult<IReadOnlyList<FloatingWindowRecord>>.Success(records);
        }
        catch(Exception ex) when(ex is FormatException or InvalidCastException or OverflowException) {return OperationResult<IReadOnlyList<FloatingWindowRecord>>.Failure(new(ErrorCodes.StorageUnavailable,"悬浮窗记录无法读取。"));}
    }
    private static double Number(SqlRow row,string name)=>Convert.ToDouble(row.Value(row.Names.ToList().IndexOf(name)),CultureInfo.InvariantCulture);
    private static bool Valid(FloatingWindowRecord record)=>record.SchemaVersion==1 && record.Revision>0 && Enum.IsDefined(record.Mode) && new[]{record.CompactWidth,record.ExpandedWidth,record.ExpandedHeight}.All(n=>double.IsFinite(n)&&n>0&&n<=10000) && double.IsFinite(record.X)&&double.IsFinite(record.Y);
    public async Task<OperationResult> SaveAsync(FloatingWindowRecord r,long expectedRevision,CancellationToken cancellationToken=default)
    {
        if(!Valid(r)||r.Revision!=expectedRevision+1) return OperationResult.Failure(new(ErrorCodes.InvalidRequest,"悬浮窗记录或版本无效。"));
        var values=new Dictionary<string,object?> { ["$p"]=r.Reference.ProviderId,["$k"]=r.Reference.ItemKey,["$o"]=r.IsOpen?1:0,["$m"]=(int)r.Mode,["$cw"]=r.CompactWidth,["$ew"]=r.ExpandedWidth,["$eh"]=r.ExpandedHeight,["$monitor"]=r.MonitorId,["$x"]=r.X,["$y"]=r.Y,["$top"]=r.IsTopmost?1:0,["$rev"]=r.Revision,["$schema"]=r.SchemaVersion };
        var sql=expectedRevision==0?"INSERT OR IGNORE INTO floating_windows VALUES($p,$k,$o,$m,$cw,$ew,$eh,$monitor,$x,$y,$top,$rev,$schema)":"UPDATE floating_windows SET is_open=$o,mode=$m,compact_width=$cw,expanded_width=$ew,expanded_height=$eh,monitor_id=$monitor,x=$x,y=$y,topmost=$top,revision=$rev,schema_version=$schema WHERE provider_id=$p AND item_key=$k AND revision=$expected";
        if(expectedRevision>0) values["$expected"]=expectedRevision;
        var saved=await database.ExecuteAsync(new(sql,values),cancellationToken);
        return !saved.IsSuccess?OperationResult.Failure(saved.Error!):saved.Value==1?OperationResult.Success():OperationResult.Failure(new(ErrorCodes.Conflict,"窗口记录已变更，请重试。"));
    }
}
