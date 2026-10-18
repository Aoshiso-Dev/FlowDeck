using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private bool identificationBusy;
    private string identificationMessage = "";
    public bool LightIdentificationSupported => client.SupportsLightingIdentification;
    public bool LightIdentificationRunning => identificationBusy || client.IdentificationRunning;
    public string LightIdentificationMessage => identificationMessage;
    public bool CanEditLightAlias => LightingIdle && HasLightAliasTarget;
    public string? LightIdentificationProblem => LightAliasTarget is null ? "照明未選択" : LightingDraft.Dirty ? "未適用の編集あり · 適用または再取得が必要" :
        client.Snapshot is not { Connected: true, ElapsedSeconds: >= 0 and < 3 } snapshot ? "状態更新が必要" : DeviceLightingIdentification.Problem(snapshot.State, LightAliasTarget.Target);
    public bool CanIdentifyLight => LightingIdle && LightIdentificationSupported && LightIdentificationProblem is null;
    public ICommand IdentifyLight => new AsyncCommand(_ => IdentifyLightAsync(), _ => CanIdentifyLight, e => { identificationMessage = e.Message; PublishIdentificationControls(); });
    public ICommand CancelLightIdentification => new AsyncCommand(_ => client.CancelLightingIdentificationAsync(), _ => LightIdentificationRunning && !closing, e => { identificationMessage=e.Message; PublishIdentificationControls(); });
    private void PublishIdentificationControls()
    {
        foreach (var name in new[] { nameof(LightIdentificationSupported), nameof(LightIdentificationRunning), nameof(LightIdentificationMessage), nameof(LightIdentificationProblem), nameof(CanIdentifyLight), nameof(CanEditLightAlias) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
    public async Task IdentifyLightAsync()
    {
        Render(); if (!CanIdentifyLight) return;
        var edit = DeviceLightingIdentificationEdit.Capture(client.Snapshot!, LightAliasTarget!.Target);
        identificationBusy=true; identificationMessage="識別点灯中…"; PublishLightingEditor(); PublishIdentificationControls();
        try
        {
            var result=await client.IdentifyLightingAsync(edit);
            identificationMessage=result.Message;
            foreach(var target in result.Targets) Lights.SingleOrDefault(l=>l.Target==target.Target)?.Report(target);
        }
        finally { identificationBusy=false; Render(); PublishIdentificationControls(); }
    }
}
