using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using EeroDesktop.Api;

namespace EeroDesktop;

/// <summary>
/// Green / yellow / red status lights. Always shown next to the status text, so color is never the only cue.
/// Fixed status colors (not themed): they read on both light and dark backgrounds.
/// </summary>
public static class StatusLights
{
    public enum Level { Good, Warning, Bad }

    public static readonly SolidColorBrush Good = Freeze("#0ca30c");
    public static readonly SolidColorBrush Warning = Freeze("#fab219");
    public static readonly SolidColorBrush Bad = Freeze("#d03b3b");

    private static SolidColorBrush Freeze(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

    private static readonly HashSet<string> GoodWords = ["connected", "green", "online", "up", "ok", "active", "enabled", "yes", "up to date", "healthy"];
    private static readonly HashSet<string> BadWords = ["disconnected", "red", "offline", "down", "failed", "error", "unreachable", "no internet", "still down", "no"];

    /// <summary>Connected/green/online → Good; disconnected/red/offline → Bad; anything else (yellow, degraded, unknown) → Warning.</summary>
    public static Level LevelOf(string? status)
    {
        var s = (status ?? "").Trim().Replace('_', ' ').ToLowerInvariant();
        if (GoodWords.Contains(s)) return Level.Good;
        if (BadWords.Contains(s)) return Level.Bad;
        return Level.Warning;
    }

    public static Brush BrushOf(Level level) => level switch { Level.Good => Good, Level.Bad => Bad, _ => Warning };

    public static Brush BrushOf(string? status) => BrushOf(LevelOf(status));

    /// <summary>A 10-px light for code-built layouts.</summary>
    public static Ellipse Light(string? status, double size = 10) => new()
    {
        Width = size, Height = size, Fill = BrushOf(status), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
    };

    /// <summary>A status column for DataGrids: light + text, sortable by the text.</summary>
    public static DataGridTemplateColumn Column(string header, string path, DataGridLength? width = null)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var dot = new FrameworkElementFactory(typeof(Ellipse));
        dot.SetValue(FrameworkElement.WidthProperty, 10.0);
        dot.SetValue(FrameworkElement.HeightProperty, 10.0);
        dot.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        dot.SetBinding(Shape.FillProperty, new Binding(path) { Converter = new StatusBrushConverter() });
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        panel.AppendChild(dot);
        panel.AppendChild(text);
        return new DataGridTemplateColumn
        {
            Header = header, SortMemberPath = path, Width = width ?? DataGridLength.Auto,
            CellTemplate = new DataTemplate { VisualTree = panel },
        };
    }
}

/// <summary>Uses StatusFactTemplate (with a light) for facts that are statuses, FactTemplate for the rest.</summary>
public sealed class FactTemplateSelector : DataTemplateSelector
{
    private static readonly HashSet<string> StatusKeys = new(StringComparer.OrdinalIgnoreCase) { "Internet status", "ISP reachable", "eero network", "Status" };

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => (DataTemplate?)Application.Current.TryFindResource(item is KeyValuePair<string, string> kv && StatusKeys.Contains(kv.Key) ? "StatusFactTemplate" : "FactTemplate");
}

/// <summary>Status text → green/yellow/red brush (for XAML bindings).</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => StatusLights.BrushOf(value?.ToString());
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Label text → Title Case (see <see cref="J.Label"/>).</summary>
public sealed class LabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => J.Label(value?.ToString());
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
