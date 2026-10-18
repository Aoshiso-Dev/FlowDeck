using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private bool personalizationBusy, reconcilingAliasTargets;
    public ColorPalette? LightSavedColors { get; private set; }
    public bool LightPersonalizationSupported => client.SupportsLightingPersonalization;
    public ObservableCollection<DeviceLightRow> LightAliasTargets { get; } = [];
    private DeviceLightRow? lightAliasTarget;
    public DeviceLightAliasDraft? LightAliasDraft { get; private set; }
    public DeviceLightRow? LightAliasTarget
    {
        get => LightAliasTargets.FirstOrDefault(row => row.Target == lightAliasTarget?.Target);
        set { if (personalizationBusy || identificationBusy || reconcilingAliasTargets || !Set(ref lightAliasTarget, value)) return; identificationMessage=""; ReloadLightAlias(); }
    }
    public string LightAlias { get => HasLightAliasTarget ? LightAliasDraft!.Name : ""; set { if (!HasLightAliasTarget || LightAliasDraft is not { } draft || draft.Name == value) return; draft.Name = value; Notify(nameof(LightAlias)); PublishLightPersonalization(); } }
    public bool HasLightAliasTarget => LightAliasTarget is not null && LightAliasDraft is not null;
    public string LightAliasStatus => LightAliasDraft?.Problem ?? (LightAliasDraft?.Dirty == true ? "未保存" : "");
    public bool CanSaveLightAlias => HasLightAliasTarget && LightPersonalizationSupported && LightingIdle && LightAliasDraft is { Dirty: true, Problem: null };
    public ICommand SaveLightAlias => new AsyncCommand(_ => SaveLightAliasAsync(), _ => CanSaveLightAlias, e => Notice = e.Message);
    public ICommand ReloadLightAliasCommand => new AsyncCommand(_ => { ReloadLightAlias(); return Task.CompletedTask; }, _ => LightingIdle && LightAliasTarget is { } row &&
        client.Snapshot?.Configuration.Configuration.Devices.Devices.Any(d => d.Id == row.Target.DeviceId && d.Endpoints.Any(e => e.Id == row.Target.EndpointId && e.Kind == EndpointKind.Lighting)) == true, e => Notice = e.Message);
    public void ReloadLightAlias()
    {
        LightAliasDraft = lightAliasTarget is not null && client.Snapshot is { } snapshot ? new(snapshot, lightAliasTarget.Target) : null;
        Notify(nameof(LightAlias)); PublishLightPersonalization();
    }
    internal void RenderLightPersonalization(DeviceWorkspaceSnapshot snapshot)
    {
        var config = snapshot.Configuration.Configuration;
        var wasVisible = LightAliasTarget is not null;
        var registered = Lights.Where(l => snapshot.Connected && snapshot.State.Inventory.Devices.Any(d =>
                d.Connected && d.Device.Id == l.Target.DeviceId && d.Device.Endpoints.Any(e => e.Id == l.Target.EndpointId)) &&
            config.Devices.Devices.Any(d => d.Id == l.Target.DeviceId && d.Endpoints.Any(e => e.Id == l.Target.EndpointId && e.Kind == EndpointKind.Lighting))).ToList();
        var currentTarget = registered.FirstOrDefault(row => row.Target == lightAliasTarget?.Target);
        var currentRegistered = currentTarget is not null;
        // Keep the draft internally while its device is absent, and restore its visible selection on reconnect.
        if (currentTarget is not null) lightAliasTarget = currentTarget;
        reconcilingAliasTargets = true;
        try
        {
            foreach (var old in LightAliasTargets.Where(r => !registered.Contains(r)).ToArray()) LightAliasTargets.Remove(old);
            foreach (var row in registered) if (!LightAliasTargets.Contains(row)) LightAliasTargets.Add(row);
        }
        finally { reconcilingAliasTargets = false; }
        Notify(nameof(LightAliasTarget));
        if (wasVisible != currentRegistered) Notify(nameof(LightAlias));
        if (!personalizationBusy)
        {
            if (LightAliasDraft is { Dirty: false } && currentRegistered)
            {
                var next = new DeviceLightAliasDraft(snapshot, lightAliasTarget!.Target);
                var changed = next.Name != LightAlias; LightAliasDraft = next; if (changed) Notify(nameof(LightAlias));
            }
            else LightAliasDraft?.Update(snapshot);
        }
        if (LightSavedColors is { } palette)
        {
            palette.UpdateFromService(config.Preferences.Palette.Favorites, config.Preferences.Palette.Recent);
            palette.SetAvailable(LightingIdle && LightPersonalizationSupported);
        }
        PublishLightPersonalization();
    }
    private void PublishLightPersonalization()
    {
        foreach (var name in new[] { nameof(LightPersonalizationSupported), nameof(LightAliasStatus), nameof(CanSaveLightAlias), nameof(HasLightAliasTarget) }) Notify(name);
        PublishIdentificationControls();
        CommandManager.InvalidateRequerySuggested();
    }
    public async Task SaveLightAliasAsync()
    {
        Render(); if (!CanSaveLightAlias) return;
        var edit = LightAliasDraft!.Capture();
        using var status = BeginStatus("照明名を保存中");
        var reply = await SaveLightPersonalizationAsync(edit);
        ReportResult(reply.Message, reply.Outcome); ReloadLightAlias();
    }
    private Task SaveLightColorsAsync(bool recent, Color[] colors)
    {
        if (!LightingIdle || !LightPersonalizationSupported) throw new IOException("照明設定の保存不可 · 接続確認が必要");
        return SaveLightPersonalizationAsync(DeviceLightingPersonalization.Capture(client.Snapshot!,
            recent ? LightingLibraryOperation.RememberColors : LightingLibraryOperation.ToggleFavorite,
            colors: colors.Select(c => new RgbColor(c.R, c.G, c.B)).ToArray()));
    }
    private async Task<LightingLibraryReply> SaveLightPersonalizationAsync(DeviceLightingLibraryEdit edit)
    {
        var before = client.Snapshot!;
        lightingBusy = personalizationBusy = true; PublishLightingEditor(); PublishLightPersonalization();
        LightSavedColors?.SetAvailable(false);
        try
        {
            var reply = await client.SaveLightingEditAsync(edit);
            var after = client.Snapshot!;
            if (reply.Generation == after.Configuration.Generation)
            {
                DeviceLightingPersonalization.Preserve(LightingDraft, before, after);
                LightLibraryEditor?.Draft.AcceptMetadataSave(before, after);
                LightAliasDraft?.AcceptMetadataSave(before, after);
            }
            return reply;
        }
        finally { lightingBusy = personalizationBusy = false; Render(); }
    }
    private async Task RememberAppliedLightColorsAsync(DeviceOperationResult result)
    {
        if (LightSavedColors is null || !LightPersonalizationSupported || result.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified)) return;
        var targets = result.Targets.Select(t => t.Target).ToHashSet();
        var colors = (client.Snapshot!.Configuration.Configuration.Lighting.Preferences ?? []).Where(p => targets.Contains(p.Target))
            .SelectMany(p => p.Program.LedColors ?? p.Program.Colors).Distinct().Take(32).Select(c => Color.FromRgb((byte)c.R, (byte)c.G, (byte)c.B)).ToArray();
        await LightSavedColors.RememberAsync(colors);
    }
}
