using System.Diagnostics;
using Mosaic.Contracts;
using Mosaic.Maintenance.Contracts;
using Microsoft.Win32;

namespace Mosaic.Maintenance.Windows;

/// <summary>Locates the per-user MSI product and prepares uninstall/repair helpers.</summary>
public sealed class WindowsMaintenanceService : IMaintenanceService
{
    private const string ProductDisplayName = "Mosaic";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private readonly string _dataRoot;
    private Process? _pending;

    public WindowsMaintenanceService(string dataRoot, string? executablePath = null)
    {
        _dataRoot = dataRoot;
        ExecutablePath = executablePath ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mosaic.Desktop.exe");
        Info = ReadInfo(_dataRoot);
    }

    public MaintenanceInfo Info { get; private set; }

    public string ExecutablePath { get; }

    public Task<OperationResult<MaintenanceInfo>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Info = ReadInfo(_dataRoot);
        return Task.FromResult(OperationResult<MaintenanceInfo>.Success(Info));
    }

    public async Task<OperationResult> PrepareUninstallAsync(
        UninstallRequest request,
        CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (Info.Availability != MaintenanceAvailability.Installed)
            return OperationResult.Failure(new(ErrorCodes.CapabilityUnavailable, "当前运行的是便携或开发版本，请通过安装包卸载。"));
        if (request.DeleteUserData && !UserDataRootGuard.IsSafeToDelete(_dataRoot))
            return OperationResult.Failure(new(ErrorCodes.InvalidRequest, "数据目录不在受支持的默认位置，已阻止删除。"));
        await CancelPendingAsync(cancellationToken).ConfigureAwait(false);
        return StartHelper(
            mode: "uninstall",
            deleteUserData: request.DeleteUserData,
            relaunchPath: null,
            relaunchArguments: null);
    }

    public async Task<OperationResult> PrepareRepairAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (Info.Availability != MaintenanceAvailability.Installed)
            return OperationResult.Failure(new(ErrorCodes.CapabilityUnavailable, "未检测到已安装的 Mosaic，无法修复。"));
        await CancelPendingAsync(cancellationToken).ConfigureAwait(false);
        return StartHelper(
            mode: "repair",
            deleteUserData: false,
            relaunchPath: File.Exists(ExecutablePath) ? ExecutablePath : null,
            relaunchArguments: null);
    }

    public Task<OperationResult> CancelPendingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (_pending is { HasExited: false }) _pending.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The helper already exited or belongs to another session; nothing left to cancel.
        }
        _pending?.Dispose();
        _pending = null;
        return Task.FromResult(OperationResult.Success());
    }

    private OperationResult StartHelper(string mode, bool deleteUserData, string? relaunchPath, string? relaunchArguments)
    {
        using var current = Process.GetCurrentProcess();
        var result = WindowsProcessScheduler.StartInstallerHelper(
            current.Id,
            Info.ProductCode!,
            mode,
            _dataRoot,
            deleteUserData,
            relaunchPath,
            relaunchArguments,
            out var helperId);
        if (!result.IsSuccess) return result;
        try { _pending = Process.GetProcessById(helperId); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { _pending = null; }
        return OperationResult.Success();
    }

    private static MaintenanceInfo ReadInfo(string dataRoot)
    {
        var found = FindProduct();
        if (found is null)
            return new MaintenanceInfo(MaintenanceAvailability.Portable, null, null, null, dataRoot);
        var (productCode, installLocation, version) = found.Value;
        return new MaintenanceInfo(MaintenanceAvailability.Installed, productCode, installLocation, version, dataRoot);
    }

    private static (string ProductCode, string? InstallLocation, string? Version)? FindProduct()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                var found = FindInHive(hive, view);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static (string ProductCode, string? InstallLocation, string? Version)? FindInHive(RegistryHive hive, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallKey);
            if (uninstall is null) return null;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                if (!name.StartsWith('{')) continue;
                using var key = uninstall.OpenSubKey(name);
                if (key?.GetValue("DisplayName") is not string displayName ||
                    !string.Equals(displayName.Trim(), ProductDisplayName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var location = key.GetValue("InstallLocation") as string;
                var version = key.GetValue("DisplayVersion") as string;
                return (name, string.IsNullOrWhiteSpace(location) ? null : location, version);
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable hives are simply skipped; the app still reports Portable.
        }
        return null;
    }
}
