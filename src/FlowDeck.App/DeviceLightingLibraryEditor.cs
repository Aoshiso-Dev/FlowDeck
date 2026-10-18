using System.Windows.Media;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed class DeviceLightingLibraryMember(DeviceLightMember member, DeviceLightingLibraryEditor editor) : Observable
{
    public EndpointReference Target => member.Target;
    public string Name => member.Name;
    public bool Selected { get => editor.Targets.Contains(Target); set => editor.SelectTarget(Target, value); }
    public bool Available => !editor.IsScene || editor.Part is not null && editor.Draft.Available(editor.Part.Id, Target);
    public void Publish() { Notify(nameof(Selected)); Notify(nameof(Available)); }
}
public sealed partial class DeviceLightingLibraryEditor : Observable
{
    private readonly Action changed;
    private string? selectedPart;
    private DeviceScenePart? displayedPart;
    public DeviceLightingLibraryDraft Draft { get; }
    public DeviceLightingLibraryMember[] Members { get; }
    public DeviceLightChoice[] Parts { get; private set; } = [];
    public DeviceLightChoice[] Effects { get; private set; } = [];
    public ColorPalette? SavedColors { get; }
    public DeviceLightingLibraryEditor(DeviceLightingLibraryDraft draft, Action changed, ColorPalette? savedColors = null)
    {
        Draft = draft; this.changed = changed; SavedColors = savedColors;
        Members = draft.Candidates.Select(c => new DeviceLightingLibraryMember(c, this)).ToArray();
        selectedPart = Draft.PartIds.FirstOrDefault(); PublishParts();
    }
    public bool IsScene => Draft.Kind == LightingPresetKind.Scene;
    public bool IsGroup => !IsScene;
    public string Title => (Draft.Existing ? "編集 · " : "新規 · ") + (IsScene ? "シーン" : "グループ");
    public string Name { get => Draft.Name; set { if (Draft.Name == value) return; Draft.Name = value; Notify(nameof(Name)); Changed(); } }
    public string Status => Draft.Problem ?? (Draft.Dirty ? "未保存" : "保存済み");
    public string? SelectedPart { get => selectedPart; set { if (value is null || value == selectedPart || !Draft.PartIds.Contains(value)) return; selectedPart = value; PublishPart(); } }
    public DeviceScenePart? Part => displayedPart;
    public EndpointReference[] Targets => IsScene ? Part?.Targets ?? [] : Draft.Targets;
    public bool HasPart => Part is not null;
    public bool CanAdd => Draft.CanAddPart;
    public bool CommonPart => Part is { } p && DeviceLighting.FrameProgram(p.Program);
    public bool AnimatedPart => Part is { } p && DeviceLighting.Animated(p.Program);
    public bool CanPickColor => CommonPart && Effect is not ("off" or "color-cycle") || EditableNative && NativeEffect!.Color;
    public string Effect
    {
        get => Part?.Program.EffectId ?? "fixed";
        set
        {
            if (publishing || Part is not { } part || value is null || value == part.Program.EffectId) return;
            if (nativeEffects.SingleOrDefault(e => e.Id == value) is { } native) { ChangeProgram(DeviceNativeLighting.Create(native, part.Program)); return; }
            if (!new[] { "fixed", "off", "breathing", "color-cycle" }.Contains(value)) return;
            ChangeProgram(new(value, value == "off" ? [] : [part.Program.Colors.FirstOrDefault() ?? new(52, 137, 255)],
                Math.Clamp(part.Program.Speed, 0, 4), Brightness: part.Program.Brightness));
        }
    }
    public Color Color
    {
        get { var p = Part?.Program; var c = (p?.LedColors ?? p?.Colors)?.ElementAtOrDefault(SelectedColor) ?? new(52, 137, 255); return System.Windows.Media.Color.FromRgb((byte)c.R, (byte)c.G, (byte)c.B); }
        set { if (publishing || Part is not { } part || !CanPickColor) return;
            var colors = (part.Program.LedColors ?? part.Program.Colors).ToArray(); if (SelectedColor >= colors.Length) return; colors[SelectedColor] = new(value.R, value.G, value.B);
            ChangeProgram(part.Program.LedColors is null ? part.Program with { Colors = colors } : part.Program with { LedColors = colors }); }
    }
    public int Brightness { get => Part?.Program.Brightness ?? 100; set { if (!publishing && Part is { } part && CanEditBrightness) ChangeProgram(part.Program with { Brightness = Math.Clamp(value, 0, 100) }); } }
    public int Speed { get => Part?.Program.Speed ?? 2; set { if (!publishing && Part is { } part && HasSpeed) ChangeProgram(part.Program with { Speed = Math.Clamp(value, SpeedMinimum, SpeedMaximum) }); } }
    private void ChangeProgram(LightingProgram program)
    {
        var part = Part!; if (DeviceLighting.SameProgram(part.Program, program)) return;
        Draft.ChangeKnownProgram(part.Id, program); PublishParts(); Changed();
    }
    public void SelectTarget(EndpointReference target, bool selected)
    {
        var values = Targets.ToHashSet(); if (selected ? !values.Add(target) : !values.Remove(target)) return;
        if (IsScene) { if (Part is not { } part) return; Draft.ChangePart(part.Id, values.ToArray(), part.Program); }
        else Draft.SetTargets(values);
        PublishParts(); Changed();
    }
    public void AddPart() { selectedPart = Draft.AddPart(); PublishParts(); Changed(); }
    public void RemovePart() { if (Part is not { } part) return; Draft.RemovePart(part.Id); selectedPart = Draft.PartIds.FirstOrDefault(); PublishParts(); Changed(); }
    public void Update(DeviceWorkspaceSnapshot snapshot) { Draft.Update(snapshot); if (!nativeEffects.SequenceEqual(Draft.NativeEffects(selectedPart))) PublishPart(); Notify(nameof(Status)); }
    private void Changed() { Notify(nameof(Status)); changed(); }
    private void PublishParts()
    {
        var choices = Draft.PartIds.Select((id, i) => new DeviceLightChoice(id, $"パート {i + 1}")).ToArray();
        if (!Parts.SequenceEqual(choices)) { Parts = choices; Notify(nameof(Parts)); }
        Notify(nameof(CanAdd)); PublishPart();
    }
    private static string EffectName(string id) => id switch { "fixed" => "固定", "off" => "消灯", "breathing" => "呼吸", "color-cycle" => "カラーサイクル", _ => id };
    private void PublishPart()
    {
        publishing = true;
        try {
        displayedPart = Draft.ReadPart(selectedPart);
        nativeEffects = Draft.NativeEffects(selectedPart);
        var effects = new[] { "fixed", "breathing", "color-cycle", "off" }.Select(id => new DeviceLightChoice(id, EffectName(id)))
            .Concat(nativeEffects.Select(e => new DeviceLightChoice(e.Id, e.Name + " · 機器内蔵"))).ToList();
        if (!effects.Any(e => e.Id == Effect)) effects.Add(new(Effect, Effect + " · 利用不可"));
        if (!Effects.SequenceEqual(effects)) Effects = effects.ToArray();
        PublishNativePart();
        foreach (var member in Members) member.Publish();
        foreach (var name in new[] { nameof(SelectedPart), nameof(HasPart), nameof(CommonPart), nameof(AnimatedPart), nameof(CanPickColor), nameof(Effect), nameof(Color), nameof(Brightness), nameof(Speed), nameof(Effects) }) Notify(name);
        } finally { publishing = false; }
    }
}
