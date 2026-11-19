using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mosaic.Auth.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;

namespace Mosaic.Auth.Wpf.Internal;

/// <summary>
/// The account entry in the window status bar. It observes <see cref="IAuthApi"/> only: when
/// signed in it shows the account name and email, otherwise it reports the offline state.
/// Clicking the entry opens the account page through the generic status-bar navigation contract,
/// so the shell never learns anything about authentication.
/// </summary>
internal sealed class AccountStatusItem : ObservableObject, IStatusContribution, IDisposable
{
    private readonly IAuthApi _auth;
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;

    public AccountStatusItem(IAuthApi auth, IUiDispatcher dispatcher)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _auth.StateChanged += OnAuthChanged;
        MosaicLocalizationManager.LanguageChanged += OnLanguageChanged;
    }

    public string Id => "account.status";

    public int Order => 0;

    public string? TargetViewId => "account.home";

    public string Glyph => "\uE77B"; // Contact

    public bool IsSignedIn => _auth.State.Status == AuthStatus.SignedIn;

    public string Text => IsSignedIn ? AccountName : Localize("Auth.Status.SignedOut", "未登录");

    public string? Detail => IsSignedIn
        ? (string.IsNullOrWhiteSpace(_auth.State.Account?.DisplayName) ? null : _auth.State.Account?.Email)
        : Localize("Auth.Status.Offline", "离线");

    public string? ToolTip => IsSignedIn
        ? string.Format(CultureInfo.CurrentCulture, Localize("Auth.Status.Tooltip", "{0}，点击进入账户页"), Text)
        : Localize("Auth.Status.Tooltip.SignedOut", "离线：未登录，点击进入账户页");

    private string AccountName => _auth.State.Account is { } account
        ? (string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : account.DisplayName)
        : "";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _auth.StateChanged -= OnAuthChanged;
        MosaicLocalizationManager.LanguageChanged -= OnLanguageChanged;
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