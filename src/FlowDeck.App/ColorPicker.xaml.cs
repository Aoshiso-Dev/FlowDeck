using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlowDeck.App;

public partial class ColorPicker : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(nameof(SelectedColor), typeof(Color), typeof(ColorPicker),
        new FrameworkPropertyMetadata(Colors.Red, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, ColorChanged));
    public static readonly DependencyProperty PaletteProperty = DependencyProperty.Register(nameof(Palette), typeof(ColorPalette), typeof(ColorPicker), new PropertyMetadata(null, PaletteChanged));
    public Color SelectedColor { get => (Color)GetValue(SelectedColorProperty); set => SetValue(SelectedColorProperty, value); }
    public ColorPalette? Palette { get => (ColorPalette?)GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Hex => ColorMath.Hex(SelectedColor);
    private string hexDraft = "#FF0000", hexError = "";
    public string HexDraft { get => hexDraft; set { hexDraft = value; hexError = ""; Notify(nameof(HexError)); } }
    public string HexError => hexError;
    public string FavoriteLabel => Palette?.Contains(SelectedColor) == true ? "★ 登録済み" : "☆ 保存";
    public Brush SelectedBrush => new SolidColorBrush(SelectedColor);
    public int Red { get => SelectedColor.R; set => Choose(Color.FromRgb((byte)Math.Clamp(value, 0, 255), SelectedColor.G, SelectedColor.B)); }
    public int Green { get => SelectedColor.G; set => Choose(Color.FromRgb(SelectedColor.R, (byte)Math.Clamp(value, 0, 255), SelectedColor.B)); }
    public int Blue { get => SelectedColor.B; set => Choose(Color.FromRgb(SelectedColor.R, SelectedColor.G, (byte)Math.Clamp(value, 0, 255))); }
    public ColorPicker() { InitializeComponent(); }
    private void Notify(string name = "") => PropertyChanged?.Invoke(this, new(name));
    private static void ColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColorPicker)d; picker.hexDraft = picker.Hex; picker.hexError = ""; picker.Notify();
    }
    private static void PaletteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColorPicker)d;
        if (e.OldValue is ColorPalette old) PropertyChangedEventManager.RemoveHandler(old, picker.PaletteUpdated, "");
        if (e.NewValue is ColorPalette next) PropertyChangedEventManager.AddHandler(next, picker.PaletteUpdated, "");
        picker.Notify();
    }
    private void PaletteUpdated(object? sender, PropertyChangedEventArgs e) => Notify(nameof(FavoriteLabel));
    internal void Choose(Color color) => SetCurrentValue(SelectedColorProperty, color);
    private void SwatchClick(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is ColorSwatch swatch) Choose(swatch.Color); }
    private async void FavoriteClick(object sender, RoutedEventArgs e) { if (Palette is { } palette) await palette.ToggleFavoriteAsync(SelectedColor); }
    internal bool CommitHex()
    {
        if (!ColorMath.TryHex(hexDraft, out var color)) { hexError = "#RRGGBBで入力"; Notify(nameof(HexError)); return false; }
        Choose(color); hexDraft = Hex; hexError = ""; Notify(); return true;
    }
    private void HexLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitHex();
    private void HexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; }
        else if (e.Key == Key.Escape) { hexDraft = Hex; hexError = ""; Notify(); e.Handled = true; }
    }
}
