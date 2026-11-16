using System.Windows;
using Mosaic.Presentation.Contracts;

namespace Mosaic.Shell.Wpf;

public sealed class UserDialogService : IUserDialogService
{
    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsVisible && window.IsActive)
            ?? (Application.Current?.MainWindow is { IsVisible: true } main ? main : null);
        return owner is null ? MessageBox.Show(message, title, buttons, image) : MessageBox.Show(owner, message, title, buttons, image);
    }
    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Show(
        message,
        title,
        MessageBoxButton.YesNo,
        MessageBoxImage.Question) == MessageBoxResult.Yes);
    public Task<bool> ConfirmDeleteAsync(string title) => Task.FromResult(Show(
        string.Format(ShellLocalization.Localize("Shell.Dialog.Delete.Message", "确定永久删除「{0}」？"), title),
        ShellLocalization.Localize("Shell.Dialog.Delete.Title", "删除任务"),
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning) == MessageBoxResult.Yes);
    public Task<UnsavedChoice> AskUnsavedChangesAsync() => Task.FromResult(Show(
        ShellLocalization.Localize("Shell.Dialog.Unsaved.Message", "当前编辑尚未保存。\n\n是：保存并离开\n否：放弃更改\n取消：继续编辑"),
        ShellLocalization.Localize("Shell.Dialog.Unsaved.Title", "未保存的更改"),
        MessageBoxButton.YesNoCancel,
        MessageBoxImage.Question) switch
    {
        MessageBoxResult.Yes => UnsavedChoice.Save,
        MessageBoxResult.No => UnsavedChoice.Discard,
        _ => UnsavedChoice.Cancel
    });
    public Task ShowErrorAsync(string message) { Show(message, "Mosaic", MessageBoxButton.OK, MessageBoxImage.Warning); return Task.CompletedTask; }
}
