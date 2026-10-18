using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Infrastructure;
namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    public ObservableCollection<DeviceNameChoice> CacheHistoryChoices { get; }=[];
    private DeviceNameChoice? selectedCacheHistory;
    private DeviceCacheRetirementEdit? cacheRetirementPreview;
    private bool renderingCacheHistory;
    public bool CacheRetirementSupported => client.SupportsCacheRetirement;
    public DeviceNameChoice? SelectedCacheHistory
    {
        get=>selectedCacheHistory;
        set
        {
            if(renderingCacheHistory||!SetupIdle||value==selectedCacheHistory||value is not null&&!CacheHistoryChoices.Contains(value)) return;
            selectedCacheHistory=value; ClearCacheRetirement(); Notify();
        }
    }
    public bool HasCacheRetirementPreview=>cacheRetirementPreview is not null;
    public string CacheRetirementText=>cacheRetirementPreview?.Description??"";
    public string CacheRetirementStatus=>CacheHistoryChoices.Count==0?"切断済みの履歴なし":
        cacheRetirementPreview is not null&&!cacheRetirementPreview.Matches(client.Snapshot)?"接続・機器・設定に変更あり · 要再確認":"";
    public bool CanPreviewCacheRetirement=>SetupIdle&&CacheRetirementSupported&&selectedCacheHistory is not null&&
        client.Snapshot is { Connected:true,ExitAttempted:false } s&&!s.State.Releasing&&!s.State.ConfigurationChanging&&
        s.State.Inventory.Devices.Any(d=>d.Device.Id==selectedCacheHistory.Id&&!d.Connected);
    public bool CanApplyCacheRetirement=>CanPreviewCacheRetirement&&cacheRetirementPreview is not null&&
        cacheRetirementPreview.Target.DeviceId==selectedCacheHistory!.Id&&cacheRetirementPreview.Matches(client.Snapshot);
    public ICommand PreviewCacheRetirement=>new AsyncCommand(_=>PreviewCacheRetirementAsync(),_=>CanPreviewCacheRetirement,e=>Notice=e.Message);
    public ICommand ApplyCacheRetirement=>new AsyncCommand(_=>ApplyCacheRetirementAsync(),_=>CanApplyCacheRetirement,e=>Notice=e.Message);
    public ICommand CancelCacheRetirement=>new AsyncCommand(_=>{ClearCacheRetirement();return Task.CompletedTask;},_=>SetupIdle&&HasCacheRetirementPreview);
    private void ClearCacheRetirement() { cacheRetirementPreview=null; PublishCacheRetirement(); }
    private void RenderCacheHistory(DeviceWorkspaceSnapshot snapshot)
    {
        var missing=snapshot.State.Inventory.Devices.Where(d=>!d.Connected).ToArray(); renderingCacheHistory=true;
        try
        {
            foreach(var old in CacheHistoryChoices.Where(c=>!missing.Any(d=>d.Device.Id==c.Id)).ToArray()) CacheHistoryChoices.Remove(old);
            foreach(var d in missing)
            {
                var name=snapshot.Configuration.Configuration.Devices.Devices.SingleOrDefault(r=>r.Id==d.Device.Id)?.Name??d.Device.Name;
                var choice=CacheHistoryChoices.SingleOrDefault(c=>c.Id==d.Device.Id);
                if(choice is null) { choice=new(d.Device.Id,name);CacheHistoryChoices.Add(choice); }
                else choice.Update(name);
                choice.UpdateLabel(name+" · "+d.Device.Id);
            }
            if(selectedCacheHistory is null||!CacheHistoryChoices.Contains(selectedCacheHistory))
            { selectedCacheHistory=CacheHistoryChoices.FirstOrDefault();cacheRetirementPreview=null;Notify(nameof(SelectedCacheHistory)); }
        }
        finally { renderingCacheHistory=false; }
        PublishCacheRetirement();
    }
    private void PublishCacheRetirement()
    {
        foreach(var name in new[]{nameof(CacheRetirementSupported),nameof(HasCacheRetirementPreview),nameof(CacheRetirementText),
            nameof(CacheRetirementStatus),nameof(CanPreviewCacheRetirement),nameof(CanApplyCacheRetirement)}) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
    public async Task PreviewCacheRetirementAsync()
    {
        Render();if(!CanPreviewCacheRetirement) return;
        var edit=DeviceCacheRetirementEdit.Capture(client.Snapshot!,selectedCacheHistory!.Id);
        setupBusy=true;ClearConfigurationPreviews();Render();
        using var status = BeginStatus("検出履歴を確認中");
        try { cacheRetirementPreview=await client.PreviewCacheRetirementAsync(edit);ReportStatus("整理内容の確認待ち"); }
        finally { setupBusy=false;Render(); }
    }
    public async Task ApplyCacheRetirementAsync()
    {
        Render();if(!CanApplyCacheRetirement) return;
        var edit=cacheRetirementPreview!;setupBusy=true;ClearConfigurationPreviews();Render();
        using var status = BeginStatus("検出履歴を整理中");
        try { await client.RetireDisconnectedAsync(edit);ReportStatus("検出履歴を整理済み · 名前・設定は保持"); }
        finally { setupBusy=false;Render(); }
    }
}
