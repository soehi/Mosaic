using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Mosaic.Presentation.Wpf;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TaskFloatingDetailsView : Grid
{
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _empty = new() { IsHitTestVisible = false, Margin = new Thickness(6, 4, 6, 0) };
    private readonly TextBlock _created = new();
    private readonly TextBlock _updated = new();
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _edit = Button("FloatingNotesEdit");
    private readonly Button _save = Button("FloatingNotesSave");
    private readonly Button _cancel = Button("FloatingNotesCancel");
    private readonly Button _copy = Button("FloatingNotesCopy");
    private bool _rendering;

    internal TaskFloatingDetailsView(TaskFloatingNotesViewModel model, Action save, Action cancel)
    {
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        _notes.SetResourceReference(StyleProperty, "TaskFloatingNotesStyle");
        AutomationProperties.SetAutomationId(_notes, "FloatingNotes");
        InputMethod.SetIsInputMethodEnabled(_notes, true);
        Children.Add(_notes);
        Children.Add(_empty);

        var footer = new StackPanel { Margin = new Thickness(4, 4, 4, 0) };
        foreach (var label in new[] { _created, _updated, _feedback, _empty })
        {
            label.FontSize = 12;
            label.SetResourceReference(TextBlock.ForegroundProperty, MosaicTheme.TextSecondaryBrushKey);
        }
        _created.TextTrimming = _updated.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Children.Add(_created);
        footer.Children.Add(_updated);
        footer.Children.Add(_feedback);
        AutomationProperties.SetAutomationId(_feedback, "FloatingNotesFeedback");
        var actions = new WrapPanel();
        foreach (var button in new[] { _edit, _save, _cancel, _copy }) actions.Children.Add(button);
        footer.Children.Add(actions);
        SetRow(footer, 1);
        Children.Add(footer);

        _notes.TextChanged += (_, _) => { if (!_rendering) model.Change(_notes.Text); };
        _edit.Click += (_, _) => { model.Begin(); if (model.CanEdit) { _notes.Focus(); _notes.CaretIndex = _notes.Text.Length; } };
        _save.Click += (_, _) => save();
        _cancel.Click += (_, _) => cancel();
        _copy.Click += (_, _) => { _notes.Focus(); _notes.SelectAll(); _notes.Copy(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.S || Keyboard.Modifiers != ModifierKeys.Control || !model.IsEditing) return;
            e.Handled = true;
            if (_save.IsEnabled) save();
        };
    }

    internal void Render(TaskFloatingNotesViewModel model, bool completing)
    {
        var text = model.IsEditing ? model.Draft : model.Current?.Notes ?? "";
        _rendering = true;
        try { if (_notes.Text != text) _notes.Text = text; }
        finally { _rendering = false; }
        _notes.IsReadOnly = !model.CanEdit;
        AutomationProperties.SetName(_notes, Text("Label", "任务备注"));
        _empty.Text = TaskLocalization.Localize("Tasks.Peek.NoNotes", "暂无备注");
        _empty.Visibility = Visible(text.Length == 0 && !model.IsEditing && model.Current is not null);
        var culture = MosaicLocalizationManager.Current == MosaicLanguage.English ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("zh-CN");
        if (model.Current is { } current)
        {
            _created.Text = TaskLocalization.Localize("Tasks.Peek.CreatedAt", "创建时间") + " · " + current.CreatedAtUtc.ToLocalTime().ToString("g", culture);
            _updated.Text = TaskLocalization.Localize("Tasks.Peek.UpdatedAt", "更新时间") + " · " + current.UpdatedAtUtc.ToLocalTime().ToString("g", culture);
            _created.ToolTip = _created.Text;
            _updated.ToolTip = _updated.Text;
        }
        _created.Visibility = _updated.Visibility = Visible(model.Current is not null);
        _feedback.Text = model.IsMissing ? Text("Missing", "原任务已删除。可复制未保存的备注，或放弃草稿并关闭浮窗。")
            : !model.Allowed && model.IsDirty ? Text("Disabled", "浮窗编辑已关闭。未保存内容可复制或放弃。")
            : model.Error ?? (model.HasConflict ? Text("Conflict", "任务已在其他位置更改。可复制草稿，或重新读取已保存内容。")
            : model.IsSaving ? Text("Saving", "正在保存备注…")
            : model.IsDirty ? Text("Dirty", "备注尚未保存") : "");
        _feedback.Visibility = Visible(_feedback.Text.Length > 0);
        _edit.Content = Text("Edit", "编辑备注");
        _save.Content = Text("Save", "保存");
        _cancel.Content = model.HasConflict && !model.IsMissing ? Text("Reload", "重新读取") : Text("Cancel", "取消");
        _copy.Content = Text("Copy", "复制草稿");
        _edit.Visibility = Visible(model.Allowed && !model.IsEditing && model.Current is not null);
        _edit.IsEnabled = !model.Reserved && !completing;
        _save.Visibility = Visible(model.IsEditing && model.Allowed && !model.IsMissing);
        _save.IsEnabled = model.CanEdit && !completing && !model.HasConflict;
        _cancel.Visibility = Visible(model.IsEditing);
        _cancel.IsEnabled = !model.Reserved && !model.IsSaving && !completing;
        _copy.Visibility = Visible(model.IsDirty && (model.HasConflict || model.IsMissing || !model.Allowed));
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    private static string Text(string key, string fallback) => TaskFloatingNotesViewModel.Text(key, fallback);
    private static Button Button(string id)
    {
        var button = new Button { Padding = new Thickness(6, 3, 6, 3), MinHeight = 26, Margin = new Thickness(0, 2, 4, 0) };
        button.SetResourceReference(StyleProperty, "TaskFloatingButtonStyle");
        AutomationProperties.SetAutomationId(button, id);
        return button;
    }
}
