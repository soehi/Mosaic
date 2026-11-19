using System.Text.Json;
using Mosaic.Mobile.Storage;

namespace Mosaic.Mobile.Sync;

public enum AppLanguagePreference
{
    FollowSystem = 0,
    Chinese,
    English,
}

public enum ThemePreference
{
    FollowSystem = 0,
    Light,
    Dark,
}

public enum TaskSortPreference
{
    CreatedDesc = 0,
    CreatedAsc,
    TitleAsc,
    TitleDesc,
}

public enum TaskDefaultFilter
{
    All = 0,
    Open,
    Completed,
}

public sealed record SettingDiagnostic(string Key, string Reason);

/// <summary>
/// Keys the phone reads from the shared portable whitelist (protocol §7.2).
/// Everything else — shell, storage, floating windows, startup, presence,
/// machine paths — is deliberately ignored. The desktop owns this list; the
/// phone hardcodes "known key + type" and never fails on an unknown key.
/// </summary>
public static class PortableSettingKeys
{
    public const string Language = "ui.language";
    public const string Theme = "appearance.theme";
    public const string SortOrder = "tasks.sortOrder";
    public const string DefaultFilter = "tasks.defaultFilter";
    public const string ArchiveAutoEnabled = "tasks.archive.autoEnabled";
    public const string ArchiveDelayDays = "tasks.archive.delayDays";
    public const string TitleMaxCharacters = "tasks.title.maxCharacters";
    public const string NotesMaxCharacters = "tasks.notes.maxCharacters";
    public const string TitleRequired = "tasks.title.required";
    public const string DefaultCompleted = "tasks.defaultCompleted";
    public const string ConfirmDelete = "tasks.confirmDelete";
    public const string ConfirmUnsaved = "tasks.confirmUnsaved";
    public const string ShowCharacterCount = "tasks.showCharacterCount";
    public const string AutoSwitchToAllOnHidden = "tasks.autoSwitchToAllOnHidden";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        Language,
        Theme,
        SortOrder,
        DefaultFilter,
        ArchiveAutoEnabled,
        ArchiveDelayDays,
        TitleMaxCharacters,
        NotesMaxCharacters,
        TitleRequired,
        DefaultCompleted,
        ConfirmDelete,
        ConfirmUnsaved,
        ShowCharacterCount,
        AutoSwitchToAllOnHidden,
    };

    public static IReadOnlyCollection<string> All => Known;

    public static bool IsKnown(string? key) => key is not null && Known.Contains(key);
}

/// <summary>
/// The applied view of the portable settings. Reads are safe from any thread;
/// <see cref="Apply"/> replaces one key at a time and never throws on malformed
/// input — a bad value keeps the previous one and adds a diagnostic.
/// </summary>
public sealed class PortableSettings
{
    private const int MaxDiagnostics = 50;

    private readonly object _gate = new();
    private readonly List<SettingDiagnostic> _diagnostics = [];

    public event EventHandler? Changed;

    public AppLanguagePreference Language { get; private set; } = AppLanguagePreference.FollowSystem;

    public ThemePreference Theme { get; private set; } = ThemePreference.FollowSystem;

    /// <summary>Set when the server asked for HighContrast, which the phone renders as FollowSystem.</summary>
    public bool HighContrastDowngraded { get; private set; }

    public TaskSortPreference SortOrder { get; private set; } = TaskSortPreference.CreatedDesc;

    public TaskDefaultFilter DefaultFilter { get; private set; } = TaskDefaultFilter.All;

    /// <summary>Display only: archiving runs on the desktop/server, never on the phone.</summary>
    public bool ArchiveAutoEnabled { get; private set; }

    public int ArchiveDelayDays { get; private set; } = 7;

    public int TitleMaxCharacters { get; private set; } = 200;

    public int NotesMaxCharacters { get; private set; } = 20_000;

    public bool TitleRequired { get; private set; } = true;

    public bool DefaultCompleted { get; private set; }

    public bool ConfirmDelete { get; private set; } = true;

    public bool ConfirmUnsaved { get; private set; } = true;

    public bool ShowCharacterCount { get; private set; } = true;

    public bool AutoSwitchToAllOnHidden { get; private set; } = true;

    public IReadOnlyList<SettingDiagnostic> Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return [.. _diagnostics];
            }
        }
    }

    public void ApplyAll(IEnumerable<StoredSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var changed = false;
        foreach (var setting in settings.OrderBy(s => s.ServerRevision))
        {
            changed |= ApplyJson(setting.Key, setting.ValueJson);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Apply(string key, JsonElement value)
    {
        if (ApplyCore(key, value))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Restores every whitellisted key to its default (used when the projection is dropped).</summary>
    public void ResetAll()
    {
        foreach (var key in PortableSettingKeys.All)
        {
            Reset(key);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool ApplyJson(string key, string valueJson)
    {
        try
        {
            using var document = JsonDocument.Parse(valueJson);
            return ApplyCore(key, document.RootElement);
        }
        catch (JsonException)
        {
            AddDiagnostic(key, "invalid_json");
            return false;
        }
    }

    private bool ApplyCore(string key, JsonElement value)
    {
        if (!PortableSettingKeys.IsKnown(key))
        {
            AddDiagnostic(key, "unknown_key");
            return false;
        }

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Reset(key);
        }

        switch (key)
        {
            case PortableSettingKeys.Language:
                if (!TryString(value, out var language))
                {
                    AddDiagnostic(key, "type");
                    return false;
                }

                if (!TryParseLanguage(language, out var parsedLanguage))
                {
                    AddDiagnostic(key, "value");
                    return false;
                }

                Language = parsedLanguage;
                return true;

            case PortableSettingKeys.Theme:
                if (!TryString(value, out var theme))
                {
                    AddDiagnostic(key, "type");
                    return false;
                }

                if (string.Equals(theme, "HighContrast", StringComparison.OrdinalIgnoreCase))
                {
                    HighContrastDowngraded = true;
                    Theme = ThemePreference.FollowSystem;
                    return true;
                }

                if (!TryParseTheme(theme, out var parsedTheme))
                {
                    AddDiagnostic(key, "value");
                    return false;
                }

                HighContrastDowngraded = false;
                Theme = parsedTheme;
                return true;

            case PortableSettingKeys.SortOrder:
                if (!TryString(value, out var sort)
                    || !Enum.TryParse<TaskSortPreference>(sort, ignoreCase: true, out var parsedSort)
                    || !Enum.IsDefined(parsedSort))
                {
                    AddDiagnostic(key, "value");
                    return false;
                }

                SortOrder = parsedSort;
                return true;

            case PortableSettingKeys.DefaultFilter:
                if (!TryString(value, out var filter)
                    || !Enum.TryParse<TaskDefaultFilter>(filter, ignoreCase: true, out var parsedFilter)
                    || !Enum.IsDefined(parsedFilter))
                {
                    AddDiagnostic(key, "value");
                    return false;
                }

                DefaultFilter = parsedFilter;
                return true;

            case PortableSettingKeys.ArchiveAutoEnabled:
                return ApplyBool(key, value, v => ArchiveAutoEnabled = v);

            case PortableSettingKeys.ArchiveDelayDays:
                return ApplyInt(key, value, 1, 365, v => ArchiveDelayDays = v);

            case PortableSettingKeys.TitleMaxCharacters:
                return ApplyInt(key, value, 1, 100_000, v => TitleMaxCharacters = v);

            case PortableSettingKeys.NotesMaxCharacters:
                return ApplyInt(key, value, 1, 1_000_000, v => NotesMaxCharacters = v);

            case PortableSettingKeys.TitleRequired:
                return ApplyBool(key, value, v => TitleRequired = v);

            case PortableSettingKeys.DefaultCompleted:
                return ApplyBool(key, value, v => DefaultCompleted = v);

            case PortableSettingKeys.ConfirmDelete:
                return ApplyBool(key, value, v => ConfirmDelete = v);

            case PortableSettingKeys.ConfirmUnsaved:
                return ApplyBool(key, value, v => ConfirmUnsaved = v);

            case PortableSettingKeys.ShowCharacterCount:
                return ApplyBool(key, value, v => ShowCharacterCount = v);

            case PortableSettingKeys.AutoSwitchToAllOnHidden:
                return ApplyBool(key, value, v => AutoSwitchToAllOnHidden = v);

            default:
                AddDiagnostic(key, "unknown_key");
                return false;
        }
    }

    private bool Reset(string key)
    {
        switch (key)
        {
            case PortableSettingKeys.Language:
                Language = AppLanguagePreference.FollowSystem;
                return true;
            case PortableSettingKeys.Theme:
                Theme = ThemePreference.FollowSystem;
                HighContrastDowngraded = false;
                return true;
            case PortableSettingKeys.SortOrder:
                SortOrder = TaskSortPreference.CreatedDesc;
                return true;
            case PortableSettingKeys.DefaultFilter:
                DefaultFilter = TaskDefaultFilter.All;
                return true;
            case PortableSettingKeys.ArchiveAutoEnabled:
                ArchiveAutoEnabled = false;
                return true;
            case PortableSettingKeys.ArchiveDelayDays:
                ArchiveDelayDays = 7;
                return true;
            case PortableSettingKeys.TitleMaxCharacters:
                TitleMaxCharacters = 200;
                return true;
            case PortableSettingKeys.NotesMaxCharacters:
                NotesMaxCharacters = 20_000;
                return true;
            case PortableSettingKeys.TitleRequired:
                TitleRequired = true;
                return true;
            case PortableSettingKeys.DefaultCompleted:
                DefaultCompleted = false;
                return true;
            case PortableSettingKeys.ConfirmDelete:
                ConfirmDelete = true;
                return true;
            case PortableSettingKeys.ConfirmUnsaved:
                ConfirmUnsaved = true;
                return true;
            case PortableSettingKeys.ShowCharacterCount:
                ShowCharacterCount = true;
                return true;
            case PortableSettingKeys.AutoSwitchToAllOnHidden:
                AutoSwitchToAllOnHidden = true;
                return true;
            default:
                return false;
        }
    }

    private bool ApplyBool(string key, JsonElement value, Action<bool> assign)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            AddDiagnostic(key, "type");
            return false;
        }

        assign(value.GetBoolean());
        return true;
    }

    private bool ApplyInt(string key, JsonElement value, int min, int max, Action<int> assign)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            AddDiagnostic(key, "type");
            return false;
        }

        if (number < min || number > max)
        {
            AddDiagnostic(key, "range");
            return false;
        }

        assign(number);
        return true;
    }

    private static bool TryString(JsonElement value, out string result)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text)
        {
            result = text;
            return true;
        }

        result = string.Empty;
        return false;
    }

    private static bool TryParseLanguage(string value, out AppLanguagePreference language)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "followsystem":
            case "system":
                language = AppLanguagePreference.FollowSystem;
                return true;
            case "chinese":
            case "zh":
            case "zh-hans":
                language = AppLanguagePreference.Chinese;
                return true;
            case "english":
            case "en":
                language = AppLanguagePreference.English;
                return true;
            default:
                language = AppLanguagePreference.FollowSystem;
                return false;
        }
    }

    private static bool TryParseTheme(string value, out ThemePreference theme)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "followsystem":
            case "system":
                theme = ThemePreference.FollowSystem;
                return true;
            case "light":
                theme = ThemePreference.Light;
                return true;
            case "dark":
                theme = ThemePreference.Dark;
                return true;
            default:
                theme = ThemePreference.FollowSystem;
                return false;
        }
    }

    private void AddDiagnostic(string key, string reason)
    {
        lock (_gate)
        {
            if (_diagnostics.Count >= MaxDiagnostics)
            {
                _diagnostics.RemoveAt(0);
            }

            _diagnostics.Add(new SettingDiagnostic(key, reason));
        }
    }
}
