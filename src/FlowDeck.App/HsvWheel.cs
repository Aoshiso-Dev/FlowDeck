using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace FlowDeck.App;

public sealed class HsvWheel : FrameworkElement
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(nameof(SelectedColor), typeof(Color), typeof(HsvWheel),
        new FrameworkPropertyMetadata(Colors.Red, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public Color SelectedColor { get => (Color)GetValue(SelectedColorProperty); set => SetValue(SelectedColorProperty, value); }
    public double Hue { get; private set; }
    public double Saturation { get; private set; } = 1;
    public double Value { get; private set; } = 1;
    private bool updating, dragging, ring = true;
    private BitmapSource? wheel;
    private double cachedSize;
    private double Size => Math.Min(ActualWidth, ActualHeight);
    private Point Center => new(ActualWidth / 2, ActualHeight / 2);
    internal Rect ColorArea => new(Center.X - Size * .245, Center.Y - Size * .245, Size * .49, Size * .49);
    public HsvWheel() { Focusable = true; Cursor = Cursors.Cross; ToolTip = "外周：色合い　中央：鮮やかさ・明るさ\n矢印キーで調整、Spaceで操作する場所を切替"; }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (HsvWheel)d;
        if (!self.updating) (self.Hue, self.Saturation, self.Value) = ColorMath.ToHsv((Color)e.NewValue, self.Hue, self.Saturation);
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (Size < 24) return;
        dc.DrawRectangle(Brushes.Transparent, null, new(0, 0, ActualWidth, ActualHeight));
        if (wheel is null || cachedSize != Size)
        {
            cachedSize = Size;
            // Supersampled polar gradient avoids seams between individual colored arc segments.
            var pixels = (int)Math.Ceiling(Size * 2); var data = new byte[pixels * pixels * 4];
            for (var y = 0; y < pixels; y++)
            for (var x = 0; x < pixels; x++)
            {
                var dx = x + .5 - pixels / 2d; var dy = y + .5 - pixels / 2d; var radius = Math.Sqrt(dx * dx + dy * dy);
                var alpha = Math.Clamp(Math.Min(pixels * .4725 - radius, radius - pixels * .3775) + .5, 0, 1);
                if (alpha == 0) continue;
                var color = ColorMath.FromHsv(Math.Atan2(dy, dx) * 180 / Math.PI + 90, 1, 1); var offset = (y * pixels + x) * 4;
                data[offset] = color.B; data[offset + 1] = color.G; data[offset + 2] = color.R; data[offset + 3] = (byte)Math.Round(alpha * 255);
            }
            wheel = BitmapSource.Create(pixels, pixels, 192, 192, PixelFormats.Bgra32, null, data, pixels * 4); wheel.Freeze();
        }
        dc.DrawImage(wheel, new(Center.X - Size / 2, Center.Y - Size / 2, Size, Size));
        var area = ColorArea;
        dc.DrawRectangle(new LinearGradientBrush(Colors.White, ColorMath.FromHsv(Hue, 1, 1), 0), null, area);
        dc.DrawRectangle(new LinearGradientBrush(Colors.Transparent, Colors.Black, 90), null, area);
        dc.DrawRectangle(null, new Pen((Brush)FindResource("BorderBrush"), 1), area);
        var angle = (Hue - 90) * Math.PI / 180;
        Marker(new(Center.X + Math.Cos(angle) * Size * .425, Center.Y + Math.Sin(angle) * Size * .425), ColorMath.FromHsv(Hue, 1, 1));
        Marker(new(area.X + Saturation * area.Width, area.Bottom - Value * area.Height), SelectedColor);
        if (IsKeyboardFocused)
            dc.DrawRoundedRectangle(null, new Pen((Brush)FindResource("AccentBrush"), 1) { DashStyle = DashStyles.Dot },
                new(1, 1, ActualWidth - 2, ActualHeight - 2), 8, 8);
        void Marker(Point at, Color fill)
        {
            dc.DrawEllipse(new SolidColorBrush(fill), new Pen(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), 4), at, 6, 6);
            dc.DrawEllipse(null, new Pen(Brushes.White, 2), at, 6, 6);
        }
    }
    internal bool BeginSelection(Point point)
    {
        if (!IsEnabled || Size < 24) return false;
        var distance = (point - Center).Length;
        if (ColorArea.Contains(point)) ring = false;
        else if (distance >= Size * .365 && distance <= Size * .49) ring = true;
        else return false;
        UpdateSelection(point); return true;
    }
    internal void UpdateSelection(Point point)
    {
        if (!IsEnabled || Size < 24) return;
        if (ring) Hue = (Math.Atan2(point.Y - Center.Y, point.X - Center.X) * 180 / Math.PI + 450) % 360;
        else { Saturation = Math.Clamp((point.X - ColorArea.X) / ColorArea.Width, 0, 1); Value = Math.Clamp((ColorArea.Bottom - point.Y) / ColorArea.Height, 0, 1); }
        Commit();
    }
    private void Commit()
    {
        updating = true;
        try { SetCurrentValue(SelectedColorProperty, ColorMath.FromHsv(Hue, Saturation, Value)); }
        finally { updating = false; }
        InvalidateVisual();
    }
    internal bool Adjust(Key key, bool large = false)
    {
        if (!IsEnabled) return false;
        if (key == Key.Space) { ring = !ring; InvalidateVisual(); return true; }
        var step = large ? 10 : 1;
        if (key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return false;
        if (ring) Hue = (Hue + (key is Key.Right or Key.Up ? step : -step) + 360) % 360;
        else if (key is Key.Left or Key.Right) Saturation = Math.Clamp(Saturation + (key == Key.Right ? step : -step) / 100d, 0, 1);
        else Value = Math.Clamp(Value + (key == Key.Up ? step : -step) / 100d, 0, 1);
        Commit(); return true;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonDown(e); if (BeginSelection(e.GetPosition(this))) { Focus(); dragging = CaptureMouse(); e.Handled = true; } }
    protected override void OnMouseMove(MouseEventArgs e)
    { base.OnMouseMove(e); if (dragging && e.LeftButton == MouseButtonState.Pressed) { UpdateSelection(e.GetPosition(this)); e.Handled = true; } }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonUp(e); if (dragging) { dragging = false; ReleaseMouseCapture(); e.Handled = true; } }
    protected override void OnLostMouseCapture(MouseEventArgs e) { dragging = false; base.OnLostMouseCapture(e); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (Adjust(e.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))) e.Handled = true; }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new WheelPeer(this);
    private sealed class WheelPeer(HsvWheel owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        protected override string GetClassNameCore() => nameof(HsvWheel);
        protected override string GetNameCore() => "色相リングと鮮やかさ・明るさ";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);
        public bool IsReadOnly => !owner.IsEnabled;
        public string Value => ColorMath.Hex(owner.SelectedColor);
        public void SetValue(string value)
        {
            if (IsReadOnly) throw new InvalidOperationException("色選択は無効です");
            if (!ColorMath.TryHex(value, out var color)) throw new ArgumentException("色形式: #RRGGBB");
            owner.SetCurrentValue(SelectedColorProperty, color);
        }
    }
}
