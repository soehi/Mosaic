using Microsoft.Win32;

namespace Mosaic.Desktop;

internal interface IWindowsStartupRegistration
{
    void SetEnabled(AppPaths paths, bool enabled);
    bool IsEnabled(AppPaths paths);
}

internal sealed class WindowsStartupRegistration : IWindowsStartupRegistration
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "Mosaic";
    internal const string OwnershipKeyPath = @"Software\Mosaic\Startup";
    internal const string OwnershipRootName = "DataRoot";

    private static string CommandFor(AppPaths paths)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
        var root = Path.GetFullPath(paths.Root);
        var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mosaic");
        return string.Equals(root, defaultRoot, StringComparison.OrdinalIgnoreCase)
            ? Quote(exe)
            : Quote(exe) + " --data-root " + Quote(root);
    }

    private static string Quote(string value) => '"' + value + '"';

    public void SetEnabled(AppPaths paths, bool enabled)
    {
        var root = Path.GetFullPath(paths.Root);
        using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        using var ownership = Registry.CurrentUser.OpenSubKey(OwnershipKeyPath, writable: true);
        if (enabled)
        {
            run?.SetValue(ValueName, CommandFor(paths), RegistryValueKind.String);
            ownership?.SetValue(OwnershipRootName, root, RegistryValueKind.String);
            return;
        }
        var ownedByThisWorkspace = ownership?.GetValue(OwnershipRootName) is string owned &&
            string.Equals(Path.GetFullPath(owned), root, StringComparison.OrdinalIgnoreCase);
        if (run?.GetValue(ValueName) is string existing && ownedByThisWorkspace && ExistingPointsToThisInstall(existing))
        {
            run.DeleteValue(ValueName, throwOnMissingValue: false);
            ownership?.DeleteValue(OwnershipRootName, throwOnMissingValue: false);
        }
    }

    private static bool ExistingPointsToThisInstall(string command)
    {
        var exe = Environment.ProcessPath;
        return exe is not null && command.TrimStart().StartsWith(Quote(exe), StringComparison.OrdinalIgnoreCase);
    }

    public bool IsEnabled(AppPaths paths)
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return run?.GetValue(ValueName) is string command &&
            command.Contains(Quote(Path.GetFullPath(paths.Root)), StringComparison.OrdinalIgnoreCase);
    }
}
