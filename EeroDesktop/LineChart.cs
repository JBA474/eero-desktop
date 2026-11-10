using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace EeroDesktop;

/// <summary>One line on a <see cref="LineChart"/>. Slot is the fixed categorical color (1-8) that belongs to this entity.</summary>
public sealed record ChartSeries(string Name, int Slot, IReadOnlyList<(DateTime X, double Y)> Points);

/// <summary>
/// Small time-series line chart drawn with WPF shapes: one y-axis from zero, recessive grid, 2px lines, markers when
/// points are sparse, legend for two or more series, direct end labels for up to four, and a crosshair tooltip on hover.
/// Colors are a validated categorical palette with separate light and dark steps; it redraws when the theme changes.
/// The page always shows the same data as a table too (some light-mode colors are below 3:1 contrast).
/// </summary>
public sealed class LineChart : Grid
{
    // Categorical slots, light and dark steps (validated for CVD and contrast against the Fluent card surfaces).
    private static readonly string[] LightSlots = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948"];
    private static readonly string[] DarkSlots = ["#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300", "#9085e9", "#e66767"];

    private const double PlotHeight = 220, Left = 52, Top = 10, Bottom = 24;

    private readonly WrapPanel _legend = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly Canvas _canvas = new() { Height = PlotHeight, ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Canvas _hover = new() { IsHitTestVisible = false };
    private IReadOnlyList<ChartSeries> _series = [];
    private string _emptyText = "No data yet.";

    /// <summary>Formats a y value for axis ticks, labels and the tooltip (e.g. "512 Mbps").</summary>
    public Func<double, string> FormatY { get; set; } = v => v.ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>Formats an x value for the tooltip.</summary>
    public Func<DateTime, string> FormatX { get; set; } = d => d.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture);

    /// <summary>Optional fixed x range (UTC); otherwise the data's own range.</summary>
    public (DateTime From, DateTime To)? XRange { get; set; }

    public LineChart()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(_legend);
        SetRow(_canvas, 1);
        Children.Add(_canvas);
        _canvas.SizeChanged += (_, _) => Draw();
        _canvas.MouseMove += (_, e) => ShowHover(e.GetPosition(_canvas));
        _canvas.MouseLeave += (_, _) => _hover.Children.Clear();
        Loaded += (_, _) => { App.ThemeChanged += Redraw; SystemEvents.UserPreferenceChanged += OnUserPreference; Draw(); };
        Unloaded += (_, _) => { App.ThemeChanged -= Redraw; SystemEvents.UserPreferenceChanged -= OnUserPreference; };
    }

    /// <summary>Exposes the chart to screen readers and UI Automation as an image named with its data summary.</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new ChartPeer(this);

    private sealed class ChartPeer(LineChart owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
    {
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore()
            => System.Windows.Automation.Peers.AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(LineChart);
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override List<System.Windows.Automation.Peers.AutomationPeer>? GetChildrenCore() => null; // the table beside it has the details
    }

    public void SetData(IReadOnlyList<ChartSeries> series, string emptyText)
    {
        _series = series;
        _emptyText = emptyText;
        var count = series.Sum(s => s.Points.Count);
        AutomationProperties.SetName(this, count == 0 ? emptyText
            : string.Join("; ", series.Where(s => s.Points.Count > 0).Select(s => $"{s.Name}: {s.Points.Count} points, latest {FormatY(s.Points[^1].Y)}")));
        AutomationProperties.SetItemStatus(this, count.ToString(CultureInfo.InvariantCulture));
        Draw();
    }

    private void OnUserPreference(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(Redraw);
    private void Redraw() => Dispatcher.BeginInvoke(Draw, System.Windows.Threading.DispatcherPriority.Background);

    // ------------------------------------------------------------------ colors

    private static bool IsDark()
    {
        // The Fluent theme's primary text is light in dark mode.
        if (Application.Current.TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush b)
            return (0.2126 * b.Color.R + 0.7152 * b.Color.G + 0.0722 * b.Color.B) / 255 > 0.5;
        return false;
    }

    private static SolidColorBrush Hex(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

    public static Brush SlotBrush(int slot, bool dark) => Hex((dark ? DarkSlots : LightSlots)[Math.Clamp(slot, 1, 8) - 1]);

    // ------------------------------------------------------------------ drawing

    private double _x0, _x1, _yMax, _plotW;
    private DateTime _from, _to;

    private double Px(DateTime x) => _x0 + (_to == _from ? 0.5 : (x - _from).TotalSeconds / (_to - _from).TotalSeconds) * (_x1 - _x0);
    private double Py(double y) => Top + (1 - y / _yMax) * (PlotHeight - Top - Bottom);

    private void Draw()
    {
        _canvas.Children.Clear();
        _legend.Children.Clear();
        _hover.Children.Clear();
        var dark = IsDark();
        var grid = Hex(dark ? "#2c2c2a" : "#e1e0d9");
        var axis = Hex(dark ? "#383835" : "#c3c2b7");
        var muted = Hex("#898781");
        var withData = _series.Where(s => s.Points.Count > 0).ToList();
        var width = _canvas.ActualWidth;
        if (width < 120) return;

        if (withData.Count == 0)
        {
            var empty = new TextBlock { Text = _emptyText, TextWrapping = TextWrapping.Wrap, Width = width - 40 };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Canvas.SetLeft(empty, 20); Canvas.SetTop(empty, PlotHeight / 2 - 10);
            _canvas.Children.Add(empty);
            return;
        }

        // Legend for two or more series (identity is never color alone).
        if (_series.Count >= 2)
            foreach (var s in _series)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
                item.Children.Add(new Rectangle { Width = 12, Height = 12, RadiusX = 3, RadiusY = 3, Fill = SlotBrush(s.Slot, dark), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                var t = new TextBlock { Text = s.Name, FontSize = 12 };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                item.Children.Add(t);
                _legend.Children.Add(item);
            }

        var directLabels = withData.Count <= 4;
        var all = withData.SelectMany(s => s.Points).ToList();
        (_from, _to) = XRange ?? (all.Min(p => p.X), all.Max(p => p.X));
        _yMax = NiceMax(all.Max(p => p.Y));
        // The y-axis gutter fits the widest tick label (e.g. "2500 Mbps").
        var axisWidth = Enumerable.Range(0, 5).Max(i =>
        {
            var probe = new TextBlock { Text = FormatY(_yMax * i / 4), FontSize = 11 };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return probe.DesiredSize.Width;
        });
        _x0 = Math.Max(Left, axisWidth + 14);
        _x1 = width - (directLabels ? 96 : 14);
        _plotW = _x1 - _x0;

        // Horizontal gridlines with y labels (hairline, recessive); the zero line is the baseline.
        for (var i = 0; i <= 4; i++)
        {
            var v = _yMax * i / 4;
            var y = Py(v);
            _canvas.Children.Add(new Line { X1 = _x0, X2 = _x1, Y1 = y, Y2 = y, Stroke = i == 0 ? axis : grid, StrokeThickness = 1, SnapsToDevicePixels = true });
            var label = new TextBlock { Text = FormatY(v), FontSize = 11, Foreground = muted, TextAlignment = TextAlignment.Right, Width = _x0 - 8 };
            Canvas.SetLeft(label, 0); Canvas.SetTop(label, y - 8);
            _canvas.Children.Add(label);
        }

        // X labels: start, middle, end of the range.
        var span = _to - _from;
        var fmt = span.TotalDays > 300 ? "MMM yyyy" : span.TotalDays >= 2 ? "MMM d" : "h:mm tt";
        foreach (var f in _from == _to ? new[] { 0.5 } : new[] { 0.0, 0.5, 1.0 })
        {
            var x = _from + TimeSpan.FromTicks((long)(span.Ticks * f));
            var t = new TextBlock { Text = x.ToLocalTime().ToString(fmt, CultureInfo.CurrentCulture), FontSize = 11, Foreground = muted };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var left = Math.Clamp(Px(x) - t.DesiredSize.Width / 2, _x0 - 10, _x1 - t.DesiredSize.Width + 10);
            Canvas.SetLeft(t, left); Canvas.SetTop(t, PlotHeight - Bottom + 4);
            _canvas.Children.Add(t);
        }

        var ring = (Brush)(Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") ?? Brushes.White);
        var ends = new List<(double Y, ChartSeries S)>();
        foreach (var s in withData)
        {
            var brush = SlotBrush(s.Slot, dark);
            var pts = s.Points.OrderBy(p => p.X).ToList();
            if (pts.Count > 1)
                _canvas.Children.Add(new Polyline
                {
                    Points = new PointCollection(pts.Select(p => new Point(Px(p.X), Py(p.Y)))),
                    Stroke = brush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                });
            if (pts.Count <= 60) // markers only while sparse enough to read
                foreach (var p in pts) _canvas.Children.Add(Marker(p, brush, ring, 8));
            ends.Add((Py(pts[^1].Y), s));
        }

        // Direct labels at the right end, nudged apart so they never overlap. Text wears ink, not the series color.
        if (directLabels)
        {
            ends.Sort((a, b) => a.Y.CompareTo(b.Y));
            var last = double.NegativeInfinity;
            foreach (var (y0, s) in ends)
            {
                var y = Math.Max(y0, last + 15);
                last = y;
                var t = new TextBlock { Text = s.Name, FontSize = 11, MaxWidth = 90, TextTrimming = TextTrimming.CharacterEllipsis };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                Canvas.SetLeft(t, _x1 + 8); Canvas.SetTop(t, Math.Min(y, PlotHeight - Bottom - 12) - 8);
                _canvas.Children.Add(t);
            }
        }
        _canvas.Children.Add(_hover);
    }

    private Ellipse Marker((DateTime X, double Y) p, Brush fill, Brush ring, double size)
    {
        var e = new Ellipse { Width = size + 4, Height = size + 4, Fill = fill, Stroke = ring, StrokeThickness = 2 };
        Canvas.SetLeft(e, Px(p.X) - (size + 4) / 2); Canvas.SetTop(e, Py(p.Y) - (size + 4) / 2);
        return e;
    }

    private static double NiceMax(double max)
    {
        if (max <= 0) return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var m in new[] { 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
            if (m * mag >= max * 1.05) return m * mag;
        return 10 * mag;
    }

    /// <summary>Crosshair at the nearest time with data, plus a tooltip listing each series' value there.</summary>
    private void ShowHover(Point at)
    {
        _hover.Children.Clear();
        var withData = _series.Where(s => s.Points.Count > 0).ToList();
        if (withData.Count == 0 || _plotW <= 0 || at.X < _x0 - 12 || at.X > _x1 + 12) return;
        var nearest = withData.SelectMany(s => s.Points).MinBy(p => Math.Abs(Px(p.X) - at.X));
        var x = Px(nearest.X);
        var dark = IsDark();
        _hover.Children.Add(new Line { X1 = x, X2 = x, Y1 = Top, Y2 = PlotHeight - Bottom, Stroke = Hex(dark ? "#6b6a66" : "#a9a8a2"), StrokeThickness = 1 });

        var panel = new StackPanel();
        var head = new TextBlock { Text = FormatX(nearest.X), FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
        head.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        panel.Children.Add(head);
        var ring = (Brush)(Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") ?? Brushes.White);
        foreach (var s in withData)
        {
            // The value at this time, or (for series sampled at other times) the closest point within the same day.
            var p = s.Points.MinBy(q => Math.Abs((q.X - nearest.X).TotalSeconds));
            if (Math.Abs((p.X - nearest.X).TotalHours) > 12) continue;
            _hover.Children.Add(Marker(p, SlotBrush(s.Slot, dark), ring, 10));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Fill = SlotBrush(s.Slot, dark), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            var t = new TextBlock { Text = $"{s.Name}  {FormatY(p.Y)}", FontSize = 12 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            row.Children.Add(t);
            panel.Children.Add(row);
        }
        var tip = new Border { Child = panel, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        tip.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        tip.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var left = x + 12 + tip.DesiredSize.Width > _canvas.ActualWidth ? x - 12 - tip.DesiredSize.Width : x + 12;
        Canvas.SetLeft(tip, Math.Max(0, left));
        Canvas.SetTop(tip, Top);
        _hover.Children.Add(tip);
    }
}
