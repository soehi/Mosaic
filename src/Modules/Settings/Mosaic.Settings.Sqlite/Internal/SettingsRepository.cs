using System.Text.Json;
using Mosaic.Contracts;
using Mosaic.Settings.Core;
using Mosaic.Storage.Contracts;

namespace Mosaic.Settings.Sqlite.Internal;

internal sealed class SettingsRepository(IDatabaseSession database) : ISettingRepository
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public async Task<OperationResult<IReadOnlyDictionary<string, object?>>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var rows = await database.QueryAsync(new("SELECT setting_id, value_json FROM settings"), cancellationToken).ConfigureAwait(false);
        if (!rows.IsSuccess) return OperationResult<IReadOnlyDictionary<string, object?>>.Failure(rows.Error!);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var row in rows.Value)
        {
            try
            {
                using var document = JsonDocument.Parse(row.String("value_json"));
                values[row.String("setting_id")] = ToClr(document.RootElement);
            }
            catch (JsonException) { return OperationResult<IReadOnlyDictionary<string, object?>>.Failure(new(ErrorCodes.StorageUnavailable, "设置数据格式无效。")); }
        }
        return OperationResult<IReadOnlyDictionary<string, object?>>.Success(values);
    }

    public Task<OperationResult> SaveAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        ExecuteTransaction(database => { SaveCore(database, values); return true; }, cancellationToken);

    public Task<OperationResult> DeleteAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default) =>
        ExecuteTransaction(database => { DeleteCore(database, ids); return true; }, cancellationToken);

    private static void SaveCore(ITransactionSession database, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var (id, value) in values)
        {
            var json = JsonSerializer.Serialize(value ?? string.Empty, Options);
            database.Execute(new("INSERT INTO settings(setting_id,value_json) VALUES($id,$value) ON CONFLICT(setting_id) DO UPDATE SET value_json=excluded.value_json",
                new Dictionary<string, object?> { ["$id"] = id, ["$value"] = json }));
        }
    }

    private static void DeleteCore(ITransactionSession database, IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
            database.Execute(new("DELETE FROM settings WHERE setting_id=$id", new Dictionary<string, object?> { ["$id"] = id }));
    }

    private async Task<OperationResult> ExecuteTransaction<T>(Func<ITransactionSession, T> action, CancellationToken cancellationToken)
    {
        var result = await database.TransactionAsync(action, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? OperationResult.Success() : OperationResult.Failure(result.Error!);
    }

    private static object? ToClr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(ToClr).ToArray(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => ToClr(property.Value),
            StringComparer.Ordinal),
        JsonValueKind.Null => null,
        _ => element.GetRawText()
    };
}
