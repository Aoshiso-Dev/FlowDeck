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
    private string? RegistrationRemovalProblem => selectedDeviceName is null||client.Snapshot is not { } snapshot ? "登録を選択してください" :
        SelectedDeviceNameDraft is { Dirty:true } or { Conflict:true } ? "名前を保存するか、現在の名前を読み込んでください" :
        DeviceRegistrationRemoval.Problem(snapshot,selectedDeviceName.Id);
    public string RegistrationRemovalStatus => selectedDeviceName is null ? "" : RegistrationRemovalProblem ??
        (registrationRemovalPreview is not null&&!registrationRemovalPreview.Edit.Matches(client.Snapshot) ? "接続・機器・設定が変わりました。解除内容を確認し直してください" : "");
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
        try { registrationRemovalPreview=await client.PreviewRegistrationRemovalAsync(edit); Notice="解除する登録と関連設定を確認してください"; }
        finally { setupBusy=false; Render(); }
    }
    public async Task ApplyRegistrationRemovalAsync()
    {
        Render(); if(!CanApplyRegistrationRemoval) return;
        var preview=registrationRemovalPreview!; setupBusy=true; ClearConfigurationPreviews(); Render();
        try
        {
            var result=await client.ApplyRegistrationRemovalAsync(preview); Notice=result.Message;
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