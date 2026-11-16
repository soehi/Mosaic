using System.Text.Json;
using System.Text.Json.Nodes;
using Mosaic.Contracts;
using Mosaic.Settings.Contracts;

namespace Mosaic.Desktop;

internal sealed class FileSettingsCommitJournal(AppPaths paths) : ISettingsCommitJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string JournalPath => Path.Combine(paths.ConfigDirectory, "settings.pending.json");

    public async Task<OperationResult<SettingsCommitJournalEntry?>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(JournalPath))
                return OperationResult<SettingsCommitJournalEntry?>.Success(null);
            try
            {
                var root = JsonNode.Parse(await File.ReadAllTextAsync(JournalPath, cancellationToken).ConfigureAwait(false)) as JsonObject;
                var commitId = root?["commitId"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(commitId) || root?["hostValues"] is not JsonObject hostValues ||
                    root["userValues"] is not JsonObject userValues)
                    return Failure<SettingsCommitJournalEntry?>("提交日志格式无效。");
                return OperationResult<SettingsCommitJournalEntry?>.Success(new(
                    commitId,
                    ConvertObject(hostValues),
                    ConvertObject(userValues)));
            }
            catch (JsonException)
            {
                return Failure<SettingsCommitJournalEntry?>("提交日志损坏，请保留文件并检查 Config/settings.pending.json。");
            }
        }
        catch (Exception exception)
        {
            return Failure<SettingsCommitJournalEntry?>("无法读取提交日志：" + exception.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<OperationResult> PrepareAsync(
        SettingsCommitJournalEntry entry,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(JournalPath))
                return OperationResult.Failure(new(
                    ErrorCodes.Conflict, "存在未完成的设置提交日志，请重新启动应用以恢复。", null,
                    MessageKey: "Settings.Error.PendingCommit"));
            Directory.CreateDirectory(paths.ConfigDirectory);
            var root = new JsonObject
            {
                ["commitId"] = entry.CommitId,
                ["hostValues"] = SerializeObject(entry.HostValues),
                ["userValues"] = SerializeObject(entry.UserValues)
            };
            var temporaryPath = JournalPath + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, root.ToJsonString(JsonOptions), cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, JournalPath);
                return OperationResult.Success();
            }
            catch (OperationCanceledException)
            {
                TryDelete(temporaryPath);
                throw;
            }
            catch (Exception exception)
            {
                TryDelete(temporaryPath);
                return OperationResult.Failure(new(
                    ErrorCodes.StorageUnavailable, "提交日志写入失败：" + exception.Message, null,
                    MessageKey: "Settings.Error.JournalWrite"));
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<OperationResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(JournalPath)) File.Delete(JournalPath);
            return OperationResult.Success();
        }
        catch (Exception exception)
        {
            return OperationResult.Failure(new(
                ErrorCodes.StorageUnavailable, "提交日志清理失败：" + exception.Message, null,
                MessageKey: "Settings.Error.JournalCleanup"));
        }
        finally
        {
            _lock.Release();
        }
    }

    private static JsonObject SerializeObject(IReadOnlyDictionary<string, object?> values)
    {
        var result = new JsonObject();
        foreach (var (id, value) in values)
            result[id] = JsonSerializer.SerializeToNode(value, JsonOptions);
        return result;
    }

    private static Dictionary<string, object?> ConvertObject(JsonObject values) =>
        values.ToDictionary(
            item => item.Key,
            item => item.Value is null ? null : ConvertNode(item.Value),
            StringComparer.Ordinal);

    private static object? ConvertNode(JsonNode node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean)) return boolean;
            if (value.TryGetValue<int>(out var integer)) return integer;
            if (value.TryGetValue<long>(out var longValue)) return longValue;
            if (value.TryGetValue<double>(out var doubleValue)) return doubleValue;
            if (value.TryGetValue<string>(out var text)) return text;
        }
        if (node is JsonArray array)
            return array.Select(item => item is null ? null : ConvertNode(item)).ToArray();
        if (node is JsonObject @object)
            return ConvertObject(@object);
        return node.ToJsonString();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static OperationResult<T> Failure<T>(string message) =>
        OperationResult<T>.Failure(new(ErrorCodes.StorageUnavailable, message));
}
