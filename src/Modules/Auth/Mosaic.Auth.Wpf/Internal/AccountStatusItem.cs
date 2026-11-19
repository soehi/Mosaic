using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mosaic.Auth.Contracts;
using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Settings.Contracts;

namespace Mosaic.Auth.Wpf.Internal;

/// <summary>
/// The account entry in the window status bar and the sidebar's bottom area. It observes
/// <see cref="IAuthApi"/> only: when signed in it shows the account name and email, otherwise it
/// reports the offline state. Clicking the entry opens the account page through the generic
/// status navigation contract, so the shell never learns anything about authentication.
/// While signed in it also offers a sign-out action: the shell renders the button generically
/// and forwards the request, and this item runs the module's sign-out flow (server session
/// revoked, local tasks kept) and clears the first-run choice flag so the account-choice page
/// comes back.
/// </summary>
internal sealed class AccountStatusItem : ObservableObject, IStatusContribution, IDisposable
{
    private const string AccountChoiceMadeId = "auth.accountChoiceMade";

    private readonly IAuthApi _auth;
    private readonly ISettingsApi _settings;
    private readonly IUserDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly IDiagnosticSink _diagnostics;
    private bool _disposed;
    private bool _signingOut;
    private Task? _signOutTask;

    public AccountStatusItem(
        IAuthApi auth,
        ISettingsApi settings,
        IUserDialogService dialogs,
        IUiDispatcher dispatcher,
        IDiagnosticSink diagnostics)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _auth.StateChanged += OnAuthChanged;
        MosaicLocalizationManager.LanguageChanged += OnLanguageChanged;
    }

    public string Id => "account.status";

    public int Order => 0;

    public string? TargetViewId => "account.home";

    public string Glyph => ""; // Contact

    // The same contribution renders in the sidebar's bottom area next to the workspace status.
    public StatusPlacement SidebarPlacement => StatusPlacement.Bottom;

    public bool IsSignedIn => _auth.State.Status == AuthStatus.SignedIn;

    public string Text => IsSignedIn ? AccountName : Localize("Auth.Status.SignedOut", "未登录");

    public string? Detail => IsSignedIn
        ? (string.IsNullOrWhiteSpace(_auth.State.Account?.DisplayName) ? null : _auth.State.Account?.Email)
        : Localize("Auth.Status.Offline", "离线");

    public string? ToolTip => IsSignedIn
        ? string.Format(CultureInfo.CurrentCulture, Localize("Auth.Status.Tooltip", "{0}，点击进入账户页"), Text)
        : Localize("Auth.Status.Tooltip.SignedOut", "离线：未登录，点击进入账户页");

    // Sign-out is the one sidebar action; the shell renders it whenever this reports available.
    public bool CanAct => IsSignedIn && !_signingOut;

    public string? ActionGlyph => ""; // Leave

    public string? ActionLabel => Localize("Auth.Status.SignOut", "退出登录");

    public string? ActionToolTip => Localize("Auth.Status.SignOut.Tooltip", "退出登录并重新选择账号方式");

    public event EventHandler? ActionRequested;

    public void RequestAction() => _signOutTask = SignOutAsync();

    /// <summary>Test helper: runs the sign-out action and awaits its completion, since the
    /// shell invokes <see cref="RequestAction"/> fire-and-forget.</summary>
    public Task RequestActionAndWaitForIdleAsync()
    {
        RequestAction();
        return _signOutTask ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _auth.StateChanged -= OnAuthChanged;
        MosaicLocalizationManager.LanguageChanged -= OnLanguageChanged;
    }

    private string AccountName => _auth.State.Account is { } account
        ? (string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : account.DisplayName)
        : "";

    private async Task SignOutAsync()
    {
        if (_disposed || _signingOut || !IsSignedIn) return;
        var confirmed = await _dialogs.ConfirmAsync(
            Localize("Auth.Account.Logout", "退出登录"),
            Localize("Auth.Logout.Confirm", "退出后本机保存的登录凭据会被清除，任务数据仍保留在本机。")).ConfigureAwait(true);
        if (!confirmed || _disposed) return;
        _signingOut = true;
        RaiseChanged();
        try
        {
            // The server session is revoked; local tasks stay on this device.
            var result = await _auth.LogoutAsync().ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                await _dialogs.ShowErrorAsync(AuthLocalization.Error(result.Error)).ConfigureAwait(true);
                return;
            }
            // Signing out re-arms the account-choice flow for the next launch.
            await ClearAccountChoiceAsync().ConfigureAwait(true);
            if (!_disposed) ActionRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _diagnostics.Report(Guid.NewGuid().ToString("N"), ex);
            await _dialogs.ShowErrorAsync(Localize("Auth.Error.Unknown", "操作未完成，请稍后再试。")).ConfigureAwait(true);
        }
        finally
        {
            _signingOut = false;
            RaiseChanged();
        }
    }

    private async Task ClearAccountChoiceAsync()
    {
        try
        {
            var session = await _settings.BeginEditSessionAsync().ConfigureAwait(true);
            if (session.IsSuccess && session.Value is { } edit)
            {
                await using (edit.ConfigureAwait(true))
                {
                    var preview = await edit.PreviewAsync(
                        new Dictionary<string, object?> { [AccountChoiceMadeId] = null }, 1).ConfigureAwait(true);
                    if (preview.IsSuccess) await edit.CommitAsync(1).ConfigureAwait(true);
                }
            }
        }
        catch (Exception ex) { _diagnostics.Report(Guid.NewGuid().ToString("N"), ex); }
    }

    private void OnAuthChanged(object? sender, EventArgs e) => Refresh();

    private void OnLanguageChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) RaiseChanged();
        else _ = _dispatcher.InvokeAsync(() => { if (!_disposed) RaiseChanged(); });
    }

    private void RaiseChanged() => OnPropertyChanged(string.Empty);

    private static string Localize(string key, string fallback) => AuthLocalization.Localize(key, fallback);
}
