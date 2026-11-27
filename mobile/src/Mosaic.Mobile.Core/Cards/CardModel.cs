using System.Globalization;
using Mosaic.Mobile.Core.Projection;

namespace Mosaic.Mobile.Core.Cards;

public enum CardState
{
    Ready = 0,
    Empty,
    NeedsConfiguration,
    NeedsSignIn,
    SessionExpired,
    NeedsRestore,
    Offline,
    Unavailable,
}

/// <summary>One visible row. Only the title and completion flag are exposed.</summary>
public sealed record CardTaskRow(Guid Id, string Title, bool IsCompleted);

/// <summary>
/// What a card surface renders. It never carries an email, a token, a note or a
/// server address; the fluid-cloud state is carried explicitly so an
/// unsupported device can show "不可用" instead of pretending.
/// </summary>
public sealed record CardModel(
    CardState State,
    string Headline,
    string Detail,
    int PendingCount,
    int VisibleCount,
    IReadOnlyList<CardTaskRow> Rows,
    FluidCloudStatus FluidCloud)
{
    public bool HasTasks => Rows.Count > 0;
}

public static class CardModelBuilder
{
    public const int RowLimit = 5;

    public static CardModel Ready(ProjectionCounts counts, IReadOnlyList<CardTaskRow> rows, FluidCloudStatus fluidCloud)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(fluidCloud);

        if (counts.Visible == 0)
        {
            return Empty(fluidCloud);
        }

        var headline = counts.Open == 0
            ? "任务全部完成"
            : string.Create(CultureInfo.InvariantCulture, $"{counts.Open} 项待办");
        var detail = string.Create(CultureInfo.InvariantCulture, $"共 {counts.Visible} 项未归档");
        return new CardModel(CardState.Ready, headline, detail, counts.Open, counts.Visible, rows, fluidCloud);
    }

    public static CardModel Empty(FluidCloudStatus fluidCloud)
    {
        ArgumentNullException.ThrowIfNull(fluidCloud);
        return new CardModel(CardState.Empty, "暂无任务", "在桌面端添加任务后会显示在这里", 0, 0, [], fluidCloud);
    }

    public static CardModel Status(CardState state, FluidCloudStatus fluidCloud, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(fluidCloud);
        var (headline, fallback) = state switch
        {
            CardState.NeedsConfiguration => ("未配置服务器", "请为手机端配置 Mosaic 服务器地址"),
            CardState.NeedsSignIn => ("等待登录", "请在桌面端完成设备授权"),
            CardState.SessionExpired => ("会话已失效", "请重新授权此设备"),
            CardState.NeedsRestore => ("等待首次同步", "请先在桌面端上传一次备份"),
            CardState.Offline => ("离线", "显示上次同步的数据"),
            CardState.Unavailable => ("本地数据不可用", "无法校验的本地投影已隔离"),
            CardState.Empty => ("暂无任务", "在桌面端添加任务后会显示在这里"),
            _ => ("Mosaic", string.Empty),
        };

        return new CardModel(state, headline, detail ?? fallback, 0, 0, [], fluidCloud);
    }
}