using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Contracts;
using Mosaic.Maintenance.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Storage.Contracts;

namespace Mosaic.Maintenance.Wpf.Internal;

internal sealed class MaintenanceViewModel : ObservableObject, IPageController
{
    private readonly IStorageRelocation _relocation;
    private readonly IMaintenanceService _maintenance;
    private readonly IApplicationLifecycle _lifecycle;
    private readonly IUserDialogService _dialogs;
    private readonly IDiagnosticSink _diagnostics;
    private string _currentLocation = string.Empty;
    private string _currentDatabase = string.Empty;
    private string _databaseSize = string.Empty;
    private string _targetFolder = string.Empty;
    private string _statusMessage = string.Empty;
    private string _installSummary = string.Empty;
    private string _dataRoot = string.Empty;
    private bool _isBusy;
    private bool _isInstalled;
    private bool _deleteUserData;

    public MaintenanceViewModel(
        IStorageRelocation relocation,
        IMaintenanceService maintenance,
        IApplicationLifecycle lifecycle,
        IUserDialogService dialogs,
        IDiagnosticSink diagnostics)
    {
        _relocation = relocation;
        _maintenance = maintenance;
        _lifecycle = lifecycle;
        _dialogs = dialogs;
        _diagnostics = diagnostics;
        BrowseCommand = new RelayCommand(Browse);
        MigrateCommand = new AsyncRelayCommand(MigrateAsync, () => !IsBusy);
        UninstallCommand = new AsyncRelayCommand(UninstallAsync, () => !IsBusy && IsInstalled);
        RepairCommand = new AsyncRelayCommand(RepairAsync, () => !IsBusy && IsInstalled);
    }

    public string CurrentLocation { get => _currentLocation; private set => SetProperty(ref _currentLocation, value); }
    public string CurrentDatabase { get => _currentDatabase; private set => SetProperty(ref _currentDatabase, value); }
    public string DatabaseSize { get => _databaseSize; private set => SetProperty(ref _databaseSize, value); }
    public string TargetFolder { get => _targetFolder; set => SetProperty(ref _targetFolder, value); }
    public string StatusMessage { get => _statusMessage; private set { if (SetProperty(ref _statusMessage, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public string InstallSummary { get => _installSummary; private set => SetProperty(ref _installSummary, value); }
    public string DataRoot { get => _dataRoot; private set => SetProperty(ref _dataRoot, value); }
    public bool IsInstalled { get => _isInstalled; private set { if (SetProperty(ref _isInstalled, value)) { UninstallCommand.NotifyCanExecuteChanged(); RepairCommand.NotifyCanExecuteChanged(); } } }
    public bool DeleteUserData { get => _deleteUserData; set => SetProperty(ref _deleteUserData, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            MigrateCommand.NotifyCanExecuteChanged();
            UninstallCommand.NotifyCanExecuteChanged();
            RepairCommand.NotifyCanExecuteChanged();
        }
    }

    public RelayCommand BrowseCommand { get; }
    public AsyncRelayCommand MigrateCommand { get; }
    public AsyncRelayCommand UninstallCommand { get; }
    public AsyncRelayCommand RepairCommand { get; }

    public async Task InitializeAsync()
    {
        await LoadStorageAsync().ConfigureAwait(true);
        await LoadMaintenanceAsync().ConfigureAwait(true);
    }

    private async Task LoadStorageAsync()
    {
        try
        {
            var result = await _relocation.GetLocationAsync().ConfigureAwait(true);
            if (!result.IsSuccess) { Report(result.Error); return; }
            var location = result.Value;
            CurrentLocation = location.Directory;
            CurrentDatabase = location.DatabasePath;
            DatabaseSize = FormatSize(location.SizeBytes);
            if (string.IsNullOrWhiteSpace(TargetFolder)) TargetFolder = location.Directory;
            var outcome = await _relocation.ReadResultAsync().ConfigureAwait(true);
            if (outcome is not null)
                StatusMessage = outcome.Success
                    ? outcome.Message
                    : MaintenanceLocalization.Localize("Maintenance.Migrate.Failed", "上次迁移未完成：") + outcome.Message;
        }
        catch (Exception exception) { _diagnostics.Report("maintenance.location", exception); }
    }

    private async Task LoadMaintenanceAsync()
    {
        try
        {
            var result = await _maintenance.RefreshAsync().ConfigureAwait(true);
            if (!result.IsSuccess) { Report(result.Error); return; }
            var info = result.Value;
            DataRoot = info.DataRoot;
            IsInstalled = info.Availability == MaintenanceAvailability.Installed;
            InstallSummary = info.Availability switch
            {
                MaintenanceAvailability.Installed => string.Format(
                    CultureInfo.CurrentCulture,
                    MaintenanceLocalization.Localize("Maintenance.Install.Installed", "已安装 {0} · {1}"),
                    info.Version ?? "?",
                    info.InstallLocation ?? string.Empty),
                MaintenanceAvailability.Portable => MaintenanceLocalization.Localize(
                    "Maintenance.Install.Portable", "便携或开发运行，不支持应用内卸载"),
                _ => MaintenanceLocalization.Localize("Maintenance.Install.Unavailable", "无法确定安装状态")
            };
        }
        catch (Exception exception) { _diagnostics.Report("maintenance.info", exception); }
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = MaintenanceLocalization.Localize("Maintenance.Storage.Browse", "选择新的存储文件夹"),
            InitialDirectory = Directory.Exists(TargetFolder) ? TargetFolder : CurrentLocation,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true) TargetFolder = dialog.FolderName;
    }

    private async Task MigrateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusMessage = string.Empty;
            var plan = await _relocation.PlanAsync(new StorageRelocationRequest(TargetFolder)).ConfigureAwait(true);
            if (!plan.IsSuccess) { Report(plan.Error); return; }
            var confirmed = await _dialogs.ConfirmAsync(
                MaintenanceLocalization.Localize("Maintenance.Migrate.ConfirmTitle", "迁移数据库"),
                string.Format(
                    CultureInfo.CurrentCulture,
                    MaintenanceLocalization.Localize(
                        "Maintenance.Migrate.ConfirmMessage",
                        "将把数据库复制到 {0}，校验通过后切换到新位置并重启。原位置的数据会保留。" +
                        "登录凭据、日志和配置不会迁移。"),
                    plan.Value.TargetDatabasePath)).ConfigureAwait(true);
            if (!confirmed) return;
            var scheduled = await _relocation.ScheduleAsync(plan.Value).ConfigureAwait(true);
            if (!scheduled.IsSuccess) { Report(scheduled.Error); return; }
            var restart = await _lifecycle.RequestRestartAsync().ConfigureAwait(true);
            if (!restart.IsSuccess)
            {
                Report(restart.Error);
                return;
            }
            StatusMessage = MaintenanceLocalization.Localize("Maintenance.Migrate.Scheduled", "迁移已排队，应用退出后会立即执行。");
        }
        catch (Exception exception)
        {
            _diagnostics.Report("maintenance.migrate", exception);
            StatusMessage = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task UninstallAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusMessage = string.Empty;
            var message = DeleteUserData
                ? MaintenanceLocalization.Localize(
                    "Maintenance.Uninstall.ConfirmWithData",
                    "将卸载 Mosaic，并删除本机数据目录（数据库、登录凭据、日志和配置）。此操作不可撤销。")
                : MaintenanceLocalization.Localize(
                    "Maintenance.Uninstall.Confirm",
                    "将卸载 Mosaic，本机数据会保留，重新安装后可以继续使用。");
            if (!await _dialogs.ConfirmAsync(
                    MaintenanceLocalization.Localize("Maintenance.Uninstall.Title", "卸载 Mosaic"), message).ConfigureAwait(true))
                return;
            if (DeleteUserData && !await _dialogs.ConfirmDeleteAsync(
                    MaintenanceLocalization.Localize("Maintenance.Uninstall.DeleteDataTitle", "同时删除本机数据？")).ConfigureAwait(true))
                return;
            var prepared = await _maintenance.PrepareUninstallAsync(new UninstallRequest(DeleteUserData)).ConfigureAwait(true);
            if (!prepared.IsSuccess) { Report(prepared.Error); return; }
            var exit = await _lifecycle.RequestExitAsync().ConfigureAwait(true);
            if (!exit.IsSuccess)
            {
                await _maintenance.CancelPendingAsync().ConfigureAwait(true);
                Report(exit.Error);
                return;
            }
            StatusMessage = MaintenanceLocalization.Localize("Maintenance.Uninstall.Started", "卸载将在应用退出后开始。");
        }
        catch (Exception exception)
        {
            _diagnostics.Report("maintenance.uninstall", exception);
            StatusMessage = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task RepairAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusMessage = string.Empty;
            if (!await _dialogs.ConfirmAsync(
                    MaintenanceLocalization.Localize("Maintenance.Repair.Title", "修复安装"),
                    MaintenanceLocalization.Localize(
                        "Maintenance.Repair.Confirm",
                        "将调用 Windows 安装程序修复 Mosaic，完成后自动重新启动。")).ConfigureAwait(true))
                return;
            var prepared = await _maintenance.PrepareRepairAsync().ConfigureAwait(true);
            if (!prepared.IsSuccess) { Report(prepared.Error); return; }
            var exit = await _lifecycle.RequestExitAsync().ConfigureAwait(true);
            if (!exit.IsSuccess)
            {
                await _maintenance.CancelPendingAsync().ConfigureAwait(true);
                Report(exit.Error);
                return;
            }
            StatusMessage = MaintenanceLocalization.Localize("Maintenance.Repair.Started", "修复将在应用退出后开始。");
        }
        catch (Exception exception)
        {
            _diagnostics.Report("maintenance.repair", exception);
            StatusMessage = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private void Report(OperationError? error) =>
        StatusMessage = error?.Message ?? MaintenanceLocalization.Localize("Maintenance.Error.Unknown", "操作未完成，请稍后再试。");

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1024L * 1024 => (bytes / (1024d * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
        >= 1024 => (bytes / 1024d).ToString("0.0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes.ToString(CultureInfo.CurrentCulture) + " B"
    };

    public Task<bool> CanLeaveAsync() => Task.FromResult(true);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
