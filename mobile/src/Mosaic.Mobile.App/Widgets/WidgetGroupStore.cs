using System.Globalization;
using Android.Content;
using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// Per-widget task group, stored exactly as the reconfigure contract says:
/// <c>SharedPreferences("widget_prefs")</c>, key <c>filter_&lt;appWidgetId&gt;</c>,
/// value one of <c>pending|completed|archived|all</c>, default <c>pending</c>.
/// It is a separate file from the row snapshot so clearing one never affects
/// the other, and <see cref="TaskWidgetProvider.OnDeleted"/> removes the entry
/// when the host removes the widget.
/// </summary>
internal static class WidgetGroupStore
{
    internal const string PreferencesFile = "widget_prefs";

    private const string KeyPrefix = "filter_";

    public static WidgetTaskGroup Get(Context context, int appWidgetId)
    {
        ArgumentNullException.ThrowIfNull(context);
        var raw = Preferences(context)?.GetString(Key(appWidgetId), null);
        _ = WidgetTaskGroups.TryParse(raw, out var group);
        return group;
    }

    public static void Save(Context context, int appWidgetId, WidgetTaskGroup group)
    {
        ArgumentNullException.ThrowIfNull(context);
        Preferences(context)?.Edit()
            ?.PutString(Key(appWidgetId), WidgetTaskGroups.ToWire(group))
            ?.Commit();
    }

    public static void Delete(Context context, int appWidgetId)
    {
        ArgumentNullException.ThrowIfNull(context);
        Preferences(context)?.Edit()
            ?.Remove(Key(appWidgetId))
            ?.Commit();
    }

    private static string Key(int appWidgetId) =>
        KeyPrefix + appWidgetId.ToString(CultureInfo.InvariantCulture);

    private static ISharedPreferences? Preferences(Context context) =>
        context.GetSharedPreferences(PreferencesFile, FileCreationMode.Private);
}
