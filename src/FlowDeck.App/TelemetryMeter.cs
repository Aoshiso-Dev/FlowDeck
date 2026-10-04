using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace FlowDeck.App;

/// <summary>A read-only, theme-aware scale. Missing values draw only the empty track.</summary>
public sealed class TelemetryMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double?), typeof(TelemetryMeter), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(TelemetryMeter), new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CircularProperty = DependencyProperty.Register(nameof(Circular), typeof(bool), typeof(TelemetryMeter), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(TelemetryMeter), new FrameworkPropertyMetadata(Brushes.SlateBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(TelemetryMeter), new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public bool Circular { get => (bool)GetValue(CircularProperty); set => SetValue(CircularProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double Fraction => Value is { } value && double.IsFinite(value) && double.IsFinite(Maximum) && Maximum > 0 ? Math.Clamp(value / Maximum, 0, 1) : 0;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 1 || ActualHeight < 1) return;
        if (!Circular)
        {
            var height = Math.Min(5, ActualHeight);
            var y = (ActualHeight - height) / 2;
            dc.DrawRoundedRectangle(Track, null, new(0, y, ActualWidth, height), 2, 2);
            if (Fraction > 0) dc.DrawRoundedRectangle(Fill, null, new(0, y, ActualWidth * Fraction, height), 2, 2);
            return;
        }
        var radius = Math.Max(0, (Math.Min(ActualWidth, ActualHeight) - 10) / 2);
        if (radius < 1) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        DrawArc(dc, center, radius, 270, Track);
        if (Fraction > 0) DrawArc(dc, center, radius, 270 * Fraction, Fill);
    }

    private static void DrawArc(DrawingContext dc, Point center, double radius, double sweep, Brush brush)
    {
        Point At(double angle) => new(center.X + radius * Math.Cos(angle * Math.PI / 180), center.Y + radius * Math.Sin(angle * Math.PI / 180));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(At(135), false, false);
            context.ArcTo(At(135 + sweep), new(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(brush, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }
}

public sealed class RpmScaleConverter : IMultiValueConverter
{
    // Display scales, not rated maxima. Expand before a high-RPM fan reaches the end.
    public static double Scale(double? value, bool pump) => Math.Max(pump ? 6000 : 3000, value is { } rpm && double.IsFinite(rpm) ? Math.Ceiling(rpm / 1000) * 1000 : 0);
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => Scale(values.ElementAtOrDefault(0) is double rpm ? rpm : null, values.ElementAtOrDefault(1)?.ToString() == "ポンプ");
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
