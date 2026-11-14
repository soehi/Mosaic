using System.Windows;
using Mosaic.Presentation.Contracts;

namespace Mosaic.Shell.Wpf;

public sealed class UserDialogService : IUserDialogService
{
    public Task<bool> ConfirmDeleteAsync(string title) => Task.FromResult(MessageBox.Show(Application.Current.MainWindow, $"确定永久删除「{title}」？", "删除任务", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
    public Task<UnsavedChoice> AskUnsavedChangesAsync() => Task.FromResult(MessageBox.Show(Application.Current.MainWindow, "当前编辑尚未保存。\n\n是：保存并离开\n否：放弃更改\n取消：继续编辑", "未保存的更改", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
    {
        MessageBoxResult.Yes => UnsavedChoice.Save,
        MessageBoxResult.No => UnsavedChoice.Discard,
        _ => UnsavedChoice.Cancel
    });
    public Task ShowErrorAsync(string message) { MessageBox.Show(Application.Current.MainWindow, message, "Mosaic", MessageBoxButton.OK, MessageBoxImage.Warning); return Task.CompletedTask; }
}
