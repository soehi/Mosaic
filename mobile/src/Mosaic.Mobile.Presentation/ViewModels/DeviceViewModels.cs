using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Mobile.Auth;
using Mosaic.Mobile.Wire;

namespace Mosaic.Mobile.Presentation;

/// <summary>Approval side: the signed-in phone approves a desktop.</summary>
public sealed partial class DeviceAuthorizeViewModel : ViewModelBase
{
    private readonly DeviceApprovalService _approval;
    private readonly AuthSessionManager _auth;

    public DeviceAuthorizeViewModel(
        DeviceApprovalService approval,
        AuthSessionManager auth,
        LocalizationService localizer)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(auth);
        _approval = approval;
        _auth = auth;
    }

    [ObservableProperty]
    private string userCode = string.Empty;

    [ObservableProperty]
    private string description = string.Empty;

    [ObservableProperty]
    private string stateText = string.Empty;

    [ObservableProperty]
    private string errorText = string.Empty;

    [ObservableProperty]
    private string noticeText = string.Empty;

    [ObservableProperty]
    private bool infoVisible;

    [ObservableProperty]
    private bool approvalConfirmVisible;

    [ObservableProperty]
    private bool isBusy;

    public string TitleText => T(StringKeys.DeviceAuthorizeTitle);

    public string HintText => T(StringKeys.DeviceAuthorizeHint);

    public string CodeLabel => T(StringKeys.DeviceCode);

    public string CodeFormatText => T(StringKeys.DeviceCodeFormat);

    public string InfoLabel => T(StringKeys.DeviceInfo);

    public string ApproveText => T(StringKeys.DeviceApprove);

    public string DenyText => T(StringKeys.DeviceDeny);

    public string ApproveConfirmText => T(StringKeys.DeviceApproveConfirm);

    public string ConfirmText => T(StringKeys.Confirm);

    public string CancelText => T(StringKeys.Cancel);

    partial void OnUserCodeChanged(string value)
    {
        InfoVisible = false;
        ApprovalConfirmVisible = false;
        Description = string.Empty;
        StateText = string.Empty;
        ErrorText = string.Empty;
        NoticeText = string.Empty;
    }

    protected override void RefreshLocalized()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(CodeLabel));
        OnPropertyChanged(nameof(CodeFormatText));
        OnPropertyChanged(nameof(InfoLabel));
        OnPropertyChanged(nameof(ApproveText));
        OnPropertyChanged(nameof(DenyText));
        OnPropertyChanged(nameof(ApproveConfirmText));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(CancelText));
    }

    [RelayCommand]
    private async Task InspectAsync()
    {
        ErrorText = string.Empty;
        NoticeText = string.Empty;
        ApprovalConfirmVisible = false;

        if (_auth.State != AuthState.SignedIn)
        {
            ErrorText = T(StringKeys.SessionExpired);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _approval.InspectAsync(UserCode).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ErrorText = Describe(result.Error);
                InfoVisible = false;
                return;
            }

            var info = result.Value;
            Description = string.Join(
                " · ",
                new[] { info?.DeviceLabel, info?.Platform }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            StateText = DescribeDeviceState(info?.State);
            InfoVisible = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BeginApprove()
    {
        if (InfoVisible)
        {
            ApprovalConfirmVisible = true;
        }
    }

    [RelayCommand]
    private void CancelApprove() => ApprovalConfirmVisible = false;

    [RelayCommand]
    private async Task ConfirmApproveAsync()
    {
        ApprovalConfirmVisible = false;
        IsBusy = true;
        try
        {
            var result = await _approval.ApproveAsync(UserCode).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ErrorText = Describe(result.Error);
                return;
            }

            NoticeText = T(StringKeys.DeviceApproved);
            InfoVisible = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DenyAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _approval.DenyAsync(UserCode).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                ErrorText = Describe(result.Error);
                return;
            }

            NoticeText = T(StringKeys.DeviceDenied);
            InfoVisible = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string DescribeDeviceState(string? state) => DeviceFlowStateParser.Parse(state) switch
    {
        DeviceFlowState.Pending => T(StringKeys.DevicePending),
        DeviceFlowState.Approved => T(StringKeys.DeviceApproved),
        DeviceFlowState.Denied => T(StringKeys.DeviceDenied),
        DeviceFlowState.Expired => T(StringKeys.DeviceExpired),
        DeviceFlowState.Consumed => T(StringKeys.DeviceConsumed),
        _ => string.Empty,
    };
}

/// <summary>Initiator side: the phone obtains its own session through device/start + poll.</summary>
public sealed partial class DeviceSignInViewModel : ViewModelBase
{
    private readonly DeviceFlowClient _client;
    private readonly AuthSessionManager _auth;
    private DeviceFlowPollingSession? _polling;
    private CancellationTokenSource? _runCts;

    public DeviceSignInViewModel(
        DeviceFlowClient client,
        AuthSessionManager auth,
        LocalizationService localizer)
        : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(auth);
        _client = client;
        _auth = auth;
    }

    public event EventHandler? SignedIn;

    public event EventHandler<string>? OpenLinkRequested;

    [ObservableProperty]
    private string userCode = string.Empty;

    [ObservableProperty]
    private string verificationUri = string.Empty;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string errorText = string.Empty;

    [ObservableProperty]
    private bool isPolling;

    [ObservableProperty]
    private bool hasCode;

    public string TitleText => T(StringKeys.DeviceSignInTitle);

    public string HintText => T(StringKeys.DeviceSignInHint);

    public string StartText => T(StringKeys.DeviceStart);

    public string StopText => T(StringKeys.DeviceStop);

    public string OpenLinkText => T(StringKeys.DeviceOpenLink);

    protected override void RefreshLocalized()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(StopText));
        OnPropertyChanged(nameof(OpenLinkText));
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (HasCode || IsPolling)
        {
            return;
        }

        ErrorText = string.Empty;
        StatusText = string.Empty;

        if (string.IsNullOrWhiteSpace(_auth.ServerUrl))
        {
            ErrorText = T(StringKeys.DeviceSignInNeedsServer);
            return;
        }

        var start = await _client
            .StartAsync(
                $"{MobileDeviceInfo.Platform} {MobileDeviceInfo.DeviceName}",
                MobileDeviceInfo.Platform,
                MobileDeviceInfo.AppVersion)
            .ConfigureAwait(true);
        if (!start.IsSuccess || start.Value is null)
        {
            ErrorText = Describe(AuthError.FromApi(start.StatusCode, start.Error, start.RetryAfter));
            return;
        }

        _polling = new DeviceFlowPollingSession(_client, start.Value);
        _polling.StateChanged += OnPollingStateChanged;
        UserCode = _polling.DisplayUserCode;
        VerificationUri = _polling.AuthorizationUri;
        HasCode = true;

        await RunPollingAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void Stop()
    {
        _runCts?.Cancel();
        _polling?.Stop();
        IsPolling = false;
        StatusText = string.Empty;
    }

    [RelayCommand]
    private void OpenLink()
    {
        if (!string.IsNullOrWhiteSpace(VerificationUri))
        {
            OpenLinkRequested?.Invoke(this, VerificationUri);
        }
    }

    /// <summary>App lifecycle: stop polling when the screen leaves the foreground.</summary>
    public void Pause()
    {
        _runCts?.Cancel();
        _polling?.Stop();
        IsPolling = false;
    }

    /// <summary>App lifecycle: resume with the same code (the flow state is preserved).</summary>
    public async Task ResumeAsync()
    {
        if (_polling is not null
            && !IsPolling
            && _polling.State is DeviceFlowState.Pending or DeviceFlowState.Unknown)
        {
            await RunPollingAsync().ConfigureAwait(true);
        }
    }

    public override void Dispose()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        if (_polling is not null)
        {
            _polling.StateChanged -= OnPollingStateChanged;
            _polling.Stop();
        }

        base.Dispose();
    }

    private async Task RunPollingAsync()
    {
        if (_polling is null || IsPolling)
        {
            return;
        }

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        IsPolling = true;
        StatusText = T(StringKeys.DeviceWaiting);
        try
        {
            var result = await _polling.RunAsync(_runCts.Token).ConfigureAwait(true);
            if (result.State == DeviceFlowState.Approved && result.Session is not null)
            {
                var adopt = await _auth.CompleteDeviceSignInAsync(result.Session).ConfigureAwait(true);
                if (!adopt.Succeeded)
                {
                    ErrorText = Describe(adopt.Error);
                    return;
                }

                StatusText = T(StringKeys.DeviceSignedIn);
                IsPolling = false;
                HasCode = false;
                SignedIn?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (result.Error is { Kind: not AuthErrorKind.Cancelled } error)
            {
                ErrorText = Describe(error);
            }

            StatusText = result.State switch
            {
                DeviceFlowState.Denied => T(StringKeys.DeviceDenied),
                DeviceFlowState.Expired => T(StringKeys.DeviceExpired),
                DeviceFlowState.Consumed => T(StringKeys.DeviceConsumed),
                _ => StatusText,
            };
        }
        finally
        {
            IsPolling = false;
        }
    }

    private void OnPollingStateChanged(object? sender, DeviceFlowState state)
    {
        if (state == DeviceFlowState.Pending)
        {
            StatusText = T(StringKeys.DeviceWaiting);
        }
    }
}
