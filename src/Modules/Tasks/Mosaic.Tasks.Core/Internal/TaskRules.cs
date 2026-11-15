using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Core.Internal;

internal sealed class TaskRules
{
    private TaskConstraintsDto _current = new(
        TaskConstraints.TitleMaxCharacters,
        TaskConstraints.NotesMaxCharacters,
        TitleRequired: true,
        DefaultCompleted: false,
        RevisionStart: 1);

    internal TaskConstraintsDto Current => Volatile.Read(ref _current);

    internal void Apply(IReadOnlyDictionary<string, object?> values)
    {
        var current = Current;
        var next = new TaskConstraintsDto(
            ReadInt(values, "tasks.title.maxCharacters", current.TitleMaxCharacters),
            ReadInt(values, "tasks.notes.maxCharacters", current.NotesMaxCharacters),
            ReadBool(values, "tasks.title.required", current.TitleRequired),
            ReadBool(values, "tasks.defaultCompleted", current.DefaultCompleted),
            ReadLong(values, "tasks.revisionStart", current.RevisionStart));
        Volatile.Write(ref _current, next);
    }

    private static int ReadInt(IReadOnlyDictionary<string, object?> values, string id, int fallback) =>
        values.TryGetValue(id, out var value) ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    private static long ReadLong(IReadOnlyDictionary<string, object?> values, string id, long fallback) =>
        values.TryGetValue(id, out var value) ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    private static bool ReadBool(IReadOnlyDictionary<string, object?> values, string id, bool fallback) =>
        values.TryGetValue(id, out var value) && value is bool boolean ? boolean : fallback;
}
