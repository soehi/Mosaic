using System.Globalization;
using System.Text;

namespace Mosaic.Mobile.Core.Cards;

/// <summary>
/// The only operations a card surface may request. Restore/unarchive, create,
/// edit and delete are deliberately absent.
/// </summary>
public enum CardAction
{
    Complete = 0,
    Archive,
    Refresh,
}

public enum TaskAction
{
    Complete = 0,
    Archive,
}

public static class CardActionCodec
{
    public const string CompleteWire = "complete";

    public const string ArchiveWire = "archive";

    public const string RefreshWire = "refresh";

    /// <summary>
    /// Rejects everything that is not one of the three permitted actions. A
    /// stale or crafted PendingIntent therefore cannot reach a fourth operation.
    /// </summary>
    public static bool TryParse(string? raw, out CardAction action)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case CompleteWire:
                action = CardAction.Complete;
                return true;
            case ArchiveWire:
                action = CardAction.Archive;
                return true;
            case RefreshWire:
                action = CardAction.Refresh;
                return true;
            default:
                action = default;
                return false;
        }
    }

    public static bool TryParseTaskAction(CardAction action, out TaskAction taskAction)
    {
        switch (action)
        {
            case CardAction.Complete:
                taskAction = TaskAction.Complete;
                return true;
            case CardAction.Archive:
                taskAction = TaskAction.Archive;
                return true;
            default:
                taskAction = default;
                return false;
        }
    }

    public static string ToWire(CardAction action) => action switch
    {
        CardAction.Complete => CompleteWire,
        CardAction.Archive => ArchiveWire,
        CardAction.Refresh => RefreshWire,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>
    /// Stable PendingIntent request code derived from <c>(taskId, action)</c>.
    /// FNV-1a is used instead of <see cref="string.GetHashCode()"/>, which is
    /// randomized per process and would break pending-intent reuse.
    /// </summary>
    public static int RequestCode(Guid taskId, CardAction action)
    {
        var material = Encoding.UTF8.GetBytes(taskId.ToString("D", CultureInfo.InvariantCulture) + ":" + ToWire(action));
        var hash = 2166136261u;
        foreach (var value in material)
        {
            hash ^= value;
            hash *= 16777619u;
        }

        // Keep the low action bit so Complete and Archive of the same task can
        // never collide even when the hash does.
        return (int)((hash & 0x7FFFFFF0u) | (uint)action);
    }
}