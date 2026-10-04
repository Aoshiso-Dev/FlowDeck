using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed class HistoryChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples), typeof(DashboardSample[]), typeof(HistoryChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty NowProperty = DependencyProperty.Register(nameof(Now), typeof(double), typeof(HistoryChart), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(HistoryChart), new FrameworkPropertyMetadata("°C", FrameworkPropertyMetadataOptions.AffectsRender));
    public DashboardSample[]? Samples { get => (DashboardSample[]?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public double Now { get => (double)GetValue(NowProperty); set => SetValue(NowProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = Math.Max(1, ActualWidth - 85); var height = Math.Max(1, ActualHeight - 50);
        var muted = (Brush)FindResource("MutedBrush"); var grid = new Pen((Brush)FindResource("BorderBrush"), 1);
        var accent = (Brush)FindResource("AccentBrush");
        void Label(string text, double x, double y) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
        var samples = (Samples ?? []).Where(p => p.Seconds >= Now - 600 && p.Seconds <= Now).ToArray();
        var valid = samples.Where(p => p.Value is { } v && double.IsFinite(v)).ToArray();
        var min = valid.Length == 0 ? 0 : Math.Min(0, valid.Min(p => p.Value!.Value));
        var max = Math.Max(Unit == "rpm" ? 1000 : 100, valid.Select(p => p.Value!.Value).DefaultIfEmpty(0).Max() * 1.1);
        for (var i = 0; i <= 4; i++)
        {
            var y = 16 + height * i / 4; dc.DrawLine(grid, new(58, y), new(58 + width, y));
            Label((max - (max - min) * i / 4).ToString("0"), 1, y - 7);
        }
        Label(Unit, 1, height + 28);
        for (var i = 0; i <= 5; i++) Label(i == 5 ? "現在" : $"{10 - i * 2}分前", 45 + width * i / 5, height + 27);
        if (valid.Length == 0) { Label("有効な測定値を待っています", 80, height / 2); return; }
        Point Position(DashboardSample p) => new(58 + width * (p.Seconds - Now + 600) / 600, 16 + height * (1 - (p.Value!.Value - min) / (max - min)));
        DashboardSample? last = null;
        foreach (var sample in samples)
        {
            if (sample.Value is not { } value || !double.IsFinite(value)) { last = null; continue; }
            if (last is not null && sample.Seconds - last.Seconds < 3) dc.DrawLine(new(accent, 2), Position(last), Position(sample));
            else dc.DrawEllipse(accent, null, Position(sample), 2, 2);
            last = sample;
        }
    }
}
