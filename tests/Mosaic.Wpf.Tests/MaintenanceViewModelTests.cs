using Mosaic.Contracts;
using Mosaic.Maintenance.Contracts;
using Mosaic.Maintenance.Wpf.Internal;
using Mosaic.Presentation.Contracts;
using Mosaic.Storage.Contracts;

namespace Mosaic.Wpf.Tests;

public sealed class MaintenanceViewModelTests
{
    private static readonly StorageRelocationPlan Plan = new(
        @"C:\data\mosaic.sqlite", @"D:\moved", @"D:\moved\mosaic.sqlite", 4096, true);

    [Fact]
    public async Task Initialize_shows_storage_and_install_information()
    {
        var relocation = new FakeRelocation { Location = new(@"C:\data", @"C:\data\mosaic.sqlite", 4096) };
        var maintenance = new FakeMaintenance { Info = Installed("0.16.6", @"C:\Program Files\Mosaic") };
        var viewModel = Create(relocation, maintenance);

        await viewModel.InitializeAsync();

        Assert.Equal(@"C:\data", viewModel.CurrentLocation);
        Assert.Equal(@"C:\data\mosaic.sqlite", viewModel.CurrentDatabase);
        Assert.Equal("4.0 KB", viewModel.DatabaseSize);
        Assert.Equal(@"C:\data", viewModel.TargetFolder);
        Assert.True(viewModel.IsInstalled);
        Assert.Contains("0.16.6", viewModel.InstallSummary);
        Assert.True(viewModel.UninstallCommand.CanExecute(null));
        Assert.True(viewModel.RepairCommand.CanExecute(null));
    }

    [Fact]
    public async Task Portable_install_disables_uninstall_and_repair()
    {
        var maintenance = new FakeMaintenance { Info = new(MaintenanceAvailability.Portable, null, null, null, @"C:\data") };
        var viewModel = Create(new FakeRelocation(), maintenance);

        await viewModel.InitializeAsync();

        Assert.False(viewModel.IsInstalled);
        Assert.False(viewModel.UninstallCommand.CanExecute(null));
        Assert.False(viewModel.RepairCommand.CanExecute(null));
    }

    [Fact]
    public async Task Pending_relocation_result_is_shown_once()
    {
        var relocation = new FakeRelocation
        {
            Outcome = new(false, "目标磁盘可用空间不足。", null, DateTimeOffset.Now)
        };
        var viewModel = Create(relocation, new FakeMaintenance());

        await viewModel.InitializeAsync();

        Assert.True(viewModel.HasStatus);
        Assert.Contains("目标磁盘可用空间不足。", viewModel.StatusMessage);
        Assert.Null(await relocation.ReadResultAsync());
    }

    [Fact]
    public async Task Migrate_reports_plan_failure_without_asking_for_confirmation()
    {
        var relocation = new FakeRelocation
        {
            PlanResult = OperationResult<StorageRelocationPlan>.Failure(
                new(ErrorCodes.InvalidRequest, "目标文件夹不存在，请先创建。", "target"))
        };
        var dialogs = new FakeDialogs();
        var viewModel = Create(relocation, new FakeMaintenance(), dialogs: dialogs);

        await viewModel.MigrateCommand.ExecuteAsync(null);

        Assert.False(dialogs.Confirmed);
        Assert.Null(relocation.Scheduled);
        Assert.Equal("目标文件夹不存在，请先创建。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Migrate_schedules_after_confirmation_and_requests_restart()
    {
        var relocation = new FakeRelocation();
        var dialogs = new FakeDialogs();
        var lifecycle = new FakeLifecycle();
        var viewModel = Create(relocation, new FakeMaintenance(), lifecycle: lifecycle, dialogs: dialogs);
        viewModel.TargetFolder = @"D:\moved";

        await viewModel.MigrateCommand.ExecuteAsync(null);

        Assert.True(dialogs.Confirmed);
        Assert.Equal(Plan, relocation.Scheduled);
        Assert.Equal(@"D:\moved", relocation.Requested!.TargetDirectory);
        Assert.True(relocation.Requested.KeepSource);
        Assert.Equal(1, lifecycle.Restarts);
        Assert.True(viewModel.HasStatus);
    }

    [Fact]
    public async Task Migrate_stops_when_the_user_declines()
    {
        var relocation = new FakeRelocation();
        var dialogs = new FakeDialogs { ConfirmResult = false };
        var lifecycle = new FakeLifecycle();
        var viewModel = Create(relocation, new FakeMaintenance(), lifecycle: lifecycle, dialogs: dialogs);

        await viewModel.MigrateCommand.ExecuteAsync(null);

        Assert.Null(relocation.Scheduled);
        Assert.Equal(0, lifecycle.Restarts);
        Assert.False(viewModel.HasStatus);
    }

    [Fact]
    public async Task Uninstall_needs_a_second_confirmation_before_deleting_data()
    {
        var relocation = new FakeRelocation();
        var maintenance = new FakeMaintenance { Info = Installed("0.16.6", @"C:\Program Files\Mosaic") };
        var dialogs = new FakeDialogs { DeleteConfirmResult = false };
        var viewModel = Create(relocation, maintenance, dialogs: dialogs);
        await viewModel.InitializeAsync();
        viewModel.DeleteUserData = true;

        await viewModel.UninstallCommand.ExecuteAsync(null);

        Assert.True(dialogs.Confirmed);
        Assert.True(dialogs.DeleteConfirmed);
        Assert.Null(maintenance.UninstallRequest);
    }

    [Fact]
    public async Task Uninstall_keeps_data_by_default_and_requests_exit()
    {
        var maintenance = new FakeMaintenance { Info = Installed("0.16.6", @"C:\Program Files\Mosaic") };
        var dialogs = new FakeDialogs();
        var lifecycle = new FakeLifecycle();
        var viewModel = Create(new FakeRelocation(), maintenance, lifecycle: lifecycle, dialogs: dialogs);
        await viewModel.InitializeAsync();

        await viewModel.UninstallCommand.ExecuteAsync(null);

        Assert.False(dialogs.DeleteConfirmed);
        Assert.False(maintenance.UninstallRequest!.DeleteUserData);
        Assert.Equal(1, lifecycle.Exits);
        Assert.True(viewModel.HasStatus);
    }

    [Fact]
    public async Task Uninstall_cancels_the_helper_when_exit_is_refused()
    {
        var maintenance = new FakeMaintenance { Info = Installed("0.16.6", @"C:\Program Files\Mosaic") };
        var lifecycle = new FakeLifecycle { ExitResult = OperationResult.Failure(new(ErrorCodes.Canceled, "退出已取消，操作未执行。")) };
        var viewModel = Create(new FakeRelocation(), maintenance, lifecycle: lifecycle);
        await viewModel.InitializeAsync();

        await viewModel.UninstallCommand.ExecuteAsync(null);

        Assert.NotNull(maintenance.UninstallRequest);
        Assert.Equal(1, maintenance.CancelCalls);
        Assert.Equal("退出已取消，操作未执行。", viewModel.StatusMessage);
    }

    [Fact]
    public Task View_loads_with_theme_resources_and_bindings() => Sta.Run(() =>
    {
        var view = ThemedView();
        view.Measure(new System.Windows.Size(900, 700));
        view.Arrange(new System.Windows.Rect(0, 0, 900, 700));
        Assert.NotNull(view.Content);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Browse_button_moves_below_the_path_when_the_view_is_narrow() => Sta.Run(() =>
    {
        var view = ThemedView();
        var button = Assert.IsType<System.Windows.Controls.Button>(view.FindName("BrowseButton"));

        view.Measure(new System.Windows.Size(560, 700));
        view.Arrange(new System.Windows.Rect(0, 0, 560, 700));
        view.UpdateLayout();
        Assert.Equal(1, System.Windows.Controls.Grid.GetRow(button));
        Assert.Equal(0, System.Windows.Controls.Grid.GetColumn(button));

        view.Measure(new System.Windows.Size(900, 700));
        view.Arrange(new System.Windows.Rect(0, 0, 900, 700));
        view.UpdateLayout();
        Assert.Equal(0, System.Windows.Controls.Grid.GetRow(button));
        Assert.Equal(1, System.Windows.Controls.Grid.GetColumn(button));
        return Task.CompletedTask;
    });

    private static Mosaic.Maintenance.Wpf.Views.MaintenanceView ThemedView()
    {
        var view = new Mosaic.Maintenance.Wpf.Views.MaintenanceView
        {
            DataContext = Create(new FakeRelocation(), new FakeMaintenance())
        };
        view.Resources.MergedDictionaries.Add(Assert.IsType<System.Windows.ResourceDictionary>(
            System.Windows.Application.LoadComponent(
                new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative))));
        Mosaic.Presentation.Wpf.MosaicThemeManager.Apply(view.Resources, Mosaic.Presentation.Wpf.MosaicThemeVariant.Dark);
        return view;
    }

    private static MaintenanceViewModel Create(
        FakeRelocation relocation,
        FakeMaintenance maintenance,
        FakeLifecycle? lifecycle = null,
        FakeDialogs? dialogs = null) =>
        new(relocation, maintenance, lifecycle ?? new FakeLifecycle(), dialogs ?? new FakeDialogs(), new FakeDiagnostics());

    private static MaintenanceInfo Installed(string version, string location) =>
        new(MaintenanceAvailability.Installed, "{4D2B073C-72D5-4C3A-B845-65FC2C52D23C}", location, version, @"C:\data");

    private sealed class FakeRelocation : IStorageRelocation
    {
        public StorageLocationInfo Location { get; set; } = new(@"C:\data", @"C:\data\mosaic.sqlite", 0);
        public OperationResult<StorageRelocationPlan> PlanResult { get; set; } = OperationResult<StorageRelocationPlan>.Success(Plan);
        public StorageRelocationResult? Outcome { get; set; }
        public StorageRelocationRequest? Requested { get; private set; }
        public StorageRelocationPlan? Scheduled { get; private set; }

        public Task<OperationResult<StorageLocationInfo>> GetLocationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<StorageLocationInfo>.Success(Location));

        public Task<OperationResult<StorageRelocationPlan>> PlanAsync(StorageRelocationRequest request, CancellationToken cancellationToken = default)
        {
            Requested = request;
            return Task.FromResult(PlanResult);
        }

        public Task<OperationResult> ScheduleAsync(StorageRelocationPlan plan, CancellationToken cancellationToken = default)
        {
            Scheduled = plan;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<StorageRelocationResult?> ReadResultAsync(CancellationToken cancellationToken = default)
        {
            var outcome = Outcome;
            Outcome = null;
            return Task.FromResult(outcome);
        }
    }

    private sealed class FakeMaintenance : IMaintenanceService
    {
        public MaintenanceInfo Info { get; set; } = new(MaintenanceAvailability.Portable, null, null, null, @"C:\data");
        public UninstallRequest? UninstallRequest { get; private set; }
        public int CancelCalls { get; private set; }

        public Task<OperationResult<MaintenanceInfo>> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<MaintenanceInfo>.Success(Info));

        public Task<OperationResult> PrepareUninstallAsync(UninstallRequest request, CancellationToken cancellationToken = default)
        {
            UninstallRequest = request;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> PrepareRepairAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> CancelPendingAsync(CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            return Task.FromResult(OperationResult.Success());
        }
    }

    private sealed class FakeLifecycle : IApplicationLifecycle
    {
        public OperationResult ExitResult { get; set; } = OperationResult.Success();
        public OperationResult RestartResult { get; set; } = OperationResult.Success();
        public int Exits { get; private set; }
        public int Restarts { get; private set; }

        public Task<OperationResult> RequestExitAsync(CancellationToken cancellationToken = default)
        {
            Exits++;
            return Task.FromResult(ExitResult);
        }

        public Task<OperationResult> RequestRestartAsync(CancellationToken cancellationToken = default)
        {
            Restarts++;
            return Task.FromResult(RestartResult);
        }
    }

    private sealed class FakeDialogs : IUserDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public bool DeleteConfirmResult { get; set; } = true;
        public bool Confirmed { get; private set; }
        public bool DeleteConfirmed { get; private set; }

        public Task<bool> ConfirmDeleteAsync(string title)
        {
            DeleteConfirmed = true;
            return Task.FromResult(DeleteConfirmResult);
        }

        public Task<bool> ConfirmAsync(string title, string message)
        {
            Confirmed = true;
            return Task.FromResult(ConfirmResult);
        }

        public Task<UnsavedChoice> AskUnsavedChangesAsync() => Task.FromResult(UnsavedChoice.Cancel);

        public Task ShowErrorAsync(string message) => Task.CompletedTask;
    }

    private sealed class FakeDiagnostics : IDiagnosticSink
    {
        public void Report(string correlationId, Exception exception) { }
    }
}
