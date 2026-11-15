using System.Reflection;

namespace Mosaic.Settings.Sqlite.Internal;

internal static class Embedded
{
    public static string Get(string name)
    {
        using var stream = typeof(Embedded).Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException("Embedded migration resource not found.", name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
