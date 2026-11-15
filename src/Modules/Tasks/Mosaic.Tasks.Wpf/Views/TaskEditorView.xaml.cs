using System.Windows.Controls;
using System.Windows;
using System.ComponentModel;
using Mosaic.Tasks.Wpf.Internal;

namespace Mosaic.Tasks.Wpf.Views;

public partial class TaskEditorView : UserControl
{
    private bool? _compactActions;
    private TasksViewModel? _model;

    public TaskEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        UpdateResponsiveActions(ActualWidth, ActualHeight);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveActions(e.NewSize.Width, e.NewSize.Height);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged previous) previous.PropertyChanged -= OnModelPropertyChanged;
        _model = e.NewValue as TasksViewModel;
        if (_model is not null) _model.PropertyChanged += OnModelPropertyChanged;
        ApplyModelSettings();
        _compactActions = null;
        UpdateResponsiveActions(ActualWidth, ActualHeight);
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TasksViewModel.CompactActionsWidth) or nameof(TasksViewModel.CompactActionsHeight) or
            nameof(TasksViewModel.NotesMinHeight) or nameof(TasksViewModel.TaskButtonMinWidth) or nameof(TasksViewModel.TaskButtonMinHeight))
        {
            ApplyModelSettings();
            _compactActions = null;
            UpdateResponsiveActions(ActualWidth, ActualHeight);
        }
    }

    private void ApplyModelSettings()
    {
        NotesTextBox.MinHeight = _model?.NotesMinHeight ?? 128;
        var minimumWidth = _model?.TaskButtonMinWidth ?? 96;
        var minimumHeight = _model?.TaskButtonMinHeight ?? 40;
        foreach (var button in new[] { ToggleButton, ReloadButton, DeleteButton })
        {
            button.MinWidth = minimumWidth;
            button.MinHeight = minimumHeight;
        }
    }

    private void UpdateResponsiveActions(double width, double height)
    {
        if (width <= 0 || height <= 0) return;

        var useCompactActions = width < (_model?.CompactActionsWidth ?? 480) || height < (_model?.CompactActionsHeight ?? 680);
        if (_compactActions == useCompactActions) return;
        _compactActions = useCompactActions;

        if (useCompactActions)
        {
            EditorActionGrid.ColumnDefinitions[3].Width = new GridLength(0);
            EditorActionGrid.ColumnDefinitions[4].Width = new GridLength(0);
            EditorActionSecondRow.Height = GridLength.Auto;
            Grid.SetRow(ToggleButton, 0);
            Grid.SetColumn(ToggleButton, 0);
            Grid.SetColumnSpan(ToggleButton, 1);
            Grid.SetRow(ReloadButton, 0);
            Grid.SetColumn(ReloadButton, 2);
            Grid.SetColumnSpan(ReloadButton, 1);
            Grid.SetRow(DeleteButton, 1);
            Grid.SetColumn(DeleteButton, 0);
            Grid.SetColumnSpan(DeleteButton, 5);
            DeleteButton.Margin = new Thickness(0, 7, 0, 0);
            FormScrollViewer.Padding = new Thickness(18, 14, 10, 10);
            EditorActionBar.Padding = new Thickness(14, 10, 14, 10);
            return;
        }

        EditorActionGrid.ColumnDefinitions[3].Width = new GridLength(7);
        EditorActionGrid.ColumnDefinitions[4].Width = new GridLength(1, GridUnitType.Star);
        EditorActionSecondRow.Height = new GridLength(0);
        Grid.SetRow(ToggleButton, 0);
        Grid.SetColumn(ToggleButton, 0);
        Grid.SetColumnSpan(ToggleButton, 1);
        Grid.SetRow(ReloadButton, 0);
        Grid.SetColumn(ReloadButton, 2);
        Grid.SetColumnSpan(ReloadButton, 1);
        Grid.SetRow(DeleteButton, 0);
        Grid.SetColumn(DeleteButton, 4);
        Grid.SetColumnSpan(DeleteButton, 1);
        DeleteButton.Margin = new Thickness(0);
        FormScrollViewer.Padding = new Thickness(22, 20, 14, 12);
        EditorActionBar.Padding = new Thickness(14, 12, 14, 12);
    }
}
