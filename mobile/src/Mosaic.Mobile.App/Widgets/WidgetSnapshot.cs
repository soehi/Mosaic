using System.Text;
using Android.Content;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>One row the widget list renders: id, title, completed flag.</summary>
internal sealed record WidgetRow(string Id, string Title, bool Completed);

/// <summary>
/// The last rendered rows, persisted in a private preference so the
/// <see cref="TaskWidgetService"/> factory can rebuild the list after the
/// process was killed. Title and id are the only fields kept; no token, email
/// or note is ever written here.
/// </summary>
internal static class WidgetSnapshotStore
{
    private const string PreferencesFile = "mosaic.mobile.v3.widget";

    private const string RowsKey = "rows";

    private const char FieldSeparator = '\u001F';

    private const char RowSeparator = '\u001E';

    private static readonly object Gate = new();

    public static void Save(Context context, IReadOnlyList<WidgetRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (Gate)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < rows.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(RowSeparator);
                }

                var row = rows[index];
                builder.Append(row.Id).Append(FieldSeparator)
                    .Append(row.Title.Replace(FieldSeparator, ' ').Replace(RowSeparator, ' ')).Append(FieldSeparator)
                    .Append(row.Completed ? '1' : '0');
            }

            Preferences(context)?.Edit()?.PutString(RowsKey, builder.ToString())?.Commit();
        }
    }

    public static IReadOnlyList<WidgetRow> Load(Context context)
    {
        lock (Gate)
        {
            var raw = Preferences(context)?.GetString(RowsKey, null);
            if (string.IsNullOrEmpty(raw))
            {
                return [];
            }

            var rows = new List<WidgetRow>();
            foreach (var chunk in raw.Split(RowSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = chunk.Split(FieldSeparator);
                if (fields.Length != 3 || fields[0].Length == 0)
                {
                    continue;
                }

                rows.Add(new WidgetRow(fields[0], fields[1], fields[2] == "1"));
            }

            return rows;
        }
    }

    private static ISharedPreferences? Preferences(Context context) =>
        context.GetSharedPreferences(PreferencesFile, FileCreationMode.Private);
}