using Mosaic.Contracts;
using Mosaic.Presentation.Contracts;
using Mosaic.Tasks.Contracts;

namespace Mosaic.Tasks.Wpf.Internal;

internal sealed class TaskFloatingNotesViewModel(TasksUiDependencies dependencies, CancellationToken lifetime)
{
    private TaskItemDto? _baseline;
    internal TaskItemDto? Current { get; private set; }
    internal string Draft { get; private set; } = "";
    internal bool Allowed { get; set; }
    internal bool Reserved { get; set; }
    internal bool IsSaving { get; private set; }
    internal bool IsEditing => _baseline is not null;
    internal bool IsDirty => _baseline is not null && Draft != _baseline.Notes;
    internal bool IsMissing { get; private set; }
    internal bool HasConflict { get; private set; }
    internal bool IsArchived => Current?.IsArchived == true;
    internal IReadOnlyList<TaskAttachmentDto> Attachments { get; private set; } = [];
    internal bool CanEdit => !IsArchived && IsEditing && Allowed && !IsMissing && !Reserved && !IsSaving;
    internal string? Error { get; private set; }
    internal event Action? Changed;

    internal void Observe(TaskItemDto item, long? completionRevision = null)
    {
        if (IsMissing || (Current is not null && Current.Revision > item.Revision)) return;
        Current = item;
        // An acknowledged local completion only changed status. External revisions still require review.
        if (_baseline is { } baseline && baseline.Revision == completionRevision)
        {
            _baseline = item;
            HasConflict = false;
        }
        if (_baseline is { } edited && edited.Revision != item.Revision && !IsSaving)
            HasConflict = true;
    }

    internal void MarkMissing()
    {
        IsMissing = true;
        Current = null;
        if (!IsDirty) Discard();
    }

    internal void Begin()
    {
        if (!Allowed || IsArchived || Reserved || IsEditing || Current is not { } current) return;
        _baseline = current;
        Draft = current.Notes;
        Changed?.Invoke();
    }

    internal void Change(string text)
    {
        if (!CanEdit || Draft == text) return;
        Draft = text;
        Error = null;
        Changed?.Invoke();
    }

    internal void Discard()
    {
        _baseline = null;
        Draft = "";
        HasConflict = false;
        Error = null;
        Changed?.Invoke();
    }

    // 从附件 API 拉取当前任务的附件列表；失败保留已有列表，不阻断备注编辑。
    internal async Task RefreshAttachmentsAsync()
    {
        if (Current is not { } current || dependencies.Attachments is null || IsMissing) return;
        try
        {
            var result = await dependencies.Attachments.ListAsync(current.Id, lifetime);
            if (!result.IsSuccess || IsMissing) return;
            Attachments = result.Value;
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { dependencies.Diagnostics.Report(Guid.NewGuid().ToString("N"), ex); }
    }

    // 拖出返回 Move 后删除原件与记录，成功后刷新当前任务与附件列表。
    internal async Task RemoveAttachmentAsync(TaskAttachmentDragTransfer transfer)
    {
        if (Current is not { } current || dependencies.Attachments is null || IsArchived || IsMissing) return;
        var result = await dependencies.Attachments.RemoveAsync(new(current.Id, transfer.AttachmentId, current.Revision), lifetime);
        if (!result.IsSuccess) return;
        Current = result.Value;
        await RefreshAttachmentsAsync();
        Changed?.Invoke();
    }

    internal async Task<OperationResult> SaveAsync(bool closing = false)
    {
        if (_baseline is not { } baseline || !Allowed || IsArchived || IsMissing || IsSaving || (Reserved && !closing))
            return Canceled();
        if (!IsDirty) { Discard(); return OperationResult.Success(); }
        if (HasConflict) return Failed(new(ErrorCodes.Conflict, Text("Conflict", "任务已在其他位置更改。可复制草稿，或重新读取已保存内容。")));

        IsSaving = true;
        Error = null;
        Changed?.Invoke();
        TaskConstraintsDto? constraints = null;
        try
        {
            var limits = await dependencies.Api.GetConstraintsAsync(lifetime);
            if (!limits.IsSuccess) return Failed(limits.Error!);
            constraints = limits.Value;
            if (!Allowed || IsMissing) return Canceled();
            if (Draft.EnumerateRunes().Count() > constraints.NotesMaxCharacters)
                return Failed(new(ErrorCodes.ValidationFailed, "", "Notes", MessageKey: "Tasks.Error.NotesTooLong"), constraints);
            var saved = await dependencies.Api.UpdateAsync(new(baseline.Id, baseline.Title, Draft, baseline.Revision), lifetime);
            if (!saved.IsSuccess) return Failed(saved.Error!, constraints);
            Observe(saved.Value);
            Discard();
            return OperationResult.Success();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return Canceled(); }
        catch (Exception ex)
        {
            dependencies.Diagnostics.Report(Guid.NewGuid().ToString("N"), ex);
            return Failed(new(ErrorCodes.InternalError, Text("SaveFailed", "无法保存备注，请重试。")));
        }
        finally { IsSaving = false; Changed?.Invoke(); }
    }

    internal async Task<OperationResult> ConfirmCloseAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsDirty) return OperationResult.Success();
        if (!Allowed || IsMissing || IsArchived)
        {
            var discard = await dependencies.Dialogs.ConfirmAsync(Text("DiscardTitle", "放弃未保存的备注？"),
                IsMissing ? Text("DiscardMissing", "原任务已删除。要放弃草稿并关闭浮窗吗？选择否可返回并复制草稿。")
                    : IsArchived ? TaskLocalization.Localize("Tasks.Archived.DiscardDraft", "任务已归档。要放弃草稿并关闭浮窗吗？选择否可返回并复制草稿。")
                    : Text("DiscardDisabled", "浮窗编辑已关闭。要放弃草稿并关闭浮窗吗？选择否可返回并复制草稿。"));
            ct.ThrowIfCancellationRequested();
            return discard ? OperationResult.Success() : Canceled();
        }
        var choice = await dependencies.Dialogs.AskUnsavedChangesAsync();
        ct.ThrowIfCancellationRequested();
        // Discard takes effect on disposal. If layout persistence or another window blocks exit, retain the draft.
        return choice switch
        {
            UnsavedChoice.Discard => OperationResult.Success(),
            UnsavedChoice.Save => await SaveAsync(closing: true),
            _ => Canceled()
        };
    }

    private OperationResult Failed(OperationError error, TaskConstraintsDto? limits = null)
    {
        if (error.Code == ErrorCodes.Conflict) HasConflict = true;
        if (error.Code == ErrorCodes.NotFound) MarkMissing();
        Error = HasConflict && !IsMissing ? Text("Conflict", "任务已在其他位置更改。可复制草稿，或重新读取已保存内容。")
            : TaskErrorLocalizer.Localize(error, limits);
        Changed?.Invoke();
        return OperationResult.Failure(error with { Message = Error });
    }

    internal static OperationResult Canceled() => OperationResult.Failure(new(ErrorCodes.Canceled, Text("Canceled", "已取消关闭或保存浮窗备注。")));
    internal static string Text(string key, string fallback) => TaskLocalization.Localize("Tasks.Floating.Notes." + key, fallback);
}
