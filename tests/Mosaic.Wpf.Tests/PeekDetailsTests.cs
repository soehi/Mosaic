using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mosaic.Presentation.Wpf;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Wpf.Internal;

namespace Mosaic.Wpf.Tests;

public sealed class PeekDetailsTests
{
    private static readonly TaskItemDto Sample = new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"), "整理本周任务",
        "确认发布范围，核对安装包，补充回归记录，并在完成后更新实施记录。",
        false, new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 18, 10, 30, 0, TimeSpan.Zero), 3);

    [Fact]
    public Task Details_render_as_card_with_borderless_readonly_notes() => Sta.Run(() =>
    {
        var view = CreateView();
        view.Render(Sample, loading: false, failed: false, missing: false);

        Assert.Equal(10, view.Card.CornerRadius.TopLeft);
        Assert.Equal(new Thickness(1), view.Card.BorderThickness);
        Assert.Equal(17, view.TitleText.FontSize);
        Assert.Equal(FontWeights.SemiBold, view.TitleText.FontWeight);
        Assert.Equal(Sample.Title, view.TitleText.Text);
        Assert.Contains("创建时间", view.TimeText.Text);
        Assert.Contains(Environment.NewLine, view.TimeText.Text);
        Assert.True(view.NotesBox.IsReadOnly);
        Assert.Equal(Sample.Notes, view.NotesBox.Text);
        Assert.Equal(new Thickness(0), view.NotesBox.BorderThickness);
        Assert.Equal(Brushes.Transparent.Color, ((SolidColorBrush)view.NotesBox.Background).Color);
        Assert.NotNull(view.NotesBox.FocusVisualStyle);
        Assert.Equal("PeekDetailsNotes", AutomationProperties.GetAutomationId(view.NotesBox));

        var scroll = Assert.Single(view.Children.OfType<ScrollViewer>());
        var body = Assert.IsType<StackPanel>(scroll.Content);
        Assert.Same(view.Card, body.Children[0]);
        Assert.Same(view.TimeText, body.Children[1]);
        Assert.DoesNotContain(body.Children.OfType<TextBox>(), _ => true);
        var cardBody = Assert.IsType<StackPanel>(view.Card.Child);
        Assert.DoesNotContain(cardBody.Children.OfType<TextBox>().Where(box => box != view.NotesBox), _ => true);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Empty_notes_show_placeholder_only_when_not_compact() => Sta.Run(() =>
    {
        var view = CreateView();
        var cardBody = Assert.IsType<StackPanel>(view.Card.Child);
        view.Render(Sample with { Notes = "" }, loading: false, failed: false, missing: false);
        var placeholder = cardBody.Children.OfType<TextBlock>().Single(text => text.Text == "暂无备注");
        Assert.Equal(Visibility.Visible, placeholder.Visibility);
        Assert.Equal(Visibility.Collapsed, view.NotesBox.Visibility);

        view.ConfigureDensity(8);
        view.Render(Sample with { Notes = "" }, loading: false, failed: false, missing: false);
        Assert.Equal(Visibility.Collapsed, cardBody.Children.OfType<TextBlock>().Single(text => text.Text == "暂无备注").Visibility);
        Assert.Equal(8, view.Card.Margin.Bottom);
        Assert.Equal(0, view.Card.Margin.Left);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Details_preview_renders_light_dark_and_high_contrast() => Sta.Run(async () =>
    {
        var output = Path.Combine(RepositoryRoot(), "artifacts", "ui-preview");
        Directory.CreateDirectory(output);
        foreach (var variant in new[] { MosaicThemeVariant.Light, MosaicThemeVariant.Dark, MosaicThemeVariant.HighContrast })
        {
            var view = CreateView(variant);
            view.Render(Sample, loading: false, failed: false, missing: false);
            var window = new Window
            {
                Content = new Border
                {
                    Padding = new Thickness(16),
                    Background = (Brush)view.Resources[MosaicTheme.SurfaceBrushKey],
                    Child = view
                },
                Width = 380,
                SizeToContent = SizeToContent.Height,
                ShowActivated = false,
                Background = (Brush)view.Resources[MosaicTheme.SurfaceBrushKey]
            };
            window.Show();
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using (var stream = File.Create(Path.Combine(output, $"peek-details-{variant.ToString().ToLowerInvariant()}.png")))
                encoder.Save(stream);
            window.Close();
        }
    });

    private static TaskPeekDetailsView CreateView(MosaicThemeVariant variant = MosaicThemeVariant.Light)
    {
        var view = new TaskPeekDetailsView();
        view.Resources.MergedDictionaries.Add(Assert.IsType<ResourceDictionary>(Application.LoadComponent(
            new Uri("/Mosaic.Presentation.Wpf;component/Themes/Theme.xaml", UriKind.Relative))));
        MosaicThemeManager.Apply(view.Resources, variant);
        return view;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mosaic.sln"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate Mosaic.sln.");
    }
}
