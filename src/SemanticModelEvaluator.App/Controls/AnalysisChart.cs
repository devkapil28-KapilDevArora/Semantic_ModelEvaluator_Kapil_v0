using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using SemanticModelEvaluator.App.ViewModels;
using SemanticModelEvaluator.Core.Comparison;

namespace SemanticModelEvaluator.App.Controls;

public sealed class AnalysisChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(List<ChartSeries>),
        typeof(AnalysisChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RadarProperty = DependencyProperty.Register(nameof(Radar), typeof(RadarProjection),
        typeof(AnalysisChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThresholdProperty = DependencyProperty.Register(nameof(Threshold), typeof(double),
        typeof(AnalysisChart), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.Register(nameof(Interactive), typeof(bool),
        typeof(AnalysisChart), new PropertyMetadata(false));
    public List<ChartSeries>? Series { get => (List<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public RadarProjection? Radar { get => (RadarProjection?)GetValue(RadarProperty); set => SetValue(RadarProperty, value); }
    public double Threshold { get => (double)GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public bool Interactive { get => (bool)GetValue(InteractiveProperty); set => SetValue(InteractiveProperty, value); }
    private Rect plot;
    private double xmin, xmax;

    public AnalysisChart()
    {
        Focusable = true; MinHeight = 240;
        MouseMove += (_, e) =>
        {
            if (Radar is null && plot.Contains(e.GetPosition(this)) && Series is { Count: > 0 })
            {
                var x = X(e.GetPosition(this).X);
                ToolTip = string.Join("\n", Series.Select(s =>
                {
                    if (s.X.Length == 0) return s.Name + ": no scores";
                    var i = Enumerable.Range(0, s.X.Length).MinBy(i => Math.Abs(s.X[i] - x));
                    return $"{s.Name}: x={s.X[i]:0.000}, y={s.Y[i]?.ToString("0.000") ?? "n/a"}";
                }));
            }
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (Interactive && plot.Contains(e.GetPosition(this))) { Focus(); SetCurrentValue(ThresholdProperty, X(e.GetPosition(this).X)); }
        };
        KeyDown += (_, e) =>
        {
            if (!Interactive || e.Key is not (Key.Left or Key.Right)) return;
            SetCurrentValue(ThresholdProperty, Math.Clamp(Threshold + (e.Key == Key.Right ? 1 : -1) * (xmax - xmin) / 100, xmin, xmax));
            e.Handled = true;
        };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this);

    /// <summary>Text alternative read by screen readers (UIA HelpText) and shown as the default tooltip.</summary>
    public string TextSummary
    {
        get
        {
            if (Radar is { Axes.Count: >= 3 } radar)
                return "Radar chart (normalized 0–1). " + string.Join(" ", radar.Series.Select(s => s.Name + ": " +
                    string.Join(", ", radar.Axes.Select((a, i) => $"{a.Label} {(s.Values[i] is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : "unknown")}")) + "."));
            var series = Series?.Where(s => s.X.Length == s.Y.Length && s.X.Length > 0).ToArray() ?? [];
            if (series.Length == 0) return "No comparable values.";
            return "Line chart. " + string.Join(" ", series.Select(s =>
            {
                var ys = s.Y.Where(y => y is { } v && double.IsFinite(v)).Select(y => y!.Value).ToArray();
                return ys.Length == 0 ? $"{s.Name}: no values." :
                    $"{s.Name}: {ys.Length} points, x {s.X.Min():0.###}–{s.X.Max():0.###}, y {ys.Min():0.###}–{ys.Max():0.###}.";
            })) + (Interactive ? $" Threshold {Threshold:0.###}." : "");
        }
    }

    private sealed class ChartAutomationPeer(AnalysisChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(AnalysisChart);
        protected override string GetNameCore() => base.GetNameCore() is { Length: > 0 } n ? n : "Chart";
        protected override string GetHelpTextCore() => owner.TextSummary;
    }

    // Non-colour cues: each series has its own dash pattern and marker shape, also shown in the legend.
    private static readonly DashStyle[] SeriesDashes = [DashStyles.Solid, DashStyles.Dash, DashStyles.Dot, DashStyles.DashDot, DashStyles.DashDotDot];
    private static Pen SeriesPen(Brush brush, int index) => new(brush, 2) { DashStyle = SeriesDashes[index % SeriesDashes.Length] };
    private static void Marker(DrawingContext dc, Brush brush, Point p, int index)
    {
        switch (index % 3)
        {
            case 0: dc.DrawEllipse(brush, null, p, 2.5, 2.5); break;
            case 1: dc.DrawRectangle(brush, null, new Rect(p.X - 2.5, p.Y - 2.5, 5, 5)); break;
            default:
                var g = new StreamGeometry();
                using (var c = g.Open()) { c.BeginFigure(new Point(p.X, p.Y - 3.5), true, true); c.LineTo(new Point(p.X + 3.5, p.Y + 2.5), true, false); c.LineTo(new Point(p.X - 3.5, p.Y + 2.5), true, false); }
                dc.DrawGeometry(brush, null, g); break;
        }
    }
    private void Legend(DrawingContext dc, string name, Point at, Brush brush, int index)
    {
        dc.DrawLine(SeriesPen(brush, index), new Point(at.X, at.Y + 7), new Point(at.X + 24, at.Y + 7));
        Marker(dc, brush, new Point(at.X + 12, at.Y + 7), index);
        Text(dc, name, new Point(at.X + 30, at.Y), brush);
    }
    private double X(double pixel) => xmin + (pixel - plot.Left) / Math.Max(1, plot.Width) * (xmax - xmin);
    private Brush Ink => TryFindResource("TextBrush") as Brush ?? Brushes.Black;
    private void Text(DrawingContext dc, string text, Point p, Brush? brush = null, double size = 11)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush ?? Ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, p);
    }
    private static Brush ColorBrush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (ActualWidth < 150 || ActualHeight < 120) return;
        if (Radar is { Axes.Count: >= 3 } radar) { DrawRadar(dc, radar); return; }
        if (Radar is not null) { Text(dc, "Select at least three bounded directional metrics for radar.", new(15, 20)); return; }
        var series = Series?.Where(s => s.X.Length == s.Y.Length && s.X.Length > 0).ToArray() ?? [];
        var points = series.SelectMany(s => s.X.Zip(s.Y)).Where(p => p.Second is { } y && double.IsFinite(y) && double.IsFinite(p.First)).ToArray();
        if (points.Length == 0) { Text(dc, "No comparable values. Run an evaluation and select a metric.", new(15, 20)); return; }
        xmin = points.Min(p => p.First); xmax = points.Max(p => p.First);
        if (xmin == xmax) xmax = xmin + 1;
        double ymin = Math.Min(0, points.Min(p => p.Second!.Value)), ymax = points.Max(p => p.Second!.Value);
        if (ymin == ymax) ymax = ymin + 1;
        plot = new Rect(56, 20, ActualWidth - 80, Math.Max(60, ActualHeight - 80 - series.Length * 16));
        Point Map(double x, double y) => new(plot.Left + (x - xmin) / (xmax - xmin) * plot.Width, plot.Bottom - (y - ymin) / (ymax - ymin) * plot.Height);
        for (int i = 0; i <= 4; i++)
        {
            var y = ymin + (ymax - ymin) * i / 4;
            var p = Map(xmin, y);
            dc.DrawLine(new Pen(Brushes.Gray, 0.3), p, new Point(plot.Right, p.Y));
            Text(dc, y.ToString("0.##"), new Point(2, p.Y - 6));
            var x = xmin + (xmax - xmin) * i / 4;
            Text(dc, x.ToString("0.##"), new Point(Map(x, ymin).X - 10, plot.Bottom + 6));
        }
        for (int index = 0; index < series.Length; index++)
        {
            var s = series[index]; var color = ColorBrush(s.Color); Point? previous = null;
            for (int i = 0; i < s.X.Length; i++)
            {
                if (s.Y[i] is not { } y || !double.IsFinite(y)) { previous = null; continue; }
                var p = Map(s.X[i], y);
                if (previous is { } start) dc.DrawLine(SeriesPen(color, index), start, p);
                Marker(dc, color, p, index); previous = p;
            }
            Legend(dc, s.Name, new Point(56, plot.Bottom + 28 + index * 16), color, index);
        }
        if (Interactive && Threshold >= xmin && Threshold <= xmax)
        {
            var x = Map(Threshold, ymin).X;
            dc.DrawLine(new Pen(Ink, 1) { DashStyle = DashStyles.Dash }, new(x, plot.Top), new(x, plot.Bottom));
        }
    }
    private void DrawRadar(DrawingContext dc, RadarProjection radar)
    {
        int n = radar.Axes.Count;
        var center = new Point(ActualWidth / 2, ActualHeight / 2 - 20);
        var radius = Math.Min(ActualWidth / 2 - 115, ActualHeight / 2 - 70);
        if (radius < 15) return;
        Point At(int i, double value) => new(center.X + Math.Cos(i * 2 * Math.PI / n - Math.PI / 2) * radius * value,
            center.Y + Math.Sin(i * 2 * Math.PI / n - Math.PI / 2) * radius * value);
        for (int i = 0; i < n; i++)
        {
            dc.DrawLine(new Pen(Brushes.Gray, 0.5), center, At(i, 1));
            var p = At(i, 1.15); Text(dc, radar.Axes[i].Label, new(p.X - 45, p.Y), size: 10);
        }
        for (int index = 0; index < radar.Series.Count; index++)
        {
            var s = radar.Series[index]; var brush = ColorBrush(s.Color);
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                if (s.Values[i] is { } a && s.Values[j] is { } b) dc.DrawLine(SeriesPen(brush, index), At(i, a), At(j, b));
                if (s.Values[i] is { } v) Marker(dc, brush, At(i, v), index);
            }
            Legend(dc, s.Name, new(12, ActualHeight - 18 * (radar.Series.Count - index)), brush, index);
        }
    }
}
