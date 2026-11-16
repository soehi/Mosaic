using System.Collections;
using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Settings.Contracts;
using Mosaic.Settings.Core;
using Mosaic.Settings.Wpf;
using Mosaic.Shell.Wpf;

namespace Mosaic.Wpf.Tests;

public sealed class EditingPresentationTests
{
    private sealed class Repository : ISettingRepository
    {
        internal Dictionary<string, object?> Values = [];
        internal int Writes;
        public Task<OperationResult<IReadOnlyDictionary<string, object?>>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<IReadOnlyDictionary<string, object?>>.Success(new Dictionary<string, object?>(Values)));
        public Task<OperationResult> SaveAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
        { Writes++; foreach (var item in values) Values[item.Key] = item.Value; return Task.FromResult(OperationResult.Success()); }
        public Task<OperationResult> DeleteAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Source : ISettingSource
    {
        public string ModuleId => "test-source";
        public IReadOnlyList<SettingDefinition> Definitions { get; } =
        [
            new("test.task", "test-source", "Task toggle", "Task toggle", SettingValueKind.Boolean, true, null, null, null,
                SettingsGroup.Tasks, SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.Reversible),
            new("test.choice", "test-source", "Appearance choice", "Appearance choice", SettingValueKind.Enum, "A", null, null, ["A", "B"],
                SettingsGroup.Appearance, SettingCategory.Normal, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.Reversible),
            new("test.text", "test-source", "Advanced text", "Advanced text", SettingValueKind.String, "Saved", null, null, null,
                SettingsGroup.Text, SettingCategory.Advanced, SettingApplyMode.Immediate, SettingPersistence.UserSettings, PreviewMode: SettingPreviewMode.Reversible)
        ];
        internal Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task>? Applying;
        internal bool TaskValue = true;
        public async Task ApplyAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default)
        {
            TaskValue = (bool)values["test.task"]!;
            if (Applying is not null) await Applying(values, cancellationToken);
        }
    }
    private sealed class Dialogs : IUserDialogService
    {
        internal int Prompts;
        internal UnsavedChoice Choice = UnsavedChoice.Cancel;
        internal TaskCompletionSource<UnsavedChoice>? Pending;
        internal TaskCompletionSource Entered = new();
        public Task<UnsavedChoice> AskUnsavedChangesAsync() { Prompts++; Entered.TrySetResult(); return Pending?.Task ?? Task.FromResult(Choice); }
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<bool> ConfirmDeleteAsync(string title) => Task.FromResult(false);
        public Task ShowErrorAsync(string message) => Task.CompletedTask;
    }
    private sealed class Diagnostics : IDiagnosticSink { public void Report(string correlationId, Exception exception) { } }
    private static T Get<T>(object model, string property) => (T)model.GetType().GetProperty(property)!.GetValue(model)!;
    private static void Set(object model, string property, object value) => model.GetType().GetProperty(property)!.SetValue(model, value);
    private static object Row(object model, string id) => Get<IList>(model, "VisibleRows").Cast<object>().Single(row => Get<string>(row, "Id") == id);
    private static async Task Section(object model, string section)
    {
        var value = Enum.Parse(Get<object>(model, "SelectedSection").GetType(), section);
        await Get<IAsyncRelayCommand>(model, "SelectSectionCommand").ExecuteAsync(value);
    }
    private static Task Drain(object model) => (Task)model.GetType().GetMethod("DrainPreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null)!;
    private static async Task<ViewPage> Page(ISettingsApi api, Dialogs dialogs)
    {
        var module = new SettingsUiModule(new(api, new UiDispatcher(Dispatcher.CurrentDispatcher), dialogs, new Diagnostics()));
        await module.ActivateAsync(); var page = module.Views[0].Factory.Create(); await page.Controller.InitializeAsync(); return page;
    }

    [Fact]
    public Task All_sections_and_hidden_advanced_rows_share_one_preview_and_one_commit() => Sta.Run(async () =>
    {
        var repository = new Repository(); var source = new Source(); var dialogs = new Dialogs();
        await using var module = new SettingsModule(new(repository)); using var registration = module.Api.Register(source);
        var page = await Page(module.Api, dialogs); var model = page.View.DataContext;
        Set(Row(model, "test.task"), "BooleanValue", false);
        await Section(model, "Appearance"); Set(Row(model, "test.choice"), "ChoiceValue", "B");
        Set(model, "ShowAdvanced", true); await Section(model, "Text");
        var textRow = Row(model, "test.text"); Set(textRow, "TextValue", "Draft text");
        Set(model, "ShowAdvanced", false); await Drain(model);
        Assert.Equal(0, dialogs.Prompts); Assert.False(source.TaskValue); Assert.Equal(0, repository.Writes);
        Assert.True(Get<bool>(model, "HasChanges")); Assert.StartsWith("3", Get<string>(model, "ChangesLabel"));
        Set(model, "ShowAdvanced", true); await Section(model, "Text"); Assert.Same(textRow, Row(model, "test.text"));
        Assert.Equal("Draft text", Get<string>(textRow, "TextValue"));
        await Get<IAsyncRelayCommand>(model, "SaveCommand").ExecuteAsync(null);
        Assert.Equal(1, repository.Writes); Assert.Equal(3, repository.Values.Count); Assert.False(Get<bool>(model, "HasChanges"));
        Set(textRow, "TextValue", "Second batch"); await Drain(model);
        await Get<IAsyncRelayCommand>(model, "DiscardCommand").ExecuteAsync(null);
        Assert.Equal("Draft text", Get<string>(textRow, "TextValue")); Assert.Equal(1, repository.Writes);
        await page.Controller.DisposeAsync();
    });

    [Fact]
    public Task Concurrent_leave_requests_share_one_prompt_and_cancel_preserves_preview() => Sta.Run(async () =>
    {
        var source = new Source(); var dialogs = new Dialogs { Pending = new() };
        await using var module = new SettingsModule(new(new Repository())); using var registration = module.Api.Register(source);
        var page = await Page(module.Api, dialogs); var model = page.View.DataContext;
        Set(Row(model, "test.task"), "BooleanValue", false);
        var first = page.Controller.CanLeaveAsync(); var second = page.Controller.CanLeaveAsync();
        Assert.Same(first, second); await dialogs.Entered.Task;
        Assert.False(Get<bool>(model, "IsNotBusy")); Assert.Equal(1, dialogs.Prompts);
        dialogs.Pending.SetResult(UnsavedChoice.Cancel);
        Assert.False(await first); Assert.True(Get<bool>(model, "IsNotBusy")); Assert.False(source.TaskValue);
        await page.Controller.DisposeAsync(); Assert.True(source.TaskValue);
    });

    [Fact]
    public Task Returning_to_original_value_still_waits_for_and_reverses_an_older_inflight_preview() => Sta.Run(async () =>
    {
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var source = new Source { Applying = async (values, ct) => { if (!(bool)values["test.task"]!) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } } };
        await using var module = new SettingsModule(new(new Repository())); using var registration = module.Api.Register(source);
        var dialogs = new Dialogs(); var page = await Page(module.Api, dialogs); var model = page.View.DataContext;
        var row = Row(model, "test.task"); Set(row, "BooleanValue", false); await entered.Task;
        Set(row, "BooleanValue", true); var leave = page.Controller.CanLeaveAsync();
        Assert.False(leave.IsCompleted); release.SetResult();
        Assert.True(await leave); Assert.True(source.TaskValue); Assert.Equal(0, dialogs.Prompts);
        await page.Controller.DisposeAsync();
    });

    [Fact]
    public Task Rollback_failure_refuses_to_leave_and_keeps_the_draft() => Sta.Run(async () =>
    {
        var source = new Source(); var dialogs = new Dialogs { Choice = UnsavedChoice.Discard };
        await using var module = new SettingsModule(new(new Repository())); using var registration = module.Api.Register(source);
        var page = await Page(module.Api, dialogs); var model = page.View.DataContext;
        Set(Row(model, "test.task"), "BooleanValue", false); await Drain(model);
        source.Applying = (values, _) => (bool)values["test.task"]! ? Task.FromException(new IOException()) : Task.CompletedTask;
        Assert.False(await page.Controller.CanLeaveAsync()); Assert.True(Get<bool>(model, "HasChanges")); Assert.True(Get<bool>(model, "IsError"));
        source.Applying = null; await page.Controller.DisposeAsync(); Assert.True(source.TaskValue);
    });
}
