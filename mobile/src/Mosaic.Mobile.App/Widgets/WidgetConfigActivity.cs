using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Mosaic.Mobile.Core.Cards;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// The system's reconfigure entry point for one widget instance. An app widget
/// cannot intercept a long-press: the launcher owns that gesture and shows its
/// own menu. <c>android:configure</c> plus
/// <c>reconfigurable|configuration_optional</c> is the supported path — the user
/// long-presses, picks 编辑/配置, and lands here. This is not an App screen: no
/// launcher icon, no task list, no login, no settings page; it is the four-option
/// dialog the widget host requires, and it closes on the first choice.
///
/// The Java name is pinned with <c>Name</c> to exactly the value the widget
/// info XML and the reconfigure contract use, because
/// <c>android:configure</c> in <c>widget_tasks_info.xml</c> has to name a Java
/// class exactly, and the generated <c>crc64…</c> wrapper name is not something
/// to hard-code in a resource file.
/// </summary>
[Activity(
    Name = "app.mosaic.tasks.WidgetConfigActivity",
    Label = "@string/widget_config_label",
    Theme = "@style/WidgetConfigTheme",
    Exported = true,
    ExcludeFromRecents = true)]
public sealed class WidgetConfigActivity : Activity
{
    public const string ExtraWidgetId = AppWidgetManager.ExtraAppwidgetId;

    private static readonly WidgetTaskGroup[] GroupOrder =
    [
        WidgetTaskGroup.Pending,
        WidgetTaskGroup.Completed,
        WidgetTaskGroup.Archived,
        WidgetTaskGroup.All,
    ];

    private int _appWidgetId = AppWidgetManager.InvalidAppwidgetId;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _appWidgetId = Intent?.GetIntExtra(ExtraWidgetId, AppWidgetManager.InvalidAppwidgetId)
            ?? AppWidgetManager.InvalidAppwidgetId;
        if (_appWidgetId == AppWidgetManager.InvalidAppwidgetId)
        {
            // A launch without an id has no widget to update; closing is the
            // only correct result and it touches no other instance.
            SetResult(Result.Canceled);
            Finish();
            return;
        }

        SetContentView(Resource.Layout.widget_config);

        var current = WidgetGroupStore.Get(this, _appWidgetId);
        var container = FindViewById<LinearLayout>(Resource.Id.widget_config_options)
            ?? throw new InvalidOperationException("Config layout is missing its options container.");

        foreach (var group in GroupOrder)
        {
            container.AddView(BuildOption(group, current));
        }
    }

    public override void OnBackPressed()
    {
        // Cancel leaves the widget's group untouched.
        SetResult(Result.Canceled);
        Finish();
    }

    private Button BuildOption(WidgetTaskGroup group, WidgetTaskGroup current)
    {
        var selected = group == current;
        var option = new Button(this)
        {
            Text = selected
                ? WidgetTaskGroups.DisplayName(group) + "  ✓"
                : WidgetTaskGroups.DisplayName(group),
            TextAlignment = TextAlignment.ViewStart,
        };

        option.SetTextColor(new Color(GetColor(selected
            ? Resource.Color.widget_primary
            : Resource.Color.widget_on_surface)));
        option.SetBackgroundResource(Resource.Drawable.widget_config_option);
        option.SetPadding(28, 26, 28, 26);

        var parameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent)
        {
            BottomMargin = (int)(8 * Resources!.DisplayMetrics!.Density),
        };
        option.LayoutParameters = parameters;

        option.Click += (_, _) => Apply(group);
        return option;
    }

    /// <summary>
    /// The id must belong to this provider. Otherwise a crafted launch intent
    /// could write a group preference for a widget this app does not own.
    /// </summary>
    private bool OwnsWidget(int appWidgetId)
    {
        var manager = AppWidgetManager.GetInstance(this);
        if (manager is null)
        {
            return false;
        }

        var provider = new ComponentName(this, Java.Lang.Class.FromType(typeof(TaskWidgetProvider))!);
        var ids = manager.GetAppWidgetIds(provider);
        if (ids is null)
        {
            return false;
        }

        foreach (var id in ids)
        {
            if (id == appWidgetId)
            {
                return true;
            }
        }

        return false;
    }

    private void Apply(WidgetTaskGroup group)
    {
        // The id comes from the launcher's launch intent. Accepting one this app
        // does not own would let a crafted intent plant a group preference for a
        // widget id that may later be handed to us, so ownership is checked
        // before the write, not only at launch.
        if (!OwnsWidget(_appWidgetId))
        {
            SetResult(Result.Canceled);
            Finish();
            return;
        }

        WidgetGroupStore.Save(this, _appWidgetId, group);

        // Redraw only this instance: a second widget may show another group.
        WidgetRuntime.RenderOne(this, _appWidgetId);

        var result = new Intent();
        result.PutExtra(ExtraWidgetId, _appWidgetId);
        SetResult(Result.Ok, result);
        Finish();
    }
}
