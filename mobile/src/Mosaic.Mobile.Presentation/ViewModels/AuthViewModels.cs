using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Presentation;

public sealed partial class SignInViewModel : ViewModelBase
{
    private readonly AuthSessionManager _auth;
    private AuthPolicyDto? _policy;

    public SignInViewModel(AuthSessionManager auth, LocalizationService localizer)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(auth);
        _auth = auth;
        ServerUrl = auth.ServerUrl;
    }

    public event EventHandler? SignedIn;

    public event EventHandler? RegisterRequested;

    public event EventHandler? ForgotPasswordRequested;

    public event EventHandler? DeviceFlowRequested;

    public event EventHandler? OfflineRequested;

    [ObservableProperty]
    private string serverUrl = string.Empty;

    [ObservableProperty]
    private string email = string.Empty;

    [ObservableProperty]
    private string password = string.Empty;

    [ObservableProperty]
    private string code = string.Empty;

    [ObservableProperty]
    private bool useCodeLogin;

    [ObservableProperty]
    private string errorText = string.Empty;

    [ObservableProperty]
    private string noticeText = string.Empty;

    [ObservableProperty]
    private string serverNameText = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool serverConnected;

    public bool IsPasswordMode => !UseCodeLogin;

    public bool IsRegistrationClosed => _policy is { RegistrationOpen: false };

    public string SignInTitleText => T(StringKeys.SignInTitle);

    public string SignInHintText => T(StringKeys.SignInHint);

    public string ConnectText => T(StringKeys.Connect);

    public string ServerUrlLabel => T(StringKeys.ServerUrl);

    public string SignInText => T(StringKeys.SignIn);

    public string SendCodeText => T(StringKeys.SendCode);

    public string PasswordModeText => T(StringKeys.UsePasswordInstead);

    public string CodeModeText => T(StringKeys.UseCodeInstead);

    public string ForgotText => T(StringKeys.ForgotPassword);

    public string RegisterText => T(StringKeys.NoAccount);

    public string OfflineText => T(StringKeys.OfflineContinue);

    public string DeviceFlowText => T(StringKeys.SignInOnPhoneDeviceFlow);

    public string EmailLabel => T(StringKeys.Email);

    public string PasswordLabel => T(StringKeys.Password);

    public string CodeLabel => T(StringKeys.Code);

    partial void OnUseCodeLoginChanged(bool value)
    {
        ErrorText = string.Empty;
        OnPropertyChanged(nameof(IsPasswordMode));
    }

    protected override void RefreshLocalized()
    {
        OnPropertyChanged(nameof(SignInTitleText));
        OnPropertyChanged(nameof(SignInHintText));
        OnPropertyChanged(nameof(ConnectText));
        OnPropertyChanged(nameof(ServerUrlLabel));
        OnPropertyChanged(nameof(SignInText));
        OnPropertyChanged(nameof(SendCodeText));
        OnPropertyChanged(nameof(PasswordModeText));
        OnPropertyChanged(nameof(CodeModeText));
        OnPropertyChanged(nameof(ForgotText));
        OnPropertyChanged(nameof(RegisterText));
        OnPropertyChanged(nameof(OfflineText));
        OnPropertyChanged(nameof(DeviceFlowText));
        OnPropertyChanged(nameof(EmailLabel));
        OnPropertyChanged(nameof(PasswordLabel));
        OnPropertyChanged(nameof(CodeLabel));
    }

    [RelayCommand]
    private async Task ConnectServerAsync()
    {
        ErrorText = string.Empty;
        var probe = await _auth.ProbePolicyAsync(ServerUrl).ConfigureAwait(true);
        if (!probe.IsSuccess)
        {
            ErrorText = Describe(AuthError.FromApi(probe.StatusCode, probe.Error, probe.RetryAfter));
            return;
        }

        var configured = _auth.ConfigureServer(ServerUrl);
        if (!configured.Succeeded)
        {
            ErrorText = Describe(configured.Error);
            return;
        }

        _policy = probe.Value;
        ServerUrl = _auth.ServerUrl;
        ServerConnected = true;
        var name = string.IsNullOrWhiteSpace(_policy?.ServerName) ? ServerUrl : _policy.ServerName;
        ServerNameText = F(StringKeys.ServerConnected, name);
        NoticeText = IsRegistrationClosed ? T(StringKeys.RegisterClosed) : string.Empty;
        OnPropertyChanged(nameof(IsRegistrationClosed));
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorText = string.Empty;
        NoticeText = string.Empty;

        await EnsureServerAsync().ConfigureAwait(true);
        if (!ServerConnected)
        {
            return;
        }

        if (UseCodeLogin)
        {
            if (!AuthInputRules.IsEmailCodeShapeValid(Code))
            {
                ErrorText = T(StringKeys.CodeHint);
                return;
            }
        }
        else if (string.IsNullOrEmpty(Password))
        {
            ErrorText = T(StringKeys.Required);
            return;
        }

        IsBusy = true;
        try
        {
            var result = UseCodeLogin
                ? await _auth.SignInWithCodeAsync(Email, Code, DeviceLabel()).ConfigureAwait(true)
                : await _auth.SignInAsync(Email, Password, DeviceLabel()).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ErrorText = Describe(result.Error);
                return;
            }

            Password = string.Empty;
            Code = string.Empty;
            SignedIn?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SendLoginCodeAsync()
    {
        ErrorText = string.Empty;
        await EnsureServerAsync().ConfigureAwait(true);
        if (!ServerConnected)
        {
            return;
        }

        var result = await _auth.RequestEmailCodeAsync(Email, EmailCodePurpose.Login)
            .ConfigureAwait(true);
        if (!result.Succeeded)
        {
            ErrorText = Describe(result.Error);
            return;
        }

        NoticeText = T(StringKeys.CodeSent);
    }

    [RelayCommand]
    private void UsePasswordMode() => UseCodeLogin = false;

    [RelayCommand]
    private void UseCodeMode() => UseCodeLogin = true;

    [RelayCommand]
    private void OpenRegister() => RegisterRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenForgot() => ForgotPasswordRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenDeviceFlow() => DeviceFlowRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void GoOffline() => OfflineRequested?.Invoke(this, EventArgs.Empty);

    private async Task EnsureServerAsync()
    {
        if (ServerConnected)
        {
            return;
        }

        await ConnectServerAsync().ConfigureAwait(true);
    }

    private static string DeviceLabel() => $"{MobileDeviceInfo.Platform} {MobileDeviceInfo.DeviceName}";
}

public sealed partial class RegisterViewModel : ViewModelBase
{
    private readonly AuthSessionManager _auth;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _countdown;
    private AuthPolicyDto? _policy;

    public RegisterViewModel(AuthSessionManager auth, LocalizationService localizer, TimeProvider? time = null)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(auth);
        _auth = auth;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler? Registered;

    public event EventHandler? BackRequested;

    [ObservableProperty]
    private string email = string.Empty;

    [ObservableProperty]
    private string code = string.Empty;

    [ObservableProperty]
    private string password = string.Empty;

    [ObservableProperty]
    private string confirmPassword = string.Empty;

    [ObservableProperty]
    private string errorText = string.Empty;

    [ObservableProperty]
    private string noticeText = string.Empty;

    [ObservableProperty]
    private string deviceLabel = string.Empty;

    [ObservableProperty]
    private int resendRemaining;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool registrationClosed;

    public bool CanSendCode => ResendRemaining <= 0 && !IsBusy;

    public string ResendText => ResendRemaining > 0
        ? F(StringKeys.ResendIn, ResendRemaining)
        : T(StringKeys.SendCode);

    public string TitleText => T(StringKeys.RegisterTitle);

    public string HintText => T(StringKeys.RegisterHint);

    public string SendCodeText => ResendText;

    public string RegisterText => T(StringKeys.Register);

    public string PasswordHintText => T(StringKeys.PasswordPolicyHint);

    public string BackText => T(StringKeys.AlreadyHaveAccount);

    public string EmailLabel => T(StringKeys.Email);

    public string CodeLabel => T(StringKeys.Code);

    public string PasswordLabel => T(StringKeys.Password);

    public string ConfirmLabel => T(StringKeys.ConfirmPassword);

    public async Task LoadPolicyAsync()
    {
        var policy = await _auth.ProbePolicyAsync(_auth.ServerUrl).ConfigureAwait(true);
        if (policy.IsSuccess && policy.Value is { } value)
        {
            _policy = value;
            RegistrationClosed = !value.RegistrationOpen;
            if (RegistrationClosed)
            {
                ErrorText = T(StringKeys.RegisterClosed);
            }
        }
    }

    partial void OnResendRemainingChanged(int value)
    {
        OnPropertyChanged(nameof(CanSendCode));
        OnPropertyChanged(nameof(ResendText));
        OnPropertyChanged(nameof(SendCodeText));
    }

    protected override void RefreshLocalized()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(SendCodeText));
        OnPropertyChanged(nameof(RegisterText));
        OnPropertyChanged(nameof(PasswordHintText));
        OnPropertyChanged(nameof(BackText));
        OnPropertyChanged(nameof(EmailLabel));
        OnPropertyChanged(nameof(CodeLabel));
        OnPropertyChanged(nameof(PasswordLabel));
        OnPropertyChanged(nameof(ConfirmLabel));
    }

    [RelayCommand]
    private async Task SendCodeAsync()
    {
        if (!CanSendCode)
        {
            return;
        }

        ErrorText = string.Empty;
        var result = await _auth.RequestEmailCodeAsync(Email, EmailCodePurpose.Register)
            .ConfigureAwait(true);
        if (!result.Succeeded)
        {
            ErrorText = Describe(result.Error);
            return;
        }

        NoticeText = T(StringKeys.CodeSent);
        var seconds = result.Value?.ResendAfterSeconds
            ?? _policy?.ResendAfterSeconds
            ?? 60;
        StartCountdown(seconds, result.RetryAfter);
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorText = string.Empty;

        if (!AuthInputRules.IsEmailShapeValid(Email))
        {
            ErrorText = T(StringKeys.EmailInvalid);
            return;
        }

        if (!AuthInputRules.IsEmailCodeShapeValid(Code))
        {
            ErrorText = T(StringKeys.CodeHint);
            return;
        }

        if (!AuthInputRules.IsPasswordLengthValid(Password))
        {
            ErrorText = T(StringKeys.PasswordPolicy);
            return;
        }

        if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
        {
            ErrorText = T(StringKeys.PasswordsDoNotMatch);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _auth
                .RegisterAsync(Email, Password, Code, DeviceLabel)
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ErrorText = Describe(result.Error);
                return;
            }

            Password = string.Empty;
            ConfirmPassword = string.Empty;
            Code = string.Empty;
            Registered?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    public override void Dispose()
    {
        _countdown?.Cancel();
        _countdown?.Dispose();
        base.Dispose();
    }

    private void StartCountdown(int seconds, TimeSpan? retryAfter)
    {
        _countdown?.Cancel();
        _countdown?.Dispose();
        _countdown = new CancellationTokenSource();
        var total = retryAfter is { } wait && wait.TotalSeconds > seconds
            ? (int)Math.Ceiling(wait.TotalSeconds)
            : seconds;
        _ = CountdownAsync(Math.Max(1, total), _countdown.Token);
    }

    private async Task CountdownAsync(int seconds, CancellationToken cancellationToken)
    {
        for (var remaining = seconds; remaining > 0; remaining--)
        {
            ResendRemaining = remaining;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        ResendRemaining = 0;
    }
}

public sealed partial class ForgotPasswordViewModel : ViewModelBase
{
    private readonly AuthSessionManager _auth;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _countdown;

    public ForgotPasswordViewModel(AuthSessionManager auth, LocalizationService localizer, TimeProvider? time = null)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(auth);
        _auth = auth;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler? Completed;

    public event EventHandler? BackRequested;

    [ObservableProperty]
    private string email = string.Empty;

    [ObservableProperty]
    private string code = string.Empty;

    [ObservableProperty]
    private string newPassword = string.Empty;

    [ObservableProperty]
    private string confirmPassword = string.Empty;

    [ObservableProperty]
    private string errorText = string.Empty;

    [ObservableProperty]
    private string noticeText = string.Empty;

    [ObservableProperty]
    private int resendRemaining;

    [ObservableProperty]
    private bool isBusy;

    public bool CanSendCode => ResendRemaining <= 0 && !IsBusy;

    public string TitleText => T(StringKeys.ForgotTitle);

    public string HintText => T(StringKeys.ForgotHint);

    public string SendCodeText => ResendRemaining > 0
        ? F(StringKeys.ResendIn, ResendRemaining)
        : T(StringKeys.SendCode);

    public string ResetText => T(StringKeys.ResetPassword);

    public string BackText => T(StringKeys.Back);

    public string EmailLabel => T(StringKeys.Email);

    public string CodeLabel => T(StringKeys.Code);

    public string PasswordLabel => T(StringKeys.Password);

    public string ConfirmLabel => T(StringKeys.ConfirmPassword);

    partial void OnResendRemainingChanged(int value)
    {
        OnPropertyChanged(nameof(CanSendCode));
        OnPropertyChanged(nameof(SendCodeText));
    }

    protected override void RefreshLocalized()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(SendCodeText));
        OnPropertyChanged(nameof(ResetText));
        OnPropertyChanged(nameof(BackText));
        OnPropertyChanged(nameof(EmailLabel));
        OnPropertyChanged(nameof(CodeLabel));
        OnPropertyChanged(nameof(PasswordLabel));
        OnPropertyChanged(nameof(ConfirmLabel));
    }

    [RelayCommand]
    private async Task SendCodeAsync()
    {
        if (!CanSendCode)
        {
            return;
        }

        ErrorText = string.Empty;
        var result = await _auth.RequestEmailCodeAsync(Email, EmailCodePurpose.Reset)
            .ConfigureAwait(true);
        if (!result.Succeeded)
        {
            ErrorText = Describe(result.Error);
            return;
        }

        NoticeText = T(StringKeys.CodeSent);
        StartCountdown(result.Value?.ResendAfterSeconds ?? 60, result.RetryAfter);
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorText = string.Empty;

        if (!AuthInputRules.IsEmailShapeValid(Email))
        {
            ErrorText = T(StringKeys.EmailInvalid);
            return;
        }

        if (!AuthInputRules.IsEmailCodeShapeValid(Code))
        {
            ErrorText = T(StringKeys.CodeHint);
            return;
        }

        if (!AuthInputRules.IsPasswordLengthValid(NewPassword))
        {
            ErrorText = T(StringKeys.PasswordPolicy);
            return;
        }

        if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
        {
            ErrorText = T(StringKeys.PasswordsDoNotMatch);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _auth.ResetPasswordAsync(Email, Code, NewPassword).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ErrorText = Describe(result.Error);
                return;
            }

            NewPassword = string.Empty;
            ConfirmPassword = string.Empty;
            Code = string.Empty;
            Completed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    public override void Dispose()
    {
        _countdown?.Cancel();
        _countdown?.Dispose();
        base.Dispose();
    }

    private void StartCountdown(int seconds, TimeSpan? retryAfter)
    {
        _countdown?.Cancel();
        _countdown?.Dispose();
        _countdown = new CancellationTokenSource();
        var total = retryAfter is { } wait && wait.TotalSeconds > seconds
            ? (int)Math.Ceiling(wait.TotalSeconds)
            : seconds;
        _ = CountdownAsync(Math.Max(1, total), _countdown.Token);
    }

    private async Task CountdownAsync(int seconds, CancellationToken cancellationToken)
    {
        for (var remaining = seconds; remaining > 0; remaining--)
        {
            ResendRemaining = remaining;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        ResendRemaining = 0;
    }
}

/// <summary>Platform facts used for the device label; no hardware identifier.</summary>
public static class MobileDeviceInfo
{
    public static string Platform { get; set; } = "android";

    public static string DeviceName { get; set; } = "phone";

    public static string AppVersion { get; set; } = "0.1.0";
}
