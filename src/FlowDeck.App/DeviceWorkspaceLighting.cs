using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed record DeviceLightChoice(string Id, string Name, long Revision = 0);
public sealed class DeviceLightRow(EndpointReference target, Action selectionChanged) : Observable
{
    public EndpointReference Target { get; } = target;
    private bool selected;
    public bool Selected { get => selected; set { if (Set(ref selected, value)) selectionChanged(); } }
    public string Name { get; private set; } = "";
    public string Status { get; private set; } = "";
    public string Result { get; private set; } = "";
    public string Details { get; private set; } = "";
    public bool NeedsSetup { get; private set; }
    public bool Ready { get; private set; }
    public string SetupLabel { get; private set; } = "照明端子を設定";
    public void Update(string name, string status, bool needsSetup = false, bool ready = false, string setupLabel = "照明端子を設定")
    {
        if (Name != name) { Name = name; Notify(nameof(Name)); }
        if (Status != status) { Status = status; Notify(nameof(Status)); }
        if (NeedsSetup != needsSetup) { NeedsSetup = needsSetup; Notify(nameof(NeedsSetup)); }
        if (Ready != ready) { Ready = ready; Notify(nameof(Ready)); }
        if (SetupLabel != setupLabel) { SetupLabel = setupLabel; Notify(nameof(SetupLabel)); }
    }
    public void Report(DeviceTargetResult result)
    {
        Result = result.Outcome switch { Outcome.Confirmed => "確認済み", Outcome.WrittenUnverified => "送信済み・点灯未確認", Outcome.Rejected => "拒否", Outcome.Partial => "一部未確認", _ => "結果未確認" };
        Details = result.Message;
        if (result.Persistence is { State: DevicePolicySaveState.SessionOnly } transient) { Result += " · 接続中のみ"; Details += " / " + transient.Message; }
        else if (result.Persistence is { State: not DevicePolicySaveState.Saved } save) { Result += " · 保存未確認"; Details += " / " + save.Message; }
        Notify(nameof(Result)); Notify(nameof(Details));
    }
}

public sealed partial class DeviceWorkspaceViewModel
{
    public DeviceLightingDraft LightingDraft { get; } = new();
    public ObservableCollection<DeviceLightRow> Lights { get; } = [];
    public ObservableCollection<DeviceLightChoice> LightScenes { get; } = [];
    public ObservableCollection<DeviceLightChoice> LightGroups { get; } = [];
    public ObservableCollection<DeviceLightChoice> LightEffects { get; } = [];
    private bool lightingBusy, selectingLights, lightingConnected;
    private LightingProgram? displayedProgram;
    private readonly Stopwatch previewClock = Stopwatch.StartNew();
    private string lightSceneName = "", lightGroupName = "";
    private DeviceLightChoice? selectedScene, selectedGroup;
    public string LightSceneName { get => lightSceneName; set => Set(ref lightSceneName, value); }
    public string LightGroupName { get => lightGroupName; set => Set(ref lightGroupName, value); }
    public DeviceLightChoice? SelectedLightScene { get => selectedScene; set => Set(ref selectedScene, value); }
    public DeviceLightChoice? SelectedLightGroup { get => selectedGroup; set => Set(ref selectedGroup, value); }
    public bool LightingIdle => !lightingBusy && !identificationBusy && !setupBusy && !closing && lightingConnected;
    public bool CanApplyLighting => LightingIdle && LightingDraft.CanApply;
    public bool CanAdjustLighting => LightingIdle && LightingDraft.CanAdjust;
    public string LightEditStatus => LightingDraft.Problem ?? (LightingDraft.Mixed ? "設定が混在しています" : LightingDraft.Dirty ? "未適用" : "現在の設定");
    public string LightSelectionSummary => $"{LightingDraft.Targets.Length} 個の照明" + (DeviceLighting.Animated(LightingDraft.Program) && LightingDraft.Targets.Length > 1 ? " · 同期" : "");
    public string LightEffect { get => LightingDraft.Program.EffectId; set {
        if (value is null || value == LightEffect) return; var p = LightingDraft.Program;
        if (LightingDraft.NativeEffects.SingleOrDefault(e => e.Id == value) is { } native) ChangeLight(DeviceNativeLighting.Create(native, p));
        else if (value is "fixed" or "off" or "breathing" or "color-cycle") ChangeLight(new(value, value == "off" ? [] : [p.Colors.FirstOrDefault() ?? new(52, 137, 255)], Math.Clamp(p.Speed, 0, 4), Brightness: p.Brightness));
    } }
    public Color LightColor
    {
        get { var p = LightingDraft.Program; var c = (p.LedColors ?? p.Colors).ElementAtOrDefault(SelectedLightColor) ?? new(52, 137, 255); return Color.FromRgb((byte)c.R, (byte)c.G, (byte)c.B); }
        set { if (publishingLightProgram) return; var p = LightingDraft.Program; var color = new RgbColor(value.R, value.G, value.B);
            if (p.EffectId.StartsWith("native/", StringComparison.Ordinal)) {
                if (!CanPickLightColor) return; var colors = (p.LedColors ?? p.Colors).ToArray(); if (SelectedLightColor >= colors.Length) return;
                colors[SelectedLightColor] = color; ChangeLight(p.LedColors is null ? p with { Colors = colors } : p with { LedColors = colors });
            } else if (DeviceLighting.FrameProgram(p)) ChangeLight(p with { EffectId = p.EffectId == "off" ? "fixed" : p.EffectId, Colors = [color] }); }
    }
    public int LightBrightness { get => LightingDraft.Program.Brightness; set { if (!publishingLightProgram && CanEditLightBrightness) ChangeLight(LightingDraft.Program with { Brightness = Math.Clamp(value, 0, 100) }); } }
    public int LightSpeed { get => LightingDraft.Program.Speed; set { if (!publishingLightProgram && LightHasSpeed) ChangeLight(LightingDraft.Program with { Speed = Math.Clamp(value, LightSpeedMinimum, LightSpeedMaximum) }); } }
    public bool LightAnimated => DeviceLighting.Animated(LightingDraft.Program);
    public bool CanPickLightColor => LightingIdle && (NativeLightEffect is { Color: true } || DeviceLighting.FrameProgram(LightingDraft.Program) && (LightEffect is "fixed" or "breathing"));
    public Brush LightPreviewBrush { get { var p = LightingDraft.Program; var c = DeviceLighting.FrameProgram(p) ? DeviceLighting.Frame(p, previewClock.Elapsed.TotalSeconds) :
                NativeLightEffect is { Color: true } ? (p.LedColors ?? p.Colors).ElementAtOrDefault(SelectedLightColor)?.Scale(p.Brightness / 100d) : null;
            return c is null ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb((byte)c.R, (byte)c.G, (byte)c.B)); } }
    public void TickLightingPreview() { if (SelectedPage == 2) Notify(nameof(LightPreviewBrush)); }
    private void ChangeLight(LightingProgram program)
    {
        if (lightingBusy || publishingLightProgram) return;
        LightingDraft.SetProgram(program); PublishLightingEditor(); CommandManager.InvalidateRequerySuggested();
    }
    private void PublishLightingEditor()
    {
        publishingLightProgram = true;
        try {
        var program = LightingDraft.Program;
        var programChanged = displayedProgram is null || !DeviceLighting.SameProgram(displayedProgram, program);
        if (programChanged)
        {
            displayedProgram = program;
            foreach (var name in new[] { nameof(LightEffect), nameof(LightColor), nameof(LightBrightness), nameof(LightSpeed), nameof(LightAnimated), nameof(CanPickLightColor) }) Notify(name);
        }
        foreach (var name in new[] { nameof(LightEditStatus), nameof(LightSelectionSummary), nameof(LightingIdle), nameof(CanApplyLighting), nameof(CanAdjustLighting), nameof(CanPickLightColor) }) Notify(name);
        PublishLightNative(programChanged); PublishLightLevels(); PublishIdentificationControls();
        } finally { publishingLightProgram = false; }
    }
    private void LightingSelectionChanged()
    {
        if (selectingLights) return;
        LightingDraft.Select(Lights.Where(l => l.Selected).Select(l => l.Target)); PublishLightingEditor();
        if (client.Snapshot is { } snapshot) RenderLighting(snapshot);
    }
    public void SelectLights(IEnumerable<EndpointReference> targets)
    {
        var ids = targets.ToHashSet();
        if (ids.Any(t => Lights.All(l => l.Target != t))) throw new InvalidOperationException("保存された照明対象を再取得してください");
        selectingLights = true;
        try { foreach (var row in Lights) row.Selected = ids.Contains(row.Target); }
        finally { selectingLights = false; }
        LightingSelectionChanged();
    }
    private void RenderLighting(DeviceWorkspaceSnapshot snapshot)
    {
        var config = snapshot.Configuration.Configuration;
        lightingConnected = snapshot.Connected && snapshot.ElapsedSeconds is >= 0 and < 3 && !snapshot.State.Releasing && !snapshot.State.ConfigurationChanging;
        LightingDraft.Update(snapshot.State, snapshot.Configuration.Generation, config.Lighting, snapshot.ElapsedSeconds, snapshot.Connected);
        var targets = config.Devices.Devices.SelectMany(d => d.Endpoints.Where(e => e.Kind == EndpointKind.Lighting).Select(e => new EndpointReference(d.Id, e.Id)))
            .Concat(snapshot.State.Inventory.Devices.SelectMany(d => d.Device.Endpoints.Where(e => e.Kind == EndpointKind.Lighting).Select(e => new EndpointReference(d.Device.Id, e.Id))))
            .Distinct().ToArray();
        foreach (var target in targets) if (Lights.All(l => l.Target != target)) Lights.Add(new(target, LightingSelectionChanged));
        foreach (var row in Lights)
        {
            var device = snapshot.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == row.Target.DeviceId);
            var registration = config.Devices.Devices.SingleOrDefault(d => d.Id == row.Target.DeviceId);
            var endpoint = device?.Device.Endpoints.SingleOrDefault(e => e.Id == row.Target.EndpointId);
            var name = config.Lighting.Names.SingleOrDefault(n => n.Target == row.Target)?.Name ?? registration?.Endpoints.SingleOrDefault(e => e.Id == row.Target.EndpointId)?.Name ?? endpoint?.Name ?? row.Target.EndpointId;
            var execution = snapshot.State.Endpoints.SingleOrDefault(e => e.Target == row.Target);
            var capability = endpoint?.Capabilities.SingleOrDefault(c => c.Feature == DeviceFeature.FixedColor);
            var fresh = snapshot.Connected && device?.Connected == true && snapshot.ElapsedSeconds is >= 0 and < 3;
            var needsSetup = fresh && capability is { Support: ImplementationSupport.Supported, Availability: DeviceAvailability.NeedsSetup };
            var detectedSetup = needsSetup && SetupSupported ? DeviceDetectedLightingSetup.Describe(snapshot, row.Target) : null;
            var status = !snapshot.Connected ? "サービス未接続" : device?.Connected != true ? "未接続" : snapshot.ElapsedSeconds >= 3 ? "更新停止" :
                DeviceLightingIdentification.Running(execution?.Identification) ? execution!.Identification!.Message :
                execution?.Lighting?.Fault ?? (execution?.Lighting?.Running == true ? "同期中" : capability?.Usable == true ? "使用可" : capability?.Reason ??
                    (needsSetup ? "照明端子の設定が必要です" : "この照明は操作できません"));
            if (registration is null && device?.Device.Provider == "ene-memory" && capability?.Usable == true && execution?.Lighting?.Running != true) status = "使用可 · 今回の接続中のみ";
            row.Update((registration?.Name ?? device?.Device.Name ?? "未接続の機器") + " / " + name,
                detectedSetup is null ? status : DeviceDetectedLightingSetup.Description(detectedSetup), needsSetup,
                fresh && capability?.Usable == true && !DeviceLightingIdentification.Running(execution?.Identification),
                detectedSetup is null ? "照明端子を設定" : "照明を使う");
        }
        var effects = new[] { new DeviceLightChoice("fixed", "固定"), new("breathing", "呼吸"), new("color-cycle", "カラーサイクル"), new("off", "消灯") }
            .Where(e => LightingDraft.Targets.Length == 0 || LightingDraft.Supports(e.Id) || e.Id == LightEffect)
            .Concat(LightingDraft.NativeEffects.Select(e => new DeviceLightChoice(e.Id, e.Name + " · 機器内蔵"))).ToList();
        if (!effects.Any(e => e.Id == LightEffect)) effects.Add(new(LightEffect, LightEffect + " · 利用不可"));
        Reconcile(LightEffects, effects.ToArray());
        Reconcile(LightScenes, config.Lighting.Scenes.Select(s => new DeviceLightChoice(s.Id, s.Name, s.Revision)).ToArray());
        Reconcile(LightGroups, config.Lighting.Groups.Select(g => new DeviceLightChoice(g.Id, g.Name, g.Revision)).ToArray());
        if (!personalizationBusy) LightLibraryEditor?.Update(snapshot); PublishLightLibrary();
        LightLevelDraft.Update(snapshot, LightingDraft.Targets);
        PublishLightingEditor();
        RenderLightPersonalization(snapshot);
    }
    public ICommand SelectAllLights => new AsyncCommand(p => { SelectLights(p?.ToString() == "none" ? [] : Lights.Where(l => l.Ready).Select(l => l.Target)); return Task.CompletedTask; }, _ => LightingIdle);
    public ICommand ApplyLights => new AsyncCommand(_ => ApplyLightsAsync(), _ => CanApplyLighting, e => Notice = e.Message);
    public ICommand AdjustLights => new AsyncCommand(p => ApplyLightsAsync(p?.ToString() == "restore" ? LightingAdjustmentKind.Restore : LightingAdjustmentKind.Off), _ => CanAdjustLighting, e => Notice = e.Message);
    public ICommand StopLights => new AsyncCommand(_ => RunLightingAsync(LightingDraft.CaptureStop()), _ => LightingIdle && LightingDraft.CanStop, e => Notice = e.Message);
    public ICommand ReloadLights => new AsyncCommand(_ => { LightingDraft.Reset(); PublishLightingEditor(); return Task.CompletedTask; }, _ => LightingIdle);
    public async Task ApplyLightsAsync(LightingAdjustmentKind? adjustment = null)
    {
        Render();
        await RunLightingAsync(adjustment is null ? LightingDraft.Capture() : LightingDraft.CaptureAdjustment(adjustment.Value));
    }
    private async Task RunLightingAsync(DeviceLightingEdit edit)
    {
        if (!LightingIdle) return; lightingBusy = true; PublishLightingEditor();
        DeviceOperationResult? applied = null;
        try
        {
            var result = await client.ApplyLightingAsync(edit);
            if (edit.Operation is DeviceOperation.SetLighting or DeviceOperation.ApplyLightingPreset) applied = result;
            foreach (var target in result.Targets) Lights.SingleOrDefault(l => l.Target == target.Target)?.Report(target);
            if (result.Outcome is Outcome.Confirmed or Outcome.WrittenUnverified) LightingDraft.Reset();
            Notice = result.Message; Render();
        }
        finally { lightingBusy = false; Render(); }
        if (applied is not null) await RememberAppliedLightColorsAsync(applied);
    }
    private bool LightingTargetsRegistered
    {
        get { var registrations = client.Snapshot?.Configuration.Configuration.Devices; return LightingDraft.Targets.All(t =>
            registrations?.Devices.Any(d => d.Id == t.DeviceId && d.Endpoints.Any(e => e.Id == t.EndpointId)) == true); }
    }
    public ICommand SaveLightGroup => new AsyncCommand(_ => SaveLightLibraryAsync(false), _ => LightingIdle && LightingTargetsRegistered && LightingDraft.Targets.Length > 0 && DeviceConfigurationRules.Text(LightGroupName, 80), e => Notice = e.Message);
    public ICommand SaveLightScene => new AsyncCommand(_ => SaveLightLibraryAsync(true), _ => CanAdjustLighting && LightingTargetsRegistered && DeviceConfigurationRules.Text(LightSceneName, 80), e => Notice = e.Message);
    public ICommand ChooseLightGroup => new AsyncCommand(_ => { var group = client.Snapshot!.Configuration.Configuration.Lighting.Groups.Single(g => g.Id == SelectedLightGroup!.Id && g.Revision == SelectedLightGroup.Revision); SelectLights(group.Targets); return Task.CompletedTask; }, _ => LightingIdle && SelectedLightGroup is not null && LightGroups.Contains(SelectedLightGroup), e => Notice = e.Message);
    public ICommand ApplyLightScene => new AsyncCommand(_ => RecallLightSceneAsync(), _ => LightingIdle && SelectedLightScene is not null && LightScenes.Contains(SelectedLightScene), e => Notice = e.Message);
    public Task RecallLightSceneAsync()
    {
        if (SelectedLightScene is null || !LightScenes.Contains(SelectedLightScene)) throw new InvalidOperationException("シーンを選択し直してください");
        return RunLightingAsync(DeviceWorkspaceClient.Preset(client.Snapshot!, LightingPresetKind.Scene, SelectedLightScene.Id));
    }
    public async Task SaveLightLibraryAsync(bool scene)
    {
        var snapshot = client.Snapshot ?? throw new IOException("サービスへ接続されていません");
        var id = Guid.NewGuid().ToString("N"); var targets = LightingDraft.Targets;
        var request = scene ? new LightingLibraryRequest(LightingLibraryOperation.SaveScene, snapshot.Configuration.Generation, id, 0,
                Scene: DeviceLightingSceneCapture.Create(id, LightSceneName.Trim(), snapshot.Configuration.Configuration.Lighting, targets)) :
            new(LightingLibraryOperation.SaveGroup, snapshot.Configuration.Generation, id, 0, Group: new(id, LightGroupName.Trim(), EndpointKind.Lighting, targets));
        lightingBusy = true; PublishLightingEditor();
        try
        {
            var result = await client.SaveLightingAsync(request); Notice = result.Message; Render();
            if (scene) SelectedLightScene = LightScenes.SingleOrDefault(s => s.Id == id);
            else SelectedLightGroup = LightGroups.SingleOrDefault(g => g.Id == id);
        }
        finally { lightingBusy = false; PublishLightingEditor(); }
    }
}
