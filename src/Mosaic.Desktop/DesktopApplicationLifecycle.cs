using System.Diagnostics;
using Mosaic.Contracts;
using Mosaic.Maintenance.Contracts;
using Mosaic.Maintenance.Windows;

namespace Mosaic.Desktop;

/// <summary>Bridges modules to the shell shutdown path; the coordinator exists only after startup.</summary>
internal sealed class DesktopApplicationLifecycle(string executable, IReadOnlyList<string> arguments) : IApplicationLifecycle
{
    internal ShutdownCoordinator? Coordinator { get; set; }

    public async Task<OperationResult> RequestExitAsync(CancellationToken cancellationToken = default)
    {
        var coordinator = Coordinator;
        if (coordinator is null)
            return OperationResult.Failure(new(ErrorCodes.CapabilityUnavailable, "应用尚未准备好退出，请稍后再试。"));
        var result = await coordinator.RequestExitAsync(cancellationToken).ConfigureAwait(true);
        return result switch
        {
            DesktopPresence.Contracts.ExitRequestResult.Accepted => OperationResult.Success(),
            DesktopPresence.Contracts.ExitRequestResult.Cancelled =>
                OperationResult.Failure(new(ErrorCodes.Canceled, "退出已取消，操作未执行。")),
            _ => OperationResult.Failure(new(ErrorCodes.InternalError, "退出失败，操作未执行。"))
        };
    }

    public async Task<OperationResult> RequestRestartAsync(CancellationToken cancellationToken = default)
    {
        if (Coordinator is null)
            return OperationResult.Failure(new(ErrorCodes.CapabilityUnavailable, "应用尚未准备好重启，请稍后再试。"));
        using var current = Process.GetCurrentProcess();
        var helper = WindowsProcessScheduler.StartRestartHelper(current.Id, executable, arguments);
        if (!helper.IsSuccess) return helper;
        var exit = await RequestExitAsync(cancellationToken).ConfigureAwait(true);
        if (!exit.IsSuccess)
        {
            await WindowsProcessScheduler.CancelRestartHelperAsync().ConfigureAwait(true);
            return exit;
        }
        return OperationResult.Success();
    }

    internal static DesktopApplicationLifecycle Create()
    {
        var process = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mosaic.exe");
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToList();
        if (string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(assembly)) commandLine.Insert(0, assembly);
        }
        return new DesktopApplicationLifecycle(process, commandLine);
    }
}
