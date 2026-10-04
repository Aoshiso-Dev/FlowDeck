using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Media;

namespace FlowDeck.App;

public static class ColorMath
{
    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    public static bool TryHex(string? text, out Color color)
    {
        color = Colors.Black;
        var digits = text?.Trim().TrimStart('#');
        if (digits?.Length != 6 || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); return true;
    }
    public static Color FromHsv(double hue, double saturation, double value)
    {
        var h = ((hue % 360) + 360) % 360 / 60;
        var s = Math.Clamp(saturation, 0, 1); var v = Math.Clamp(value, 0, 1);
        var c = v * s; var x = c * (1 - Math.Abs(h % 2 - 1)); var m = v - c;
        var (r, g, b) = (int)h switch { 0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x),
            3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
    public static (double Hue, double Saturation, double Value) ToHsv(Color color, double previousHue = 0, double previousSaturation = 0)
    {
        var r = color.R / 255d; var g = color.G / 255d; var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b)); var d = max - min;
        var h = d == 0 ? previousHue : max == r ? 60 * (((g - b) / d + 6) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h, max == 0 ? previousSaturation : d / max, max);
    }
}

public sealed record ColorSwatch(Color Color, string Name)
{
    public string Hex => ColorMath.Hex(Color);
    public Brush Brush => new SolidColorBrush(Color);
    public string Label => Name.Length == 0 ? Hex : Name + " · " + Hex;
}

public sealed class ColorPalette : Observable
{
    private sealed record Saved(string[] Favorites, string[] Recent);
    private readonly string? path;
    private readonly Func<bool, Color[], Task>? commit;
    private bool busy, available = true;
    public bool CanEdit => available && !busy;
    public void SetAvailable(bool value) { if (available == value) return; available = value; Notify(nameof(CanEdit)); }
    public static ColorSwatch[] Presets { get; } = [new(Color.FromRgb(52,137,255), "ブルー"), new(Color.FromRgb(160,121,236), "パープル"),
        new(Color.FromRgb(255,48,100), "ピンク"), new(Color.FromRgb(255,65,48), "レッド"), new(Color.FromRgb(255,184,100), "電球色"),
        new(Color.FromRgb(32,220,120), "グリーン"), new(Color.FromRgb(34,211,220), "シアン"), new(Color.FromRgb(233,242,255), "ホワイト")];
    public ObservableCollection<ColorSwatch> Favorites { get; } = [];
    public ObservableCollection<ColorSwatch> Recent { get; } = [];
    public string Warning { get; private set; } = "";
    public bool FavoritesEmpty => Favorites.Count == 0;
    public bool RecentEmpty => Recent.Count == 0;
    public ColorPalette(string directory)
    {
        path = Path.Combine(directory, "color-palette.json");
        try
        {
            if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path));
            foreach (var hex in (saved?.Favorites ?? []).Distinct().Take(12)) if (ColorMath.TryHex(hex, out var color)) Favorites.Add(new(color, ""));
            foreach (var hex in (saved?.Recent ?? []).Distinct().Take(8)) if (ColorMath.TryHex(hex, out var color)) Recent.Add(new(color, ""));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { Warning = "色見本を読み込めませんでした"; }
    }
    // v2 palettes are owned by the service and included in its backups. Never read the legacy local file.
    public ColorPalette(Func<bool, Color[], Task> commit) { this.commit = commit; }
    public void UpdateFromService(string[] favorites, string[] recent)
    {
        ColorSwatch[] Swatches(string[] values) => values.Where(v => ColorMath.TryHex(v, out _)).Select(v => { ColorMath.TryHex(v, out var c); return new ColorSwatch(c, ""); }).Distinct().ToArray();
        DeviceWorkspaceViewModel.Reconcile(Favorites, Swatches(favorites)); DeviceWorkspaceViewModel.Reconcile(Recent, Swatches(recent)); Refresh();
    }
    public async Task ToggleFavoriteAsync(Color color)
    {
        if (commit is null) { ToggleFavorite(color); return; }
        await CommitAsync(false, [color]);
    }
    public async Task RememberAsync(params Color[] colors)
    {
        if (colors.Length == 0) return;
        if (commit is null) { Remember(colors); return; }
        await CommitAsync(true, colors.Distinct().Take(32).ToArray());
    }
    private async Task CommitAsync(bool recent, Color[] colors)
    {
        if (!CanEdit) return;
        busy = true; Warning = ""; Refresh();
        try { await commit!(recent, colors); }
        catch (Exception e) { Warning = e.Message; }
        finally { busy = false; Refresh(); }
    }
    public bool Contains(Color color) => Favorites.Any(s => s.Color == color);
    public void ToggleFavorite(Color color)
    {
        if (commit is not null) { _ = ToggleFavoriteAsync(color); return; }
        var existing = Favorites.FirstOrDefault(s => s.Color == color);
        if (existing is not null) Favorites.Remove(existing);
        else if (Favorites.Count < 12) Favorites.Add(new(color, ""));
        else { Warning = "お気に入りは12色までです"; Refresh(); return; }
        Save();
    }
    public void Remember(params Color[] colors)
    {
        if (commit is not null) { _ = RememberAsync(colors); return; }
        foreach (var color in colors.Reverse())
        {
            var existing = Recent.FirstOrDefault(s => s.Color == color);
            if (existing is not null) Recent.Remove(existing);
            Recent.Insert(0, new(color, ""));
        }
        while (Recent.Count > 8) Recent.RemoveAt(Recent.Count - 1);
        Save();
    }
    private void Save()
    {
        if (path is null) throw new InvalidOperationException("サービスの色見本をローカルへ保存することはできません");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new Saved(Favorites.Select(s => s.Hex).ToArray(), Recent.Select(s => s.Hex).ToArray())));
            File.Move(path + ".tmp", path, true); Warning = "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Warning = "色見本を保存できませんでした"; }
        Refresh();
    }
}
