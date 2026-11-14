using System.Globalization;
using System.Windows;

namespace Mosaic.Presentation.Wpf;

public enum MosaicLanguage
{
    FollowSystem,
    Chinese,
    English
}

public static class MosaicLocalizationManager
{
    private static readonly Dictionary<string, ResourceDictionary> Owners = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Func<MosaicLanguage, ResourceDictionary>> Factories = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static MosaicLanguage Current { get; private set; } = Resolve(MosaicLanguage.FollowSystem);

    public static event EventHandler? LanguageChanged;

    public static IDisposable Register(string owner, Func<MosaicLanguage, ResourceDictionary> dictionaryFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(dictionaryFactory);
        ResourceDictionary? previous;
        lock (Gate)
        {
            previous = Owners.GetValueOrDefault(owner);
            Factories[owner] = dictionaryFactory;
            Owners[owner] = CreateDictionary(dictionaryFactory(Current));
        }

        ApplyOwner(owner, Owners[owner], previous);
        return new OwnerRegistration(owner);
    }

    public static void SetLanguage(MosaicLanguage language)
    {
        var resolved = Resolve(language);
        Dictionary<string, ResourceDictionary> replacements;
        lock (Gate)
        {
            if (Current == resolved && Owners.Count == 0) return;
            if (Current == resolved) return;
            Current = resolved;
            replacements = Owners.ToDictionary(
                item => item.Key,
                item => CreateDictionary(ResolveDictionary(item.Key, resolved)),
                StringComparer.Ordinal);
        }

        ApplyReplacements(replacements);
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    private static ResourceDictionary ResolveDictionary(string owner, MosaicLanguage language) =>
        Factories[owner](language);

    private static void ApplyReplacements(IReadOnlyDictionary<string, ResourceDictionary> replacements)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => ApplyReplacements(replacements));
            return;
        }

        var resources = Application.Current?.Resources;
        if (resources is null) return;
        lock (Gate)
        {
            foreach (var (owner, dictionary) in replacements)
            {
                var previous = Owners.GetValueOrDefault(owner);
                if (previous is not null) resources.MergedDictionaries.Remove(previous);
                Owners[owner] = dictionary;
                resources.MergedDictionaries.Add(dictionary);
            }
        }
    }

    private static void ApplyOwner(string owner, ResourceDictionary dictionary, ResourceDictionary? previous)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => ApplyOwner(owner, dictionary, previous));
            return;
        }

        var resources = Application.Current?.Resources;
        if (resources is null) return;
        if (previous is not null) resources.MergedDictionaries.Remove(previous);
        resources.MergedDictionaries.Add(dictionary);
    }

    private static MosaicLanguage Resolve(MosaicLanguage language) => language switch
    {
        MosaicLanguage.Chinese => MosaicLanguage.Chinese,
        MosaicLanguage.English => MosaicLanguage.English,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? MosaicLanguage.Chinese
            : MosaicLanguage.English
    };

    private static ResourceDictionary CreateDictionary(ResourceDictionary source) => source;

    private sealed class OwnerRegistration(string owner) : IDisposable
    {
        public void Dispose()
        {
            ResourceDictionary? dictionary;
            lock (Gate)
            {
                dictionary = Owners.GetValueOrDefault(owner);
                Owners.Remove(owner);
            }

            if (dictionary is null) return;
            var resources = Application.Current?.Resources;
            resources?.MergedDictionaries.Remove(dictionary);
        }
    }
}
