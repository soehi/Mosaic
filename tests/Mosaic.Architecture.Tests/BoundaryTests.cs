using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Mosaic.Architecture.Tests;

public sealed class BoundaryTests
{
    private static string Configuration => new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mosaic.sln"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Solution not found.");
        }
    }
    [Fact]
    public void Project_and_compiled_assembly_dependencies_respect_explicit_whitelist()
    {
        var whitelist = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(Path.Combine(Root, "build/project-boundaries.json")))!;
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            Assert.True(whitelist.ContainsKey(name), $"Undeclared project: {name}");
            var xml = XDocument.Load(file);
            foreach (var reference in xml.Descendants("ProjectReference")) Assert.Contains(Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')), whitelist[name]);
            foreach (var package in xml.Descendants("PackageReference")) CheckFramework(name, package.Attribute("Include")!.Value);
            var framework = IsWindowsProject(name) ? "net10.0-windows" : "net10.0";
            using var stream = File.OpenRead(Path.Combine(Path.GetDirectoryName(file)!, "bin", Configuration, framework, name + ".dll"));
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                if (reference.StartsWith("Mosaic.", StringComparison.Ordinal)) Assert.Contains(reference, whitelist[name]);
                CheckFramework(name, reference);
            }
        }
    }
    // Windows-targeted projects: WPF views, DPAPI storage and the host. Everything else must stay
    // platform-neutral so the core modules remain testable on a plain net10.0 target.
    private static bool IsWindowsProject(string name) =>
        name is "Mosaic.Desktop" or "Mosaic.Shell.Wpf" or "Mosaic.Presentation.Contracts" or "Mosaic.Presentation.Wpf"
            or "Mosaic.Tasks.Wpf" or "Mosaic.Settings.Wpf" or "Mosaic.DesktopPresence.Wpf" or "Mosaic.FloatingWindows.Wpf"
            or "Mosaic.Auth.Windows" or "Mosaic.Auth.Wpf"
            or "Mosaic.Maintenance.Windows" or "Mosaic.Maintenance.Wpf";
    private static void CheckFramework(string project, string dependency)
    {
        if (dependency.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) || dependency.StartsWith("SQLitePCLRaw", StringComparison.Ordinal)) Assert.Equal("Mosaic.Storage.Sqlite", project);
        if (dependency.StartsWith("CommunityToolkit.Mvvm", StringComparison.Ordinal)) Assert.Contains(project, new[] { "Mosaic.Desktop", "Mosaic.Shell.Wpf", "Mosaic.Tasks.Wpf", "Mosaic.Settings.Wpf", "Mosaic.DesktopPresence.Wpf", "Mosaic.FloatingWindows.Wpf", "Mosaic.Auth.Wpf", "Mosaic.Maintenance.Wpf" });
        if (dependency is "PresentationFramework" or "PresentationCore" or "WindowsBase") Assert.Contains(project, new[] { "Mosaic.Desktop", "Mosaic.Shell.Wpf", "Mosaic.Presentation.Contracts", "Mosaic.Presentation.Wpf", "Mosaic.Tasks.Wpf", "Mosaic.Settings.Wpf", "Mosaic.DesktopPresence.Wpf", "Mosaic.FloatingWindows.Wpf", "Mosaic.Auth.Wpf", "Mosaic.Maintenance.Wpf" });
        if (dependency.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)) Assert.Equal("Mosaic.Desktop", project);
    }
    [Fact]
    public void Exported_types_match_reviewable_api_inventory()
    {
        var expected = File.ReadAllLines(Path.Combine(Root, "build/public-api.txt")).Where(l => !string.IsNullOrWhiteSpace(l)).Order().ToArray();
        var actual = new List<string>();
        foreach (var project in Directory.GetFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var framework = IsWindowsProject(name) ? "net10.0-windows" : "net10.0";
            using var file = File.OpenRead(Path.Combine(Path.GetDirectoryName(project)!, "bin", Configuration, framework, name + ".dll"));
            using var pe = new PEReader(file); var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                // WPF may emit this one compiler helper on clean builds and omit it incrementally.
                if (metadata.GetString(type.Namespace) == "XamlGeneratedNamespace" && metadata.GetString(type.Name) == "GeneratedInternalTypeHelper")
                {
                    Assert.Contains(name, new[] { "Mosaic.Desktop", "Mosaic.Shell.Wpf", "Mosaic.Presentation.Wpf", "Mosaic.Tasks.Wpf", "Mosaic.Settings.Wpf", "Mosaic.DesktopPresence.Wpf", "Mosaic.FloatingWindows.Wpf", "Mosaic.Auth.Wpf", "Mosaic.Maintenance.Wpf" });
                    continue;
                }
                if ((type.Attributes & TypeAttributes.VisibilityMask) is TypeAttributes.Public or TypeAttributes.NestedPublic)
                    actual.Add(metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name));
            }
        }
        Assert.Equal(expected, actual.Order().ToArray());
    }
    [Fact]
    public void Source_has_no_service_locator_reflection_bypass_or_cross_module_sql()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("InternalsVisibleTo", source);
            Assert.False(Regex.IsMatch(source, @"Assembly\.Load|Type\.GetType|Activator\.CreateInstance|ServiceLocator"), file);
            if (ProjectOf(file) != "Mosaic.Desktop") Assert.False(Regex.IsMatch(source, @"IServiceProvider|IHost\b|GetRequiredService|GetService<"), file);
            if (!file.Contains("Mosaic.Storage.Sqlite") && !file.Contains("Mosaic.Tasks.Sqlite") && !file.Contains("Mosaic.Settings.Sqlite") && !file.Contains("Mosaic.FloatingWindows.Sqlite"))
                Assert.False(Regex.IsMatch(source, @"(?i)\b(SELECT .+ FROM|INSERT INTO|UPDATE .+ SET|CREATE TABLE|DELETE FROM)\b"), file);
            if (!file.Contains("Mosaic.Settings.Sqlite"))
                Assert.False(Regex.IsMatch(source, @"(?i)\b(from|into|update|create table)\s+settings\b"), file);
            if (file.Contains("Mosaic.Tasks.Core") || file.Contains("Mosaic.Kernel") || ProjectOf(file) == "Mosaic.DesktopPresence.Core") Assert.DoesNotContain("System.Windows", source);
        }
    }
    [Fact]
    public void Setting_definitions_are_owned_by_the_module_that_declares_them()
    {
        var ownership = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "Mosaic.Settings.Core",
            ["tasks-core"] = "Mosaic.Tasks.Core",
            ["tasks-ui"] = "Mosaic.Tasks.Wpf",
            ["shell"] = "Mosaic.Shell.Wpf",
            ["storage-sqlite"] = "Mosaic.Storage.Sqlite",
            ["desktop-presence"] = "Mosaic.DesktopPresence.Wpf",
            ["floating-windows"] = "Mosaic.FloatingWindows.Wpf",
            ["auth-core"] = "Mosaic.Auth.Core",
            ["sync-core"] = "Mosaic.Sync.Core"
        };
        var pattern = new Regex(@"(?:new|Definition)\(\s*""([a-z][a-z0-9.]*)"",\s*""([a-z][a-z0-9-]*)""");
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            var project = ProjectOf(file);
            foreach (var line in File.ReadAllLines(file).Where(line => line.Contains("SettingValueKind")))
            {
                foreach (Match match in pattern.Matches(line))
                {
                    Assert.True(ownership.TryGetValue(match.Groups[2].Value, out var owner), $"Unknown setting module '{match.Groups[2].Value}' for '{match.Groups[1].Value}' in {file}");
                    Assert.True(project == owner, $"Setting '{match.Groups[1].Value}' belongs to {owner} but is declared in {file}");
                }
            }
        }
    }
    private static string ProjectOf(string file)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null)
        {
            var project = directory.GetFiles("*.csproj").SingleOrDefault();
            if (project is not null) return Path.GetFileNameWithoutExtension(project.Name);
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Project not found for " + file);
    }
}
