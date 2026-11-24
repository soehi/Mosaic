using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mosaic.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.ReleaseSummary;

public sealed record ReleaseContent(
    string ReleaseId,
    string Version,
    string ReleasedOn,
    string? PreviousReleaseId,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Improved,
    IReadOnlyList<string> Fixed,
    string ContentSha256);

public sealed record ReleaseManifest(int SchemaVersion, string CurrentReleaseId, IReadOnlyList<ReleaseContent> Releases)
{
    public static OperationResult<ReleaseManifest> Parse(string json, string expectedVersion)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            });
            if (manifest is null) return Failure("发布清单为空。");
            var error = manifest.Validate(expectedVersion);
            return error is null ? OperationResult<ReleaseManifest>.Success(manifest) : Failure(error);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
        {
            return Failure("发布清单无法读取：" + exception.Message);
        }
    }

    public string? Validate(string expectedVersion)
    {
        if (SchemaVersion != 1 || Releases is null || Releases.Count == 0 || string.IsNullOrWhiteSpace(CurrentReleaseId))
            return "发布清单版本或必填内容无效。";
        if (!SemanticReleaseVersion.TryParse(expectedVersion.Split('+')[0], out var assemblyVersion))
            return "程序版本不是合法的语义版本。";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var release in Releases)
        {
            if (release is null || string.IsNullOrWhiteSpace(release.ReleaseId) || release.ReleaseId.Length > 160 ||
                !ids.Add(release.ReleaseId) || !SemanticReleaseVersion.TryParse(release.Version, out _) ||
                !versions.Add(release.Version) || !DateOnly.TryParseExact(release.ReleasedOn, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                release.Added is null || release.Improved is null || release.Fixed is null ||
                release.Added.Concat(release.Improved).Concat(release.Fixed).Any(string.IsNullOrWhiteSpace))
                return "发布条目标识、版本、日期或正文无效。";
            if (!string.Equals(release.ContentSha256, ComputeContentSha256(release), StringComparison.OrdinalIgnoreCase))
                return $"发布条目 {release.ReleaseId} 内容指纹不匹配；同一发布身份不能替换正文。";
        }
        var current = Releases.SingleOrDefault(release => release.ReleaseId == CurrentReleaseId);
        if (current is null || !SemanticReleaseVersion.TryParse(current.Version, out var currentVersion) ||
            currentVersion!.CompareTo(assemblyVersion) != 0)
            return "包内发布清单与程序版本不一致。";
        foreach (var release in Releases)
        {
            if (release.PreviousReleaseId is null) continue;
            var previous = Releases.SingleOrDefault(item => item.ReleaseId == release.PreviousReleaseId);
            if (previous is null || SemanticReleaseVersion.Parse(previous.Version).CompareTo(SemanticReleaseVersion.Parse(release.Version)) >= 0)
                return $"发布条目 {release.ReleaseId} 缺少合法的前置发布关系。";
        }
        foreach (var announced in Releases.Select(release => release.ReleaseId).Prepend<string?>(null))
        {
            if (announced is not null &&
                SemanticReleaseVersion.Parse(Releases.Single(release => release.ReleaseId == announced).Version)
                    .CompareTo(currentVersion) >= 0) continue;
            var composed = ReleaseSummaryComposer.Compose(this, announced is null ? [] : [announced]);
            if (!composed.IsSuccess) return composed.Error!.Message;
            if (RuneCount(ReleaseSummaryComposer.TaskTitle) > TaskConstraints.TitleMaxCharacters ||
                RuneCount(composed.Value.Notes) > TaskConstraints.NotesMaxCharacters)
                return "发布范围内容超过默认任务限额，不能完整生成更新简要。";
        }
        return null;
    }

    public static string ComputeContentSha256(ReleaseContent release)
    {
        // A newline-framed canonical format is shared with the packaging script.
        var lines = new List<string> { release.ReleaseId, release.Version, release.ReleasedOn, release.PreviousReleaseId ?? "" };
        foreach (var (heading, items) in new[] { ("added", release.Added), ("improved", release.Improved), ("fixed", release.Fixed) })
        {
            lines.Add(heading);
            lines.Add(items.Count.ToString(CultureInfo.InvariantCulture));
            lines.AddRange(items);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    internal static int RuneCount(string text) => text.EnumerateRunes().Count();
    private static OperationResult<ReleaseManifest> Failure(string message) =>
        OperationResult<ReleaseManifest>.Failure(new(ErrorCodes.InvalidRequest, message));
}

internal sealed class SemanticReleaseVersion(int major, int minor, int patch, string[] prerelease) : IComparable<SemanticReleaseVersion>
{
    private static readonly Regex Pattern = new(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$", RegexOptions.CultureInvariant);
    internal static SemanticReleaseVersion Parse(string text) =>
        TryParse(text, out var version) ? version! : throw new FormatException("Invalid semantic version.");

    internal static bool TryParse(string? text, out SemanticReleaseVersion? version)
    {
        version = null;
        if (text is null) return false;
        var match = Pattern.Match(text);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch)) return false;
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(item => item.All(char.IsAsciiDigit) && item.Length > 1 && item[0] == '0')) return false;
        version = new(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticReleaseVersion? other)
    {
        if (other is null) return 1;
        var comparison = major.CompareTo(other.Major);
        if (comparison == 0) comparison = minor.CompareTo(other.Minor);
        if (comparison == 0) comparison = patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;
        if (prerelease.Length == 0) return other.Prerelease.Length == 0 ? 0 : 1;
        if (other.Prerelease.Length == 0) return -1;
        for (var index = 0; index < Math.Min(prerelease.Length, other.Prerelease.Length); index++)
        {
            var left = prerelease[index];
            var right = other.Prerelease[index];
            var leftNumeric = left.All(char.IsAsciiDigit);
            var rightNumeric = right.All(char.IsAsciiDigit);
            comparison = leftNumeric && rightNumeric
                ? (left.Length != right.Length ? left.Length.CompareTo(right.Length) : StringComparer.Ordinal.Compare(left, right))
                : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : StringComparer.Ordinal.Compare(left, right);
            if (comparison != 0) return comparison;
        }
        return prerelease.Length.CompareTo(other.Prerelease.Length);
    }

    private int Major => major;
    private int Minor => minor;
    private int Patch => patch;
    private string[] Prerelease => prerelease;
}
