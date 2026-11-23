using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Mosaic.Tasks.Wpf.Internal;

// 附件文件的类型分类：编辑器、速览、浮窗共用同一套徽标，避免各处图标风格不一致。
internal enum TaskAttachmentKind
{
    Document,
    Pdf,
    Spreadsheet,
    Presentation,
    Image,
    Archive,
    Audio,
    Video,
    Code,
    Text,
    Other
}

internal static class TaskAttachmentVisuals
{
    // 浅色/深色主题都可读的中等饱和度配色；徽标底色取同色低透明度。
    private static readonly IReadOnlyDictionary<TaskAttachmentKind, Color> Palette = new Dictionary<TaskAttachmentKind, Color>
    {
        [TaskAttachmentKind.Document] = Color.FromRgb(0x3B, 0x82, 0xC4),
        [TaskAttachmentKind.Pdf] = Color.FromRgb(0xD9, 0x53, 0x4F),
        [TaskAttachmentKind.Spreadsheet] = Color.FromRgb(0x2E, 0x9E, 0x6B),
        [TaskAttachmentKind.Presentation] = Color.FromRgb(0xE0, 0x7B, 0x39),
        [TaskAttachmentKind.Image] = Color.FromRgb(0x8B, 0x6C, 0xEF),
        [TaskAttachmentKind.Archive] = Color.FromRgb(0xC0, 0x8A, 0x28),
        [TaskAttachmentKind.Audio] = Color.FromRgb(0xC4, 0x51, 0xA1),
        [TaskAttachmentKind.Video] = Color.FromRgb(0xD4, 0x46, 0x7F),
        [TaskAttachmentKind.Code] = Color.FromRgb(0x3E, 0x9B, 0x9C),
        [TaskAttachmentKind.Text] = Color.FromRgb(0x6F, 0x80, 0x93),
        [TaskAttachmentKind.Other] = Color.FromRgb(0x7A, 0x84, 0x94),
    };

    private static readonly IReadOnlyDictionary<TaskAttachmentKind, SolidColorBrush> Foregrounds =
        Palette.ToDictionary(pair => pair.Key, pair => Frozen(pair.Value, 0xFF));

    private static readonly IReadOnlyDictionary<TaskAttachmentKind, SolidColorBrush> Backgrounds =
        Palette.ToDictionary(pair => pair.Key, pair => Frozen(pair.Value, 0x26));

    private static readonly IReadOnlyDictionary<string, TaskAttachmentKind> Extensions = BuildExtensions();

    internal static TaskAttachmentKind Classify(string? fileName) =>
        Extensions.TryGetValue(Extension(fileName), out var kind) ? kind : TaskAttachmentKind.Other;

    // 徽标只显示扩展名本身（最多 4 个字符），没有扩展名时退化为 FILE。
    internal static string Label(string? fileName)
    {
        var extension = Extension(fileName);
        if (extension.Length == 0) return "FILE";
        var label = extension.ToUpperInvariant();
        return label.Length <= 4 ? label : label[..4];
    }

    internal static Brush Foreground(TaskAttachmentKind kind) => Foregrounds[kind];

    internal static Brush Background(TaskAttachmentKind kind) => Backgrounds[kind];

    internal static string SizeLabel(long bytes) => bytes switch
    {
        >= 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / 1024d / 1024d),
        >= 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", bytes / 1024d),
        _ => string.Format(CultureInfo.CurrentCulture, "{0} B", bytes)
    };

    internal static FrameworkElement CreateIcon(string? fileName)
    {
        var kind = Classify(fileName);
        var label = Label(fileName);
        var icon = new Border
        {
            MinWidth = 26,
            Height = 18,
            Padding = new Thickness(3, 0, 3, 0),
            CornerRadius = new CornerRadius(4),
            Background = Background(kind),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Focusable = false,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                Foreground = Foreground(kind),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        AutomationProperties.SetName(icon, label);
        return icon;
    }

    private static Dictionary<string, TaskAttachmentKind> BuildExtensions()
    {
        var map = new Dictionary<string, TaskAttachmentKind>(StringComparer.OrdinalIgnoreCase);
        void Add(TaskAttachmentKind kind, params string[] extensions)
        {
            foreach (var extension in extensions) map[extension] = kind;
        }

        Add(TaskAttachmentKind.Pdf, "pdf");
        Add(TaskAttachmentKind.Document, "doc", "docx", "docm", "dot", "dotx", "rtf", "odt", "wps", "pages");
        Add(TaskAttachmentKind.Spreadsheet, "xls", "xlsx", "xlsm", "xlsb", "csv", "tsv", "ods", "numbers");
        Add(TaskAttachmentKind.Presentation, "ppt", "pptx", "pptm", "pps", "ppsx", "odp", "key");
        Add(TaskAttachmentKind.Image, "png", "jpg", "jpeg", "jfif", "gif", "bmp", "webp", "svg", "tif", "tiff", "ico", "heic", "avif");
        Add(TaskAttachmentKind.Archive, "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "iso");
        Add(TaskAttachmentKind.Audio, "mp3", "wav", "flac", "ogg", "m4a", "aac", "wma", "opus");
        Add(TaskAttachmentKind.Video, "mp4", "mkv", "mov", "avi", "webm", "wmv", "m4v", "mpg", "mpeg");
        Add(TaskAttachmentKind.Code, "cs", "csx", "js", "mjs", "cjs", "ts", "tsx", "jsx", "py", "java", "kt", "kts",
            "go", "rs", "rb", "php", "c", "h", "cc", "cpp", "hpp", "swift", "json", "xml", "yaml", "yml", "toml",
            "ini", "config", "html", "htm", "css", "scss", "less", "sql", "sh", "bash", "ps1", "bat", "cmd", "vue",
            "dart", "lua", "pl", "r");
        Add(TaskAttachmentKind.Text, "txt", "md", "markdown", "log", "rst", "tex", "nfo");
        return map;
    }

    private static string Extension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "";
        var extension = Path.GetExtension(fileName);
        return extension.Length > 1 ? extension[1..].ToLowerInvariant() : "";
    }

    private static SolidColorBrush Frozen(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}

// XAML 侧（编辑器附件行）通过同一个转换器取标签、前景色与底色。
internal sealed class AttachmentTypeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) => (parameter as string) switch
    {
        "label" => TaskAttachmentVisuals.Label(value as string),
        "background" => TaskAttachmentVisuals.Background(TaskAttachmentVisuals.Classify(value as string)),
        "foreground" => TaskAttachmentVisuals.Foreground(TaskAttachmentVisuals.Classify(value as string)),
        _ => TaskAttachmentVisuals.Label(value as string)
    };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}