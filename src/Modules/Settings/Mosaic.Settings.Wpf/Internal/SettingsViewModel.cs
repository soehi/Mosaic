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
    Tasks,
    Appearance,
    Storage,
    Text,
    DesktopPresence,
    FloatingWindows,
    AccountsAndBackup
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
    public string DisplayName => SettingsLocalization.Localize(Definition.DisplayNameKey ?? "Settings.Setting." + Id, Definition.DisplayName);
    public string Description => SettingsLocalization.Localize(Definition.DescriptionKey ?? "Settings.Setting." + Id + ".Description", Definition.Description);
    public string Constraint => GetConstraint();
    private bool _previewed;
    public bool HasPreviewStatus => IsDirty;
    public string PreviewStatus => !IsDirty ? string.Empty : !string.IsNullOrEmpty(Error)
        ? SettingsLocalization.Localize("Settings.Preview.InvalidRow", "未预览：值无效")
        : RequiresRestart ? SettingsLocalization.Localize("Settings.Preview.Restart", "保存后重启")
        : _previewed ? SettingsLocalization.Localize("Settings.Preview.Row", "预览中")
        : SettingsLocalization.Localize("Settings.Preview.SaveOnly", "暂存，保存后生效");
    public void SetPreviewed(bool value)
    {
        _previewed = value;
        OnPropertyChanged(nameof(PreviewStatus)); OnPropertyChanged(nameof(HasPreviewStatus));
    }
    public bool IsAdvanced => Definition.Category == SettingCategory.Advanced;
    public bool RequiresRestart => Definition.ApplyMode == SettingApplyMode.Restart;
    public bool HasWarning => !string.IsNullOrWhiteSpace(Definition.Warning);
    public string Warning => Definition.Warning ?? string.Empty;
    public bool IsDefault => SettingValueComparer.Equivalent(Definition, CurrentValue, Definition.DefaultValue);
    public bool IsDirty => IsInteger && !long.TryParse(TextValue, out _) ||
        !SettingValueComparer.Equivalent(Definition, NormalizedProposed, NormalizedCurrent);
    public IReadOnlyList<SettingOption> Choices => Definition.Options is { Count: > 0 } options
        ? options
        : (Definition.AllowedValues ?? []).Select(value => new SettingOption(value, "Setting.Value." + value)).ToArray();
    public bool IsBoolean => Definition.Kind == SettingValueKind.Boolean;
    public bool IsEnum => Definition.Kind == SettingValueKind.Enum;
    public bool IsInteger => Definition.Kind == SettingValueKind.Integer;
    public bool IsText => Definition.Kind is SettingValueKind.String or SettingValueKind.ModuleList or SettingValueKind.ProviderMap;
    public bool IsProviderMap => Definition.Kind == SettingValueKind.ProviderMap;
    public bool IsChoice => IsBoolean || IsEnum;
    public string DefaultValueText => DisplayValue(Definition.DefaultValue);
    public string CurrentValueText => DisplayValue(CurrentValue);
    private string DisplayValue(object? value)
    {
        if (value is bool enabled)
            return SettingsLocalization.Localize(enabled ? "Settings.Value.On" : "Settings.Value.Off", enabled ? "开启" : "关闭");
        var text = FormatText(value);
        var option = Choices.FirstOrDefault(item => item.Value == text);
        return option is null ? text : SettingsLocalization.Localize(option.DisplayNameKey, text);
    }
    public string TextHint => Definition.Kind switch
    {
        SettingValueKind.ModuleList => string.Format(
            SettingsLocalization.Localize("Settings.Hint.ModuleList", "模块 ID 以逗号分隔，例如：{0}"),
            SettingValueFormat.ModuleListExample),
        SettingValueKind.ProviderMap => string.Format(
            SettingsLocalization.Localize("Settings.Hint.ProviderMap", "每行或分号分隔，例如：{0}"),
            SettingValueFormat.ProviderMapExample),
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
                OnPropertyChanged(nameof(BooleanValue));
                OnPropertyChanged(nameof(ChoiceValue));
                NotifyChanged();
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
        OnPropertyChanged(nameof(DefaultValueText));
        OnPropertyChanged(nameof(CurrentValueText));
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(PreviewStatus));
        OnPropertyChanged(nameof(HasPreviewStatus));
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
        RowErrorKind.ProviderMap => string.Format(
            SettingsLocalization.Localize("Settings.Error.ProviderMap", "格式应为 capability@version=module-id，且不能重复。例如：{0}"),
            SettingValueFormat.ProviderMapExample),
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
            case SettingValueKind.ModuleList:
            case SettingValueKind.ProviderMap:
                if (!SettingValueFormat.TryParse(Definition.Kind, TextValue, out value, out var formatError))
                {
                    error = SetLocalizedError(formatError == SettingFormatError.ProviderMap
                        ? RowErrorKind.ProviderMap
                        : RowErrorKind.Integer);
                    return false;
                }
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
            SettingValueKind.ProviderMap when value is string text &&
                                               SettingValueFormat.TryParseProviderMap(text, out var providers) => providers,
            _ => value
        };
    }

    private string GetConstraint()
    {
        if (Definition.AllowedValues is { Count: > 0 }) return string.Join(" / ", Choices.Select(item => SettingsLocalization.Localize(item.DisplayNameKey, item.Value)));
        if (Definition.Min is not null || Definition.Max is not null) return $"{Definition.Min} – {Definition.Max}";
        if (Definition.Kind == SettingValueKind.String) return SettingsLocalization.Localize("Settings.Constraint.Max200", "最多 200 个字符");
        return Definition.Kind switch
        {
            SettingValueKind.Boolean => SettingsLocalization.Localize("Settings.Constraint.Boolean", "开 / 关"),
            SettingValueKind.ModuleList => string.Format(
                SettingsLocalization.Localize("Settings.Constraint.ModuleList", "逗号分隔的模块 ID，例如：{0}"),
                SettingValueFormat.ModuleListExample),
            SettingValueKind.ProviderMap => string.Format(
                SettingsLocalization.Localize("Settings.Constraint.ProviderMap", "capability@version=module-id，分号分隔，例如：{0}"),
                SettingValueFormat.ProviderMapExample),
            _ => string.Empty
        };
    }

    private string FormatText(object? value) => SettingValueFormat.Format(Definition.Kind, value);

    private static string Localize(string key, string fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;
}

internal sealed partial class SettingsViewModel : ObservableObject, IPageController
{
    private readonly ISettingsApi _settings;
    private readonly IUserDialogService _dialogs;
    private readonly IReadOnlyList<SettingsPageContribution> _pages;
    private readonly IReadOnlyDictionary<SettingsSection, SettingsPageContribution> _pagesBySection;
    private readonly Dictionary<string, SettingRow> _rows = [];
    private readonly CancellationTokenSource _lifetime = new();
    private ISettingsEditSession? _session;
    private bool _isLoading, _isBusy, _hasChanges, _showAdvanced, _isError, _updating, _leaving, _disposed;
    private bool _previewRequested;
    private int _previewDelay = 1;
    private long _revision;
    private long _previewRevision = -1;
    private Task _previewTask = Task.CompletedTask;
    private Task<bool>? _leaveTask;
    private SettingPreviewResult? _lastPreview;
    private SettingsSection _selectedSection = SettingsSection.Tasks;
    private string? _message;
    private TaskCompletionSource? _busyCompletion;
    private bool _isPreviewing;
    private ViewPage? _selectedPageView;
    private readonly Dictionary<string, ViewPage> _createdPages = [];

    public SettingsViewModel(ISettingsApi settings, IUserDialogService dialogs) : this(settings, dialogs, new SettingsPageRegistry()) { }

    public SettingsViewModel(ISettingsApi settings, IUserDialogService dialogs, SettingsPageRegistry pages)
    {
        _settings = settings; _dialogs = dialogs;
        _pages = pages.Pages;
        _pagesBySection = _pages.ToDictionary(page => Enum.Parse<SettingsSection>(page.GroupId, ignoreCase: false));
        if (pages.InitialGroupId is { } groupId) _selectedSection = Enum.Parse<SettingsSection>(groupId, ignoreCase: false);
        SaveCommand = new AsyncRelayCommand(() => SaveCoreAsync(), () => IsNotBusy && HasChanges);
        DiscardCommand = new AsyncRelayCommand(DiscardChangesAsync, () => IsNotBusy && (HasChanges || IsError));
        ResetSectionCommand = new AsyncRelayCommand(() => ResetCoreAsync(VisibleRows.ToArray()), () => IsNotBusy && HasVisibleRows);
        ResetAllCommand = new AsyncRelayCommand(() => ResetCoreAsync(_rows.Values.ToArray()), () => IsNotBusy && ShowAdvanced);
        ResetToDefaultCommand = new RelayCommand<SettingRow>(row => { if (row is not null) ResetRows([row]); }, row => IsNotBusy && row is not null);
        SelectSectionCommand = new AsyncRelayCommand<SettingsSection>(SelectSectionAsync, _ => IsNotBusy);
        MosaicLocalizationManager.LanguageChanged += OnLanguageChanged;
    }
    public ObservableCollection<SettingsSection> Sections { get; } = [];
    public ObservableCollection<SettingRow> VisibleRows { get; } = [];
    public SettingsPageContribution? SelectedPage => _pagesBySection.GetValueOrDefault(SelectedSection);
    public ViewPage? SelectedPageView => _selectedPageView;
    public bool HasSelectedPage => SelectedPageView is not null;
    public bool HasSettingsSectionContent => SelectedPage is null;
    public SettingsSection SelectedSection
    {
        get => _selectedSection;
        set { if (SetProperty(ref _selectedSection, value)) ApplySectionFilter(); }
    }
    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (!IsNotBusy || !SetProperty(ref _showAdvanced, value)) return;
            RefreshSections(); ApplySectionFilter(); OnPropertyChanged(nameof(ModeDescription)); UpdateCommands();
        }
    }
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            if (value) _busyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously); else _busyCompletion?.TrySetResult();
            NotifyState();
        }
    }
    public bool IsNotBusy => !IsBusy && !_leaving && !_disposed && !IsLoading;
    public bool IsPreviewing { get => _isPreviewing; private set { if (SetProperty(ref _isPreviewing, value)) NotifyState(); } }
    public bool HasChanges { get => _hasChanges; private set { if (SetProperty(ref _hasChanges, value)) NotifyState(); } }
    public bool ShowActionBar => IsBusy || IsPreviewing || IsError || HasChanges;
    public bool IsError { get => _isError; private set { if (SetProperty(ref _isError, value)) NotifyState(); } }
    public string? Message { get => _message; private set { if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message) && !IsError;
    public bool HasRows => VisibleRows.Count > 0;
    public bool HasVisibleRows => HasRows;
    public string EmptyMessage => Local("Settings.Empty.Section", "当前模式下没有可见设置。");
    public string ModeDescription => Local(ShowAdvanced ? "Settings.Mode.Advanced" : "Settings.Mode.Daily",
        ShowAdvanced ? "显示全部参数，请留意风险和重启提示。" : "常用偏好直接调整，更多参数可开启高级设置。");
    public string VisibleCountLabel => string.Format(Local("Settings.VisibleCount", "{0} 项设置"), VisibleRows.Count);
    public string ChangesLabel
    {
        get
        {
            if (IsBusy) return Local("Settings.Working", "正在处理，请稍候…");
            var count = _rows.Values.Count(row => row.IsDirty);
            if (count == 0) return IsPreviewing ? Local("Settings.Preview.Wait", "正在同步预览…") : Local("Settings.NoUnsaved", "没有未保存修改");
            var text = string.Format(Local("Settings.UnsavedCount", "{0} 项修改尚未保存"), count);
            var hidden = _rows.Values.Count(row => row.IsDirty && !VisibleRows.Contains(row));
            if (hidden > 0) text += string.Format(Local("Settings.Preview.Hidden", "，含其他分区 {0} 项"), hidden);
            return IsPreviewing ? text + Local("Settings.Preview.Updating", " · 更新预览中") : text;
        }
    }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand DiscardCommand { get; }
    public IAsyncRelayCommand ResetSectionCommand { get; }
    public IAsyncRelayCommand ResetAllCommand { get; }
    public IRelayCommand ResetToDefaultCommand { get; }
    public IAsyncRelayCommand<SettingsSection> SelectSectionCommand { get; }
    private static string Local(string key, string fallback) => SettingsLocalization.Localize(key, fallback);

    private async Task SelectSectionAsync(SettingsSection section)
    {
        if (!Sections.Contains(section) || section == SelectedSection) return;
        if (_selectedPageView is not null && !await _selectedPageView.Controller.CanLeaveAsync()) return;
        if (_pagesBySection.ContainsKey(section) && _session is not null && !await CanLeaveAsync()) return;
        SelectedSection = section;
        if (SelectedPage is { } contribution)
        {
            if (_session is not null)
            {
                if (!await CanLeaveAsync()) return;
                await _session.DisposeAsync();
                _session = null;
            }
            await SelectPageAsync(contribution);
        }
        else
        {
            _selectedPageView = null;
            OnPropertyChanged(nameof(SelectedPageView));
            OnPropertyChanged(nameof(HasSelectedPage));
            if (_session is null)
            {
                foreach (var row in _rows.Values) row.Changed -= RowChanged;
                _rows.Clear();
                await InitializeAsync();
            }
        }
    }

    private async Task SelectPageAsync(SettingsPageContribution contribution)
    {
        if (!_createdPages.TryGetValue(contribution.Id, out var page))
        {
            page = contribution.Factory.Create();
            await page.Controller.InitializeAsync();
            _createdPages.Add(contribution.Id, page);
        }
        _selectedPageView = page;
        OnPropertyChanged(nameof(SelectedPageView));
        OnPropertyChanged(nameof(HasSelectedPage));
    }

    public async Task InitializeAsync()
    {
        if (SelectedPage is { } contribution)
        {
            var definitions = await _settings.ListDefinitionsAsync(_lifetime.Token);
            if (definitions.IsSuccess)
            {
                foreach (var definition in definitions.Value)
                {
                    var row = new SettingRow(definition, definition.DefaultValue);
                    row.Changed += RowChanged;
                    _rows[definition.Id] = row;
                }
            }
            await SelectPageAsync(contribution);
            RefreshSections();
            return;
        }
        if (_rows.Count != 0 || IsLoading || _disposed) return;
        IsLoading = true;
        try
        {
            var definitions = await _settings.ListDefinitionsAsync(_lifetime.Token);
            if (!definitions.IsSuccess) { Fail(SettingsLocalization.Error(definitions.Error)); return; }
            var session = await _settings.BeginEditSessionAsync(_lifetime.Token);
            if (!session.IsSuccess) { Fail(SettingsLocalization.Error(session.Error)); return; }
            _session = session.Value;
            foreach (var definition in definitions.Value)
            {
                var row = new SettingRow(definition, _session.SavedValues.GetValueOrDefault(definition.Id));
                row.Changed += RowChanged; _rows[definition.Id] = row;
            }
            RefreshSections(); ApplySectionFilter(); RefreshDirty();
        }
        catch (Exception) { Fail(Local("Settings.Error.Load", "无法加载设置，请重试。")); }
        finally { IsLoading = false; NotifyState(); }
    }
    private void RowChanged(object? sender, EventArgs e)
    {
        if (_updating || _disposed) return;
        if (sender is SettingRow row) row.SetPreviewed(false);
        RefreshDirty(); RequestPreview(sender is SettingRow changed && changed.Definition.Kind == SettingValueKind.String ? 130 : 1);
    }
    private void RequestPreview(int delay = 1)
    {
        if (_disposed || _session is null) return;
        ++_revision; _previewRequested = true; _previewDelay = delay;
        if (_previewTask.IsCompleted) _previewTask = PreviewLoopAsync();
    }
    private Dictionary<string, object?> Changes()
    {
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var row in _rows.Values.Where(row => row.IsDirty))
            changes[row.Id] = row.TryGetProposedValue(out var value, out _) ? value : row.TextValue;
        return changes;
    }
    private async Task PreviewLoopAsync()
    {
        IsPreviewing = true;
        try
        {
            while (_previewRequested && !_disposed)
            {
                var queuedRevision = _revision;
                await Task.Delay(_previewDelay, _lifetime.Token);
                if (queuedRevision != _revision && _previewDelay > 1) continue;
                _previewRequested = false;
                var revision = _revision;
                var result = await _session!.PreviewAsync(Changes(), revision, _lifetime.Token);
                if (_disposed || revision != _revision) continue;
                _previewRevision = revision;
                if (!result.IsSuccess) { Fail(SettingsLocalization.Error(result.Error)); _lastPreview = null; continue; }
                _lastPreview = result.Value;
                foreach (var row in _rows.Values)
                {
                    row.SetError(result.Value.Errors.GetValueOrDefault(row.Id)?.Message);
                    row.SetPreviewed(result.Value.AppliedDefinitionIds.Contains(row.Id));
                }
                IsError = result.Value.Errors.Count > 0;
                Message = IsError ? Local("Settings.Preview.Invalid", "部分输入无效，当前显示上次有效预览。") : null;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { if (!_disposed) Fail(Local("Settings.Preview.Failed", "预览失败，输入已保留。")); }
        finally { IsPreviewing = false; }
    }
    private async Task DrainPreviewAsync()
    {
        if (_session is null) return;
        _previewDelay = 1;
        if (_previewRevision != _revision) _previewRequested = true;
        if (_previewRequested && _previewTask.IsCompleted) _previewTask = PreviewLoopAsync();
        await _previewTask;
    }
    private void Fail(string message) { IsError = true; Message = message; }
    private async Task<bool> SaveCoreAsync()
    {
        if (IsBusy || _session is null) return false;
        IsBusy = true;
        try
        {
            await DrainPreviewAsync();
            if (_lastPreview is null || _lastPreview.Errors.Count > 0)
            {
                var invalid = _rows.Values.FirstOrDefault(row => !string.IsNullOrEmpty(row.Error));
                if (invalid is not null) Reveal(invalid);
                Fail(Local("Settings.Error.InvalidBeforeSave", "部分输入无效，请修正后保存。")); return false;
            }
            var risky = _rows.Values.Where(row => row.IsDirty && row.HasWarning).ToArray();
            if (risky.Length > 0 && !await _dialogs.ConfirmAsync(Local("Settings.Risk", "风险"),
                string.Join("\n\n", risky.Select(row => row.DisplayName + "：" + row.Warning)))) return false;
            var committed = await _session.CommitAsync(_revision, _lifetime.Token);
            if (!committed.IsSuccess)
            {
                var message = SettingsLocalization.Error(committed.Error);
                if (_rows.GetValueOrDefault(committed.Error!.Field ?? "") is { } row) { row.SetError(message); Reveal(row); }
                Fail(message); return false;
            }
            UpdateFromSaved(); IsError = false;
            Message = committed.Value.ApplyWarnings.Count > 0 ? committed.Value.ApplyWarnings.First().Value.Message : Local("Settings.Message.Saved", "设置已保存。");
            return true;
        }
        catch (Exception) { Fail(Local("Settings.Error.SaveRetry", "设置保存失败，输入已保留。")); return false; }
        finally { IsBusy = false; }
    }
    private async Task DiscardChangesAsync()
    {
        if (IsBusy || _session is null) return;
        IsBusy = true;
        try
        {
            await DrainPreviewAsync();
            var result = await _session.DiscardAsync(_lifetime.Token);
            if (!result.IsSuccess) { Fail(SettingsLocalization.Error(result.Error)); return; }
            UpdateFromSaved(); IsError = false; Message = null;
        }
        catch (Exception) { Fail(Local("Settings.Preview.RollbackFailed", "未能恢复保存的设置，请重试。")); }
        finally { IsBusy = false; }
    }
    private void UpdateFromSaved()
    {
        _updating = true;
        try { foreach (var row in _rows.Values) { row.SetValue(_session!.SavedValues.GetValueOrDefault(row.Id)); row.SetPreviewed(false); } }
        finally { _updating = false; }
        _lastPreview = null; _previewRevision = _revision; RefreshDirty();
    }
    private Task ResetCoreAsync(SettingRow[] rows) { ResetRows(rows); return Task.CompletedTask; }
    private void ResetRows(SettingRow[] rows)
    {
        _updating = true;
        try { foreach (var row in rows) row.Reset(); }
        finally { _updating = false; }
        RefreshDirty(); RequestPreview();
    }
    private void Reveal(SettingRow row)
    {
        if (row.IsAdvanced && !ShowAdvanced)
        {
            _showAdvanced = true; OnPropertyChanged(nameof(ShowAdvanced)); RefreshSections();
        }
        SelectedSection = SectionFor(row.Definition.Group);
    }
    private static SettingsSection SectionFor(SettingsGroup group) => group switch
    {
        SettingsGroup.StorageSystem => SettingsSection.Storage,
        SettingsGroup.Appearance => SettingsSection.Appearance,
        SettingsGroup.Text => SettingsSection.Text,
        SettingsGroup.DesktopPresence => SettingsSection.DesktopPresence,
        SettingsGroup.FloatingWindows => SettingsSection.FloatingWindows,
        SettingsGroup.AccountsAndBackup => SettingsSection.AccountsAndBackup,
        _ => SettingsSection.Tasks
    };
    private void ApplySectionFilter()
    {
        VisibleRows.Clear();
        foreach (var row in _rows.Values.Where(row => (ShowAdvanced || !row.IsAdvanced) && SectionFor(row.Definition.Group) == SelectedSection).OrderBy(row => row.Id, StringComparer.Ordinal)) VisibleRows.Add(row);
        OnPropertyChanged(nameof(SelectedPage));
        OnPropertyChanged(nameof(HasSelectedPage));
        OnPropertyChanged(nameof(HasSettingsSectionContent));
        NotifyState();
    }
    private void RefreshSections()
    {
        Sections.Clear();
        foreach (var section in Enum.GetValues<SettingsSection>())
            if (_pagesBySection.ContainsKey(section) || _rows.Values.Any(row => SectionFor(row.Definition.Group) == section && (ShowAdvanced || !row.IsAdvanced))) Sections.Add(section);
        if (!Sections.Contains(SelectedSection) && Sections.Count > 0) SelectedSection = Sections[0];
    }
    private void RefreshDirty() { HasChanges = _rows.Values.Any(row => row.IsDirty); NotifyState(); }
    private void NotifyState()
    {
        foreach (var name in new[] { nameof(IsNotBusy), nameof(ShowActionBar), nameof(ChangesLabel), nameof(HasRows), nameof(HasVisibleRows), nameof(VisibleCountLabel), nameof(HasMessage), nameof(EmptyMessage) }) OnPropertyChanged(name);
        UpdateCommands();
    }
    private void UpdateCommands()
    {
        SaveCommand?.NotifyCanExecuteChanged(); DiscardCommand?.NotifyCanExecuteChanged(); ResetSectionCommand?.NotifyCanExecuteChanged();
        ResetAllCommand?.NotifyCanExecuteChanged(); ResetToDefaultCommand?.NotifyCanExecuteChanged(); SelectSectionCommand?.NotifyCanExecuteChanged();
    }
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var row in _rows.Values) row.RefreshLocalizedText();
        RefreshSections(); OnPropertyChanged(nameof(ModeDescription)); NotifyState();
    }
    public Task<bool> CanLeaveAsync()
    {
        if (_leaveTask is { IsCompleted: false }) return _leaveTask;
        return _leaveTask = LeaveAsync();
    }
    private async Task<bool> LeaveAsync()
    {
        _leaving = true; NotifyState();
        try
        {
            if (IsBusy && _busyCompletion is { } completion) await completion.Task;
            await DrainPreviewAsync();
            RefreshDirty();
            if (!HasChanges && !IsError) return true;
            var choice = await _dialogs.AskUnsavedChangesAsync();
            if (choice == UnsavedChoice.Cancel) return false;
            if (choice == UnsavedChoice.Save) return await SaveCoreAsync();
            await DiscardChangesAsync();
            return !HasChanges && !IsError;
        }
        finally { _leaving = false; NotifyState(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        MosaicLocalizationManager.LanguageChanged -= OnLanguageChanged;
        foreach (var row in _rows.Values) row.Changed -= RowChanged;
        await _previewTask;
        foreach (var page in _createdPages.Values) await page.Controller.DisposeAsync();
        _createdPages.Clear();
        if (_session is not null) await _session.DisposeAsync();
        _lifetime.Dispose();
    }
}

internal static class SettingsLocalization
{
    public static string Localize(string key, string fallback) =>
        MosaicLocalizationManager.TryFindResource(key) ?? fallback;

    public static string Error(Mosaic.Contracts.OperationError? error)
    {
        if (error is null) return Localize("Settings.Error.Unknown", "设置操作失败。");
        if (error.MessageKey is { } key && MosaicLocalizationManager.TryFindResource(key) is { } localized)
            return localized;
        var keyByCode = error.Code switch
        {
            Mosaic.Contracts.ErrorCodes.Conflict => "Settings.Error.Conflict",
            Mosaic.Contracts.ErrorCodes.CapabilityUnavailable => "Settings.Error.Unavailable",
            Mosaic.Contracts.ErrorCodes.StorageUnavailable => "Settings.Error.Storage",
            Mosaic.Contracts.ErrorCodes.ValidationFailed or Mosaic.Contracts.ErrorCodes.InvalidRequest => "Settings.Error.Validation",
            Mosaic.Contracts.ErrorCodes.Canceled => "Settings.Error.Canceled",
            _ => "Settings.Error.Unknown"
        };
        return Localize(keyByCode, error.Message);
    }
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
        SettingsSection.Tasks => SettingsLocalization.Localize("Settings.Section.Tasks", "任务"),
        SettingsSection.Appearance => SettingsLocalization.Localize("Settings.Section.Appearance", "外观与布局"),
        SettingsSection.Storage => SettingsLocalization.Localize("Settings.Section.Storage", "存储与系统"),
        SettingsSection.Text => SettingsLocalization.Localize("Settings.Section.Text", "文案"),
        SettingsSection.DesktopPresence => SettingsLocalization.Localize("Settings.Section.DesktopPresence", "驻留与边缘"),
        SettingsSection.FloatingWindows => SettingsLocalization.Localize("Settings.Section.FloatingWindows", "悬浮任务窗"),
        SettingsSection.AccountsAndBackup => SettingsLocalization.Localize("Settings.Section.AccountsAndBackup", "账户与备份"),
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
        if (parameter as string == "Selected")
            return values is [SettingsSection item, SettingsSection current] && item == current;
        if (values is [SettingsSection section, SettingsSection selected] && section == selected)
            return System.Windows.Application.Current?.TryFindResource(MosaicTheme.AccentSoftBrushKey) ?? System.Windows.Media.Brushes.Transparent;
        return System.Windows.Media.Brushes.Transparent;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

