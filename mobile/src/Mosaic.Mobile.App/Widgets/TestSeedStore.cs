#if MOSAIC_WIDGET_TEST_SEED
using System.Globalization;
using Android.Content;
using Mosaic.Mobile.Core;
using Mosaic.Mobile.Core.Cards;
using Mosaic.Mobile.Core.Commands;
using Mosaic.Mobile.Core.Projection;

namespace Mosaic.Mobile.App.Widgets;

/// <summary>
/// Test-build-only seed data. It renders straight into the widget's
/// <see cref="CardModel"/> without a server, a session or the projection, and it
/// lives in its own preference file so it can never touch an account directory.
/// The compiled release build contains none of this type.
/// </summary>
internal static class TestSeedStore
{
    public const string PreferencesFile = "mosaic.mobile.v3.testseed";

    private const string MarkerKey = "seeded";

    private static readonly object Gate = new();

    private static readonly (Guid Id, string Title)[] Definitions =
    [
        (Guid.Parse("a1000000-0000-4000-8000-000000000001"), "整理本周项目周报"),
        (Guid.Parse("a1000000-0000-4000-8000-000000000002"), "回复客户关于交付时间的邮件并确认下周演示安排与参与人员名单"),
        (Guid.Parse("a1000000-0000-4000-8000-000000000003"), "更新部署脚本"),
        (Guid.Parse("a1000000-0000-4000-8000-000000000004"), "检查夜间备份任务"),
        (Guid.Parse("a1000000-0000-4000-8000-000000000005"), "提交本月报销单"),
        (Guid.Parse("a1000000-0000-4000-8000-000000000006"), "归档旧版设计稿"),
    ];

    private enum SeedState
    {
        Open = 0,
        Completed = 1,
        Archived = 2,
    }

    private static SeedState[]? _states;

    private static Context? _context;

    public static bool IsSeeded
    {
        get
        {
            lock (Gate)
            {
                return _states is not null;
            }
        }
    }

    public static void EnsureSeeded(Context context)
    {
        lock (Gate)
        {
            _context = context.ApplicationContext ?? context;
            if (_states is not null)
            {
                return;
            }

            var preferences = Preferences(_context);
            if (preferences?.GetBoolean(MarkerKey, false) != true)
            {
                SeedLocked(preferences);
                return;
            }

            var states = new SeedState[Definitions.Length];
            for (var index = 0; index < Definitions.Length; index++)
            {
                states[index] = (SeedState)preferences.GetInt(IndexKey(index), 0);
            }

            _states = states;
        }
    }

    public static void Reset(Context context)
    {
        lock (Gate)
        {
            _context = context.ApplicationContext ?? context;
            SeedLocked(Preferences(_context));
        }
    }


    /// <summary>Only the unfinished, unarchived tasks, as widget rows.</summary>
    public static IReadOnlyList<WidgetRow> Rows()
    {
        lock (Gate)
        {
            var states = _states ?? throw new InvalidOperationException("Test seed was not initialized.");
            var rows = new List<WidgetRow>();
            for (var index = 0; index < Definitions.Length; index++)
            {
                if (states[index] == SeedState.Archived || states[index] == SeedState.Completed)
                {
                    continue;
                }

                rows.Add(new WidgetRow(Definitions[index].Id.ToString("D"), Definitions[index].Title, false));
            }

            return rows;
        }
    }

    public static CardModel Model()
    {
        lock (Gate)
        {
            var states = _states ?? throw new InvalidOperationException("Test seed was not initialized.");
            var rows = new List<CardTaskRow>();
            var visible = 0;
            var open = 0;
            for (var index = 0; index < Definitions.Length; index++)
            {
                if (states[index] == SeedState.Archived)
                {
                    continue;
                }

                visible++;
                if (states[index] == SeedState.Open)
                {
                    open++;
                }

                if (rows.Count < CardModelBuilder.RowLimit)
                {
                    rows.Add(new CardTaskRow(
                        Definitions[index].Id,
                        Definitions[index].Title,
                        states[index] == SeedState.Completed));
                }
            }

            var model = visible == 0
                ? CardModelBuilder.Empty(FluidCloudStatus.Available())
                : CardModelBuilder.Ready(new ProjectionCounts(visible, open, 0), rows, FluidCloudStatus.Available());
            return model with { Detail = "测试数据 · " + model.Detail };
        }
    }

    public static CardActionResult Apply(Guid taskId, CardAction action)
    {
        lock (Gate)
        {
            if (_states is null)
            {
                return new CardActionResult(CommandOutcome.NotReady, "测试数据未初始化", CardModelBuilder.Empty(FluidCloudStatus.Available()));
            }

            var index = Array.FindIndex(Definitions, definition => definition.Id == taskId);
            if (index < 0)
            {
                return new CardActionResult(CommandOutcome.NotFound, "测试数据：任务不存在", Model());
            }

            switch (action)
            {
                case CardAction.Complete when _states[index] == SeedState.Completed:
                    return new CardActionResult(CommandOutcome.AlreadyApplied, "测试数据：任务已完成", Model());
                case CardAction.Archive when _states[index] == SeedState.Archived:
                    return new CardActionResult(CommandOutcome.AlreadyApplied, "测试数据：任务已归档", Model());
                case CardAction.Complete:
                    _states[index] = SeedState.Completed;
                    Persist();
                    return new CardActionResult(CommandOutcome.Applied, "测试数据：已标记完成", Model());
                case CardAction.Archive:
                    _states[index] = SeedState.Archived;
                    Persist();
                    return new CardActionResult(CommandOutcome.Applied, "测试数据：已归档", Model());
                default:
                    return new CardActionResult(CommandOutcome.Protocol, "测试数据：不支持的操作", Model());
            }
        }
    }

    private static void SeedLocked(ISharedPreferences? preferences)
    {
        var states = new SeedState[Definitions.Length];
        states[4] = SeedState.Completed;
        states[5] = SeedState.Archived;
        _states = states;

        if (preferences?.Edit() is not { } editor)
        {
            return;
        }

        editor.PutBoolean(MarkerKey, true);
        for (var index = 0; index < states.Length; index++)
        {
            editor.PutInt(IndexKey(index), (int)states[index]);
        }

        editor.Commit();
    }

    private static void Persist()
    {
        if (_context is null || _states is null || Preferences(_context)?.Edit() is not { } editor)
        {
            return;
        }

        for (var index = 0; index < _states.Length; index++)
        {
            editor.PutInt(IndexKey(index), (int)_states[index]);
        }

        editor.Commit();
    }

    private static ISharedPreferences? Preferences(Context context) =>
        context.GetSharedPreferences(PreferencesFile, FileCreationMode.Private);

    private static string IndexKey(int index) => "state." + index.ToString(CultureInfo.InvariantCulture);
}
#endif