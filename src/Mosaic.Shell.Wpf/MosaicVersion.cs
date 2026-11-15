using System.Reflection;

namespace Mosaic.Shell.Wpf;

internal static class MosaicVersion
{
    public static string Version { get; } = ReadVersion();

    public static string Commit { get; } = ReadCommit();

    public static string Display { get; } = Commit.Length == 0 ? $"v{Version}" : $"v{Version} · {Commit}";

    public static string WindowTitle { get; } = $"Mosaic v{Version}";

    private static Assembly ProductAssembly =>
        Assembly.GetEntryAssembly() ?? typeof(MosaicVersion).Assembly;

    private static string ReadVersion()
    {
        var informational = ProductAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var text = string.IsNullOrWhiteSpace(informational)
            ? ProductAssembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informational;
        var plus = text.IndexOf('+');
        return plus >= 0 ? text[..plus] : text;
    }

    private static string ReadCommit()
    {
        var informational = ProductAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return string.Empty;
        var plus = informational.IndexOf('+');
        if (plus < 0 || plus == informational.Length - 1) return string.Empty;
        var commit = informational[(plus + 1)..];
        return commit.Length > 7 ? commit[..7] : commit;
    }
}
