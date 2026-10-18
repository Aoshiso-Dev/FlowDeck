using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private DeviceRegistrationRemovalPreview? registrationRemovalPreview;
    public bool RegistrationRemovalSupported => client.SupportsRegistrationRemoval;
    public bool HasRegistrationRemovalPreview => registrationRemovalPreview is not null;
    public string RegistrationRemovalText => registrationRemovalPreview is null ? "" : string.Join(Environment.NewLine,registrationRemovalPreview.Edit.Changes);
    private string? RegistrationRemovalProblem => selectedDeviceName is null||client.Snapshot is not { } snapshot ? "登録未選択" :
        SelectedDeviceNameDraft is { Dirty:true } or { Conflict:true } ? "名前の保存または再取得が必要" :
        DeviceRegistrationRemoval.Problem(snapshot,selectedDeviceName.Id);
    public string RegistrationRemovalStatus => selectedDeviceName is null ? "" : RegistrationRemovalProblem ??
        (registrationRemovalPreview is not null&&!registrationRemovalPreview.Edit.Matches(client.Snapshot) ? "接続・機器・設定に変更あり · 解除内容の再確認が必要" : "");
    public bool CanPreviewRegistrationRemoval => DeviceNamesIdle&&RegistrationRemovalSupported&&RegistrationRemovalProblem is null;
    public bool CanApplyRegistrationRemoval => CanPreviewRegistrationRemoval&&registrationRemovalPreview is not null&&
        registrationRemovalPreview.Edit.DeviceId==selectedDeviceName!.Id&&registrationRemovalPreview.Edit.Matches(client.Snapshot);
    public ICommand PreviewRegistrationRemoval => new AsyncCommand(_=>PreviewRegistrationRemovalAsync(),_=>CanPreviewRegistrationRemoval,e=>Notice=e.Message);
    public ICommand ApplyRegistrationRemoval => new AsyncCommand(_=>ApplyRegistrationRemovalAsync(),_=>CanApplyRegistrationRemoval,e=>Notice=e.Message);
    public ICommand CancelRegistrationRemoval => new AsyncCommand(_=> { ClearRegistrationRemoval(); return Task.CompletedTask; },_=>DeviceNamesIdle&&HasRegistrationRemovalPreview);
    private void ClearRegistrationRemoval() { registrationRemovalPreview=null; PublishRegistrationRemoval(); }
    private void PublishRegistrationRemoval()
    {
        foreach(var name in new[]{nameof(RegistrationRemovalSupported),nameof(HasRegistrationRemovalPreview),nameof(RegistrationRemovalText),
            nameof(RegistrationRemovalStatus),nameof(CanPreviewRegistrationRemoval),nameof(CanApplyRegistrationRemoval)}) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
    public async Task PreviewRegistrationRemovalAsync()
    {
        Render(); if(!CanPreviewRegistrationRemoval) return;
        var edit=DeviceRegistrationRemoval.Capture(client.Snapshot!,selectedDeviceName!.Id);
        setupBusy=true; ClearConfigurationPreviews(); Render();
        using var status = BeginStatus("登録解除の内容を確認中");
        try { registrationRemovalPreview=await client.PreviewRegistrationRemovalAsync(edit); ReportStatus("登録・関連設定の解除確認待ち"); }
        finally { setupBusy=false; Render(); }
    }
    public async Task ApplyRegistrationRemovalAsync()
    {
        Render(); if(!CanApplyRegistrationRemoval) return;
        var preview=registrationRemovalPreview!; setupBusy=true; ClearConfigurationPreviews(); Render();
        using var status = BeginStatus("登録解除中");
        try
        {
            var result=await client.ApplyRegistrationRemovalAsync(preview); ReportResult(result.Message, result.Outcome);
            if(result.Outcome==Outcome.Confirmed)
            {
                reconcilingDeviceNames=true;
                try
                {
                    var id=preview.Edit.DeviceId;
                    if(selectedDeviceName?.Id==id) selectedDeviceName=null;
                    foreach(var choice in DeviceNameChoices.Where(c=>c.Id==id).ToArray()) DeviceNameChoices.Remove(choice);
                    deviceNameDrafts.Remove(id);
                }
                finally { reconcilingDeviceNames=false; }
                Notify(nameof(SelectedDeviceName)); Notify(nameof(EditedDeviceName));
            }
        }
        finally { setupBusy=false; Render(); }
    }
}
