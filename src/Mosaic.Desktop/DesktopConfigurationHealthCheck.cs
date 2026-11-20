using Mosaic.Maintenance.Contracts;

namespace Mosaic.Desktop;

/// <summary>Exposes the desktop configuration self-check to the maintenance page.</summary>
internal sealed class DesktopConfigurationHealthCheck(AppPaths paths) : IConfigurationHealthCheck
{
    public Task<ConfigurationHealthReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(AppConfiguration.Inspect(paths));
        }
        catch (Exception exception)
        {
            return Task.FromResult(new ConfigurationHealthReport(false, ["配置检查失败：" + exception.Message]));
        }
    }
}
