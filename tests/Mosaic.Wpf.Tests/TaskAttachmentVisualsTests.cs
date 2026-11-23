using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Mosaic.Tasks.Contracts;
using Mosaic.Tasks.Wpf.Internal;

namespace Mosaic.Wpf.Tests;

// 附件徽标与拖出数据的回归测试：类型识别、无表情符号、拖出只提供 Copy。
public sealed class TaskAttachmentVisualsTests
{
    [Theory]
    [InlineData("report.pdf", "Pdf", "PDF")]
    [InlineData("合同.DOCX", "Document", "DOCX")]
    [InlineData("预算.xlsx", "Spreadsheet", "XLSX")]
    [InlineData("方案.pptx", "Presentation", "PPTX")]
    [InlineData("截图.png", "Image", "PNG")]
    [InlineData("归档.tar.gz", "Archive", "GZ")]
    [InlineData("录音.mp3", "Audio", "MP3")]
    [InlineData("演示.mp4", "Video", "MP4")]
    [InlineData("Program.cs", "Code", "CS")]
    [InlineData("说明.md", "Text", "MD")]
    [InlineData("README", "Other", "FILE")]
    [InlineData("archive.backup", "Other", "BACK")]
    public void Classifies_by_extension_and_labels_without_emoji(string fileName, string kind, string label)
    {
        Assert.Equal(kind, TaskAttachmentVisuals.Classify(fileName).ToString());
        Assert.Equal(label, TaskAttachmentVisuals.Label(fileName));
        Assert.All(label, c => Assert.True(c < 128, $"{label} should stay ASCII"));
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    public void Formats_sizes_for_attachment_rows(long bytes, string expected) =>
        Assert.Equal(expected, TaskAttachmentVisuals.SizeLabel(bytes));

    [Fact]
    public Task Icon_uses_type_palette_and_never_the_paperclip_emoji() =>
        Sta.Run(() =>
        {
            var pdf = Assert.IsType<Border>(TaskAttachmentVisuals.CreateIcon("report.pdf"));
            var image = Assert.IsType<Border>(TaskAttachmentVisuals.CreateIcon("photo.png"));
            var pdfLabel = Assert.IsType<TextBlock>(pdf.Child);
            Assert.Equal("PDF", pdfLabel.Text);
            Assert.Equal(TaskAttachmentVisuals.Background(TaskAttachmentKind.Pdf), pdf.Background);
            Assert.NotEqual(pdf.Background, image.Background);
            Assert.Equal("PDF", AutomationProperties.GetName(pdf));
            Assert.False(pdf.IsHitTestVisible);
            Assert.DoesNotContain("📎", pdfLabel.Text, StringComparison.Ordinal);

            var converter = new AttachmentTypeConverter();
            Assert.Equal("PNG", converter.Convert("a.png", typeof(string), "label", CultureInfo.InvariantCulture));
            Assert.Equal(
                TaskAttachmentVisuals.Foreground(TaskAttachmentKind.Image),
                converter.Convert("a.png", typeof(Brush), "foreground", CultureInfo.InvariantCulture));
            Assert.Equal(
                TaskAttachmentVisuals.Background(TaskAttachmentKind.Image),
                converter.Convert("a.png", typeof(Brush), "background", CultureInfo.InvariantCulture));
            return Task.CompletedTask;
        });

    [Fact]
    public Task Panel_rows_show_type_icon_filename_and_size_without_count_or_paperclip() =>
        Sta.Run(() =>
        {
            var panel = new TaskAttachmentPanel(api: null);
            var attachment = new TaskAttachmentDto(Guid.NewGuid(), Guid.NewGuid(), "季度报告.pdf", 2048,
                new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero));

            panel.Update([attachment]);

            Assert.Equal(Visibility.Visible, panel.Visibility);
            var row = Assert.IsType<Border>(Assert.Single(panel.Children));
            var content = Assert.IsType<Grid>(row.Child);
            Assert.Equal(3, content.Children.Count);
            var icon = Assert.IsType<Border>(content.Children[0]);
            Assert.Equal("PDF", Assert.IsType<TextBlock>(icon.Child).Text);
            Assert.Equal("季度报告.pdf", Assert.IsType<TextBlock>(content.Children[1]).Text);
            Assert.Equal("2 KB", Assert.IsType<TextBlock>(content.Children[2]).Text);
            Assert.Equal(attachment, row.Tag);

            panel.Update([]);
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            Assert.Empty(panel.Children);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Drag_data_offers_copy_only_and_marks_in_app_drags() =>
        Sta.Run(() =>
        {
            var attachment = new TaskAttachmentDto(Guid.NewGuid(), Guid.NewGuid(), "报告.pdf", 10,
                new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero));
            var staged = new TaskAttachmentDrag.Staged(@"C:\Temp\MosaicAttachments\x", @"C:\Temp\MosaicAttachments\x\报告.pdf");

            var data = TaskAttachmentDrag.CreateData(attachment, staged);

            Assert.True(data.GetDataPresent(DataFormats.FileDrop));
            Assert.Equal(new[] { staged.FilePath }, Assert.IsType<string[]>(data.GetData(DataFormats.FileDrop)));
            Assert.Equal(attachment.Id.ToString("D"), data.GetData(TaskAttachmentDrag.InternalFormatName));
            Assert.Equal(DragDropEffects.Copy, TaskAttachmentDrag.Effects);
            return Task.CompletedTask;
        });
}