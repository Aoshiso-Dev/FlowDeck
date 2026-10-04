using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed partial class CurveChart
{
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var plot = Plot; var range = Range;
        Brush Brush(string key) => (Brush)FindResource(key);
        var muted = Brush("MutedBrush"); var accent = Brush("AccentBrush"); var border = Brush("BorderBrush");
        void Label(string text, Point at, Brush? color = null, double size = 10) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, color ?? muted, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, ActualWidth - at.X) }, at);
        dc.DrawRoundedRectangle(Brush("InsetBrush"), IsKeyboardFocused ? new Pen(accent, 1) : null, new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)), 6, 6);
        var liveT = CurrentTemperature is { } t && double.IsFinite(t) ? $"{t:0.#}°C" : "—°C";
        var liveD = CurrentDuty is { } d && double.IsFinite(d) ? $"{d:0.#}%" : "—%";
        Label($"現在 {liveT} / {liveD}", new(10, 10), Brush("TextBrush"), 11);
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + plot.Height * i / 4; dc.DrawLine(new(border, 1), new(plot.Left, y), new(plot.Right, y));
            Label((100 - i * 25) + "%", new(3, y - 7));
        }
        for (var i = 0; i <= 5; i++) Label((range.Low + (range.High - range.Low) * i / 5).ToString("0.#") + (i == 5 ? "°C" : ""), new(plot.Left - (i == 5 ? 27 : 7) + plot.Width * i / 5, plot.Bottom + 7));
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left, plot.Top - 6, plot.Width, plot.Height + 12)));
        if (MinimumDuty is >= 1 and <= 100)
        {
            var y = Position(range.Low, MinimumDuty.Value).Y;
            dc.PushOpacity(.1); dc.DrawRectangle(Brush("WarningBrush"), null, new Rect(plot.Left, y, plot.Width, plot.Bottom - y)); dc.Pop();
            dc.DrawLine(new Pen(muted, 1) { DashStyle = DashStyles.Dash }, new(plot.Left, y), new(plot.Right, y));
        }
        var rows = Rows; var drawable = rows.All(p => double.IsFinite(p.Temperature));
        if (drawable && rows.Length > 0)
        {
            var shape = new StreamGeometry(); using (var context = shape.Open())
            {
                context.BeginFigure(new(plot.Left, plot.Bottom), true, true);
                context.LineTo(Position(range.Low, rows[0].Duty), true, false);
                foreach (var row in rows) context.LineTo(Position(row.Temperature, row.Duty), true, false);
                context.LineTo(Position(range.High, rows[^1].Duty), true, false); context.LineTo(new(plot.Right, plot.Bottom), true, false);
            }
            dc.PushOpacity(.10); dc.DrawGeometry(accent, null, shape); dc.Pop(); var pen = new Pen(accent, 2.2);
            dc.DrawLine(pen, Position(range.Low, rows[0].Duty), Position(rows[0].Temperature, rows[0].Duty));
            for (var i = 1; i < rows.Length; i++) dc.DrawLine(pen, Position(rows[i - 1].Temperature, rows[i - 1].Duty), Position(rows[i].Temperature, rows[i].Duty));
            dc.DrawLine(pen, Position(rows[^1].Temperature, rows[^1].Duty), Position(range.High, rows[^1].Duty));
            foreach (var row in rows) dc.DrawEllipse(row == SelectedPoint ? Brush("PanelBrush") : accent, new(accent, 2), Position(row.Temperature, row.Duty), row == SelectedPoint ? 6 : 4, row == SelectedPoint ? 6 : 4);
        }
        if (CurrentTemperature is { } temperature && double.IsFinite(temperature) && temperature >= range.Low && temperature <= range.High)
        {
            var x = Position(temperature, 0).X;
            dc.DrawLine(new Pen(muted, 1) { DashStyle = DashStyles.Dot }, new(x, plot.Top), new(x, plot.Bottom));
            if (CurrentDuty is { } duty && double.IsFinite(duty) && duty is >= 0 and <= 100) dc.DrawEllipse(Brush("WarningBrush"), new(Brush("PanelBrush"), 2), Position(temperature, duty), 5, 5);
        }
        dc.Pop();
        var note = MinimumDuty is null ? "最低出力を設定してください" : !CurveEditing.ValidShape(Values, MinimumDuty, AllowStop) ? "数値一覧で範囲・順序を確認" : SelectedPoint is { } selected
            ? $"選択 {selected.Temperature:0.#}°C · {selected.Duty}%" : rows.Length == 0 ? "点を追加" : $"最低 {MinimumDuty}%" + (AllowStop ? " · 停止許可" : "");
        Label(note, new(10, ActualHeight - 16), CanEdit ? muted : Brush("WarningBrush"));
    }
}
