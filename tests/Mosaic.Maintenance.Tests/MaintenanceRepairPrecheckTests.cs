using System.IO;
using Mosaic.Contracts;
using Mosaic.Maintenance.Contracts;
using Mosaic.Maintenance.Wpf.Internal;
using Mosaic.Presentation.Contracts;
using Mosaic.Storage.Contracts;

namespace Mosaic.Maintenance.Tests;

/// <summary>
/// Repairing from the maintenance page must behave like clicking the same MSI again: a healthy
/// configuration means no work at all, only a broken one reaches msiexec.
/// </summary>
public sealed class MaintenanceRepairPrecheckTests
{
    [Fact]
    public async Task Healthy_configuration_skips_the_installer_and_leaves_the_installation_alone()
    {
        var harness = new Harness(ConfigurationHealthReport.Healthy);

        await harness.RepairAsync();

        Assert.Equal(0, harness.Maintenance.PrepareRepairCalls);
        Assert.Equal(0, harness.Lifecycle.ExitCalls);
        Assert.Null(harness.Dialogs.LastMessage);
        Assert.Contains("无需修复", harness.Model.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unhealthy_configuration_lists_the_issues_and_runs_the_repair()
    {
        var harness = new Harness(new ConfigurationHealthReport(
            false,
            ["有待应用的配置迁移：maintenance-v1。", "缺少能力绑定 storage.relocation@1。"]));

        await harness.RepairAsync();

        Assert.Equal(1, harness.Maintenance.PrepareRepairCalls);
        Assert.Equal(1, harness.Lifecycle.ExitCalls);
        Assert.Contains("maintenance-v1", harness.Dialogs.LastMessage!, StringComparison.Ordinal);
        Assert.Contains("storage.relocation@1", harness.Dialogs.LastMessage!, StringComparison.Ordinal);
        Assert.Contains("修复将在应用退出后开始", harness.Model.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declining_the_confirmation_leaves_the_installation_alone()
    {
        var harness = new Harness(new ConfigurationHealthReport(false, ["配置 JSON 格式无效。"]), confirm: false);

        await harness.RepairAsync();

        Assert.Equal(0, harness.Maintenance.PrepareRepairCalls);
        Assert.Equal(0, harness.Lifecycle.ExitCalls);
    }

    [Fact]
    public async Task Failing_check_falls_back_to_the_plain_repair_flow()
    {
        var harness = new Harness(failure: new InvalidOperationException("检查失败"));

        await harness.RepairAsync();

        Assert.Equal(1, harness.Maintenance.PrepareRepairCalls);
        Assert.Equal(1, harness.Diagnostics.Reports);
    }

    private sealed class Harness
    {
        public Harness(ConfigurationHealthReport? health = null, bool confirm = true, Exception? failure = null)
        {
            Health = new FakeHealthCheck(health, failure);
            Dialogs = new FakeDialogs(confirm);
            Maintenance = new FakeMaintenance();
            Lifecycle = new FakeLifecycle();
            Diagnostics = new FakeDiagnostics();
            Model = new MaintenanceViewModel(
                new FakeRelocation(), Maintenance, Lifecycle, Dialogs, Diagnostics, Health);
        }

        public FakeHealthCheck Health { get; }
        public FakeDialogs Dialogs { get; }
        public FakeMaintenance Maintenance { get; }
        public FakeLifecycle Lifecycle { get; }
        public FakeDiagnostics Diagnostics { get; }
        public MaintenanceViewModel Model { get; }

        public async Task RepairAsync()
        {
            await Model.InitializeAsync();
            Assert.True(Model.IsInstalled);
            Assert.True(Model.RepairCommand.CanExecute(null));
            await Model.RepairCommand.ExecuteAsync(null);
        }
    }

    private sealed class FakeHealthCheck(ConfigurationHealthReport? report, Exception? failure) : IConfigurationHealthCheck
    {
        public Task<ConfigurationHealthReport> CheckAsync(CancellationToken cancellationToken = default) =>
            failure is null
                ? Task.FromResult(report ?? ConfigurationHealthReport.Healthy)
                : Task.FromException<ConfigurationHealthReport>(failure);
    }

    private sealed class FakeDialogs(bool confirm) : IUserDialogService
    {
        public string? LastMessage { get; private set; }
        public Task<bool> ConfirmDeleteAsync(string title) => Task.FromResult(confirm);
        public Task<bool> ConfirmAsync(string title, string message) { LastMessage = message; return Task.FromResult(confirm); }
        public Task<UnsavedChoice> AskUnsavedChangesAsync() => Task.FromResult(UnsavedChoice.Discard);
        public Task ShowErrorAsync(string message) => Task.CompletedTask;
    }

    private sealed class FakeMaintenance : IMaintenanceService
    {
        public int PrepareRepairCalls { get; private set; }
        public MaintenanceInfo Info { get; } = new(MaintenanceAvailability.Installed, "{product}", @"C:\Program Files\Mosaic", "0.17.1", @"C:\Users\test\AppData\Local\Mosaic");
        public Task<OperationResult<MaintenanceInfo>> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<MaintenanceInfo>.Success(Info));
        public Task<OperationResult> PrepareUninstallAsync(UninstallRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());
        public Task<OperationResult> PrepareRepairAsync(CancellationToken cancellationToken = default)
        {
            PrepareRepairCalls++;
            return Task.FromResult(OperationResult.Success());
        }
        public Task<OperationResult> CancelPendingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());
    }

    private sealed class FakeLifecycle : IApplicationLifecycle
    {
        public int ExitCalls { get; private set; }
        public Task<OperationResult> RequestExitAsync(CancellationToken cancellationToken = default)
        {
            ExitCalls++;
            return Task.FromResult(OperationResult.Success());
        }
        public Task<OperationResult> RequestRestartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());
    }

    private sealed class FakeDiagnostics : IDiagnosticSink
    {
        public int Reports { get; private set; }
        public void Report(string correlationId, Exception exception) => Reports++;
    }

    private sealed class FakeRelocation : IStorageRelocation
    {
        public Task<OperationResult<StorageLocationInfo>> GetLocationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<StorageLocationInfo>.Success(new(@"C:\Users\test\AppData\Local\Mosaic\Data", @"C:\Users\test\AppData\Local\Mosaic\Data\mosaic.sqlite", 4096)));
        public Task<OperationResult<StorageRelocationPlan>> PlanAsync(StorageRelocationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<StorageRelocationPlan>.Failure(new(ErrorCodes.ValidationFailed, "not used")));
        public Task<OperationResult> ScheduleAsync(StorageRelocationPlan plan, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Failure(new(ErrorCodes.ValidationFailed, "not used")));
        public Task<StorageRelocationResult?> ReadResultAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<StorageRelocationResult?>(null);
    }
}
