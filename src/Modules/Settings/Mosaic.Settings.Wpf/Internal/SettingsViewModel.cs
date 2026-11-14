using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Contracts;
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

internal sealed class SettingRow : ObservableObject
{
    private object? _currentValue;
    private object? _proposedValue;
    private string _textValue;
    private string? _error;

    public SettingRow(SettingDefinition definition, object? value)
    {
        Definition = definition;
        _currentValue = value;
        _proposedValue = value;
        _textValue = FormatText(value);
    }

    public SettingDefinition Definition { get; }
    public string Id => Definition.Id;
    public string DisplayName => Definition.DisplayName;
    public string Description => Definition.Description;
    public string Constraint => GetConstraint();
    public bool IsAdvanced => Definition.Category == SettingCategory.Advanced;
    public bool RequiresRestart => Definition.ApplyMode == SettingApplyMode.Restart;
    public bool HasWarning => !string.IsNullOrWhiteSpace(Definition.Warning);
    public string Warning => Definition.Warning ?? string.Empty;
    public bool IsDefault => Equals(NormalizedCurrent, Definition.DefaultValue);
    public bool IsDirty => !Equals(NormalizedProposed, NormalizedCurrent);
    public IReadOnlyList<string> Choices => Definition.AllowedValues ?? [];
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
        SettingValueKind.ModuleList => "模块 ID 以逗号分隔",
        SettingValueKind.ProviderMap => "每行或分号分隔：capacity@version=module-id",
        _ => "文本"
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
            }
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
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(IsDefault));
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
        TextValue = FormatText(value);
        Error = null;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsDefault));
    }

    public void Reset()
    {
        ProposedValue = Definition.DefaultValue;
        TextValue = FormatText(Definition.DefaultValue);
        Error = null;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsDefault));
    }

    public void SetError(string? message) => Error = message;

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
                {
                    error = "请输入整数。";
                    return false;
                }
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
                        error = "格式应为 capability@version=module-id，且不能重复。";
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
                error = "不支持的设置类型。";
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
        if (Definition.AllowedValues is { Count: > 0 }) return string.Join(" / ", Definition.AllowedValues);
        if (Definition.Min is not null || Definition.Max is not null) return $"{Definition.Min} – {Definition.Max}";
        if (Definition.Kind == SettingValueKind.String) return "最多 200 个字符";
        return Definition.Kind switch
        {
            SettingValueKind.Boolean => "开 / 关",
            SettingValueKind.ModuleList => "逗号分隔的模块 ID",
            SettingValueKind.ProviderMap => "capability=module-id，分号分隔",
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
}

internal sealed partial class SettingsViewModel : ObservableObject, IPageController
{
    private readonly ISettingsApi _settings;
    private readonly IUserDialogService _dialogs;
    private readonly Dictionary<string, SettingRow> _rows = [];
    private bool _isLoading;
    private bool _isBusy;
    private bool _hasChanges;
    private string _selectedSection = "普通";
    private bool _showAdvanced;
    private string? _message;
    private bool _isError;

    public SettingsViewModel(ISettingsApi settings, IUserDialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        SaveCommand = new AsyncRelayCommand(() => SaveCoreAsync(), () => !IsBusy);
        ResetSectionCommand = new AsyncRelayCommand(() => ResetCoreAsync(SectionModuleIds()), () => !IsBusy);
        ResetAllCommand = new AsyncRelayCommand(() => ResetCoreAsync(null), () => !IsBusy);
        DiscardCommand = new RelayCommand(DiscardChanges, () => HasChanges);
        ResetToDefaultCommand = new RelayCommand<SettingRow>(row => row?.Reset(), _ => !IsBusy);
        SelectSectionCommand = new RelayCommand<string>(SelectSection, _ => !IsBusy);
        Sections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
        VisibleRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
    }

    public ObservableCollection<string> Sections { get; } = ["普通", "任务", "外观与布局", "存储与系统", "文案"];
    public ObservableCollection<SettingRow> VisibleRows { get; } = [];

    public string SelectedSection
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
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
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
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }
    public bool IsError { get => _isError; private set => SetProperty(ref _isError, value); }
    public bool HasRows => VisibleRows.Count > 0;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message) && !IsError;
    public string EmptyMessage => SelectedSection == "普通"
        ? "没有普通设置。打开高级设置查看全部参数。"
        : "当前分区没有设置。";
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand ResetSectionCommand { get; }
    public IRelayCommand ResetAllCommand { get; }
    public IRelayCommand DiscardCommand { get; }
    public IRelayCommand ResetToDefaultCommand { get; }
    public IRelayCommand<string> SelectSectionCommand { get; }

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
        try
        {
            var definitions = await _settings.ListDefinitionsAsync();
            var values = await _settings.GetValuesAsync();
            if (!definitions.IsSuccess) throw new InvalidOperationException(definitions.Error?.Message ?? "设置定义加载失败。");
            if (!values.IsSuccess) throw new InvalidOperationException(values.Error?.Message ?? "设置值加载失败。");

            var source = values.Value!;
            _rows.Clear();
            foreach (var definition in definitions.Value!)
                _rows[definition.Id] = new SettingRow(definition, source.GetValueOrDefault(definition.Id));
            ApplySectionFilter();
            HasChanges = false;
        }
        catch (Exception exception)
        {
            Message = exception is InvalidOperationException ? exception.Message : "无法加载设置，请刷新后重试。";
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
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private bool IsRowVisible(SettingRow row)
    {
        if (!ShowAdvanced && row.IsAdvanced) return false;
        return SelectedSection switch
        {
            "普通" => !row.IsAdvanced,
            "任务" => row.Definition.ModuleId.StartsWith("tasks", StringComparison.Ordinal),
            "外观与布局" => row.Definition.ModuleId is "shell" or "host" && row.Id.StartsWith("shell.", StringComparison.Ordinal),
            "存储与系统" => row.Definition.ModuleId is "storage-sqlite" or "host" &&
                (row.Id.StartsWith("storage.", StringComparison.Ordinal) || row.Id.StartsWith("host.", StringComparison.Ordinal) ||
                    row.Id.StartsWith("logging.", StringComparison.Ordinal) || row.Id.StartsWith("diagnostics.", StringComparison.Ordinal) ||
                    row.Id.StartsWith("startup.", StringComparison.Ordinal)),
            "文案" => row.Id.StartsWith("ui.text.", StringComparison.Ordinal),
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
                    row.SetError(fieldError);
                    Message = "部分设置格式无效，请修正后再保存。";
                    IsError = true;
                    return false;
                }
                parsed[row.Id] = value;
            }
            var changes = parsed;
            if (changes.Count == 0)
            {
                HasChanges = false;
                Message = "没有需要保存的修改。";
                return true;
            }

            foreach (var row in _rows.Values) row.SetError(null);
            var proposed = await _settings.ProposeChangesAsync(changes);
            if (!proposed.IsSuccess)
                throw new InvalidOperationException(proposed.Error?.Message ?? "设置保存失败。");
            if (proposed.Value!.Count > 0)
            {
                foreach (var (id, error) in proposed.Value)
                    _rows.GetValueOrDefault(id)?.SetError(error.Message);
                Message = "部分设置无效，请修正后再保存。";
                IsError = true;
                return false;
            }

            var values = await _settings.GetValuesAsync();
            if (!values.IsSuccess)
                throw new InvalidOperationException(values.Error?.Message ?? "设置刷新失败。");
            foreach (var row in _rows.Values) row.SetValue(values.Value!.GetValueOrDefault(row.Id));
            HasChanges = false;
            Message = "设置已保存。";
            return true;
        }
        catch (Exception exception)
        {
            Message = exception is InvalidOperationException ? exception.Message : "设置保存失败，请重试。";
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
                    throw new InvalidOperationException("恢复默认失败。");
            }
            else
            {
                foreach (var moduleId in moduleIds)
                {
                    if (!(await _settings.ResetAsync(moduleId)).IsSuccess)
                        throw new InvalidOperationException("恢复默认失败。");
                }
            }

            var values = await _settings.GetValuesAsync();
            if (!values.IsSuccess)
                throw new InvalidOperationException(values.Error?.Message ?? "设置刷新失败。");
            foreach (var row in _rows.Values) row.SetValue(values.Value!.GetValueOrDefault(row.Id));
            HasChanges = false;
            Message = "已恢复默认设置。";
        }
        catch (Exception exception)
        {
            Message = exception is InvalidOperationException ? exception.Message : "恢复默认失败，请重试。";
            IsError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string[] SectionModuleIds() => SelectedSection switch
    {
        "任务" => ["tasks-core", "tasks-ui"],
        "外观与布局" => ["shell"],
        "存储与系统" => ["storage-sqlite", "host"],
        "文案" => ["tasks-ui"],
        _ => ["tasks-ui", "tasks-core", "shell"]
    };

    private async Task<bool> ConfirmResetAsync(string? scope)
    {
        var title = scope is null ? "恢复全部默认" : $"恢复 {scope} 默认";
        var confirmed = await _dialogs.ConfirmDeleteAsync(title);
        return confirmed;
    }

    private void DiscardChanges()
    {
        foreach (var row in _rows.Values) row.Reset();
        HasChanges = false;
    }

    private void UpdateCommandStates()
    {
        ResetToDefaultCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void SelectSection(string? section)
    {
        if (!string.IsNullOrWhiteSpace(section)) SelectedSection = section;
    }

    public async Task<bool> CanLeaveAsync()
    {
        if (!HasChanges) return true;
        return await _dialogs.AskUnsavedChangesAsync() switch
        {
            UnsavedChoice.Save => await SaveCoreAsync(),
            UnsavedChoice.Discard => true,
            _ => false
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

