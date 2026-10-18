using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed partial class FlowDeckViewModel
{
    public ObservableCollection<LightingTargetChoice> IdentificationTargets { get; } = [];
    private LightingTargetChoice? identificationTarget;
    private string lightAlias = "", identificationResult = "", identificationSignature = "";
    private bool identificationBusy, rebuildingIdentification;
    private CancellationTokenSource? identificationCancellation;
    public LightingTargetChoice? IdentificationTarget
    {
        get => identificationTarget;
        set
        {
            if (rebuildingIdentification || !Set(ref identificationTarget, value)) return;
            lightAlias = value is null ? "" : LightingNames.Find(host.LightNames, value.Address) ?? "";
            identificationResult = ""; Refresh();
        }
    }
    public string LightAlias { get => lightAlias; set => Set(ref lightAlias, value); }
    public string IdentificationResult => host.LightNamesError ?? identificationResult;
    public bool HasIdentificationResult => !string.IsNullOrWhiteSpace(IdentificationResult);
    public bool IdentificationBusy => identificationBusy || host.IdentifyingLight;
    public string IdentificationHint => host.LightingSynchronized ? "識別には同期停止が必要" : IdentificationTarget is { } target &&
        target.Hub is { } h && (!core.Settings.LightingPorts.TryGetValue(h.Channel, out var previous) || previous.HubId != h.HubId || previous.Outcome != Outcome.WrittenUnverified || !HubLighting.Valid(previous.Effect))
        ? "識別には照明の適用が必要（復元設定なし）" : "";
    public bool HasIdentificationHint => IdentificationHint.Length > 0;
    public bool CanIdentifyLight => LightingScenesIdle && IdentificationTarget?.Available == true && !host.LightingSynchronized && IdentificationHint.Length == 0;
    public ICommand IdentifyLight { get; private set; } = null!;
    public ICommand CancelIdentification { get; private set; } = null!;
    public ICommand SaveLightAlias { get; private set; } = null!;
    public ICommand ReloadLightAliases { get; private set; } = null!;
    private void InitializeLightingIdentification()
    {
        IdentifyLight = Command(async _ =>
        {
            var address = IdentificationTarget!.Address;
            var before = core.Snapshot.LightingRevision; var draftWasCurrent = hubLightDraftRevision == before;
            using var cancellation = new CancellationTokenSource(); identificationCancellation = cancellation;
            identificationBusy = true; identificationResult = "識別点灯中…"; Refresh();
            try
            {
                var result = await host.IdentifyLightAsync(address, cancellation.Token); identificationResult = result.Message;
                if (draftWasCurrent && result.Restored && !result.Superseded && result.RestoredRevision == core.Snapshot.LightingRevision)
                    hubLightDraftRevision = result.RestoredRevision.Value;
            }
            catch (Exception e) { identificationResult = e.Message; throw; }
            finally
            {
                identificationCancellation = null; identificationBusy = false;
                RefreshMotherboard(); Refresh();
            }
        }, _ => CanIdentifyLight);
        CancelIdentification = Command(_ => { identificationCancellation?.Cancel(); return Task.CompletedTask; }, _ => identificationCancellation is not null);
        SaveLightAlias = Command(async _ =>
        {
            var address = IdentificationTarget!.Address; var name = LightAlias;
            identificationBusy = true; Refresh();
            try
            {
                await host.SaveLightNameAsync(address, name);
                lightAlias = LightingNames.Find(host.LightNames, address) ?? "";
                identificationResult = lightAlias.Length == 0 ? "標準名に復元済み" : "名前を保存済み";
            }
            catch (Exception e) { identificationResult = e.Message; throw; }
            finally { identificationBusy = false; RefreshLightNames(); }
        }, _ => LightingScenesIdle && host.LightNamesError is null && IdentificationTarget is not null && (string.IsNullOrWhiteSpace(LightAlias) || LightingNames.ValidName(LightAlias.Trim())));
        ReloadLightAliases = Command(async _ =>
        {
            identificationBusy = true; Refresh();
            try { await host.LoadLightNamesAsync(); identificationResult = "再読込済み"; }
            finally { identificationBusy = false; RefreshLightNames(); }
        }, _ => LightingScenesIdle);
    }
    private void RefreshLightNames()
    {
        RefreshLightingGroupTargets(); RefreshHubLighting(); RefreshBoardLighting(); RefreshIdentificationTargets(); Refresh();
    }
    private void RefreshIdentificationTargets()
    {
        var targets = AvailableLightingTargets().Where(t => CurrentLightingGroup is not { } g || t.Hub is not null && g.Hub.Contains(t.Hub) || t.Board is not null && g.Board.Contains(t.Board)).ToList();
        if (identificationTarget is { } missing && targets.All(t => t.Key != missing.Key) &&
            (CurrentLightingGroup is null || missing.Hub is not null && CurrentLightingGroup.Hub.Contains(missing.Hub) || missing.Board is not null && CurrentLightingGroup.Board.Contains(missing.Board)))
            targets.Add(new(missing.Key, missing.Name, missing.Hub, missing.Board, false, false));
        var signature = string.Join('|', targets.Select(t => t.Key + t.Name + t.Available));
        if (signature == identificationSignature) return;
        identificationSignature = signature; var old = identificationTarget;
        rebuildingIdentification = true;
        try { IdentificationTargets.Clear(); foreach (var t in targets) IdentificationTargets.Add(t); }
        finally { rebuildingIdentification = false; }
        identificationTarget = targets.FirstOrDefault(t => t.Key == old?.Key) ?? targets.FirstOrDefault();
        if (identificationTarget?.Key != old?.Key)
        {
            lightAlias = identificationTarget is null ? "" : LightingNames.Find(host.LightNames, identificationTarget.Address) ?? "";
            identificationResult = "";
        }
        Notify(nameof(IdentificationTarget)); Notify(nameof(LightAlias));
    }
}
