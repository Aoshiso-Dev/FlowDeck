using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using FlowDeck.Core;

namespace FlowDeck.App;

public record MemoryLightTarget(string Id, string Name, string[] Identities);
public sealed partial class FlowDeckViewModel
{
    public ObservableCollection<MemoryLightTarget> MemoryLightTargets { get; } = [];
    public IReadOnlyList<BoardLightEffect> MemoryLightEffects => MemoryLighting.Effects;
    private MemoryLightTarget? memoryLightTarget;
    private string memoryChoicesSignature = "", memoryLightResult = "";
    private bool rebuildingMemory;
    private byte memoryLightEffect = 1;
    private Color memorySelectedColor = Color.FromRgb(52, 137, 255);
    private double memoryLightSpeed = 50, memoryLightBrightness = 100;
    public MemoryLightTarget? MemoryLightTarget
    {
        get => memoryLightTarget;
        set { if (!rebuildingMemory && Set(ref memoryLightTarget, value)) LoadMemoryLightDraft(); }
    }
    public byte MemoryLightEffect { get => memoryLightEffect; set { if (Set(ref memoryLightEffect, value)) Refresh(); } }
    public Color MemorySelectedColor { get => memorySelectedColor; set => Set(ref memorySelectedColor, value); }
    public double MemoryLightSpeed { get => memoryLightSpeed; set { if (double.IsFinite(value)) Set(ref memoryLightSpeed, Math.Clamp(value, 0, 100)); } }
    public double MemoryLightBrightness { get => memoryLightBrightness; set { if (double.IsFinite(value)) Set(ref memoryLightBrightness, Math.Clamp(value, 0, 100)); } }
    public bool MemoryLightUsesColor => MemoryLightEffects.FirstOrDefault(e => e.Id == MemoryLightEffect)?.Color == true;
    public bool MemoryLightUsesSpeed => MemoryLightEffects.FirstOrDefault(e => e.Id == MemoryLightEffect)?.Speed == true;
    public bool CanApplyMemoryLight => BoardLightIdle && CanUseLightingScope && MemoryLightTarget is { } t && t.Identities.Length > 0 &&
        t.Identities.All(id => host.Rgb.Devices.SingleOrDefault(d => d.ControlIdentity == id)?.Zones.SingleOrDefault()?.Configuration is not null);
    public string MemoryLightResult { get => memoryLightResult; private set => Set(ref memoryLightResult, value); }
    public string MemoryLightCurrent => host.MemoryRgbError ?? (MemoryLightTarget is { } t ? string.Join(" / ", t.Identities.Select(id =>
        host.Rgb.Devices.SingleOrDefault(d => d.ControlIdentity == id) is { } d ? d.Name + " · " + (d.Zones[0].Configuration is { } c ? BoardLighting.Name(c.Mode) : "未対応の設定") : "未検出")) : "メモリRGB未検出");
    public ICommand ApplyMemoryLight { get; private set; } = null!;
    public ICommand ReloadMemoryLight { get; private set; } = null!;
    private void InitializeMemoryLighting()
    {
        ApplyMemoryLight = Command(async _ =>
        {
            var target = MemoryLightTarget!; var color = MemorySelectedColor; var scale = MemoryLightBrightness / 100;
            var desired = MemoryLighting.Normalize(new(MemoryLightEffect, (int)Math.Round(color.R * scale), (int)Math.Round(color.G * scale),
                (int)Math.Round(color.B * scale), (int)Math.Round((100 - MemoryLightSpeed) * 255 / 100)));
            boardLightBusy = true; MemoryLightResult = "適用中…"; Refresh();
            try
            {
                var rows = await host.ApplyMemoryLightingAsync(target.Identities.ToArray(), desired, CurrentLightingGroup);
                MemoryLightResult = string.Join(" / ", rows.Select(r => r.Target + " · " + r.Result)); ResetBrightnessDisplay();
                if (rows.All(r => r.Result == "設定一致") && MemoryLightUsesColor) LightPalette.Remember(color);
            }
            catch (Exception e) { MemoryLightResult = e.Message; throw; }
            finally { boardLightBusy = false; RefreshMotherboard(); Refresh(); }
        }, _ => CanApplyMemoryLight);
        ReloadMemoryLight = Command(async _ =>
        {
            boardLightBusy = true;
            try { await host.RefreshRgbAsync(); RefreshMotherboard(); LoadMemoryLightDraft(); MemoryLightResult = ""; }
            finally { boardLightBusy = false; Refresh(); }
        }, _ => BoardLightIdle);
    }
    private void LoadMemoryLightDraft()
    {
        if (MemoryLightTarget?.Identities.FirstOrDefault() is { } id && host.Rgb.Devices.SingleOrDefault(d => d.ControlIdentity == id)?.Zones[0].Configuration is { } c)
        {
            memoryLightEffect = c.Mode; memorySelectedColor = Color.FromRgb((byte)c.Red, (byte)c.Green, (byte)c.Blue);
            memoryLightBrightness = 100; memoryLightSpeed = Math.Round((255 - c.Speed) * 4d / 255) * 25;
        }
        MemoryLightResult = ""; Refresh();
    }
    private void RefreshMemoryLighting()
    {
        var devices = host.Rgb.Devices.Where(d => d.IsMemory && (CurrentLightingGroup is null || CurrentLightingGroup.Board.Contains(new(d.ControlIdentity!, 0)))).ToArray();
        var signature = CurrentLightingGroup?.Id + "|" + string.Join("|", devices.Select(d => d.ControlIdentity + host.LightName(new(Board: new(d.ControlIdentity!, 0)), d.Name)));
        if (signature == memoryChoicesSignature) return;
        memoryChoicesSignature = signature; var previous = memoryLightTarget?.Id;
        var choices = new List<MemoryLightTarget>();
        if (devices.Length > 0) choices.Add(new("all", CurrentLightingGroup is null ? "メモリ全体" : "グループ内のメモリ", devices.Select(d => d.ControlIdentity!).ToArray()));
        choices.AddRange(devices.Select(d => new MemoryLightTarget(d.ControlIdentity!, host.LightName(new(Board: new(d.ControlIdentity!, 0)), d.Name), [d.ControlIdentity!])));
        rebuildingMemory = true;
        try { MemoryLightTargets.Clear(); foreach (var t in choices) MemoryLightTargets.Add(t); }
        finally { rebuildingMemory = false; }
        memoryLightTarget = choices.FirstOrDefault(t => t.Id == previous) ?? choices.FirstOrDefault();
        Notify(nameof(MemoryLightTarget));
        if (previous is null) LoadMemoryLightDraft();
    }
}
