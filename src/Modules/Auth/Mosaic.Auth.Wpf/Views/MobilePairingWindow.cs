using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mosaic.Auth.Contracts;
using Mosaic.Auth.Wpf.Internal;
using Mosaic.Presentation.Wpf;

namespace Mosaic.Auth.Wpf.Views;

internal sealed class MobilePairingWindow : Window
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private readonly IAuthApi _auth;
    private readonly AuthState _initial;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Image _qr = new() { Width = 260, Height = 260, Margin = new Thickness(0, 16, 0, 16), Visibility = Visibility.Collapsed };
    private readonly TextBox _code = new() { MaxLength = 6, MinHeight = 32, Width = 104, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _approve = new() { MinHeight = 32, Padding = new Thickness(12, 0, 12, 0), IsEnabled = false };
    private readonly StackPanel _confirmation = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed };
    private string? _reference;
    private string? _bearer;
    private bool _consumed;
    private bool _closed;
    private bool _approving;

    public MobilePairingWindow(IAuthApi auth)
    {
        _auth = auth;
        _initial = auth.State;
        Title = Text("Title", "下载手机组件");
        Width = 402;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, MosaicTheme.SurfaceMutedBrushKey);
        SetResourceReference(ForegroundProperty, MosaicTheme.TextSecondaryBrushKey);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(_status);
        panel.Children.Add(_qr);
        panel.Children.Add(_confirmation);
        _confirmation.Children.Add(_code);
        _confirmation.Children.Add(_approve);
        System.Windows.Automation.AutomationProperties.SetName(_code, Text("Code", "手机显示的 6 位配对码"));
        _approve.Content = Text("Approve", "批准连接");
        _approve.Click += Approve;
        _code.TextChanged += (_, _) => UpdateApprove();
        Content = panel;
        _status.Text = Text("Preparing", "正在向服务器申请配对二维码…");
        _auth.StateChanged += AuthChanged;
        Loaded += Start;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _auth.StateChanged -= AuthChanged;
        _lifetime.Cancel();
        if (!_consumed && _reference is not null && _bearer is not null)
        {
            _ = CancelAsync(_reference, _bearer);
        }
        base.OnClosed(e);
    }

    private async void Start(object sender, RoutedEventArgs args)
    {
        Loaded -= Start;
        try
        {
            if (_initial.Server?.Scheme != Uri.UriSchemeHttps || !IsSameAccount())
                throw new PairingUnavailableException();
            var token = await _auth.GetAccessTokenAsync(_lifetime.Token);
            if (!token.IsSuccess || !IsSameAccount() || _closed)
                throw new PairingUnavailableException();
            _bearer = token.Value;

            // Let an in-flight start finish so closing the window can still cancel its reference.
            using var start = await SendAsync("start", null, _bearer, CancellationToken.None);
            using var data = await start.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
            var root = data?.RootElement ?? throw new PairingUnavailableException();
            _reference = root.GetProperty("reference").GetString();
            var uri = new Uri(root.GetProperty("url").GetString()!, UriKind.Absolute);
            if (_reference is not { Length: 43 } || uri.AbsolutePath != "/device/mobile/connect"
                || uri.GetLeftPart(UriPartial.Authority) != _initial.Server.GetLeftPart(UriPartial.Authority)
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment != "#" + _reference)
                throw new PairingUnavailableException();
            if (_closed || !IsSameAccount())
            {
                await CancelAsync(_reference, _bearer);
                return;
            }

            _qr.Source = PairingQrCode.Render(uri.AbsoluteUri);
            RenderOptions.SetBitmapScalingMode(_qr, BitmapScalingMode.NearestNeighbor);
            _qr.Visibility = Visibility.Visible;
            _status.Text = Text("Scan", "用手机相机扫描二维码。安装后返回链接打开组件，然后在此批准。");
            await PollAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or PairingUnavailableException
            or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            if (!_closed)
            {
                _qr.Visibility = Visibility.Collapsed;
                _confirmation.Visibility = Visibility.Collapsed;
                _status.Text = error is PairingUnavailableException
                    ? Text("Unavailable", "手机组件下载不可用。请确认已登录，并且服务器支持配对、已配置签名 APK。")
                    : Text("Failed", "无法连接配对服务，请关闭窗口后重试。");
            }
        }
        finally
        {
            if (!_consumed && _reference is not null && _bearer is not null)
                await CancelAsync(_reference, _bearer);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            using var response = await SendAsync("status", new { reference = _reference }, _bearer!, ct);
            var status = await response.Content.ReadFromJsonAsync<PairingStatus>(ct) ?? throw new PairingUnavailableException();
            switch (status.State)
            {
                case "waiting":
                    continue;
                case "pending":
                    if (_confirmation.Visibility != Visibility.Visible && !_approving)
                        _status.Text = string.Format(Text("Confirm", "{0} 请求连接。输入手机显示的 6 位配对码。"), status.DeviceLabel ?? "Android");
                    _qr.Visibility = Visibility.Collapsed;
                    _confirmation.Visibility = Visibility.Visible;
                    UpdateApprove();
                    break;
                case "approved":
                    ShowApproved();
                    break;
                case "consumed":
                    _consumed = true;
                    _status.Text = Text("Connected", "手机已连接，正在同步任务。可以关闭此窗口。");
                    _confirmation.Visibility = Visibility.Collapsed;
                    return;
                default:
                    _status.Text = Text("Expired", "二维码已失效，请关闭窗口后重新生成。");
                    _qr.Visibility = Visibility.Collapsed;
                    _confirmation.Visibility = Visibility.Collapsed;
                    return;
            }
        }
    }

    private async void Approve(object sender, RoutedEventArgs args)
    {
        if (!_approve.IsEnabled || !IsSameAccount())
            return;
        _approving = true;
        UpdateApprove();
        try
        {
            using var approved = await SendAsync("approve", new { reference = _reference, shortCode = _code.Text }, _bearer!, _lifetime.Token);
            if (!_closed && !_consumed) ShowApproved();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Conflict)
        {
            if (!_closed && _confirmation.Visibility == Visibility.Visible)
                _status.Text = Text("InvalidCode", "配对码不匹配或状态已变化。请核对手机显示的配对码。");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or PairingUnavailableException)
        {
            if (!_closed && !_consumed)
                _status.Text = Text("Failed", "无法连接配对服务，请关闭窗口后重试。");
        }
        finally
        {
            _approving = false;
            UpdateApprove();
        }
    }

    private void ShowApproved()
    {
        _confirmation.Visibility = Visibility.Collapsed;
        UpdateApprove();
        _status.Text = Text("Approved", "已批准，等待手机确认。请保持此窗口打开。");
    }

    private void UpdateApprove() => _approve.IsEnabled = !_closed && !_approving
        && _confirmation.Visibility == Visibility.Visible && _code.Text.Length == 6 && _code.Text.All(char.IsAsciiDigit);

    private async Task CancelAsync(string reference, string bearer)
    {
        try { using var response = await SendAsync("cancel", new { reference }, bearer, CancellationToken.None); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or PairingUnavailableException) { }
    }

    private async Task<HttpResponseMessage> SendAsync(string action, object? body, string bearer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_initial.Server!, "/v1/mobile/pairing/" + action));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await Http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        if (status is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable or HttpStatusCode.Unauthorized or HttpStatusCode.MethodNotAllowed)
            throw new PairingUnavailableException();
        throw new HttpRequestException("手机组件配对请求失败。", null, status);
    }

    private void AuthChanged(object? sender, EventArgs e)
    {
        if (!IsSameAccount()) Dispatcher.BeginInvoke(() => { if (!_closed) Close(); });
    }

    private bool IsSameAccount()
    {
        var current = _auth.State;
        return current.Status == AuthStatus.SignedIn && current.Server == _initial.Server && current.Account?.UserId == _initial.Account?.UserId;
    }

    private static string Text(string key, string fallback) => AuthLocalization.Localize("Auth.Mobile." + key, fallback);

    private sealed class PairingUnavailableException : Exception;
    private sealed record PairingStatus(string State, string? DeviceLabel);
}
