using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public record BoardLightTarget(string Id, string Name, string Identity, RgbZone[] Zones);
public sealed partial class FlowDeckViewModel
{
    public ObservableCollection<BoardLightTarget> BoardLightTargets { get; } = [];
    public IReadOnlyList<BoardLightEffect> BoardLightEffects => BoardLighting.Effects;
    public ICommand ApplyBoardLight { get; private set; } = null!;
    public ICommand ReloadBoardLight { get; private set; } = null!;
    public ICommand ConnectBoardLight { get; private set; } = null!;
    public ICommand SelectBoardLightColor { get; private set; } = null!;
    private BoardLightTarget? boardLightTarget;
    private RgbInventory? lastLightInventory;
    private MotherboardSettings? lastLightSettings;
    private LightingNameCollection? lastLightNames;
    private bool rebuildingLightTargets, boardLightBusy;
    private byte boardLightEffect = 1;
    private int boardRed = 52, boardGreen = 137, boardBlue = 255;
    private double boardLightSpeed = (255 - 224) * 100d / 255;
    private string boardLightResult = "";
    public bool BoardLightIdle => !boardLightBusy && !host.LightingSynchronized && !host.SceneApplying && !sceneBusy && !brightnessBusy && !groupBusy && !host.LightingAdjustmentBusy && !IdentificationBusy;
    public bool BoardLightRequiresUsb => core.Settings.Motherboard.OpenRgbEnabled;
    public bool CanApplyBoardLight => BoardLightIdle && CanUseLightingScope && BoardLightTarget is { } target &&
        BoardLightTargets.Any(t => t.Id == target.Id) && target.Zones.Length > 0 && target.Zones.All(z => z.LedCount > 0 && z.Configuration is not null) &&
        (Demo || !core.Settings.Motherboard.OpenRgbEnabled);
    public BoardLightTarget? BoardLightTarget
    {
        get => boardLightTarget;
        set
        {
            if (rebuildingLightTargets || !Set(ref boardLightTarget, value)) return;
            LoadBoardLightDraft();
        }
    }
    public byte BoardLightEffect { get => boardLightEffect; set { if (Set(ref boardLightEffect, value)) RefreshBoardLightDraft(); } }
    public int BoardRed { get => boardRed; set { if (Set(ref boardRed, Math.Clamp(value, 0, 255))) RefreshBoardLightDraft(); } }
    public int BoardGreen { get => boardGreen; set { if (Set(ref boardGreen, Math.Clamp(value, 0, 255))) RefreshBoardLightDraft(); } }
    public int BoardBlue { get => boardBlue; set { if (Set(ref boardBlue, Math.Clamp(value, 0, 255))) RefreshBoardLightDraft(); } }
    public double BoardLightSpeed { get => boardLightSpeed; set { if (double.IsFinite(value) && Set(ref boardLightSpeed, Math.Clamp(value, 0, 100))) RefreshBoardLightDraft(); } }
    public bool BoardLightUsesColor => BoardLightEffects.FirstOrDefault(e => e.Id == BoardLightEffect)?.Color == true;
    public bool BoardLightUsesSpeed => BoardLightEffects.FirstOrDefault(e => e.Id == BoardLightEffect)?.Speed == true;
    public string BoardLightHex => $"#{BoardRed:X2}{BoardGreen:X2}{BoardBlue:X2}";
    public Color BoardSelectedColor
    {
        get => Color.FromRgb((byte)BoardRed, (byte)BoardGreen, (byte)BoardBlue);
        set
        {
            if (value == BoardSelectedColor) return;
            boardRed = value.R; boardGreen = value.G; boardBlue = value.B; RefreshBoardLightDraft();
            Notify(nameof(BoardRed)); Notify(nameof(BoardGreen)); Notify(nameof(BoardBlue));
        }
    }
    public Brush BoardLightBrush => new SolidColorBrush(Color.FromRgb((byte)BoardRed, (byte)BoardGreen, (byte)BoardBlue));
    public string BoardLightResult { get => boardLightResult; private set => Set(ref boardLightResult, value); }
    public string BoardLightCurrent => host.LightingSynchronized ? "同期中 · " + HubLighting.Name(host.LightingSyncMode) : BoardLightTarget is { } target ? string.Join("  /  ", target.Zones.Select(z =>
        z.Configuration is { } c ? BoardLighting.Name(c.Mode) : "未取得").Distinct()) : host.Rgb.Error ?? "未検出";
    private BoardLightSetting BoardLightDraft => new(BoardLightEffect, BoardRed, BoardGreen, BoardBlue, 255 - (int)Math.Round(BoardLightSpeed * 255 / 100));

    private void InitializeBoardLighting()
    {
        ConnectBoardLight = Command(async _ =>
        {
            boardLightBusy = true; Notify(nameof(BoardLightIdle));
            try { await host.UseAsrockRgbAsync(); OpenRgbEnabled = false; RefreshMotherboard(); LoadBoardLightDraft(); }
            finally { boardLightBusy = false; Refresh(); }
        }, _ => !boardLightBusy);
        ApplyBoardLight = Command(async _ =>
        {
            var target = BoardLightTarget!; var draft = BoardLightDraft;
            // Freeze both the target and expected configuration before the asynchronous operation.
            var changes = target.Zones.Select(z => new BoardLightChange(z.HardwareZone!.Value, z.Configuration!, draft)).ToArray();
            boardLightBusy = true; Notify(nameof(BoardLightIdle)); BoardLightResult = "適用中…";
            try
            {
                var result = await host.ApplyBoardLightingAsync(target.Identity, changes, CurrentLightingGroup);
                ResetBrightnessDisplay();
                if (result.Verified && BoardLighting.Effects.Any(e => e.Id == draft.Mode && e.Color))
                    LightPalette.Remember(Color.FromRgb((byte)draft.Red, (byte)draft.Green, (byte)draft.Blue));
                BoardLightResult = result.Verified ? $"{target.Name} · 設定一致" :
                    $"設定一致 {result.Zones.Count(z => z.Status == "verified")}/{result.Zones.Length} · 再取得してください";
                if (!result.Verified) Notice = string.Join(" / ", result.Zones.Where(z => z.Status != "verified")
                    .Select(z => $"ゾーン{z.Zone}: {z.Error ?? z.Status}"));
            }
            catch (Exception e) { BoardLightResult = e.Message; throw; }
            finally { boardLightBusy = false; RefreshMotherboard(); Refresh(); }
        }, _ => CanApplyBoardLight);
        ReloadBoardLight = Command(async _ =>
        {
            boardLightBusy = true; Notify(nameof(BoardLightIdle));
            try { await host.RefreshRgbAsync(); RefreshMotherboard(); LoadBoardLightDraft(); }
            finally { boardLightBusy = false; Refresh(); }
        }, _ => !boardLightBusy);
        SelectBoardLightColor = Command(p =>
        {
            var c = p?.ToString() switch
            {
                "white" => (233, 242, 255), "purple" => (160, 121, 236), "red" => (255, 48, 64),
                "green" => (32, 220, 120), "warm" => (255, 184, 100), _ => (52, 137, 255)
            };
            BoardRed = c.Item1; BoardGreen = c.Item2; BoardBlue = c.Item3; return Task.CompletedTask;
        }, _ => !boardLightBusy);
    }
    private void RefreshBoardLightDraft()
    {
        BoardLightResult = "未適用";
        Notify(nameof(BoardLightUsesColor)); Notify(nameof(BoardLightUsesSpeed)); Notify(nameof(BoardLightHex)); Notify(nameof(BoardLightBrush));
        Notify(nameof(BoardSelectedColor));
    }
    private void LoadBoardLightDraft()
    {
        if (boardLightTarget?.Zones.FirstOrDefault()?.Configuration is { } c)
        {
            boardLightEffect = BoardLighting.IsValid(c) ? c.Mode : (byte)1;
            boardRed = c.Red; boardGreen = c.Green; boardBlue = c.Blue; boardLightSpeed = (255 - c.Speed) * 100d / 255;
        }
        BoardLightResult = ""; Refresh();
    }
    private void RefreshBoardLighting()
    {
        if (ReferenceEquals(lastLightInventory, host.Rgb) && ReferenceEquals(lastLightSettings, core.Settings.Motherboard) && ReferenceEquals(lastLightNames, host.LightNames)) return;
        lastLightInventory = host.Rgb; lastLightSettings = core.Settings.Motherboard; lastLightNames = host.LightNames;
        var choices = new List<BoardLightTarget>();
        foreach (var device in host.Rgb.Devices.Where(d => d.ControlIdentity is not null && !d.IsMemory))
        {
            var zones = device.Zones.Where(z => z.HardwareZone is not null && (CurrentLightingGroup is null || CurrentLightingGroup.Board.Contains(new(device.ControlIdentity!, z.HardwareZone.Value)))).ToArray();
            if (zones.Length == 0) continue;
            choices.Add(new(device.ControlIdentity + ":all", CurrentLightingGroup is null ? "マザーボード全体" : "グループ内のマザー", device.ControlIdentity!, zones.Where(z => z.LedCount > 0).ToArray()));
            foreach (var zone in zones)
            {
                var id = device.ZoneRegistrationId(zone.Name, Array.IndexOf(device.Zones, zone));
                var label = core.Settings.Motherboard.RgbZones?.FirstOrDefault(s => s.Id == id)?.Accessory;
                var name = host.LightName(new(Board: new(device.ControlIdentity!, zone.HardwareZone!.Value)), string.IsNullOrWhiteSpace(label) ? zone.Name : label + " · " + zone.Name);
                choices.Add(new(device.ControlIdentity + ":" + zone.HardwareZone, name, device.ControlIdentity!, [zone]));
            }
        }
        var previous = boardLightTarget;
        rebuildingLightTargets = true;
        try { BoardLightTargets.Clear(); foreach (var choice in choices) BoardLightTargets.Add(choice); }
        finally { rebuildingLightTargets = false; }
        boardLightTarget = choices.FirstOrDefault(t => t.Id == previous?.Id);
        if (previous is null) { boardLightTarget = choices.FirstOrDefault(); LoadBoardLightDraft(); }
        else if (boardLightTarget is null) BoardLightResult = "対象が見つかりません。再取得してください";
        Notify(nameof(BoardLightTarget)); Notify(nameof(BoardLightCurrent)); Notify(nameof(CanApplyBoardLight));
    }
}
