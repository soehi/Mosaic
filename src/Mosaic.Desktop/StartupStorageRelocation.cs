using Mosaic.Storage.Sqlite;

namespace Mosaic.Desktop;

/// <summary>
/// Applies a queued database relocation before the storage module opens its single long-lived
/// connection. Running inside the live app is impossible: <c>DatabaseWorker</c> holds the file open
/// and exposes no close/reopen API.
/// </summary>
internal static class StartupStorageRelocation
{
    internal static async Task RunAsync(AppPaths paths, AppConfiguration configuration, HostSettingSource hostSettings)
    {
        var request = await SqliteStorageRelocation.ReadRequestAsync(paths.ConfigDirectory).ConfigureAwait(false);
        if (request is null)
        {
            SqliteStorageRelocation.DeleteRequest(paths.ConfigDirectory);
            return;
        }
        var location = hostSettings.GetValue("storage.location") as string ?? configuration.Storage.Location;
        var filename = hostSettings.GetValue("storage.filename") as string ?? configuration.Storage.Filename;
        var source = Path.Combine(
            location == HostSettingsCatalog.DefaultStorageLocation
                ? Path.Combine(paths.Root, "Data")
                : Path.TrimEndingDirectorySeparator(location),
            filename);
        var targetPath = Path.Combine(request.TargetDirectory, filename);
        if (!File.Exists(source) && File.Exists(targetPath))
        {
            // Previous run moved the copy but crashed before persisting the new location.
            var adopted = await PersistAsync(hostSettings, request.TargetDirectory, filename).ConfigureAwait(false);
            SqliteStorageRelocation.WriteResult(paths.ConfigDirectory, adopted.IsSuccess,
                adopted.IsSuccess ? "已恢复上次完成的迁移。" : adopted.Error!.Message,
                targetPath);
            SqliteStorageRelocation.DeleteRequest(paths.ConfigDirectory);
            return;
        }
        var relocation = new SqliteStorageRelocation(source, paths.ConfigDirectory);
        var plan = await relocation.PlanAsync(request).ConfigureAwait(false);
        if (!plan.IsSuccess)
        {
            SqliteStorageRelocation.WriteResult(paths.ConfigDirectory, false, plan.Error!.Message, source);
            SqliteStorageRelocation.DeleteRequest(paths.ConfigDirectory);
            return;
        }
        var executed = await SqliteStorageRelocation.ExecuteAsync(plan.Value).ConfigureAwait(false);
        if (!executed.IsSuccess)
        {
            SqliteStorageRelocation.WriteResult(paths.ConfigDirectory, false, executed.Error!.Message, source);
            SqliteStorageRelocation.DeleteRequest(paths.ConfigDirectory);
            return;
        }
        var persisted = await PersistAsync(hostSettings, plan.Value.TargetDirectory, filename).ConfigureAwait(false);
        SqliteStorageRelocation.WriteResult(paths.ConfigDirectory, persisted.IsSuccess,
            persisted.IsSuccess ? "数据库已迁移到新位置。" : persisted.Error!.Message,
            plan.Value.TargetDatabasePath);
        SqliteStorageRelocation.DeleteRequest(paths.ConfigDirectory);
    }

    private static Task<Mosaic.Contracts.OperationResult> PersistAsync(
        HostSettingSource hostSettings,
        string directory,
        string filename) =>
        hostSettings.PersistAsync(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["storage.location"] = directory,
            ["storage.filename"] = filename
        });
}
