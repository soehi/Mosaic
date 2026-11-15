using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Settings.Contracts;
using Mosaic.Presentation.Contracts;

namespace Mosaic.Settings.Wpf.Internal;

internal enum SettingsSection
{
    Normal,
    Tasks,
    Appearance,
    Storage,
    Text
}

internal sealed class SettingDefinitionTranslation
{
    public static string DisplayName(string id) => id switch
    {
        "appearance.theme" => "Settings.Setting.Appearance.Theme",
        "ui.language" => "Settings.Setting.Language",
        "tasks.confirmDelete" => "Settings.Setting.ConfirmDelete",
        "tasks.confirmUnsaved" => "Settings.Setting.ConfirmUnsaved",
        "tasks.defaultFilter" => "Settings.Setting.DefaultFilter",
        "tasks.showCharacterCount" => "Settings.Setting.ShowCharacterCount",
        "tasks.autoSwitchToAllOnHidden" => "Settings.Setting.AutoSwitchToAll",
        "tasks.sortOrder" => "Settings.Setting.SortOrder",
        "tasks.title.maxCharacters" => "Settings.Setting.TitleMax",
        "tasks.notes.maxCharacters" => "Settings.Setting.NotesMax",
        "tasks.title.required" => "Settings.Setting.TitleRequired",
        "tasks.defaultCompleted" => "Settings.Setting.DefaultCompleted",
        "tasks.revisionStart" => "Settings.Setting.RevisionStart",
        "shell.sidebar.collapseBelowWidth" => "Settings.Setting.SidebarCollapseWidth",
        "shell.sidebar.expandAtWidth" => "Settings.Setting.SidebarExpandWidth",
        "shell.sidebar.fullWidth" => "Settings.Setting.SidebarFullWidth",
        "shell.sidebar.railWidth" => "Settings.Setting.SidebarRailWidth",
        "shell.window.minWidth" => "Settings.Setting.WindowMinWidth",
        "shell.window.minHeight" => "Settings.Setting.WindowMinHeight",
        "shell.page.marginLeft" => "Settings.Setting.PageMarginLeft",
        "shell.page.marginTop" => "Settings.Setting.PageMarginTop",
        "shell.page.marginRight" => "Settings.Setting.PageMarginRight",
        "shell.page.marginBottom" => "Settings.Setting.PageMarginBottom",
        "tasks.layout.compactBreakpoint" => "Settings.Setting.TaskCompactBreakpoint",
        "tasks.editor.compactActionsWidth" => "Settings.Setting.ActionsCompactWidth",
        "tasks.editor.compactActionsHeight" => "Settings.Setting.ActionsCompactHeight",
        "tasks.list.minColumnWidth" => "Settings.Setting.ListMinColumnWidth",
        "tasks.editor.notesMinHeight" => "Settings.Setting.NotesMinHeight",
        "tasks.button.minWidth" => "Settings.Setting.ButtonMinWidth",
        "tasks.button.minHeight" => "Settings.Setting.ButtonMinHeight",
        "startup.width" => "Settings.Setting.StartupWidth",
        "startup.height" => "Settings.Setting.StartupHeight",
        "storage.filename" => "Settings.Setting.DatabaseFilename",
        "storage.location" => "Settings.Setting.DatabaseLocation",
        "storage.queueCapacity" => "Settings.Setting.QueueCapacity",
        "storage.busyTimeoutSeconds" => "Settings.Setting.BusyTimeout",
        "storage.backupRetention" => "Settings.Setting.BackupRetention",
        "storage.journalMode" => "Settings.Setting.JournalMode",
        "storage.connectionMode" => "Settings.Setting.ConnectionMode",
        "host.modules" => "Settings.Setting.EnabledModules",
        "host.providers" => "Settings.Setting.ProviderBindings",
        "logging.level" => "Settings.Setting.LogLevel",
        "logging.retentionDays" => "Settings.Setting.LogRetention",
        "diagnostics.singleInstanceRetryMilliseconds" => "Settings.Setting.SingleInstanceRetry",
        "ui.text.emptyAllTitle" => "Settings.Setting.EmptyAllTitle",
        "ui.text.emptyAllDescription" => "Settings.Setting.EmptyAllDescription",
        "ui.text.emptyOpenTitle" => "Settings.Setting.EmptyOpenTitle",
        "ui.text.emptyOpenDescription" => "Settings.Setting.EmptyOpenDescription",
        "ui.text.emptyCompletedTitle" => "Settings.Setting.EmptyCompletedTitle",
        "ui.text.emptyCompletedDescription" => "Settings.Setting.EmptyCompletedDescription",
        "ui.text.untitledTask" => "Settings.Setting.UntitledTask",
        "ui.text.saveSuccess" => "Settings.Setting.SaveSuccess",
        "ui.text.deleteSuccess" => "Settings.Setting.DeleteSuccess",
        "ui.text.completedSuccess" => "Settings.Setting.CompletedSuccess",
        "ui.text.restoredSuccess" => "Settings.Setting.RestoredSuccess",
        _ => string.Empty
    };

    public static string Description(string id) => id switch
    {
        "appearance.theme" => "Settings.Setting.Appearance.Theme.Description",
        "ui.language" => "Settings.Setting.Language.Description",
        "tasks.confirmDelete" => "Settings.Setting.ConfirmDelete.Description",
        "tasks.confirmUnsaved" => "Settings.Setting.ConfirmUnsaved.Description",
        "tasks.defaultFilter" => "Settings.Setting.DefaultFilter.Description",
        "tasks.showCharacterCount" => "Settings.Setting.ShowCharacterCount.Description",
        "tasks.autoSwitchToAllOnHidden" => "Settings.Setting.AutoSwitchToAll.Description",
        "tasks.sortOrder" => "Settings.Setting.SortOrder.Description",
        "tasks.title.maxCharacters" => "Settings.Setting.TitleMax.Description",
        "tasks.notes.maxCharacters" => "Settings.Setting.NotesMax.Description",
        "tasks.title.required" => "Settings.Setting.TitleRequired.Description",
        "tasks.defaultCompleted" => "Settings.Setting.DefaultCompleted.Description",
        "tasks.revisionStart" => "Settings.Setting.RevisionStart.Description",
        "shell.sidebar.collapseBelowWidth" => "Settings.Setting.SidebarCollapseWidth.Description",
        "shell.sidebar.expandAtWidth" => "Settings.Setting.SidebarExpandWidth.Description",
        "shell.sidebar.fullWidth" => "Settings.Setting.SidebarFullWidth.Description",
        "shell.sidebar.railWidth" => "Settings.Setting.SidebarRailWidth.Description",
        "shell.window.minWidth" => "Settings.Setting.WindowMinWidth.Description",
        "shell.window.minHeight" => "Settings.Setting.WindowMinHeight.Description",
        "shell.page.marginLeft" => "Settings.Setting.PageMarginLeft.Description",
        "shell.page.marginTop" => "Settings.Setting.PageMarginTop.Description",
        "shell.page.marginRight" => "Settings.Setting.PageMarginRight.Description",
        "shell.page.marginBottom" => "Settings.Setting.PageMarginBottom.Description",
        "tasks.layout.compactBreakpoint" => "Settings.Setting.TaskCompactBreakpoint.Description",
        "tasks.editor.compactActionsWidth" => "Settings.Setting.ActionsCompactWidth.Description",
        "tasks.editor.compactActionsHeight" => "Settings.Setting.ActionsCompactHeight.Description",
        "tasks.list.minColumnWidth" => "Settings.Setting.ListMinColumnWidth.Description",
        "tasks.editor.notesMinHeight" => "Settings.Setting.NotesMinHeight.Description",
        "tasks.button.minWidth" => "Settings.Setting.ButtonMinWidth.Description",
        "tasks.button.minHeight" => "Settings.Setting.ButtonMinHeight.Description",
        "startup.width" => "Settings.Setting.StartupWidth.Description",
        "startup.height" => "Settings.Setting.StartupHeight.Description",
        "storage.filename" => "Settings.Setting.DatabaseFilename.Description",
        "storage.location" => "Settings.Setting.DatabaseLocation.Description",
        "storage.queueCapacity" => "Settings.Setting.QueueCapacity.Description",
        "storage.busyTimeoutSeconds" => "Settings.Setting.BusyTimeout.Description",
        "storage.backupRetention" => "Settings.Setting.BackupRetention.Description",
        "storage.journalMode" => "Settings.Setting.JournalMode.Description",
        "storage.connectionMode" => "Settings.Setting.ConnectionMode.Description",
        "host.modules" => "Settings.Setting.EnabledModules.Description",
        "host.providers" => "Settings.Setting.ProviderBindings.Description",
        "logging.level" => "Settings.Setting.LogLevel.Description",
        "logging.retentionDays" => "Settings.Setting.LogRetention.Description",
        "diagnostics.singleInstanceRetryMilliseconds" => "Settings.Setting.SingleInstanceRetry.Description",
        "ui.text.emptyAllTitle" => "Settings.Setting.EmptyAllTitle.Description",
        "ui.text.emptyAllDescription" => "Settings.Setting.EmptyAllDescription.Description",
        "ui.text.emptyOpenTitle" => "Settings.Setting.EmptyOpenTitle.Description",
        "ui.text.emptyOpenDescription" => "Settings.Setting.EmptyOpenDescription.Description",
        "ui.text.emptyCompletedTitle" => "Settings.Setting.EmptyCompletedTitle.Description",
        "ui.text.emptyCompletedDescription" => "Settings.Setting.EmptyCompletedDescription.Description",
        "ui.text.untitledTask" => "Settings.Setting.UntitledTask.Description",
        "ui.text.saveSuccess" => "Settings.Setting.SaveSuccess.Description",
        "ui.text.deleteSuccess" => "Settings.Setting.DeleteSuccess.Description",
        "ui.text.completedSuccess" => "Settings.Setting.CompletedSuccess.Description",
        "ui.text.restoredSuccess" => "Settings.Setting.RestoredSuccess.Description",
        _ => string.Empty
    };
}

internal sealed class SettingRow : ObservableObject
{
    private object? _currentValue;
    private object? _proposedValue;
    private string _textValue;
    private string? _error;
    private RowErrorKind _errorKind;
    private string? _apiErrorMessage;
    public event EventHandler? Changed;

    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public SettingRow(SettingDefinition definition, object? value)
    {
        Definition = definition;
        _currentValue = value;
        _proposedValue = value;
        _textValue = FormatText(value);
    }

    public SettingDefinition Definition { get; }
    public string Id => Definition.Id;
    public string DisplayName => SettingsLocalization.Localize(SettingDefinitionTranslation.DisplayName(Id), Definition.DisplayName);
    public string Description => SettingsLocalization.Localize(SettingDefinitionTranslation.Description(Id), Definition.Description);
    public string Constraint => GetConstraint();
    public bool IsAdvanced => Definition.Category == SettingCategory.Advanced;
    public bool RequiresRestart => Definition.ApplyMode == SettingApplyMode.Restart;
    public bool HasWarning => !string.IsNullOrWhiteSpace(Definition.Warning);
    public string Warning => Definition.Warning ?? string.Empty;
    public bool IsDefault => Equals(NormalizedCurrent, Definition.DefaultValue);
    public bool IsDirty => !Equals(NormalizedProposed, NormalizedCurrent);
    public IReadOnlyList<SettingOption> Choices => SettingOptionCatalog.OptionsFor(Id) is { Count: > 0 } options
        ? options
        : (Definition.AllowedValues ?? []).Select(value => new SettingOption(value, value)).ToArray();
    public bool IsBoolean => Definition.Kind == SettingValueKind.Boolean;
    public bool IsEnum => Definition.Kind == SettingValueKind.Enum;
    public bool IsInteger => Definition.Kind == SettingValueKind.Integer;
    public bool IsText => Definition.Kind is SettingValueKind.String or SettingValueKind.ModuleList or SettingValueKind.ProviderMap;
    public bool IsProviderMap => Definition.Kind == SettingValueKind.ProviderMap;
    public bool IsChoice => IsBoolean || IsEnum;
    public string DefaultValueText => FormatText(Definition.DefaultValue);
    public string CurrentValueText => FormatText(CurrentValue);
    public string TextHint => Definition.Kind switch
    {
        SettingValueKind.ModuleList => SettingsLocalization.Localize("Settings.Hint.ModuleList", "模块 ID 以逗号分隔"),
        SettingValueKind.ProviderMap => SettingsLocalization.Localize("Settings.Hint.ProviderMap", "每行或分号分隔：capability@version=module-id"),
        _ => SettingsLocalization.Localize("Settings.Hint.Text", "文本")
    };

    public object? CurrentValue
    {
        get => _currentValue;
        private set => SetProperty(ref _currentValue, value);
    }

    public object? ProposedValue
    {
        get => _proposedValue;
        set
        {
            if (SetProperty(ref _proposedValue, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(IsDefault));
                OnPropertyChanged(nameof(CurrentValueText));
                NotifyChanged();
                PreviewImmediateSetting();
            }
        }
    }

    private void PreviewImmediateSetting()
    {
        var resources = System.Windows.Application.Current?.Resources;
        if (resources is null) return;
        if (Id == "appearance.theme")
        {
            var variant = ProposedValue switch
            {
                "Light" => MosaicThemeVariant.Light,
                "Dark" => MosaicThemeVariant.Dark,
                "HighContrast" => MosaicThemeVariant.HighContrast,
                _ => (MosaicThemeVariant?)null
            };
            if (variant is { } fixedVariant) MosaicThemeManager.Apply(resources, fixedVariant);
            else MosaicThemeManager.Attach(resources);
        }
        else if (Id == "ui.language")
        {
            var language = ProposedValue switch
            {
                "Chinese" => MosaicLanguage.Chinese,
                "English" => MosaicLanguage.English,
                _ => MosaicLanguage.FollowSystem
            };
            MosaicLocalizationManager.SetLanguage(language);
        }
    }

    public bool BooleanValue
    {
        get => ProposedValue is bool value && value;
        set
        {
            if (BooleanValue == value) return;
            ProposedValue = value;
            OnPropertyChanged();
        }
    }

    public string ChoiceValue
    {
        get => ProposedValue as string ?? String.Empty;
        set
        {
            if (ChoiceValue == value) return;
            ProposedValue = value;
            OnPropertyChanged();
        }
    }

    public string TextValue
    {
        get => _textValue;
        set
        {
            if (!SetProperty(ref _textValue, value)) return;
            Error = null;
            if (IsText)
            {
                ProposedValue = value;
            }
            else if (IsInteger)
            {
                if (long.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    ProposedValue = parsed;
                else
                    SetLocalizedError(RowErrorKind.Integer);
            }
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(IsDefault));
            NotifyChanged();
        }
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public void SetValue(object? value)
    {
        CurrentValue = value;
        ProposedValue = value;
        _textValue = FormatText(value);
        Error = null;
        OnPropertyChanged(nameof(TextValue));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsDefault));
    }

    public void RestoreSaved()
    {
        ProposedValue = CurrentValue;
        _textValue = FormatText(CurrentValue);
        Error = null;
        OnPropertyChanged(nameof(TextValue));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsDefault));
        PreviewImmediateSetting();
        NotifyChanged();
    }

    public void Reset()
    {
        ProposedValue = Definition.DefaultValue;
        _textValue = FormatText(Definition.DefaultValue);
        Error = null;
        OnPropertyChanged(nameof(TextValue));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsDefault));
        PreviewImmediateSetting();
    }

    public void SetError(string? message)
    {
        _errorKind = RowErrorKind.Api;
        _apiErrorMessage = message;
        Error = message;
    }

    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Constraint));
        OnPropertyChanged(nameof(TextHint));
        if (_errorKind != RowErrorKind.Api && _errorKind != RowErrorKind.None)
            Error = RenderError(_errorKind);
    }

    private string SetLocalizedError(RowErrorKind kind)
    {
        _errorKind = kind;
        _apiErrorMessage = null;
        Error = RenderError(kind);
        return Error ?? string.Empty;
    }

    private string? RenderError(RowErrorKind kind) => kind switch
    {
        RowErrorKind.Integer => SettingsLocalization.Localize("Settings.Error.Integer", "请输入整数。"),
        RowErrorKind.ProviderMap => SettingsLocalization.Localize("Settings.Error.ProviderMap", "格式应为 capability@version=module-id，且不能重复。"),
        RowErrorKind.Unsupported => SettingsLocalization.Localize("Settings.Error.Unsupported", "不支持的设置类型。"),
        _ => null
    };

    private enum RowErrorKind
    {
        None,
        Integer,
        ProviderMap,
        Unsupported,
        Api
    }

    public object? NormalizedProposed => Normalize(ProposedValue);
    public object? NormalizedCurrent => Normalize(CurrentValue);

    public bool TryGetProposedValue(out object? value, out string? error)
    {
        value = null;
        error = null;
        if (IsBoolean || IsEnum)
        {
            value = Normalize(ProposedValue);
            return true;
        }

        switch (Definition.Kind)
        {
            case SettingValueKind.Integer:
                if (!long.TryParse(TextValue.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var integer))
                { error = SetLocalizedError(RowErrorKind.Integer); return false; }
                value = integer;
                return true;
            case SettingValueKind.ModuleList:
                value = TextValue.Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                return true;
            case SettingValueKind.ProviderMap:
                var providers = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var part in TextValue.Split([';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var pieces = part.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (pieces.Length != 2 || string.IsNullOrWhiteSpace(pieces[0]) || string.IsNullOrWhiteSpace(pieces[1]) ||
                        !providers.TryAdd(pieces[0], pieces[1]))
                    {
                        error = SetLocalizedError(RowErrorKind.ProviderMap);
                        return false;
                    }
                }
                value = providers;
                return true;
            case SettingValueKind.String:
                var text = TextValue.Trim();
                value = string.IsNullOrWhiteSpace(text) ? Definition.DefaultValue : text;
                return true;
            default:
                error = SetLocalizedError(RowErrorKind.Unsupported);
                return false;
        }
    }

    private object? Normalize(object? value)
    {
        if (value is null) return null;
        return Definition.Kind switch
        {
            SettingValueKind.Integer when value is long => Convert.ToInt64(value),
            SettingValueKind.Integer => Convert.ToInt64(value),
            SettingValueKind.ModuleList when value is string text => text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            SettingValueKind.ProviderMap when value is string text => text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal),
            _ => value
        };
    }

    private string GetConstraint()
    {
        if (SettingOptionCatalog.OptionsFor(Id) is { Count: > 0 } localizedOptions) return string.Join(" / ", localizedOptions.Select(item => SettingsLocalization.Localize(item.DisplayNameKey, item.Value)));
        if (Definition.AllowedValues is { Count: > 0 }) return string.Join(" / ", Definition.AllowedValues);
        if (Definition.Min is not null || Definition.Max is not null) return $"{Definition.Min} – {Definition.Max}";
        if (Definition.Kind == SettingValueKind.String) return SettingsLocalization.Localize("Settings.Constraint.Max200", "最多 200 个字符");
        return Definition.Kind switch
        {
            SettingValueKind.Boolean => SettingsLocalization.Localize("Settings.Constraint.Boolean", "开 / 关"),
            SettingValueKind.ModuleList => SettingsLocalization.Localize("Settings.Constraint.ModuleList", "逗号分隔的模块 ID"),
            SettingValueKind.ProviderMap => SettingsLocalization.Localize("Settings.Constraint.ProviderMap", "capability=module-id，分号分隔"),
            _ => string.Empty
        };
    }

    private string FormatText(object? value) => value switch
    {
        null => String.Empty,
        IReadOnlyDictionary<string, string> providers when Definition.Kind == SettingValueKind.ProviderMap =>
            String.Join("; ", providers.Select(item => $"{item.Key}={item.Value}")),
        IReadOnlyDictionary<string, object?> providers when Definition.Kind == SettingValueKind.ProviderMap =>
            String.Join("; ", providers.Select(item => $"{item.Key}={item.Value}")),
        IEnumerable<string> values when Definition.Kind == SettingValueKind.ModuleList => String.Join(", ", values),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? String.Empty
    };

    private static string Localize(string key, string fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;
}

internal sealed partial class SettingsViewModel : ObservableObject, IPageController
{
    private readonly ISettingsApi _settings;
    private readonly IUserDialogService _dialogs;
    private readonly Dictionary<string, SettingRow> _rows = [];
    private bool _isLoading;
    private bool _isBusy;
    private bool _hasChanges;
    private SettingsSection _selectedSection = SettingsSection.Normal;
    private bool _showAdvanced;
    private string? _message;
    private bool _isError;
    private SettingsFeedbackKind _feedbackKind = SettingsFeedbackKind.None;
    private string? _feedbackApiMessage;

    public SettingsViewModel(ISettingsApi settings, IUserDialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        SaveCommand = new AsyncRelayCommand(() => SaveCoreAsync(), () => !IsBusy && HasChanges);
        ResetSectionCommand = new AsyncRelayCommand(() => ResetCoreAsync(SectionModuleIds()), () => !IsBusy);
        ResetAllCommand = new AsyncRelayCommand(() => ResetCoreAsync(null), () => !IsBusy);
        DiscardCommand = new RelayCommand(DiscardChanges, () => HasChanges);
        ResetToDefaultCommand = new RelayCommand<SettingRow>(row => row?.Reset(), _ => !IsBusy);
        SelectSectionCommand = new RelayCommand<SettingsSection>(section => SelectSection(section), _ => !IsBusy);
        Sections.CollectionChanged += (_, _) => NotifyRowVisibility();
        VisibleRows.CollectionChanged += (_, _) => NotifyRowVisibility();
        MosaicLocalizationManager.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<SettingsSection> Sections { get; } =
        [SettingsSection.Normal, SettingsSection.Tasks, SettingsSection.Appearance, SettingsSection.Storage, SettingsSection.Text];
    public ObservableCollection<SettingRow> VisibleRows { get; } = [];

    public SettingsSection SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (!SetProperty(ref _selectedSection, value)) return;
            ApplySectionFilter();
        }
    }

    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (SetProperty(ref _showAdvanced, value)) ApplySectionFilter();
        }
    }

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(IsNotBusy));
        }
    }
    public bool IsNotBusy => !IsBusy;
    public bool HasChanges
    {
        get => _hasChanges;
        private set
        {
            if (SetProperty(ref _hasChanges, value))
            {
                UpdateCommandStates();
                DiscardCommand.NotifyCanExecuteChanged();
                SaveCommand.NotifyCanExecuteChanged();
            }
        }
    }
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }
    public bool IsError
    {
        get => _isError;
        private set
        {
            if (SetProperty(ref _isError, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }
    public bool HasRows => VisibleRows.Count > 0;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message) && !IsError;
    public string EmptyMessage => SelectedSection == SettingsSection.Normal
        ? SettingsLocalization.Localize("Settings.Empty.Normal", "没有普通设置。打开高级设置查看全部参数。")
        : SettingsLocalization.Localize("Settings.Empty.Section", "当前分区没有设置。");
    public bool HasVisibleRows => VisibleRows.Count > 0;
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand ResetSectionCommand { get; }
    public IRelayCommand ResetAllCommand { get; }
    public IRelayCommand DiscardCommand { get; }
    public IRelayCommand ResetToDefaultCommand { get; }
    public IRelayCommand<SettingsSection> SelectSectionCommand { get; }

    public async Task InitializeAsync()
    {
        if (VisibleRows.Count > 0 || IsLoading) return;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        Message = null;
        IsError = false;
        _feedbackKind = SettingsFeedbackKind.None;
        try
        {
            var definitions = await _settings.ListDefinitionsAsync();
            var values = await _settings.GetValuesAsync();
            if (!definitions.IsSuccess)
            {
                SetFeedback(SettingsFeedbackKind.LoadDefinitions, definitions.Error?.Message);
                IsError = true;
                return;
            }
            if (!values.IsSuccess)
            {
                SetFeedback(SettingsFeedbackKind.LoadValues, values.Error?.Message);
                IsError = true;
                return;
            }

            var source = values.Value!;
            _rows.Clear();
            foreach (var definition in definitions.Value!)
            {
                var row = new SettingRow(definition, source.GetValueOrDefault(definition.Id));
                row.Changed += (_, _) => RefreshDirtyState();
                _rows[definition.Id] = row;
            }
            ApplySectionFilter();
            HasChanges = false;
        }
        catch (Exception)
        {
            SetFeedback(SettingsFeedbackKind.Load);
            IsError = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplySectionFilter()
    {
        VisibleRows.Clear();
        foreach (var row in _rows.Values.Where(IsRowVisible).OrderBy(row => row.Id, StringComparer.Ordinal))
            VisibleRows.Add(row);
        NotifyRowVisibility();
    }

    private void NotifyRowVisibility()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasVisibleRows));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private bool IsRowVisible(SettingRow row)
    {
        if (!ShowAdvanced && row.IsAdvanced) return false;
        return SelectedSection switch
        {
            SettingsSection.Normal => ShowAdvanced || !row.IsAdvanced,
            SettingsSection.Tasks => row.Definition.ModuleId.StartsWith("tasks", StringComparison.Ordinal),
            SettingsSection.Appearance => row.Definition.ModuleId == "shell" ||
                (row.Definition.ModuleId == "host" && row.Id.StartsWith("shell.", StringComparison.Ordinal)),
            SettingsSection.Storage => row.Definition.ModuleId is "storage-sqlite" or "host" &&
                (row.Id.StartsWith("storage.", StringComparison.Ordinal) || row.Id.StartsWith("host.", StringComparison.Ordinal) ||
                    row.Id.StartsWith("logging.", StringComparison.Ordinal) || row.Id.StartsWith("diagnostics.", StringComparison.Ordinal) ||
                    row.Id.StartsWith("startup.", StringComparison.Ordinal)),
            SettingsSection.Text => row.Id.StartsWith("ui.text.", StringComparison.Ordinal),
            _ => false
        };
    }

    private async Task<bool> SaveCoreAsync()
    {
        if (IsBusy) return false;
        IsBusy = true;
        Message = null;
        IsError = false;
        try
        {
            var parsed = new Dictionary<string, object?>();
            foreach (var row in _rows.Values.Where(row => row.IsDirty))
            {
                if (!row.TryGetProposedValue(out var value, out var fieldError))
                {
                    SetFeedback(SettingsFeedbackKind.InvalidBeforeSave);
                    IsError = true;
                    return false;
                }
                parsed[row.Id] = value;
            }
            var changes = parsed;
            if (changes.Count == 0)
            {
                HasChanges = false;
                SetFeedback(SettingsFeedbackKind.NoChanges);
                return true;
            }

            foreach (var row in _rows.Values) row.SetError(null);
            var proposed = await _settings.ProposeChangesAsync(changes);
            if (!proposed.IsSuccess)
            {
                SetFeedback(SettingsFeedbackKind.Save, proposed.Error?.Message);
                IsError = true;
                return false;
            }
            if (proposed.Value!.Count > 0)
            {
                foreach (var (id, error) in proposed.Value)
                    _rows.GetValueOrDefault(id)?.SetError(error.Message);
                SetFeedback(SettingsFeedbackKind.InvalidAfterSave);
                IsError = true;
                return false;
            }

            var values = await _settings.GetValuesAsync();
            if (!values.IsSuccess)
            {
                SetFeedback(SettingsFeedbackKind.Refresh, values.Error?.Message);
                IsError = true;
                return false;
            }
            foreach (var row in _rows.Values) row.SetValue(values.Value!.GetValueOrDefault(row.Id));
            HasChanges = false;
            SetFeedback(SettingsFeedbackKind.Saved);
            return true;
        }
        catch (Exception)
        {
            SetFeedback(SettingsFeedbackKind.SaveRetry);
            IsError = true;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ResetCoreAsync(string[]? moduleIds)
    {
        if (IsBusy) return;
        if (!await ConfirmResetAsync(moduleIds is null ? null : string.Join("、", moduleIds))) return;
        IsBusy = true;
        Message = null;
        IsError = false;
        try
        {
            if (moduleIds is null)
            {
                if (!(await _settings.ResetAsync()).IsSuccess)
                {
                    SetFeedback(SettingsFeedbackKind.ResetFailed);
                    IsError = true;
                    return;
                }
            }
            else
            {
                foreach (var moduleId in moduleIds)
                {
                    if (!(await _settings.ResetAsync(moduleId)).IsSuccess)
                    {
                        SetFeedback(SettingsFeedbackKind.ResetFailed);
                        IsError = true;
                        return;
                    }
                }
            }

            var values = await _settings.GetValuesAsync();
            if (!values.IsSuccess)
            {
                SetFeedback(SettingsFeedbackKind.Refresh, values.Error?.Message);
                IsError = true;
                return;
            }
            foreach (var row in _rows.Values) row.SetValue(values.Value!.GetValueOrDefault(row.Id));
            HasChanges = false;
            SetFeedback(SettingsFeedbackKind.Reset);
        }
        catch (Exception)
        {
            SetFeedback(SettingsFeedbackKind.ResetRetry);
            IsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string[] SectionModuleIds() => SelectedSection switch
    {
        SettingsSection.Tasks => ["tasks-core", "tasks-ui"],
        SettingsSection.Appearance => ["shell"],
        SettingsSection.Storage => ["storage-sqlite", "host"],
        SettingsSection.Text => ["tasks-ui"],
        _ => ["tasks-ui", "tasks-core", "shell"]
    };

    private async Task<bool> ConfirmResetAsync(string? scope)
    {
        var title = scope is null
            ? SettingsLocalization.Localize("Settings.ResetAll", "恢复全部默认")
            : $"{SettingsLocalization.Localize("Settings.ResetSection", "恢复当前分区默认")}：{scope}";
        var message = scope is null
            ? SettingsLocalization.Localize("Settings.ResetAll.Detail", "将清除所有修改过的设置并恢复默认值，且不可撤销。")
            : SettingsLocalization.Localize("Settings.ResetSection.Detail", "将把当前分区的设置恢复为默认值，且不可撤销。");
        return await _dialogs.ConfirmAsync(title, message);
    }

    private void DiscardChanges()
    {
        foreach (var row in _rows.Values) row.RestoreSaved();
        HasChanges = false;
        PreviewDiscardedAppearance();
    }

    private void PreviewDiscardedAppearance()
    {
        var resources = System.Windows.Application.Current?.Resources;
        if (resources is null) return;
        var theme = _rows.GetValueOrDefault("appearance.theme")?.NormalizedCurrent as string;
        var language = _rows.GetValueOrDefault("ui.language")?.NormalizedCurrent as string;
        var variant = theme switch
        {
            "Light" => MosaicThemeVariant.Light,
            "Dark" => MosaicThemeVariant.Dark,
            "HighContrast" => MosaicThemeVariant.HighContrast,
            _ => (MosaicThemeVariant?)null
        };
        if (variant is { } fixedVariant) MosaicThemeManager.Apply(resources, fixedVariant);
        else MosaicThemeManager.Attach(resources);
        var resolvedLanguage = language switch
        {
            "Chinese" => MosaicLanguage.Chinese,
            "English" => MosaicLanguage.English,
            _ => MosaicLanguage.FollowSystem
        };
        MosaicLocalizationManager.SetLanguage(resolvedLanguage);
    }

    private void UpdateCommandStates()
    {
        ResetToDefaultCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    private void SelectSection(SettingsSection? section)
    {
        if (section is { } selected) SelectedSection = selected;
    }

    private void RefreshDirtyState()
    {
        HasChanges = _rows.Values.Any(row => row.IsDirty);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshLocalizedText();

    private void RefreshLocalizedText()
    {
        foreach (var row in _rows.Values) row.RefreshLocalizedText();
        if (_feedbackKind != SettingsFeedbackKind.None && _feedbackKind != SettingsFeedbackKind.Api)
            SetFeedback(_feedbackKind, _feedbackApiMessage);
        OnPropertyChanged(nameof(EmptyMessage));
        RefreshSections();
    }

    private void RefreshSections()
    {
        Sections.Clear();
        foreach (var section in new[]
        {
            SettingsSection.Normal,
            SettingsSection.Tasks,
            SettingsSection.Appearance,
            SettingsSection.Storage,
            SettingsSection.Text
        })
            Sections.Add(section);
    }

    private void SetFeedback(SettingsFeedbackKind kind, string? apiMessage = null)
    {
        _feedbackKind = kind;
        _feedbackApiMessage = apiMessage;
        Message = RenderFeedback(kind, apiMessage);
    }

    private static string? RenderFeedback(SettingsFeedbackKind kind, string? apiMessage) => kind switch
    {
        SettingsFeedbackKind.Api => apiMessage,
        SettingsFeedbackKind.Load => SettingsLocalization.Localize("Settings.Error.Load", "无法加载设置，请刷新后重试。"),
        SettingsFeedbackKind.LoadDefinitions => SettingsLocalization.Localize("Settings.Error.LoadDefinitions", "设置定义加载失败。"),
        SettingsFeedbackKind.LoadValues => SettingsLocalization.Localize("Settings.Error.LoadValues", "设置值加载失败。"),
        SettingsFeedbackKind.InvalidBeforeSave => SettingsLocalization.Localize("Settings.Error.InvalidBeforeSave", "部分设置格式无效，请修正后再保存。"),
        SettingsFeedbackKind.NoChanges => SettingsLocalization.Localize("Settings.Message.NoChanges", "没有需要保存的修改。"),
        SettingsFeedbackKind.Save => SettingsLocalization.Localize("Settings.Error.Save", "设置保存失败。"),
        SettingsFeedbackKind.InvalidAfterSave => SettingsLocalization.Localize("Settings.Error.InvalidAfterSave", "部分设置无效，请修正后再保存。"),
        SettingsFeedbackKind.Refresh => SettingsLocalization.Localize("Settings.Error.Refresh", "设置刷新失败。"),
        SettingsFeedbackKind.Saved => SettingsLocalization.Localize("Settings.Message.Saved", "设置已保存。"),
        SettingsFeedbackKind.Reset => SettingsLocalization.Localize("Settings.Message.Reset", "已恢复默认设置。"),
        SettingsFeedbackKind.ResetFailed => SettingsLocalization.Localize("Settings.Error.Reset", "恢复默认失败。"),
        SettingsFeedbackKind.ResetRetry => SettingsLocalization.Localize("Settings.Error.ResetRetry", "恢复默认失败，请重试。"),
        SettingsFeedbackKind.SaveRetry => SettingsLocalization.Localize("Settings.Error.SaveRetry", "设置保存失败，请重试。"),
        _ => null
    };

    private enum SettingsFeedbackKind
    {
        None,
        Api,
        Load,
        LoadDefinitions,
        LoadValues,
        InvalidBeforeSave,
        NoChanges,
        Save,
        InvalidAfterSave,
        Refresh,
        Saved,
        Reset,
        ResetFailed,
        ResetRetry,
        SaveRetry
    }

    public async Task<bool> CanLeaveAsync()
    {
        if (!HasChanges) return true;
        return await _dialogs.AskUnsavedChangesAsync() switch
        {
            UnsavedChoice.Save => await SaveCoreAsync(),
            UnsavedChoice.Discard => DiscardWithPreviewRollback(),
            _ => false
        };
    }

    private bool DiscardWithPreviewRollback()
    {
        foreach (var row in _rows.Values) row.RestoreSaved();
        HasChanges = false;
        PreviewDiscardedAppearance();
        return true;
    }

    public ValueTask DisposeAsync()
    {
        MosaicLocalizationManager.LanguageChanged -= OnLanguageChanged;
        return ValueTask.CompletedTask;
    }
}

internal static class SettingsLocalization
{
    public static string Localize(string key, string fallback) =>
        MosaicLocalizationManager.TryFindResource(key) ?? fallback;
}

internal static class SettingsLocalizationDictionaries
{
    public static System.Windows.ResourceDictionary Dictionary(bool english)
    {
        var uri = new Uri($"/Mosaic.Settings.Wpf;component/Localization/{(english ? "en" : "zh")}.xaml", UriKind.Relative);
        return (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(uri);
    }
}

internal sealed class SettingsSectionNameConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value switch
    {
        SettingsSection.Normal => SettingsLocalization.Localize("Settings.Section.Normal", "普通"),
        SettingsSection.Tasks => SettingsLocalization.Localize("Settings.Section.Tasks", "任务"),
        SettingsSection.Appearance => SettingsLocalization.Localize("Settings.Section.Appearance", "外观与布局"),
        SettingsSection.Storage => SettingsLocalization.Localize("Settings.Section.Storage", "存储与系统"),
        SettingsSection.Text => SettingsLocalization.Localize("Settings.Section.Text", "文案"),
        _ => string.Empty
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class InverseBooleanToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is bool enabled && enabled ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class SettingOptionDisplayConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is SettingOption option ? SettingsLocalization.Localize(option.DisplayNameKey, option.Value) : value ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class SectionSelectedHighlightConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (values is [SettingsSection section, SettingsSection selected] && section == selected)
            return System.Windows.Application.Current?.TryFindResource(MosaicTheme.AccentSoftBrushKey) ?? System.Windows.Media.Brushes.Transparent;
        return System.Windows.Media.Brushes.Transparent;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

