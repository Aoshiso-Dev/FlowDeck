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
    private void Change(DeviceEndpointSetupValues value, bool confirmation = false)
    {
        if (value == Draft.Values) return;
        if (confirmation) Draft.Change(value); else Draft.ChangeInputs(value);
        Refresh(); changed();
    }
    public string Name { get => Draft.Values.Name; set => Change(Draft.Values with { Name = value }); }
    public bool Confirmed { get => Draft.Values.Confirmed; set => Change(Draft.Values with { Confirmed = value }, confirmation: true); }
    public bool NeedsConfirmation => !Draft.ReusesSavedConfirmation;
    public string ConfirmationLabel => Cooling ? "配線と最低・異常時・終了時の出力を確認した" : "照明の対象とLED数を確認した";
    public int Minimum { get => Draft.Values.Safety?.Minimum ?? 100; set { if (Cooling) Change(Draft.Values with { Safety = Draft.Values.Safety! with { Minimum = value } }); } }
    public int Fallback { get => Draft.Values.Safety?.Fallback ?? 100; set { if (Cooling) Change(Draft.Values with { Safety = Draft.Values.Safety! with { Fallback = value } }); } }
    public int ExitDuty { get => Draft.Values.Safety?.Exit ?? 100; set { if (Cooling) Change(Draft.Values with { Safety = Draft.Values.Safety! with { Exit = value } }); } }
    public CoolingExitAction CoolingExit { get => Draft.Values.Safety?.ExitAction ?? CoolingExitAction.FixedDuty; set { if (Cooling) Change(Draft.Values with { Safety = Draft.Values.Safety! with { ExitAction = value } }); } }
    public bool FixedExit => Cooling && CoolingExit == CoolingExitAction.FixedDuty;
    public DeviceExitChoice<CoolingExitAction>[] CoolingExits { get; } = draft.Candidate.Options.CoolingExitActions.Select(a => new DeviceExitChoice<CoolingExitAction>(a,
        a == CoolingExitAction.ReleaseToDevice ? "機器管理へ戻す" : "指定した出力")).ToArray();
    public int LedCount { get => Draft.Values.LedCount ?? 1; set { if (Lighting) Change(Draft.Values with { LedCount = value }); } }
    public int LedMinimum => Draft.Candidate.Options.MinimumLeds ?? 1;
    public int LedMaximum => Draft.Candidate.Options.MaximumLeds ?? 1;
    public bool CanChangeLedCount => Lighting && Draft.Candidate.Options.DetectedLeds is null && LedMinimum < LedMaximum;
    public string Accessory => Draft.Candidate.Options.Accessory ?? "";
    public LightingExitAction LightingExit { get => Draft.Values.LightingExit?.Action ?? LightingExitAction.Off; set { if (Lighting) Change(Draft.Values with {
        LightingExit = new(value, value == LightingExitAction.FixedColor ? Draft.Values.LightingExit?.Color ?? new(52,137,255) : null) }); } }
    public DeviceExitChoice<LightingExitAction>[] LightingExits { get; } = draft.Candidate.Options.LightingExitActions.Select(a => new DeviceExitChoice<LightingExitAction>(a,
        a == LightingExitAction.RestoreOriginal ? "元の設定へ戻す" : a == LightingExitAction.Off ? "消灯" : "指定した色")).ToArray();
    public bool ColorExit => Lighting && LightingExit == LightingExitAction.FixedColor;
    public Color ExitColor
    {
        get { var color = Draft.Values.LightingExit?.Color ?? new(52,137,255); return Color.FromRgb((byte)color.R, (byte)color.G, (byte)color.B); }
        set { if (ColorExit) Change(Draft.Values with { LightingExit = new(LightingExitAction.FixedColor, new(value.R,value.G,value.B)) }); }
    }
    public string Status => Draft.Problem ?? (Draft.ReusesSavedConfirmation ? "保存済みの端子確認を使用します。再確認は不要です" : "差分を確認できます");
    public void Update(DeviceWorkspaceSnapshot snapshot) { Draft.Update(snapshot); Notify(nameof(Status)); Notify(nameof(NeedsConfirmation)); }
}

public sealed partial class DeviceWorkspaceViewModel
{
    public ObservableCollection<DeviceSetupChoice> SetupChoices { get; } = [];
    private DeviceSetupChoice? selectedSetup;
    private DeviceSetupEditor? setupEditor;
    private DeviceEndpointSetupPreview? setupPreview;
    private bool setupBusy, reconcilingSetupChoices;
    private HashSet<EndpointReference> setupCandidateTargets = [];
    public bool SetupSupported => client.SupportsEndpointSetup;
    public bool SetupIdle => !setupBusy && !calibrationBusy && !client.CalibrationRunning && !closing && !dashboardBusy && !monitoringBusy && !lightingBusy && Cooling.All(r => !r.Busy) && client.Connected;
    public DeviceSetupEditor? SetupEditor => setupEditor;
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
        get => selectedSetup;
        set
        {
            if (reconcilingSetupChoices || value?.Target == selectedSetup?.Target || !SetupIdle) return;
            selectedSetup = value; setupEditor = null; setupPreview = null;
            if (value is not null && client.Snapshot is { } snapshot && (snapshot.Configuration.Setup ?? []).Any(c => c.Target == value.Target))
                try { setupEditor = new(new(snapshot, value.Target), SetupChanged); } catch (Exception e) { Notice = e.Message; }
            PublishSetup();
        }
    }
    private void SetupChanged() { setupPreview = null; PublishSetup(); }
    public bool HasSetupEditor => setupEditor is not null;
    public bool HasSetupPreview => setupPreview is not null;
    public string SetupPreviewText => setupPreview is null ? "" : string.Join(Environment.NewLine, setupPreview.Edit.Changes);
    public bool CanPreviewSetup => SetupIdle && SetupEditor?.Draft.Problem is null && SetupEditor is not null;
    public bool HasSavedSetup => selectedSetup is not null && client.Snapshot is { } snapshot && DeviceControlSetupRemoval.Configured(snapshot.Configuration.Configuration, selectedSetup.Target);
    public bool CanRemoveSetup => SetupIdle && HasSavedSetup;
    public bool CanApplySetup => SetupIdle && setupPreview is not null && setupPreview.Edit.Matches(client.Snapshot) &&
        (setupPreview.Edit.Removing ? HasSavedSetup : CanPreviewSetup);
    public string SetupApplyLabel
    {
        get
        {
            var cooling = selectedSetup is not null && client.Snapshot?.Configuration.Configuration.Devices.Devices
                .SingleOrDefault(d => d.Id == selectedSetup.Target.DeviceId)?.Endpoints.SingleOrDefault(e => e.Id == selectedSetup.Target.EndpointId)?.Kind == EndpointKind.Cooling;
            var prefix = cooling || SetupEditor?.Cooling == true ? "この端子の制御を終了して" : "この端子の照明を終了して";
            return prefix + (setupPreview?.Edit.Removing == true ? "設定を解除" : "設定を保存");
        }
    }
    public ICommand PreviewSetup => new AsyncCommand(_ => PreviewSetupAsync(), _ => CanPreviewSetup, e => Notice = e.Message);
    public ICommand PreviewSetupRemoval => new AsyncCommand(_ => PreviewSetupRemovalAsync(), _ => CanRemoveSetup, e => Notice = e.Message);
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
    public async Task OpenLightingSetupAsync(DeviceLightRow row)
    {
        if (!CanOpenLightingSetup(row)) return;
        if (client.Snapshot is { } snapshot && DeviceDetectedLightingSetup.Create(snapshot, row.Target) is { } detected)
        {
            var edit = detected.Capture(); setupBusy = true; ClearConfigurationPreviews(); Render();
            try
            {
                var preview = await client.PreviewSetupAsync(edit);
                var result = await client.ApplySetupAsync(preview);
                Notice = result.Outcome == Outcome.Confirmed ? "検出したLED数で登録しました。色・効果を選んで適用できます" : result.Message;
            }
            finally { setupBusy = false; Render(); }
            return;
        }
        SelectedSetup = SetupChoices.Single(c => c.Target == row.Target);
        SetupExpanded = true;
        SelectedPage = 3;
    }
    private bool CanOpenLightingSetup(DeviceLightRow row) => SetupSupported && SetupIdle && Lights.Contains(row) && row.NeedsSetup &&
        client.HasFreshState && setupCandidateTargets.Contains(row.Target) && SetupChoices.Any(c => c.Target == row.Target);
    private void PublishSetup()
    {
        foreach (var name in new[] { nameof(SetupSupported), nameof(SetupIdle), nameof(SetupEditor), nameof(SelectedSetup), nameof(HasSetupEditor),
            nameof(HasSetupPreview), nameof(SetupPreviewText), nameof(CanPreviewSetup), nameof(CanApplySetup), nameof(HasSavedSetup),
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
            if (device is null || endpoint is null) return null;
            var registered = snapshot.Configuration.Configuration.Devices.Devices.SingleOrDefault(d => d.Id == device.Device.Id);
            return new DeviceSetupChoice(candidate.Target, (registered?.Name ?? DeviceMonitoringDraft.DisplayDeviceName(device.Device, snapshot.State.Inventory)) + " / " +
                (registered?.Endpoints.SingleOrDefault(e => e.Id == endpoint.Id)?.Name ?? endpoint.Name));
        }).OfType<DeviceSetupChoice>().ToList();
        var config = snapshot.Configuration.Configuration;
        foreach (var target in config.Cooling.Endpoints.Select(e => e.Target).Concat((config.Lighting.Endpoints ?? []).Select(e => e.Target)))
        {
            if (choices.Any(c => c.Target == target)) continue;
            var registered = config.Devices.Devices.Single(d => d.Id == target.DeviceId);
            var endpoint = registered.Endpoints.Single(e => e.Id == target.EndpointId);
            choices.Add(new(target, registered.Name + " / " + endpoint.Name + "（保存済み）"));
        }
        if (selectedSetup is not null && choices.All(c => c.Target != selectedSetup.Target)) choices.Add(selectedSetup);
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
        try
        {
            await client.RefreshAsync(true); var target = selectedSetup?.Target; setupPreview = null; setupEditor = null;
            if (target is not null && (client.Snapshot!.Configuration.Setup ?? []).Any(c => c.Target == target))
                setupEditor = new(new(client.Snapshot, target), SetupChanged);
        }
        finally { setupBusy = false; Render(); }
    }
    public async Task PreviewSetupAsync()
    {
        Render(); if (!CanPreviewSetup) return;
        var edit = setupEditor!.Draft.Capture(); setupBusy = true; ClearConfigurationPreviews(); Render();
        try { setupPreview = await client.PreviewSetupAsync(edit); Notice = "変更内容を確認してください"; }
        finally { setupBusy = false; Render(); }
    }
    public async Task ApplySetupAsync()
    {
        Render(); if (!CanApplySetup) return;
        var preview = setupPreview!; setupBusy = true; setupPreview = null; Render();
        try
        {
            var result = await client.ApplySetupAsync(preview); Notice = result.Message;
            if (result.Outcome == Outcome.Confirmed) { setupEditor = null; selectedSetup = null; }
        }
        finally { setupBusy = false; Render(); }
    }
    public async Task PreviewSetupRemovalAsync()
    {
        Render(); if (!CanRemoveSetup) return;
        var edit = DeviceControlSetupRemoval.Capture(client.Snapshot!, selectedSetup!.Target);
        setupBusy = true; ClearConfigurationPreviews(); Render();
        try { setupPreview = await client.PreviewSetupAsync(edit); Notice = "解除する設定を確認してください"; }
        finally { setupBusy = false; Render(); }
    }
}
