using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using OperationCanceledException = System.OperationCanceledException;
using Android.Widget;
using Mosaic.Mobile.App.Widgets;
using Mosaic.Mobile.Core.Auth;
using Mosaic.Mobile.Core.Cards;
using Mosaic.Mobile.Core.Configuration;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.App.Platform;

[Activity(Name = "app.mosaic.tasks.PairingActivity", Exported = true, ExcludeFromRecents = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class PairingActivity : Activity
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(12),
        MaxResponseContentBufferSize = 128 * 1024,
    };
    private static readonly SemaphoreSlim PairingGate = new(1);
    private readonly CancellationTokenSource _lifetime = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _ = ConnectAsync(_lifetime.Token);
    }

    protected override void OnDestroy()
    {
        _lifetime.Cancel();
        base.OnDestroy();
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var inset = (int)(24 * Resources!.DisplayMetrics!.Density);
        content.SetPadding(inset, inset, inset, inset);
        content.AddView(new TextView(this) { Text = "连接 Mosaic 手机组件", TextSize = 24 });
        var status = new TextView(this) { Text = "正在验证配对链接…", TextSize = 16 };
        content.AddView(status);
        var close = new Button(this) { Text = "关闭" };
        close.Click += (_, _) => Finish();
        content.AddView(close);
        SetContentView(content);

        try
        {
            await PairingGate.WaitAsync(ct);
            try
            {
                var (origin, reference) = ParseLink();
                await VerifyAssociationAsync(origin, ct);
                var configuration = Normalize(origin);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromMinutes(5));
                var claim = await PostAsync<Claim>(origin, "claim", new
                {
                    reference,
                    deviceLabel = string.Join(' ', Build.Manufacturer, Build.Model).Trim(),
                    appVersion = typeof(PairingActivity).Assembly.GetName().Version?.ToString(3),
                }, deadline.Token);
                if (claim.DeviceCode is not { Length: 43 } || claim.ShortCode is not { Length: 6 }
                    || claim.ExpiresIn is < 1 or > 300)
                    throw new InvalidDataException();
                deadline.CancelAfter(TimeSpan.FromSeconds(claim.ExpiresIn));
                status.Text = $"配对码 {claim.ShortCode}\n在桌面输入这 6 位数字并批准。";
                var session = await WaitForApprovalAsync(origin, claim, deadline.Token);
                var app = ApplicationContext ?? this;
                await WidgetRuntime.ApplyPairingAsync(app, token => SaveSessionAsync(configuration, session, token), ct);
                status.Text = "已连接，正在读取服务器任务…";
                var model = await WidgetRuntime.RefreshAsync(app);
                WidgetRuntime.RenderAll(app, model);
                if (!ct.IsCancellationRequested)
                    status.Text = model.State is CardState.Ready or CardState.Empty
                        ? "已同步。请从桌面小组件列表添加 Mosaic。"
                        : "已连接。" + model.Detail;
            }
            finally
            {
                PairingGate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidDataException
            or JsonException or Java.Lang.Exception or IOException or CryptographicException
            or UnauthorizedAccessException or InvalidOperationException or SecretStorageException)
        {
            if (!ct.IsCancellationRequested)
                status.Text = "连接未完成。请确认桌面窗口仍打开，并重新扫描二维码。";
        }
    }

    private (Uri Origin, string Reference) ParseLink()
    {
        var application = PackageManager?.GetApplicationInfo(PackageName!, PackageInfoFlags.MetaData);
        var host = application?.MetaData?.GetString("mosaicAppLinkHost");
        if (Intent?.Action != Intent.ActionView || !Uri.TryCreate(Intent.DataString, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
            || !string.Equals(uri.IdnHost, host, StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath != "/device/mobile/connect" || uri.Query.Length != 0
            || uri.Fragment is not { Length: 44 } fragment
            || !fragment[1..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new InvalidDataException();
        return (new Uri(uri.GetLeftPart(UriPartial.Authority)), fragment[1..]);
    }

    private async Task VerifyAssociationAsync(Uri origin, CancellationToken ct)
    {
        // 显式 Intent 也可能进入导出的 Activity，必须验证当前 APK 的签名与域关联。
        var package = PackageManager!.GetPackageInfo(PackageName!,
            OperatingSystem.IsAndroidVersionAtLeast(28) ? PackageInfoFlags.SigningCertificates : PackageInfoFlags.Signatures);
        var signatures = OperatingSystem.IsAndroidVersionAtLeast(28) ? package?.SigningInfo?.GetApkContentsSigners() : package?.Signatures;
        var actual = signatures?.Select(s => Convert.ToHexString(SHA256.HashData(s.ToByteArray()!)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        using var result = await Http.GetAsync(new Uri(origin, "/.well-known/assetlinks.json"), ct);
        result.EnsureSuccessStatusCode();
        var statements = await result.Content.ReadFromJsonAsync<AssetStatement[]>(cancellationToken: ct);
        if (actual.Count == 0 || statements?.Any(statement =>
            statement.Relation?.Contains("delegate_permission/common.handle_all_urls") == true
            && statement.Target?.Namespace == "android_app"
            && statement.Target.PackageName == PackageName
            && statement.Target.Fingerprints?.Any(fingerprint => actual.Contains(fingerprint.Replace(":", string.Empty))) == true) != true)
            throw new InvalidDataException();
    }

    private static ServerConfiguration Normalize(Uri origin) => ServerAddress.TryNormalize(origin.AbsoluteUri, out var configuration)
        ? configuration : throw new InvalidDataException();

    private static async Task<SessionDto> WaitForApprovalAsync(Uri origin, Claim claim, CancellationToken ct)
    {
        var interval = Math.Clamp(claim.Interval, 2, 10);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var result = await PostAsync<DevicePollDto>(origin, "poll", new DevicePollRequestDto(claim.DeviceCode!), ct);
            if (result.Status == "approved" && result.Session is { } session) return session;
            if (result.Status == "slow_down") interval = Math.Min(interval + 2, 10);
            else if (result.Status != "pending") throw new InvalidDataException();
        }
    }

    private async Task SaveSessionAsync(ServerConfiguration configuration, SessionDto session, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (session.User?.Id is not { Length: > 0 } id || session.AccessToken is not { Length: > 0 } access
            || session.RefreshToken is not { Length: > 0 } refresh || session.AccessTokenExpiresAt is not { } accessExpiry
            || session.RefreshTokenExpiresAt is not { } refreshExpiry || accessExpiry <= now || refreshExpiry <= now)
            throw new InvalidDataException();
        var context = ApplicationContext ?? this;
        var config = new AndroidServerConfigProvider(context);
        var tokens = new TokenStore(new AndroidSecretStorage(context));
        var previous = await tokens.LoadSessionAsync(configuration.NormalizedOrigin, ct);
        var device = await tokens.GetOrCreateDeviceIdAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (!WidgetSnapshotStore.Save(context, []))
            throw new IOException("无法清除上一账户的小组件快照。");

        await tokens.SaveSessionAsync(new StoredSession
        {
            ServerOrigin = configuration.NormalizedOrigin, UserId = id,
            AccessToken = access, RefreshToken = refresh, AccessTokenExpiresAt = accessExpiry,
            RefreshTokenExpiresAt = refreshExpiry, DeviceId = device, DeviceLabel = configuration.DeviceLabel,
        }, CancellationToken.None);
        if (!config.Save(configuration))
        {
            if (previous is null) await tokens.DeleteSessionAsync(configuration.NormalizedOrigin, CancellationToken.None);
            else await tokens.SaveSessionAsync(previous, CancellationToken.None);
            throw new IOException("无法保存服务器配置。");
        }
    }

    private static async Task<T> PostAsync<T>(Uri origin, string action, object body, CancellationToken ct)
    {
        using var response = await Http.PostAsJsonAsync(new Uri(origin, "/v1/mobile/pairing/" + action), body, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct) ?? throw new InvalidDataException();
    }

    private sealed record AssetStatement(string[]? Relation, AssetTarget? Target);
    private sealed record AssetTarget(string? Namespace,
        [property: System.Text.Json.Serialization.JsonPropertyName("package_name")] string? PackageName,
        [property: System.Text.Json.Serialization.JsonPropertyName("sha256_cert_fingerprints")] string[]? Fingerprints);
    private sealed record Claim(string? DeviceCode, string? ShortCode, long ExpiresIn, int Interval);
}
