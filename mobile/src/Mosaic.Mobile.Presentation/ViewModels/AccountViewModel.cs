using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Presentation;

public sealed record SessionRowViewModel(
    string Id,
    string Label,
    string Platform,
    string CreatedText,
    bool Current,
    string RevokeText);

public sealed partial class AccountViewModel : ViewModelBase
{
    private readonly AuthSessionManager _auth;
    private IReadOnlyList<SessionInfoDto> _sessionModels = [];

    public AccountViewModel(AuthSessionManager auth, LocalizationService localizer)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(auth);
        _auth = auth;
        _auth.StateChanged += OnAuthStateChanged;
        _auth.LocalDataCleared += OnLocalDataCleared;
        UpdateFromAuth();
    }

    public event EventHandler? SignedOut;

    public event EventHandler? SignInRequested;

    public ObservableCollection<SessionRowViewModel> Sessions { get; } = [];

    [ObservableProperty]
    private string email = string.Empty;

    [ObservableProperty]
    private string stateText = string.Empty;

    [ObservableProperty]
    private string errorText = string.Empty;

    [ObservableProperty]
    private string noticeText = string.Empty;

    [ObservableProperty]
    private string deletePassword = string.Empty;

    [ObservableProperty]
    private string deleteCode = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isSignedIn;

    [ObservableProperty]
    private bool localOnlyPromptVisible;

    [ObservableProperty]
    private bool revokeAllConfirmVisible;

    [ObservableProperty]
    private bool deleteConfirmVisible;

    public bool ShowSignIn => !IsSignedIn;

    public bool ShowSessions => IsSignedIn;

    public bool HasSessions => Sessions.Count > 0;

    public string TitleText => T(StringKeys.AccountTitle);

    public string SignedOutText => T(StringKeys.AccountSignedOut);

    public string SessionsTitle => T(StringKeys.Sessions);

    public string SignInText => T(StringKeys.SignIn);

    public string SignOutText => T(StringKeys.SignOut);

    public string RevokeAllText => T(StringKeys.RevokeAll);

    public string RevokeAllConfirmText => T(StringKeys.RevokeAllConfirm);

    public string LocalOnlyText => T(StringKeys.SignOutLocalOnly);

    public string LocalOnlyWarningText => T(StringKeys.SignOutLocalOnlyWarning);

    public string DeleteAccountText => T(StringKeys.DeleteAccount);

    public string AuthorizeDesktopText => T(StringKeys.DeviceAuthorizeTitle);

    public string DeleteAccountHintText => T(StringKeys.DeleteAccountHint);

    public string DeleteAccountConfirmText => T(StringKeys.DeleteAccountConfirm);

    public string PasswordOrCodeText => T(StringKeys.PasswordOrCode);

    public string PasswordLabel => T(StringKeys.Password);

    public string CodeLabel => T(StringKeys.Code);

    public string SendCodeText => T(StringKeys.SendCode);

    public string ConfirmText => T(StringKeys.Confirm);

    public string CancelText => T(StringKeys.Cancel);

    partial void OnIsSignedInChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSignIn));
        OnPropertyChanged(nameof(ShowSessions));
    }

    protected override void RefreshLocalized()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(SignedOutText));
        OnPropertyChanged(nameof(SessionsTitle));
        OnPropertyChanged(nameof(SignInText));
        OnPropertyChanged(nameof(SignOutText));
        OnPropertyChanged(nameof(RevokeAllText));
        OnPropertyChanged(nameof(RevokeAllConfirmText));
        OnPropertyChanged(nameof(LocalOnlyText));
        OnPropertyChanged(nameof(LocalOnlyWarningText));
        OnPropertyChanged(nameof(DeleteAccountText));
        OnPropertyChanged(nameof(AuthorizeDesktopText));
        OnPropertyChanged(nameof(DeleteAccountHintText));
        OnPropertyChanged(nameof(DeleteAccountConfirmText));
        OnPropertyChanged(nameof(PasswordOrCodeText));
        OnPropertyChanged(nameof(PasswordLabel));
        OnPropertyChanged(nameof(CodeLabel));
        OnPropertyChanged(nameof(SendCodeText));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(CancelText));
        StateText = IsSignedIn ? Email : T(StringKeys.AccountSignedOut);
        RebuildRows();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        UpdateFromAuth();
        if (!IsSignedIn)
        {
            Sessions.Clear();
            OnPropertyChanged(nameof(HasSessions));
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _auth.GetSessionsAsync().ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ErrorText = Describe(result.Error);
                return;
            }

            _sessionModels = result.Value ?? [];
            RebuildRows();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BeginSignIn() => SignInRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task SignOutAsync()
    {
        ErrorText = string.Empty;
        NoticeText = string.Empty;
        LocalOnlyPromptVisible = false;
        IsBusy = true;
        try
        {
            var result = await _auth.SignOutAsync(allDevices: false, localOnly: false).ConfigureAwait(true);
            if (result.Completed)
            {
                NoticeText = T(StringKeys.CacheCleared);
                SignedOut?.Invoke(this, EventArgs.Empty);
                return;
            }

            ErrorText = Describe(result.Error);
            LocalOnlyPromptVisible = result.Error?.Kind == AuthErrorKind.Network;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmLocalSignOutAsync()
    {
        LocalOnlyPromptVisible = false;
        var result = await _auth.SignOutAsync(allDevices: false, localOnly: true).ConfigureAwait(true);
        if (result.Completed)
        {
            NoticeText = T(StringKeys.CacheCleared);
            SignedOut?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ErrorText = Describe(result.Error);
        }
    }

    [RelayCommand]
    private void CancelLocalSignOut() => LocalOnlyPromptVisible = false;

    [RelayCommand]
    private async Task RevokeAsync(SessionRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        ErrorText = string.Empty;
        var result = await _auth.RevokeSessionAsync(row.Id, row.Current).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            ErrorText = Describe(result.Error);
            return;
        }

        NoticeText = T(StringKeys.RevokeDone);
        if (row.Current)
        {
            SignedOut?.Invoke(this, EventArgs.Empty);
            UpdateFromAuth();
            return;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void BeginRevokeAll() => RevokeAllConfirmVisible = true;

    [RelayCommand]
    private void CancelRevokeAll() => RevokeAllConfirmVisible = false;

    [RelayCommand]
    private async Task ConfirmRevokeAllAsync()
    {
        RevokeAllConfirmVisible = false;
        ErrorText = string.Empty;
        var result = await _auth.RevokeAllSessionsAsync().ConfigureAwait(true);
        if (!result.Succeeded)
        {
            ErrorText = Describe(result.Error);
            return;
        }

        NoticeText = T(StringKeys.RevokeDone);
        SignedOut?.Invoke(this, EventArgs.Empty);
        UpdateFromAuth();
    }

    [RelayCommand]
    private async Task SendDeleteCodeAsync()
    {
        ErrorText = string.Empty;
        var email = _auth.AccountEmail;
        if (string.IsNullOrWhiteSpace(email))
        {
            ErrorText = T(StringKeys.SessionExpired);
            return;
        }

        var result = await _auth.RequestDeleteAccountCodeAsync(email).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            ErrorText = Describe(result.Error);
            return;
        }

        NoticeText = T(StringKeys.CodeSent);
    }

    [RelayCommand]
    private void BeginDeleteAccount()
    {
        ErrorText = string.Empty;
        DeleteConfirmVisible = true;
    }

    [RelayCommand]
    private void CancelDeleteAccount()
    {
        DeleteConfirmVisible = false;
        DeletePassword = string.Empty;
        DeleteCode = string.Empty;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAccountAsync()
    {
        if (string.IsNullOrEmpty(DeletePassword) && string.IsNullOrWhiteSpace(DeleteCode))
        {
            ErrorText = T(StringKeys.PasswordOrCode);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _auth
                .DeleteAccountAsync(
                    string.IsNullOrEmpty(DeletePassword) ? null : DeletePassword,
                    string.IsNullOrWhiteSpace(DeleteCode) ? null : DeleteCode)
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ErrorText = Describe(result.Error);
                return;
            }

            DeleteConfirmVisible = false;
            DeletePassword = string.Empty;
            DeleteCode = string.Empty;
            NoticeText = T(StringKeys.DeleteAccountDone);
            SignedOut?.Invoke(this, EventArgs.Empty);
            UpdateFromAuth();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public override void Dispose()
    {
        _auth.StateChanged -= OnAuthStateChanged;
        _auth.LocalDataCleared -= OnLocalDataCleared;
        base.Dispose();
    }

    private void OnAuthStateChanged(object? sender, AuthStateChangedEventArgs e) => UpdateFromAuth();

    private void OnLocalDataCleared(object? sender, string reason)
    {
        Sessions.Clear();
        _sessionModels = [];
        OnPropertyChanged(nameof(HasSessions));
    }

    private void UpdateFromAuth()
    {
        IsSignedIn = _auth.State == AuthState.SignedIn;
        Email = _auth.AccountEmail ?? string.Empty;
        StateText = IsSignedIn ? Email : T(StringKeys.AccountSignedOut);
        if (!IsSignedIn)
        {
            Sessions.Clear();
            _sessionModels = [];
            OnPropertyChanged(nameof(HasSessions));
        }
    }

    private void RebuildRows()
    {
        Sessions.Clear();
        foreach (var session in _sessionModels)
        {
            var label = string.IsNullOrWhiteSpace(session.DeviceLabel)
                ? session.Platform ?? "device"
                : session.DeviceLabel;
            if (session.Current)
            {
                label = $"{label} · {T(StringKeys.CurrentSession)}";
            }

            Sessions.Add(new SessionRowViewModel(
                session.Id ?? string.Empty,
                label,
                session.Platform ?? string.Empty,
                session.CreatedAt is { } created
                    ? created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                    : string.Empty,
                session.Current,
                T(StringKeys.Revoke)));
        }

        OnPropertyChanged(nameof(HasSessions));
    }
}
