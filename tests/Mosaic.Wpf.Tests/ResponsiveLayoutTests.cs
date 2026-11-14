using System.Windows;
using System.Windows.Controls;
using Mosaic.Tasks.Wpf.Views;

namespace Mosaic.Wpf.Tests;

public sealed class ResponsiveLayoutTests
{
    [Fact]
    public Task Task_page_reflows_from_side_by_side_to_a_bounded_single_column() => Sta.Run(() =>
    {
        var view = new TasksView();
        var list = Assert.IsType<Grid>(view.FindName("TaskListPane"));
        var editor = Assert.IsType<TaskEditorView>(view.FindName("TaskEditorPane"));
        var headerText = Assert.IsType<StackPanel>(view.FindName("HeaderText"));
        var newTaskButton = Assert.IsType<Button>(view.FindName("NewTaskButton"));

        Arrange(view, 1100, 700);
        var wideEditorWidth = editor.ActualWidth;

        Assert.Equal(0, Grid.GetColumn(list));
        Assert.Equal(0, Grid.GetRow(list));
        Assert.Equal(2, Grid.GetColumn(editor));
        Assert.Equal(0, Grid.GetRow(editor));
        Assert.True(wideEditorWidth > 0);

        Arrange(view, 600, 700);
        var compactListPosition = list.TranslatePoint(new Point(), view);
        var compactEditorPosition = editor.TranslatePoint(new Point(), view);

        Assert.Equal(0, Grid.GetColumn(list));
        Assert.Equal(0, Grid.GetRow(list));
        Assert.Equal(0, Grid.GetColumn(editor));
        Assert.Equal(2, Grid.GetRow(editor));
        Assert.Equal(compactListPosition.X, compactEditorPosition.X, precision: 3);
        Assert.Equal(list.ActualWidth, editor.ActualWidth, precision: 3);
        Assert.True(compactEditorPosition.Y >= compactListPosition.Y + list.ActualHeight);
        Assert.True(editor.TranslatePoint(new Point(editor.ActualWidth, 0), view).X <= view.ActualWidth);
        Assert.True(newTaskButton.TranslatePoint(new Point(), view).Y >= headerText.TranslatePoint(new Point(), view).Y + headerText.ActualHeight);

        Arrange(view, 1400, 700);
        Assert.Equal(2, Grid.GetColumn(editor));
        Assert.Equal(0, Grid.GetRow(editor));
        Assert.True(editor.ActualWidth > wideEditorWidth);

        return Task.CompletedTask;
    });

    private static void Arrange(FrameworkElement element, double width, double height)
    {
        var size = new Size(width, height);
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
    }
}
