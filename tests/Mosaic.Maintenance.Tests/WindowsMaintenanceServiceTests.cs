using System.IO;
using Mosaic.Maintenance.Contracts;
using Mosaic.Maintenance.Windows;

namespace Mosaic.Maintenance.Tests;

public sealed class WindowsMaintenanceServiceTests
{
    [Fact]
    public void Info_reports_the_supplied_data_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "Mosaic-maintenance-tests", Guid.NewGuid().ToString("N"));
        var service = new WindowsMaintenanceService(root, Path.Combine(root, "Mosaic.exe"));

        Assert.Equal(root, service.Info.DataRoot);
        Assert.Equal(Path.Combine(root, "Mosaic.exe"), service.ExecutablePath);
        Assert.True(service.Info.Availability is MaintenanceAvailability.Installed or MaintenanceAvailability.Portable);
    }

    [Fact]
    public void Default_executable_path_is_the_running_process_not_a_missing_Mosaic_exe()
    {
        // Repair relaunches the app through this path; the published file is Mosaic.Desktop.exe, so a
        // default of "Mosaic.exe" made PrepareRepairAsync pass relaunchPath: null and never restart.
        var root = Path.Combine(Path.GetTempPath(), "Mosaic-maintenance-tests", Guid.NewGuid().ToString("N"));
        var service = new WindowsMaintenanceService(root);

        Assert.Equal(
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mosaic.Desktop.exe"),
            service.ExecutablePath);
        Assert.True(File.Exists(service.ExecutablePath));
        Assert.False(service.ExecutablePath.EndsWith(Path.DirectorySeparatorChar + "Mosaic.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Refresh_only_reads_registration_and_keeps_the_data_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "Mosaic-maintenance-tests", Guid.NewGuid().ToString("N"));
        var service = new WindowsMaintenanceService(root);

        var refreshed = await service.RefreshAsync();

        Assert.True(refreshed.IsSuccess);
        Assert.Equal(root, refreshed.Value.DataRoot);
        Assert.Equal(refreshed.Value, service.Info);
        if (refreshed.Value.Availability == MaintenanceAvailability.Installed)
            Assert.False(string.IsNullOrWhiteSpace(refreshed.Value.ProductCode));
        else
            Assert.Null(refreshed.Value.ProductCode);
    }
}
