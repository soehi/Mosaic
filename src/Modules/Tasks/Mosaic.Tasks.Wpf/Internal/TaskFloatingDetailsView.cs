using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mosaic.Presentation.Contracts;
using Mosaic.Presentation.Wpf;
using Mosaic.Tasks.Contracts;

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
    private bool _editing;
    internal TaskAttachmentPanel? Attachments { get; private set; }

    internal TaskFloatingDetailsView(TaskFloatingNotesViewModel model, Action save, Action cancel, ITaskAttachmentTransferRegistry? attachmentTransfers = null)
    {
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
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
        SetRow(footer, 2);
        Children.Add(footer);
        if (attachmentTransfers is not null)
        {
            Attachments = new TaskAttachmentPanel(attachmentTransfers) { Margin = new Thickness(4, 0, 4, 0) };
            var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 132, Content = Attachments };
            SetRow(scroller, 1);
            Children.Add(scroller);
        }

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
        _editing = model.IsEditing;
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
        if (Attachments is not null)
        {
            Attachments.Update(model.Attachments);
            Attachments.IsEnabled = !model.IsArchived && !model.IsMissing;
        }
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
        _edit.Visibility = Visible(model.Allowed && !model.IsArchived && !model.IsEditing && model.Current is not null);
        _edit.IsEnabled = !model.Reserved && !completing;
        _save.Visibility = Visible(model.IsEditing && model.Allowed && !model.IsMissing);
        _save.IsEnabled = model.CanEdit && !completing && !model.HasConflict;
        _cancel.Visibility = Visible(model.IsEditing);
        _cancel.IsEnabled = !model.Reserved && !model.IsSaving && !completing;
        _copy.Visibility = Visible(model.IsDirty && (model.HasConflict || model.IsMissing || model.IsArchived || !model.Allowed));
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    private static string Text(string key, string fallback) => TaskFloatingNotesViewModel.Text(key, fallback);

    // Drag support for the floating window: the window layer asks whether a press in this view's
    // space may move the window. Buttons, scrollbars and thumbs stay interactive; read-only notes
    // are draggable only off the text glyphs so selection and copy keep working; any other blank
    // area (footer, timestamps, action bar gaps) moves immediately.
    internal FloatingContentDragKind QueryContentDrag(Point point)
    {
        var hit = InputHitTest(point) as DependencyObject;
        if (hit is null) return FloatingContentDragKind.Immediate;
        for (DependencyObject? current = hit; current is not null && current != this; current = TreeParent(current))
        {
            if (current is ButtonBase or System.Windows.Controls.Primitives.Thumb or ScrollBar) return FloatingContentDragKind.None;
            if (current is TextBox box)
                return box.IsReadOnly && !_editing ? DragKindForNotes(box, point) : FloatingContentDragKind.None;
        }
        return FloatingContentDragKind.Immediate;
    }

    private FloatingContentDragKind DragKindForNotes(TextBox box, Point point)
    {
        if (box.Text.Length == 0) return FloatingContentDragKind.Immediate;
        // Convert the view-space point into the box's space (strip padding) so the native caret
        // queries account for wrapping, DPI and horizontal layout.
        var local = box.TransformToAncestor(this).Inverse.Transform(point);
        // Snap to the nearest character so presses just off a glyph edge still count as text; the
        // glyph rect check below tells actual text apart from blank space in the stretched box.
        var offset = box.GetCharacterIndexFromPoint(local, snapToText: true);
        if (offset < 0) return FloatingContentDragKind.Immediate;
        var text = box.Text;
        if (offset >= text.Length) return FloatingContentDragKind.Immediate;
        if (text[offset] == '\n') return FloatingContentDragKind.Immediate;
        var lead = box.GetRectFromCharacterIndex(offset, false);
        var trail = box.GetRectFromCharacterIndex(offset, true);
        var slack = GlyphSlack(box);
        var left = Math.Min(lead.X, trail.X);
        var right = Math.Max(lead.X, trail.X);
        // A wrapped last character reports its trailing caret on the next line; fall back to an
        // approximate advance so the press still counts as text.
        if (Math.Abs(trail.Y - lead.Y) > 0.5) right = left + box.FontSize;
        var onGlyph = local.X >= left - slack && local.X <= right + slack
                   && local.Y >= lead.Y - slack && local.Y <= lead.Y + lead.Height + slack;
        return onGlyph ? FloatingContentDragKind.Deferred : FloatingContentDragKind.Immediate;
    }

    private static double GlyphSlack(TextBox box) => Math.Max(2, box.FontSize * 0.25);
    private static DependencyObject? TreeParent(DependencyObject node)
    {
        try { return node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node); }
        catch (InvalidOperationException) { return LogicalTreeHelper.GetParent(node); }
    }

    private static Button Button(string id)
    {
        var button = new Button { Padding = new Thickness(6, 3, 6, 3), MinHeight = 26, Margin = new Thickness(0, 2, 4, 0) };
        button.SetResourceReference(StyleProperty, "TaskFloatingButtonStyle");
        AutomationProperties.SetAutomationId(button, id);
        return button;
    }
}
