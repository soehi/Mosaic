using System.Diagnostics;
using System.Text;
using Mosaic.Contracts;
using Mosaic.Maintenance.Contracts;

namespace Mosaic.Maintenance.Windows;

/// <summary>
/// Writes PowerShell helpers that wait for the current process to exit before acting, because a
/// running per-user MSI payload cannot be removed or replaced by the process it started from.
/// </summary>
public static class WindowsProcessScheduler
{
    private static readonly object RestartGate = new();
    private static Process? _restartHelper;

    /// <summary>Starts a helper that uninstalls or repairs the product after this process exits.</summary>
    public static OperationResult StartInstallerHelper(
        int parentProcessId,
        string productCode,
        string mode,
        string dataRoot,
        bool deleteUserData,
        string? relaunchPath,
        string? relaunchArguments,
        out int helperProcessId)
    {
        helperProcessId = 0;
        var directory = Path.Combine(Path.GetTempPath(), "Mosaic");
        try
        {
            Directory.CreateDirectory(directory);
            var script = Path.Combine(directory, $"maintenance-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(script, InstallerScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("-ParentPid");
            start.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-ProductCode");
            start.ArgumentList.Add(productCode);
            start.ArgumentList.Add("-Mode");
            start.ArgumentList.Add(mode);
            start.ArgumentList.Add("-DataRoot");
            start.ArgumentList.Add(deleteUserData ? dataRoot : string.Empty);
            start.ArgumentList.Add("-DeleteData");
            start.ArgumentList.Add(deleteUserData ? "1" : "0");
            start.ArgumentList.Add("-Relaunch");
            start.ArgumentList.Add(relaunchPath ?? string.Empty);
            start.ArgumentList.Add("-Arguments");
            start.ArgumentList.Add(relaunchArguments ?? string.Empty);
            var process = Process.Start(start);
            if (process is null)
                return OperationResult.Failure(new(ErrorCodes.InternalError, "无法启动维护助手进程。"));
            helperProcessId = process.Id;
            return OperationResult.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return OperationResult.Failure(new(ErrorCodes.InternalError, "无法启动维护助手进程：" + exception.Message));
        }
    }

    /// <summary>Starts a helper that relaunches the app with the same arguments after this process exits.</summary>
    public static OperationResult StartRestartHelper(int parentProcessId, string executable, IReadOnlyList<string> arguments)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Mosaic");
        try
        {
            Directory.CreateDirectory(directory);
            var script = Path.Combine(directory, $"restart-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(script, RestartScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("-ParentPid");
            start.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-Executable");
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add("-Arguments");
            start.ArgumentList.Add(string.Join(' ', arguments.Select(Quote)));
            var process = Process.Start(start);
            if (process is null)
                return OperationResult.Failure(new(ErrorCodes.InternalError, "无法启动重启助手进程。"));
            lock (RestartGate) _restartHelper = process;
            return OperationResult.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return OperationResult.Failure(new(ErrorCodes.InternalError, "无法启动重启助手进程：" + exception.Message));
        }
    }

    /// <summary>Stops a restart helper that is still waiting, when the exit was refused.</summary>
    public static Task CancelRestartHelperAsync()
    {
        lock (RestartGate)
        {
            try
            {
                if (_restartHelper is { HasExited: false }) _restartHelper.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Already exited.
            }
            _restartHelper?.Dispose();
            _restartHelper = null;
        }
        return Task.CompletedTask;
    }

    private static string Quote(string argument) =>
        argument.Contains(' ', StringComparison.Ordinal) ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : argument;

    private const string InstallerScript = """
param(
    [int]$ParentPid,
    [string]$ProductCode,
    [string]$Mode,
    [string]$DataRoot,
    [int]$DeleteData,
    [string]$Relaunch,
    [string]$Arguments
)
try { Wait-Process -Id $ParentPid -Timeout 180 -ErrorAction Stop } catch { }
if (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue) { exit 3 }
if ($Mode -eq 'repair') {
    # /femus 与安装包一致：e=只覆盖校验不符/缺失的文件。/famus 的 a=全量覆盖会在
    # 非系统驱动器上删除 X:\Config.Msi 回滚备份时遇到权限错误（该目录仅 SYSTEM/Admin 可写）。
    $msiArguments = @('/femus', $ProductCode, '/qn', '/norestart')
} else {
    $msiArguments = @('/x', $ProductCode, '/qn', '/norestart')
}
$process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $msiArguments -Wait -PassThru
$code = $process.ExitCode
$ok = ($code -eq 0 -or $code -eq 3010)
if ($ok -and $DeleteData -eq 1 -and $DataRoot -ne '') {
    if (Test-Path -LiteralPath $DataRoot) {
        Remove-Item -LiteralPath $DataRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
if ($ok -and $Relaunch -ne '') {
    if ($Arguments -ne '') { Start-Process -FilePath $Relaunch -ArgumentList $Arguments }
    else { Start-Process -FilePath $Relaunch }
}
exit $code
""";

    private const string RestartScript = """
param(
    [int]$ParentPid,
    [string]$Executable,
    [string]$Arguments
)
try { Wait-Process -Id $ParentPid -Timeout 180 -ErrorAction Stop } catch { }
if (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue) { exit 3 }
if ($Arguments -ne '') { Start-Process -FilePath $Executable -ArgumentList $Arguments }
else { Start-Process -FilePath $Executable }
""";
}
