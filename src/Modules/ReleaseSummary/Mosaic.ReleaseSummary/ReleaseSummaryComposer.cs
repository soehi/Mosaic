using System.Text;
using Mosaic.Contracts;

namespace Mosaic.ReleaseSummary;

public sealed record ReleaseSummaryDocument(string ReleaseId, string Version, string Notes);

public static class ReleaseSummaryComposer
{
    public const string TaskTitle = "更新简要";
    public const string SourceId = "release-summary";

    public static OperationResult<ReleaseSummaryDocument> Compose(ReleaseManifest manifest, IReadOnlyList<string> announcedReleaseIds)
    {
        var byId = manifest.Releases.ToDictionary(item => item.ReleaseId, StringComparer.Ordinal);
        if (!byId.TryGetValue(manifest.CurrentReleaseId, out var current))
            return Failure("发布清单没有当前版本。");
        ReleaseContent? highest = null;
        foreach (var id in announcedReleaseIds)
        {
            if (!byId.TryGetValue(id, out var announced))
                return Failure($"无法识别已有公告回执 {id}，已保留待重试。");
            if (highest is null || SemanticReleaseVersion.Parse(announced.Version).CompareTo(SemanticReleaseVersion.Parse(highest.Version)) > 0)
                highest = announced;
        }
        if (highest is not null && SemanticReleaseVersion.Parse(current.Version).CompareTo(SemanticReleaseVersion.Parse(highest.Version)) <= 0)
            return OperationResult<ReleaseSummaryDocument>.Success(new(current.ReleaseId, current.Version, ""));

        var applicable = new List<ReleaseContent>();
        if (highest is null) applicable.Add(current);
        else
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var next = current;
            while (next.ReleaseId != highest.ReleaseId)
            {
                if (!visited.Add(next.ReleaseId)) return Failure("发布历史存在循环。");
                applicable.Add(next);
                if (next.PreviousReleaseId is null || !byId.TryGetValue(next.PreviousReleaseId, out next!))
                    return Failure("升级范围缺少完整发布历史，已保留待重试。");
            }
            applicable.Reverse();
        }
        if (applicable.Any(item => item.Added.Count + item.Improved.Count + item.Fixed.Count == 0))
            return Failure("升级范围缺少完整发布正文，已保留待重试。");
        var text = new StringBuilder()
            .Append("Mosaic ").Append(current.Version).Append(" · 更新日期 ").AppendLine(current.ReleasedOn)
            .Append("适用范围：").Append(highest?.Version ?? "首次使用").Append(" → ").AppendLine(current.Version);
        foreach (var (heading, select) in new (string, Func<ReleaseContent, IReadOnlyList<string>>)[]
        {
            ("新增功能", item => item.Added),
            ("功能改进", item => item.Improved),
            ("问题修复与兼容变化", item => item.Fixed)
        })
        {
            var items = applicable.SelectMany(release => select(release).Select(item =>
                applicable.Count > 1 ? $"[{release.Version}] {item}" : item)).ToArray();
            if (items.Length == 0) continue;
            text.AppendLine().AppendLine(heading);
            for (var index = 0; index < items.Length; index++)
                text.Append(index + 1).Append(". ").AppendLine(items[index]);
        }
        return OperationResult<ReleaseSummaryDocument>.Success(new(current.ReleaseId, current.Version, text.ToString().TrimEnd()));
    }

    private static OperationResult<ReleaseSummaryDocument> Failure(string message) =>
        OperationResult<ReleaseSummaryDocument>.Failure(new(ErrorCodes.InvalidRequest, message));
}
