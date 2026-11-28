using System.Security.Cryptography;
using System.Text.Json;

namespace Mosaic.Server.Auth;

internal sealed record MobileRelease(Uri Origin, string ApkPath, string CertificateSha256)
{
    public const string PackageName = "app.mosaic.tasks";

    public static MobileRelease? Load(ServerOptions options, ILogger logger)
    {
        if (options.MobileReleaseManifest is not { Length: > 0 } path)
        {
            return null;
        }

        try
        {
            var origin = new Uri(options.PublicUrl, UriKind.Absolute);
            if (origin.Scheme != Uri.UriSchemeHttps || !origin.IsDefaultPort || origin.AbsolutePath != "/"
                || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            {
                throw new InvalidDataException();
            }

            var manifestPath = Path.GetFullPath(path);
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (manifest?.Apk is not { Length: > 0 } apk || !IsSha256(manifest.Sha256)
                || !IsSha256(manifest.CertificateSha256) || manifest.Package != PackageName)
            {
                throw new InvalidDataException();
            }

            var apkPath = Path.GetFullPath(apk, Path.GetDirectoryName(manifestPath)!);
            using var stream = File.OpenRead(apkPath);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException();
            }

            var fingerprint = string.Join(':', manifest.CertificateSha256!.ToUpperInvariant().Chunk(2).Select(pair => new string(pair)));
            return new MobileRelease(origin, apkPath, fingerprint);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning("手机组件发布清单无效，下载与配对已禁用。{ErrorType}", exception.GetType().Name);
            return null;
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed record Manifest(string? Package, string? Apk, string? Sha256, string? CertificateSha256);
}
