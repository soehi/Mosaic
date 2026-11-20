using System.Diagnostics;
using System.Windows.Automation;

namespace Mosaic.Desktop.SmokeTests;

/// <summary>
/// Guards the plain Bootstrapper.StartAsync path. 0.17.0 disposed the single-use ModuleRuntime before
/// starting it, so every launch ended in the "Mosaic · 无法启动" diagnostic window; the startup
/// relocation tests only exercised StartupStorageRelocation.RunAsync and could not see it.
/// </summary>
public sealed class BootstrapperStartupTests
{
    private static string Root
    {
        get { var p = new DirectoryInfo(AppContext.BaseDirectory); while (p is not null && !File.Exists(Path.Combine(p.FullName, "Mosaic.sln"))) p = p.Parent; return p!.FullName; }
    }
    private static string Executable => Environment.GetEnvironmentVariable("MOSAIC_SMOKE_EXE") ?? Path.Combine(Root, "artifacts/local-run/Mosaic.Desktop.exe");

    [Fact]
    public async Task Fresh_install_starts_and_reaches_the_main_window()
    {
        Assert.SkipUnless(File.Exists(Executable), "Run build/publish.ps1 first; the published layout is missing.");
        var root = Path.Combine(Root, "artifacts/smoke-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false };
        info.ArgumentList.Add("--data-root");
        info.ArgumentList.Add(root);
        using var process = Process.Start(info)!;
        try
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                process.Refresh();
                if (process.HasExited)
                    Assert.Fail($"Mosaic exited during startup with code {process.ExitCode}.");
                var window = AutomationElement.RootElement.FindFirst(
                    TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
                if (window is not null)
                {
                    Assert.False(
                        window.Current.Name.Contains("无法启动", StringComparison.Ordinal),
                        "Startup ended in the diagnostic window: " + window.Current.Name);
                    return;
                }
                await Task.Delay(200);
            }
            Assert.Fail("Mosaic did not open a window within 60 seconds.");
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
