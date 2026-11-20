using Mosaic.Contracts;

namespace Mosaic.Maintenance.Contracts;

public enum MaintenanceAvailability { Installed, Portable, Unavailable }

/// <summary>Describes whether the running copy came from the per-user MSI and where it lives.</summary>
public sealed record MaintenanceInfo(
    MaintenanceAvailability Availability,
    string? ProductCode,
    string? InstallLocation,
    string? Version,
    string DataRoot);

public sealed record UninstallRequest(bool DeleteUserData);

/// <summary>
/// Uninstall and repair must outlive the current process, so preparation only writes a helper script
/// and starts it; the helper waits for this process to exit before invoking msiexec.
/// </summary>
public interface IMaintenanceService
{
    MaintenanceInfo Info { get; }

    Task<OperationResult<MaintenanceInfo>> RefreshAsync(CancellationToken cancellationToken = default);

    Task<OperationResult> PrepareUninstallAsync(
        UninstallRequest request,
        CancellationToken cancellationToken = default);

    Task<OperationResult> PrepareRepairAsync(CancellationToken cancellationToken = default);

    /// <summary>Cancels a helper that is still waiting for this process, used when exit was refused.</summary>
    Task<OperationResult> CancelPendingAsync(CancellationToken cancellationToken = default);
}

public interface IApplicationLifecycle
{
    Task<OperationResult> RequestExitAsync(CancellationToken cancellationToken = default);

    Task<OperationResult> RequestRestartAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Only the default per-user data root may ever be deleted, and never through a reparse point.
/// The installer deliberately leaves user data behind on uninstall.
/// </summary>
public static class UserDataRootGuard
{
    public static bool IsSafeToDelete(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) return false;
        string normalized;
        try { normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        var expected = Path.TrimEndingDirectorySeparator(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mosaic"));
        if (!string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (!Directory.Exists(normalized)) return true;
            return (File.GetAttributes(normalized) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
