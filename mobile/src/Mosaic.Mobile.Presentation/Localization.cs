using System.ComponentModel;
using System.Globalization;
using Mosaic.Mobile.Sync;

namespace Mosaic.Mobile.Presentation;

public enum AppLanguage
{
    Chinese = 0,
    English,
}

public static class StringKeys
{
    public const string AppName = "app.name";
    public const string Ok = "common.ok";
    public const string Cancel = "common.cancel";
    public const string Retry = "common.retry";
    public const string Back = "common.back";
    public const string Confirm = "common.confirm";
    public const string Delete = "common.delete";
    public const string Loading = "common.loading";
    public const string Error = "common.error";
    public const string Email = "common.email";
    public const string Password = "common.password";
    public const string ConfirmPassword = "common.confirmPassword";
    public const string Code = "common.code";
    public const string SendCode = "common.sendCode";
    public const string ResendIn = "common.resendIn";
    public const string SignIn = "common.signIn";
    public const string SignOut = "common.signOut";
    public const string Register = "common.register";
    public const string ForgotPassword = "common.forgotPassword";
    public const string Required = "common.required";

    public const string Tasks = "nav.tasks";
    public const string Account = "nav.account";
    public const string Settings = "nav.settings";

    public const string FilterAll = "tasks.filter.all";
    public const string FilterOpen = "tasks.filter.open";
    public const string FilterCompleted = "tasks.filter.completed";
    public const string FilterArchived = "tasks.filter.archived";
    public const string NoTasks = "tasks.empty.all";
    public const string NoOpenTasks = "tasks.empty.open";
    public const string NoCompletedTasks = "tasks.empty.completed";
    public const string NoArchivedTasks = "tasks.empty.archived";
    public const string TaskArchived = "tasks.status.archived";
    public const string TaskCompleted = "tasks.status.completed";
    public const string TaskOpen = "tasks.status.open";
    public const string SwitchedToAll = "tasks.notice.switchedToAll";

    public const string SyncNow = "sync.now";
    public const string Syncing = "sync.syncing";
    public const string SyncSucceeded = "sync.succeeded";
    public const string SyncIdle = "sync.idle";
    public const string SyncOffline = "sync.offline";
    public const string SyncNeedsSignIn = "sync.needsSignIn";
    public const string SyncRebuildRequired = "sync.rebuildRequired";
    public const string SyncFailed = "sync.failed";
    public const string LastSyncedNever = "sync.lastSyncedNever";
    public const string LastSyncedAt = "sync.lastSyncedAt";
    public const string ReadOnlyCache = "sync.readOnlyCache";
    public const string OfflineContinue = "sync.offlineContinue";
    public const string RebuildFromSnapshot = "sync.rebuildFromSnapshot";
    public const string RebuildConfirm = "sync.rebuildConfirm";
    public const string RebuildDone = "sync.rebuildDone";

    public const string Server = "server.title";
    public const string ServerUrl = "server.url";
    public const string Connect = "server.connect";
    public const string ServerConnected = "server.connected";
    public const string ServerInvalid = "server.invalid";
    public const string ServerHttpsRequired = "server.httpsRequired";
    public const string ServerSwitchRequiresSignOut = "server.switchRequiresSignOut";
    public const string ServerName = "server.name";

    public const string SignInTitle = "signIn.title";
    public const string SignInHint = "signIn.hint";
    public const string SignInWithPassword = "signIn.withPassword";
    public const string SignInWithCode = "signIn.withCode";
    public const string UsePasswordInstead = "signIn.usePassword";
    public const string UseCodeInstead = "signIn.useCode";
    public const string NoAccount = "signIn.noAccount";
    public const string AlreadyHaveAccount = "signIn.haveAccount";
    public const string SignInOnPhoneDeviceFlow = "signIn.deviceFlow";

    public const string RegisterTitle = "register.title";
    public const string RegisterHint = "register.hint";
    public const string RegisterClosed = "register.closed";
    public const string PasswordPolicyHint = "register.passwordHint";
    public const string PasswordsDoNotMatch = "register.mismatch";
    public const string CodeSent = "code.sent";
    public const string CodeHint = "code.hint";

    public const string ForgotTitle = "forgot.title";
    public const string ForgotHint = "forgot.hint";
    public const string ResetPassword = "forgot.reset";
    public const string ResetDone = "forgot.done";

    public const string SessionExpired = "auth.sessionExpired";
    public const string InvalidCredentials = "auth.invalidCredentials";
    public const string CodeInvalid = "auth.codeInvalid";
    public const string CodeExpired = "auth.codeExpired";
    public const string CodeAttemptsExceeded = "auth.codeAttempts";
    public const string RateLimited = "auth.rateLimited";
    public const string PasswordPolicy = "auth.passwordPolicy";
    public const string EmailInvalid = "auth.emailInvalid";
    public const string RegistrationClosed = "auth.registrationClosed";
    public const string ReauthRequired = "auth.reauthRequired";
    public const string StorageUnavailable = "auth.storageUnavailable";
    public const string NetworkError = "auth.network";
    public const string ServerUnavailableText = "auth.serverUnavailable";
    public const string ProtocolError = "auth.protocol";
    public const string EmailDeliveryFailed = "auth.emailDeliveryFailed";
    public const string UnknownError = "auth.unknown";

    public const string AccountTitle = "account.title";
    public const string AccountSignedOut = "account.signedOut";
    public const string Sessions = "account.sessions";
    public const string CurrentSession = "account.currentSession";
    public const string Revoke = "account.revoke";
    public const string RevokeAll = "account.revokeAll";
    public const string RevokeAllConfirm = "account.revokeAllConfirm";
    public const string RevokeDone = "account.revokeDone";
    public const string SignOutConfirmTitle = "account.signOutTitle";
    public const string SignOutLocalOnly = "account.localOnly";
    public const string SignOutLocalOnlyWarning = "account.localOnlyWarning";
    public const string DeleteAccount = "account.delete";
    public const string DeleteAccountHint = "account.deleteHint";
    public const string DeleteAccountConfirm = "account.deleteConfirm";
    public const string DeleteAccountDone = "account.deleteDone";
    public const string PasswordOrCode = "account.passwordOrCode";
    public const string CacheCleared = "account.cacheCleared";

    public const string DeviceAuthorizeTitle = "device.authorize.title";
    public const string DeviceAuthorizeHint = "device.authorize.hint";
    public const string DeviceCode = "device.code";
    public const string DeviceCodeFormat = "device.codeFormat";
    public const string DeviceInfo = "device.info";
    public const string DeviceApprove = "device.approve";
    public const string DeviceDeny = "device.deny";
    public const string DeviceApproveConfirm = "device.approveConfirm";
    public const string DevicePending = "device.pending";
    public const string DeviceApproved = "device.approved";
    public const string DeviceDenied = "device.denied";
    public const string DeviceExpired = "device.expired";
    public const string DeviceConsumed = "device.consumed";
    public const string DeviceCodeInvalid = "device.invalidCode";
    public const string DeviceAlreadyHandled = "device.alreadyHandled";
    public const string DeviceSignInTitle = "device.signIn.title";
    public const string DeviceSignInHint = "device.signIn.hint";
    public const string DeviceStart = "device.signIn.start";
    public const string DeviceWaiting = "device.signIn.waiting";
    public const string DeviceOpenLink = "device.signIn.openLink";
    public const string DeviceStop = "device.signIn.stop";
    public const string DeviceSignedIn = "device.signIn.signedIn";
    public const string DeviceSignInNeedsServer = "device.signIn.needsServer";

    public const string Theme = "settings.theme";
    public const string ThemeFollowSystem = "settings.theme.system";
    public const string ThemeLight = "settings.theme.light";
    public const string ThemeDark = "settings.theme.dark";
    public const string HighContrastDowngraded = "settings.theme.highContrast";
    public const string Language = "settings.language";
    public const string LanguageFollowSystem = "settings.language.system";
    public const string LanguageChinese = "settings.language.chinese";
    public const string LanguageEnglish = "settings.language.english";
    public const string About = "settings.about";
    public const string Version = "settings.version";
    public const string AppDescription = "settings.description";
    public const string CachedTasks = "settings.cachedTasks";
    public const string LocalPreferenceHint = "settings.localPreferenceHint";
}

/// <summary>
/// Two-language string table. Every user-visible string goes through
/// <see cref="T"/>; changing the language raises one event so every view model
/// can refresh its computed labels.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly IReadOnlyDictionary<string, (string Chinese, string English)> Table =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [StringKeys.AppName] = ("Mosaic", "Mosaic"),
            [StringKeys.Ok] = ("确定", "OK"),
            [StringKeys.Cancel] = ("取消", "Cancel"),
            [StringKeys.Retry] = ("重试", "Retry"),
            [StringKeys.Back] = ("返回", "Back"),
            [StringKeys.Confirm] = ("确认", "Confirm"),
            [StringKeys.Delete] = ("删除", "Delete"),
            [StringKeys.Loading] = ("加载中…", "Loading…"),
            [StringKeys.Error] = ("出错了", "Something went wrong"),
            [StringKeys.Email] = ("邮箱", "Email"),
            [StringKeys.Password] = ("密码", "Password"),
            [StringKeys.ConfirmPassword] = ("确认密码", "Confirm password"),
            [StringKeys.Code] = ("验证码", "Code"),
            [StringKeys.SendCode] = ("发送验证码", "Send code"),
            [StringKeys.ResendIn] = ("{0} 秒后可重发", "Resend in {0}s"),
            [StringKeys.SignIn] = ("登录", "Sign in"),
            [StringKeys.SignOut] = ("登出", "Sign out"),
            [StringKeys.Register] = ("注册", "Create account"),
            [StringKeys.ForgotPassword] = ("忘记密码", "Forgot password"),
            [StringKeys.Required] = ("此项不能为空", "This field is required"),
            [StringKeys.Tasks] = ("任务", "Tasks"),
            [StringKeys.Account] = ("账户", "Account"),
            [StringKeys.Settings] = ("设置", "Settings"),
            [StringKeys.FilterAll] = ("全部", "All"),
            [StringKeys.FilterOpen] = ("未完成", "Open"),
            [StringKeys.FilterCompleted] = ("已完成", "Completed"),
            [StringKeys.FilterArchived] = ("已归档", "Archived"),
            [StringKeys.NoTasks] = ("还没有任务。在桌面端创建后同步即可在此查看。", "No tasks yet. Create one on the desktop, then sync."),
            [StringKeys.NoOpenTasks] = ("没有未完成的任务。", "No open tasks."),
            [StringKeys.NoCompletedTasks] = ("还没有已完成的任务。", "No completed tasks yet."),
            [StringKeys.NoArchivedTasks] = ("没有已归档的任务。", "No archived tasks."),
            [StringKeys.TaskArchived] = ("已归档", "Archived"),
            [StringKeys.TaskCompleted] = ("已完成", "Completed"),
            [StringKeys.TaskOpen] = ("未完成", "Open"),
            [StringKeys.SwitchedToAll] = ("当前筛选已无任务，已切回“全部”。", "The current filter is empty, so the list switched to All."),
            [StringKeys.SyncNow] = ("立即同步", "Sync now"),
            [StringKeys.Syncing] = ("正在同步…", "Syncing…"),
            [StringKeys.SyncSucceeded] = ("已是最新", "Up to date"),
            [StringKeys.SyncIdle] = ("尚未同步", "Not synced yet"),
            [StringKeys.SyncOffline] = ("离线：显示上次同步的缓存。", "Offline: showing the last synced cache."),
            [StringKeys.SyncNeedsSignIn] = ("登录已失效，请重新登录。", "Your session ended. Sign in again."),
            [StringKeys.SyncRebuildRequired] = ("本地同步基线需要重建。", "The local sync baseline needs rebuilding."),
            [StringKeys.SyncFailed] = ("同步失败，稍后重试。", "Sync failed. Try again later."),
            [StringKeys.LastSyncedNever] = ("从未同步", "Never synced"),
            [StringKeys.LastSyncedAt] = ("上次同步：{0}", "Last synced: {0}"),
            [StringKeys.ReadOnlyCache] = ("只读缓存：重新登录后才能同步。", "Read-only cache: sign in again to sync."),
            [StringKeys.OfflineContinue] = ("不连接服务器，离线使用", "Continue offline without a server"),
            [StringKeys.RebuildFromSnapshot] = ("从云端快照重建", "Rebuild from cloud snapshot"),
            [StringKeys.RebuildConfirm] = ("这会用云端快照覆盖本机缓存，确定继续？", "This replaces the local cache with the cloud snapshot. Continue?"),
            [StringKeys.RebuildDone] = ("已从快照恢复。", "Restored from snapshot."),
            [StringKeys.Server] = ("服务器", "Server"),
            [StringKeys.ServerUrl] = ("服务器地址", "Server address"),
            [StringKeys.Connect] = ("连接", "Connect"),
            [StringKeys.ServerConnected] = ("已连接：{0}", "Connected: {0}"),
            [StringKeys.ServerInvalid] = ("服务器地址无效。", "The server address is not valid."),
            [StringKeys.ServerHttpsRequired] = ("服务器必须使用 HTTPS（本机调试地址除外）。", "The server must use HTTPS (localhost development is the only exception)."),
            [StringKeys.ServerSwitchRequiresSignOut] = ("请先登出当前账号再切换服务器。", "Sign out before switching servers."),
            [StringKeys.ServerName] = ("服务器名称", "Server name"),
            [StringKeys.SignInTitle] = ("登录 Mosaic", "Sign in to Mosaic"),
            [StringKeys.SignInHint] = ("登录后，任务与设置会在设备之间同步；不登录也可以继续离线使用。", "Signing in syncs tasks and settings between devices. You can also keep working offline."),
            [StringKeys.SignInWithPassword] = ("使用密码登录", "Sign in with password"),
            [StringKeys.SignInWithCode] = ("使用邮箱验证码登录", "Sign in with an email code"),
            [StringKeys.UsePasswordInstead] = ("改用密码登录", "Use a password instead"),
            [StringKeys.UseCodeInstead] = ("改用验证码登录", "Use an email code instead"),
            [StringKeys.NoAccount] = ("还没有账号？注册", "No account? Create one"),
            [StringKeys.AlreadyHaveAccount] = ("已有账号？登录", "Already have an account? Sign in"),
            [StringKeys.SignInOnPhoneDeviceFlow] = ("在手机上授权登录", "Authorize this phone"),
            [StringKeys.RegisterTitle] = ("创建账号", "Create account"),
            [StringKeys.RegisterHint] = ("验证邮箱后即可创建账号。密码长度 10–256 个字符。", "Verify your email to create the account. Passwords are 10–256 characters."),
            [StringKeys.RegisterClosed] = ("该服务器当前未开放注册。", "This server is not accepting registrations."),
            [StringKeys.PasswordPolicyHint] = ("密码需要 10 至 256 个字符。", "Use 10 to 256 characters."),
            [StringKeys.PasswordsDoNotMatch] = ("两次输入的密码不一致。", "The passwords do not match."),
            [StringKeys.CodeSent] = ("如果该邮箱可用，验证码已发送。", "If the address is available, a code has been sent."),
            [StringKeys.CodeHint] = ("请输入邮件中的 6 位验证码。", "Enter the 6-digit code from the email."),
            [StringKeys.ForgotTitle] = ("重置密码", "Reset password"),
            [StringKeys.ForgotHint] = ("重置后所有设备都需要重新登录。", "After resetting, every device must sign in again."),
            [StringKeys.ResetPassword] = ("重置密码", "Reset password"),
            [StringKeys.ResetDone] = ("密码已重置，请使用新密码登录。", "Password reset. Sign in with the new password."),
            [StringKeys.SessionExpired] = ("登录已失效，请重新登录。", "Your session ended. Sign in again."),
            [StringKeys.InvalidCredentials] = ("邮箱或密码不正确。", "Email or password is incorrect."),
            [StringKeys.CodeInvalid] = ("验证码不正确。", "The code is incorrect."),
            [StringKeys.CodeExpired] = ("验证码已过期，请重新获取。", "The code expired. Request a new one."),
            [StringKeys.CodeAttemptsExceeded] = ("验证码尝试次数过多，请重新获取。", "Too many attempts. Request a new code."),
            [StringKeys.RateLimited] = ("操作过于频繁，请稍后再试。", "Too many requests. Try again shortly."),
            [StringKeys.PasswordPolicy] = ("密码不符合要求。", "The password does not meet the requirements."),
            [StringKeys.EmailInvalid] = ("邮箱地址无效。", "The email address is not valid."),
            [StringKeys.RegistrationClosed] = ("该服务器未开放注册。", "Registration is closed on this server."),
            [StringKeys.ReauthRequired] = ("请先完成二次验证。", "Please complete the extra verification first."),
            [StringKeys.StorageUnavailable] = ("系统安全存储不可用，无法保存登录状态。请检查设备锁屏设置后重试。", "Secure storage is unavailable, so the session cannot be saved. Check the device screen lock and try again."),
            [StringKeys.NetworkError] = ("无法连接服务器，请检查网络。", "Cannot reach the server. Check your connection."),
            [StringKeys.ServerUnavailableText] = ("服务器暂时不可用，请稍后再试。", "The server is temporarily unavailable."),
            [StringKeys.ProtocolError] = ("服务器响应无法识别，请更新应用。", "The server response was not understood. Update the app."),
            [StringKeys.EmailDeliveryFailed] = ("验证码邮件发送失败，请联系服务器管理员。", "The verification email could not be sent. Contact the server administrator."),
            [StringKeys.UnknownError] = ("操作失败，请稍后重试。", "The operation failed. Try again later."),
            [StringKeys.AccountTitle] = ("账户", "Account"),
            [StringKeys.AccountSignedOut] = ("未登录（离线使用中）", "Signed out (working offline)"),
            [StringKeys.Sessions] = ("已登录的设备", "Signed-in devices"),
            [StringKeys.CurrentSession] = ("本机", "This device"),
            [StringKeys.Revoke] = ("撤销", "Revoke"),
            [StringKeys.RevokeAll] = ("撤销全部会话", "Revoke all sessions"),
            [StringKeys.RevokeAllConfirm] = ("所有设备（包括本机）都将需要重新登录。", "Every device, including this one, will need to sign in again."),
            [StringKeys.RevokeDone] = ("已撤销。", "Revoked."),
            [StringKeys.SignOutConfirmTitle] = ("确认登出", "Confirm sign-out"),
            [StringKeys.SignOutLocalOnly] = ("仅本地登出", "Sign out locally only"),
            [StringKeys.SignOutLocalOnlyWarning] = ("服务器仍认为该设备已登录。请仅在服务器不可用时使用，稍后应撤销该会话。", "The server still considers this device signed in. Use this only when the server is unreachable, and revoke the session later."),
            [StringKeys.DeleteAccount] = ("删除账号", "Delete account"),
            [StringKeys.DeleteAccountHint] = ("账号删除后不可恢复，服务器会立即撤销所有会话；本机缓存也会被清空。", "Deleting the account cannot be undone. All sessions are revoked and the local cache is erased."),
            [StringKeys.DeleteAccountConfirm] = ("确定删除账号？此操作不可撤销。", "Delete the account? This cannot be undone."),
            [StringKeys.DeleteAccountDone] = ("账号已删除。", "The account was deleted."),
            [StringKeys.PasswordOrCode] = ("输入密码，或发送验证码以确认身份。", "Enter your password, or send a code to confirm."),
            [StringKeys.CacheCleared] = ("本机缓存已清空。", "The local cache was cleared."),
            [StringKeys.DeviceAuthorizeTitle] = ("授权桌面登录", "Authorize a desktop"),
            [StringKeys.DeviceAuthorizeHint] = ("输入桌面端显示的授权码。手机只负责批准，不会保存桌面的登录令牌。", "Enter the code shown on the desktop. The phone only approves it and never stores the desktop token."),
            [StringKeys.DeviceCode] = ("授权码", "Authorization code"),
            [StringKeys.DeviceCodeFormat] = ("格式如 BCDF-GHJK", "Format: BCDF-GHJK"),
            [StringKeys.DeviceInfo] = ("请求授权的设备", "Device requesting access"),
            [StringKeys.DeviceApprove] = ("批准", "Approve"),
            [StringKeys.DeviceDeny] = ("拒绝", "Deny"),
            [StringKeys.DeviceApproveConfirm] = ("批准这台设备登录你的 Mosaic 账号？", "Allow this device to sign in to your Mosaic account?"),
            [StringKeys.DevicePending] = ("该授权码仍在等待处理。", "The code is still pending."),
            [StringKeys.DeviceApproved] = ("已批准。设备将很快完成登录。", "Approved. The device will finish signing in shortly."),
            [StringKeys.DeviceDenied] = ("已拒绝。", "Denied."),
            [StringKeys.DeviceExpired] = ("授权码已过期，请在桌面端重新获取。", "The code expired. Request a new one on the desktop."),
            [StringKeys.DeviceConsumed] = ("该授权码已被使用。", "The code was already used."),
            [StringKeys.DeviceCodeInvalid] = ("授权码无效。", "The code is not valid."),
            [StringKeys.DeviceAlreadyHandled] = ("该授权码已被处理。", "The code was already handled."),
            [StringKeys.DeviceSignInTitle] = ("在一台已登录的设备上授权", "Authorize on a signed-in device"),
            [StringKeys.DeviceSignInHint] = ("生成授权码后，在一台已登录 Mosaic 的手机上输入该码完成登录。", "Generate a code, then enter it on a phone already signed in to Mosaic."),
            [StringKeys.DeviceStart] = ("生成授权码", "Generate code"),
            [StringKeys.DeviceWaiting] = ("等待批准…", "Waiting for approval…"),
            [StringKeys.DeviceOpenLink] = ("打开授权页面", "Open authorization page"),
            [StringKeys.DeviceStop] = ("停止等待", "Stop waiting"),
            [StringKeys.DeviceSignedIn] = ("登录成功。", "Signed in."),
            [StringKeys.DeviceSignInNeedsServer] = ("请先填写服务器地址。", "Enter the server address first."),
            [StringKeys.Theme] = ("外观", "Appearance"),
            [StringKeys.ThemeFollowSystem] = ("跟随系统", "Follow system"),
            [StringKeys.ThemeLight] = ("浅色", "Light"),
            [StringKeys.ThemeDark] = ("深色", "Dark"),
            [StringKeys.HighContrastDowngraded] = ("云端的“高对比度”主题已按“跟随系统”显示。", "The cloud High contrast theme is shown as Follow system."),
            [StringKeys.Language] = ("语言", "Language"),
            [StringKeys.LanguageFollowSystem] = ("跟随系统", "Follow system"),
            [StringKeys.LanguageChinese] = ("中文", "Chinese"),
            [StringKeys.LanguageEnglish] = ("English", "English"),
            [StringKeys.About] = ("关于", "About"),
            [StringKeys.Version] = ("版本", "Version"),
            [StringKeys.AppDescription] = ("Mosaic 手机端：查看同步到账号的任务与设置。", "Mosaic Mobile: view tasks and settings synced to your account."),
            [StringKeys.CachedTasks] = ("本机缓存任务：{0}", "Cached tasks: {0}"),
            [StringKeys.LocalPreferenceHint] = ("外观与语言只保存在本机；任务与设置由服务器同步。", "Appearance and language stay on this device; tasks and settings sync from the server."),
        };

    private AppLanguagePreference _preference = AppLanguagePreference.FollowSystem;
    private string _systemLanguage = "zh-Hans";

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public AppLanguage Current { get; private set; } = AppLanguage.Chinese;

    /// <summary>Bindable self-reference: <c>{Binding Strings[app.name]}</c>.</summary>
    public LocalizationService Strings => this;

    public AppLanguagePreference Preference => _preference;

    public void Apply(AppLanguagePreference preference, string? systemLanguageTag)
    {
        _preference = preference;
        if (!string.IsNullOrWhiteSpace(systemLanguageTag))
        {
            _systemLanguage = systemLanguageTag;
        }

        var resolved = preference switch
        {
            AppLanguagePreference.Chinese => AppLanguage.Chinese,
            AppLanguagePreference.English => AppLanguage.English,
            _ => _systemLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? AppLanguage.Chinese
                : AppLanguage.English,
        };

        Current = resolved;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string T(string key)
    {
        if (!Table.TryGetValue(key, out var value))
        {
            return key;
        }

        return Current == AppLanguage.Chinese ? value.Chinese : value.English;
    }

    public string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, T(key), arguments);

    public string CultureName => Current == AppLanguage.Chinese ? "zh-Hans" : "en";
}
