using System.Windows;
using System.Windows.Controls;
namespace Mosaic.Tasks.Wpf.Internal;
internal partial class TaskPeekView : UserControl
{
    internal event Action<TaskPeekRow>? OpenRequested;
    internal TaskPeekView() { InitializeComponent(); }
    private void OpenTask(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TaskPeekRow row }) OpenRequested?.Invoke(row);
    }
}
