using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed record DeviceSetupChoice(EndpointReference Target, string Name);
public sealed record DeviceExitChoice<T>(T Value, string Name);
public sealed class DeviceSetupEditor(DeviceEndpointSetupDraft draft, Action changed) : Observable
{
    public DeviceEndpointSetupDraft Draft { get; } = draft;
    public bool Cooling => Draft.Cooling;
    public bool Lighting => !Cooling;
    private void Change(DeviceEndpointSetupValues value)
    {
        if (value == Draft.Values) return;
        Draft.ChangeInputs(value);
        Refresh(); changed();
    }
    public string Name { get => Draft.Values.Name; set => Change(Draft.Values with { Name = value }); }
    public int Minimum { get => Draft.Values.Safety?.Minimum ?? 100; set { if (Cooling) Change(Draft.Values with { Safety = Draft.Values.Safety! with { Minimum = value } }); } }
    public int Fallback { get => Draft.Values.Safety?.Fallback ?? 100; set { if (Cooling) Change(Draft.Values with { Safety = Draft.Values.Safety! with { Fallback = value } }); } }
    public int ExitDuty { get => Draft.Values.Safety?.Exit ?? 100; set { if (Cooling) Change(Draft.Values with { Safety = Draft.Values.Safety! with { Exit = value } }); } }
    public string MinimumText { get => Draft.Values.Safety?.Minimum?.ToString() ?? ""; set { if (Cooling) Change(Draft.Values with { Safety = Draft.Values.Safety! with { Minimum = Number(value) } }); } }
    public string FallbackText { get => Draft.Values.Safety?.Fallback?.ToString() ?? ""; set { if (Cooling) Change(Draft.Values with { Safety = Draft.Values.Safety! with { Fallback = Number(value) } }); } }
    public string ExitDutyText { get => Draft.Values.Safety?.Exit?.ToString() ?? ""; set { if (Cooling) Change(Draft.Values with { Safety = Draft.Values.Safety! with { Exit = Number(value) } }); } }
    public CoolingExitAction CoolingExit { get => Draft.Values.Safety?.ExitAction ?? CoolingExitAction.FixedDuty; set { if (Cooling) Change(Draft.Values with { Safety = Draft.Values.Safety! with { ExitAction = value } }); } }
    public bool FixedExit => Cooling && CoolingExit == CoolingExitAction.FixedDuty;
    public DeviceExitChoice<CoolingExitAction>[] CoolingExits { get; } = draft.Candidate.Options.CoolingExitActions.Select(a => new DeviceExitChoice<CoolingExitAction>(a,
        a == CoolingExitAction.ReleaseToDevice ? "機器管理" : "指定出力")).ToArray();
    public int LedCount { get => Draft.Values.LedCount ?? 1; set { if (Lighting) Change(Draft.Values with { LedCount = value }); } }
    public string LedCountText { get => Draft.Values.LedCount?.ToString() ?? ""; set { if (Lighting && CanChangeLedCount) Change(Draft.Values with { LedCount = Number(value) }); } }
    private static int? Number(string value) => int.TryParse(value, out var number) ? number : null;
    public int LedMinimum => Draft.Candidate.Options.MinimumLeds ?? 1;
    public int LedMaximum => Draft.Candidate.Options.MaximumLeds ?? 1;
    public bool CanChangeLedCount => Lighting && Draft.Candidate.Options.DetectedLeds is null && LedMinimum < LedMaximum;
    public string Accessory => Draft.Candidate.Options.Accessory ?? "";
    public string LedCountNotice => Lighting && Draft.Candidate.Options.DetectedLeds is null && LedMinimum == LedMaximum
        ? "機器設定のLED数 · 接続未確認" : "";
    public LightingExitAction LightingExit { get => Draft.Values.LightingExit?.Action ?? LightingExitAction.Off; set { if (Lighting) Change(Draft.Values with {
        LightingExit = new(value, value == LightingExitAction.FixedColor ? Draft.Values.LightingExit?.Color ?? new(52,137,255) : null) }); } }
    public DeviceExitChoice<LightingExitAction>[] LightingExits { get; } = draft.Candidate.Options.LightingExitActions.Select(a => new DeviceExitChoice<LightingExitAction>(a,
        a == LightingExitAction.RestoreOriginal ? "元の設定" : a == LightingExitAction.Off ? "消灯" : "指定色")).ToArray();
    public bool ColorExit => Lighting && LightingExit == LightingExitAction.FixedColor;
    public Color ExitColor
    {
        get { var color = Draft.Values.LightingExit?.Color ?? new(52,137,255); return Color.FromRgb((byte)color.R, (byte)color.G, (byte)color.B); }
        set { if (ColorExit) Change(Draft.Values with { LightingExit = new(LightingExitAction.FixedColor, new(value.R,value.G,value.B)) }); }
    }
    public string Status => Draft.Problem ?? "";
    public void Update(DeviceWorkspaceSnapshot snapshot) { Draft.Update(snapshot); Notify(nameof(Status)); }
}

public sealed partial class DeviceWorkspaceViewModel
{
    public ObservableCollection<DeviceSetupChoice> SetupChoices { get; } = [];
    private DeviceSetupChoice? selectedSetup;
    private DeviceSetupEditor? setupEditor;
    private bool setupBusy, reconcilingSetupChoices;
    private HashSet<EndpointReference> setupCandidateTargets = [];
    public bool SetupSupported => client.SupportsEndpointSetup;
    public bool SetupIdle => !setupBusy && !calibrationBusy && !client.CalibrationRunning && !closing && !dashboardBusy && !monitoringBusy && !lightingBusy && Cooling.All(r => !r.Busy) && client.Connected;
    public DeviceSetupEditor? SetupEditor => SelectedSetup is not null ? setupEditor : null;
    public string SetupUsage
    {
        get
        {
            if (client.Snapshot is not { } snapshot) return "";
            var config = snapshot.Configuration.Configuration;
            var usage = DeviceControlCapacity.Count(config.Devices, config.Cooling, config.Lighting);
            return $"制御設定　冷却 {usage.Cooling} / {DeviceControlCapacity.MaximumCooling}　照明 {usage.Lighting} / {DeviceControlCapacity.MaximumLighting}　LED {usage.Leds:N0} / {DeviceControlCapacity.MaximumLeds:N0}" +
                (usage.UnknownLedCounts == 0 ? "" : $"（LED数未設定 {usage.UnknownLedCounts}端子）");
        }
    }
    public DeviceSetupChoice? SelectedSetup
    {
        get => selectedSetup is not null && SetupChoices.Contains(selectedSetup) ? selectedSetup : null;
        set
        {
            if (reconcilingSetupChoices || value?.Target == selectedSetup?.Target || !SetupIdle) return;
            selectedSetup = value; setupEditor = null;
            if (value is not null && client.Snapshot is { } snapshot && (snapshot.Configuration.Setup ?? []).Any(c => c.Target == value.Target))
                try { setupEditor = new(new(snapshot, value.Target), SetupChanged); } catch (Exception e) { Notice = e.Message; }
            PublishSetup();
        }
    }
    private void SetupChanged() => PublishSetup();
    public bool HasSetupEditor => SetupEditor is not null;
    public bool HasSavedSetup => SelectedSetup is { } selected && client.Snapshot is { } snapshot && DeviceControlSetupRemoval.Configured(snapshot.Configuration.Configuration, selected.Target);
    public bool CanRemoveSetup => SetupIdle && HasSavedSetup;
    public bool CanApplySetup => SetupIdle && SetupEditor is not null && SetupEditor.Draft.Problem is null;
    public string SetupApplyLabel
    {
        get
        {
            var cooling = selectedSetup is not null && client.Snapshot?.Configuration.Configuration.Devices.Devices
                .SingleOrDefault(d => d.Id == selectedSetup.Target.DeviceId)?.Endpoints.SingleOrDefault(e => e.Id == selectedSetup.Target.EndpointId)?.Kind == EndpointKind.Cooling;
            var prefix = cooling || SetupEditor?.Cooling == true ? "制御終了・" : "照明終了・";
            return client.Snapshot?.State.Endpoints.Any(e => e.Target == selectedSetup?.Target && e.Owned) == true
                ? prefix + "保存" : "保存";
        }
    }
    public ICommand RemoveSetup => new AsyncCommand(_ => RemoveSetupAsync(), _ => CanRemoveSetup, e => Notice = e.Message);
    public ICommand ApplySetup => new AsyncCommand(_ => ApplySetupAsync(), _ => CanApplySetup, e => Notice = e.Message);
    public ICommand ReloadSetup => new AsyncCommand(_ => ReloadSetupAsync(), _ => SetupIdle, e => Notice = e.Message);
    public ICommand OpenCoolingSetup => new AsyncCommand(p =>
    {
        if (p is DeviceCoolingRow row && CanOpenCoolingSetup(row))
        {
            SelectedSetup = SetupChoices.Single(c => c.Target == row.Draft.Target);
            SetupExpanded = true;
            SelectedPage = 3;
        }
        return Task.CompletedTask;
    }, p => p is DeviceCoolingRow row && CanOpenCoolingSetup(row), e => Notice = e.Message);
    private bool CanOpenCoolingSetup(DeviceCoolingRow row) => SetupSupported && SetupIdle && Cooling.Contains(row) && row.NeedsSetup &&
        client.HasFreshState && setupCandidateTargets.Contains(row.Draft.Target) && SetupChoices.Any(c => c.Target == row.Draft.Target);
    public ICommand OpenLightingSetup => new AsyncCommand(p => p is DeviceLightRow row ? OpenLightingSetupAsync(row) : Task.CompletedTask,
        p => p is DeviceLightRow row && CanOpenLightingSetup(row), e => Notice = e.Message);
    public Task OpenLightingSetupAsync(DeviceLightRow row)
    {
        if (!CanOpenLightingSetup(row)) return Task.CompletedTask;
        SelectedSetup = SetupChoices.Single(c => c.Target == row.Target);
        SetupExpanded = true;
        SelectedPage = 3;
        return Task.CompletedTask;
    }
    private bool CanOpenLightingSetup(DeviceLightRow row) => SetupSupported && SetupIdle && Lights.Contains(row) && row.NeedsSetup &&
        client.HasFreshState && setupCandidateTargets.Contains(row.Target) && SetupChoices.Any(c => c.Target == row.Target);
    private void PublishSetup()
    {
        foreach (var name in new[] { nameof(SetupSupported), nameof(SetupIdle), nameof(SetupEditor), nameof(SelectedSetup), nameof(HasSetupEditor),
            nameof(CanApplySetup), nameof(HasSavedSetup),
            nameof(CanRemoveSetup), nameof(SetupApplyLabel), nameof(SetupUsage) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
    private void RenderSetup(DeviceWorkspaceSnapshot snapshot)
    {
        setupCandidateTargets = (snapshot.Configuration.Setup ?? []).Select(c => c.Target).ToHashSet();
        var choices = (snapshot.Configuration.Setup ?? []).Select(candidate =>
        {
            var device = snapshot.State.Inventory.Devices.SingleOrDefault(d => d.Device.Id == candidate.Target.DeviceId && d.Connected && d.Generation == candidate.Generation);
            var endpoint = device?.Device.Endpoints.SingleOrDefault(e => e.Id == candidate.Target.EndpointId);
            if (!snapshot.Connected || device is null || endpoint is null) return null;
            var registered = snapshot.Configuration.Configuration.Devices.Devices.SingleOrDefault(d => d.Id == device.Device.Id);
            return new DeviceSetupChoice(candidate.Target, (registered?.Name ?? DeviceMonitoringDraft.DisplayDeviceName(device.Device, snapshot.State.Inventory)) + " / " +
                (registered?.Endpoints.SingleOrDefault(e => e.Id == endpoint.Id)?.Name ?? endpoint.Name));
        }).OfType<DeviceSetupChoice>().ToList();
        var config = snapshot.Configuration.Configuration;
        foreach (var target in config.Cooling.Endpoints.Select(e => e.Target).Concat((config.Lighting.Endpoints ?? []).Select(e => e.Target)))
        {
            if (choices.Any(c => c.Target == target)) continue;
            if (!snapshot.Connected || !snapshot.State.Inventory.Devices.Any(d => d.Connected && d.Device.Id == target.DeviceId &&
                d.Device.Endpoints.Any(e => e.Id == target.EndpointId))) continue;
            var registered = config.Devices.Devices.Single(d => d.Id == target.DeviceId);
            var endpoint = registered.Endpoints.Single(e => e.Id == target.EndpointId);
            choices.Add(new(target, registered.Name + " / " + endpoint.Name + "（保存済み）"));
        }
        // Polling refreshes inventory before setup candidates. A reconnected endpoint may therefore
        // have a newer generation than its candidate; show its retained draft as a conflict until reload.
        if (selectedSetup is not null && choices.All(c => c.Target != selectedSetup.Target) && snapshot.Connected &&
            snapshot.State.Inventory.Devices.Any(d => d.Connected && d.Device.Id == selectedSetup.Target.DeviceId &&
                d.Device.Endpoints.Any(e => e.Id == selectedSetup.Target.EndpointId))) choices.Add(selectedSetup);
        // Preserve the selected record while its draft is open, even after a name changes elsewhere.
        // WPF temporarily clears SelectedItem when a changed row replaces its old index.
        // That collection event must not discard the user's draft; publish the retained choice after reconciliation.
        reconcilingSetupChoices = true;
        try { Reconcile(SetupChoices, choices.Select(c => c.Target == selectedSetup?.Target ? selectedSetup! : c).ToArray()); }
        finally { reconcilingSetupChoices = false; }
        setupEditor?.Update(snapshot); PublishSetup();
    }
    public async Task ReloadSetupAsync()
    {
        if (!SetupIdle) return; setupBusy = true; Render();
        using var status = BeginStatus("機器状態を再取得中");
        try
        {
            await client.RefreshAsync(true); var target = selectedSetup?.Target; setupEditor = null;
            if (target is not null && (client.Snapshot!.Configuration.Setup ?? []).Any(c => c.Target == target))
                setupEditor = new(new(client.Snapshot, target), SetupChanged);
            ReportStatus("機器状態を再取得済み");
        }
        finally { setupBusy = false; Render(); }
    }
    public async Task ApplySetupAsync()
    {
        Render(); if (!CanApplySetup) return;
        var edit = setupEditor!.Draft.Capture(); setupBusy = true; ClearConfigurationPreviews(); Render();
        using var status = BeginStatus("端子設定を保存中");
        try
        {
            var preview = await client.PreviewSetupAsync(edit);
            var result = await client.ApplySetupAsync(preview); ReportResult(result.Message, result.Outcome);
            if (result.Outcome == Outcome.Confirmed) { setupEditor = null; selectedSetup = null; }
        }
        finally { setupBusy = false; Render(); }
    }
    public async Task RemoveSetupAsync()
    {
        Render(); if (!CanRemoveSetup) return;
        var edit = DeviceControlSetupRemoval.Capture(client.Snapshot!, selectedSetup!.Target);
        setupBusy = true; ClearConfigurationPreviews(); Render();
        using var status = BeginStatus("端子設定を解除中");
        try
        {
            var preview = await client.PreviewSetupAsync(edit);
            var result = await client.ApplySetupAsync(preview); ReportResult(result.Message, result.Outcome);
            if (result.Outcome == Outcome.Confirmed) { setupEditor = null; selectedSetup = null; }
        }
        finally { setupBusy = false; Render(); }
    }
}
