using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;
using Microsoft.Win32;

namespace FlowDeck.App;

public sealed class DeviceImportRow(DeviceSettingsImportDraft draft,DeviceImportEndpoint endpoint,Action changed,Action<string> failed) : Observable
{
    public string Name=>endpoint.Name;
    public DeviceImportChoice[] Choices=>endpoint.Choices;
    public EndpointReference Source=>endpoint.Source;
    public DeviceImportChoice Selected
    {
        get=>Choices.Single(c=>c.Target==draft.Mapping(endpoint.Source));
        set
        {
            if(value is null||value.Target==draft.Mapping(endpoint.Source)) return;
            try { draft.Map(endpoint.Source,value.Target); changed(); }
            catch(Exception e) { failed(e.Message); }
            Notify(); Notify(nameof(Status));
        }
    }
    public string Status=>draft.MappingStatus(endpoint.Source);
}

public sealed partial class DeviceWorkspaceViewModel
{
    private DeviceSettingsImportDraft? importDraft;
    private DeviceSettingsImportPreview? importPreview;
    private string importFile="";
    private bool portableExport;
    public ObservableCollection<DeviceImportRow> ImportRows { get; }=[];
    public DeviceExitChoice<bool>[] ExportKinds { get; }=[new(false,"このPC用"),new(true,"別のPC用")];
    public bool PortableExport { get=>portableExport; set=>Set(ref portableExport,value); }
    public bool TransferIdle=>SetupIdle;
    public bool HasImport=>importDraft is not null;
    public bool HasImportPreview=>importPreview is not null;
    public string ImportFile=>importFile;
    public string ImportSummary=>importDraft is null?"":$"{importDraft.CreatedUtc.ToLocalTime():yyyy/MM/dd HH:mm} / "+
        (importDraft.SameInstallation?"このPCの設定":"個体確認は引継ぎなし")+$" / 対応 {importDraft.MappedCount}/{ImportRows.Count}端子";
    public string ImportStatus=>importDraft?.Problem??"";
    public string ImportChanges=>importPreview is null?"":string.Join(Environment.NewLine,importPreview.Edit.Changes) +
        (importPreview.Transfer.ProviderRoute is null ? "" : Environment.NewLine + DeviceProviderRoutePresentation.Describe(importPreview.Transfer.ProviderRoute, client.Snapshot));
    public bool ImportConfirmed { get=>importDraft?.Confirmed==true; set { if(importDraft is not null) { importDraft.Confirmed=value; importPreview=null; PublishTransfer(); } } }
    public bool CanPreviewImport=>TransferIdle&&importDraft is { Problem:null };
    public bool CanApplyImport=>CanPreviewImport&&importPreview is not null;
    public ICommand ExportSettings=>new AsyncCommand(async _=>
    {
        var dialog=new SaveFileDialog { Title="設定の書き出し",Filter="FlowDeck設定 (*.flowdeck.json)|*.flowdeck.json",DefaultExt=".flowdeck.json",
            AddExtension=true,FileName=$"FlowDeck-{DateTime.Now:yyyyMMdd-HHmm}.flowdeck.json",OverwritePrompt=true };
        if(dialog.ShowDialog()==true) await ExportSettingsToAsync(dialog.FileName);
    },_=>TransferIdle,e=>Notice=e.Message);
    public ICommand SelectImportFile=>new AsyncCommand(async _=>
    {
        var dialog=new OpenFileDialog { Title="設定の復元",Filter="FlowDeck設定 (*.flowdeck.json)|*.flowdeck.json",CheckFileExists=true,Multiselect=false };
        if(dialog.ShowDialog()==true) await LoadImportAsync(dialog.FileName);
    },_=>TransferIdle,e=>Notice=e.Message);
    public ICommand PreviewImport=>new AsyncCommand(_=>PreviewImportAsync(),_=>CanPreviewImport,e=>Notice=e.Message);
    public ICommand ApplyImport=>new AsyncCommand(_=>ApplyImportAsync(),_=>CanApplyImport,e=>Notice=e.Message);
    public ICommand ClearImport=>new AsyncCommand(_=> { ClearImportDraft(); ReportStatus("復元候補なし"); return Task.CompletedTask; },_=>TransferIdle&&HasImport);
    private void ClearImportDraft() { importDraft=null; importPreview=null; importFile=""; ImportRows.Clear(); PublishTransfer(); }
    private void ClearConfigurationPreviews()
    {
        // The authenticated connection holds one configuration upload; a new preview supersedes all prior uploads.
        importPreview=null; providerRoutePreview=null; ClearRegistrationRemoval(); ClearCacheRetirement(); PublishProviderRoutes();
        PublishSetup(); PublishTransfer();
    }
    private void ImportMappingChanged()
    { importPreview=null; foreach(var row in ImportRows) row.Refresh(); PublishTransfer(); }
    private void PublishTransfer()
    {
        foreach(var property in new[]{nameof(TransferIdle),nameof(HasImport),nameof(HasImportPreview),nameof(ImportFile),nameof(ImportSummary),nameof(ImportStatus),
            nameof(ImportChanges),nameof(ImportConfirmed),nameof(CanPreviewImport),nameof(CanApplyImport)}) Notify(property);
        CommandManager.InvalidateRequerySuggested();
    }
    private void RenderTransfer(DeviceWorkspaceSnapshot snapshot) { importDraft?.Update(snapshot); PublishTransfer(); }
    public async Task ExportSettingsToAsync(string path)
    {
        if(!TransferIdle) return; setupBusy=true; Render();
        using var status = BeginStatus("設定を書き出し中");
        try { var archive=await client.ExportSettingsAsync(PortableExport,ui); await DeviceSettingsArchives.SaveAsync(path,archive); ReportStatus("設定書き出し済み: "+Path.GetFileName(path)); }
        finally { setupBusy=false; Render(); }
    }
    public async Task LoadImportAsync(string path)
    {
        if(!TransferIdle) return; setupBusy=true; ClearImportDraft(); Render();
        using var status = BeginStatus("復元設定を読み込み中");
        try
        {
            var archive=await DeviceSettingsArchives.ReadAsync(path); await client.RefreshAsync(true); var snapshot=client.Snapshot!;
            snapshot=snapshot with { Configuration=snapshot.Configuration with { Configuration=snapshot.Configuration.Configuration with {
                Preferences=snapshot.Configuration.Configuration.Preferences with { Ui=ui } } } };
            importDraft=new(snapshot,archive); importFile=Path.GetFileName(path);
            foreach(var endpoint in importDraft.Endpoints) ImportRows.Add(new(importDraft,endpoint,ImportMappingChanged,m=>Notice=m));
            ReportStatus("機器対応の確認待ち");
        }
        finally { setupBusy=false; Render(); }
    }
    public async Task PreviewImportAsync()
    {
        Render(); if(!CanPreviewImport) return;
        var edit=importDraft!.Capture(); setupBusy=true; ClearConfigurationPreviews(); Render();
        using var status = BeginStatus("復元内容を確認中");
        try { importPreview=await client.PreviewImportAsync(edit); ReportStatus("復元内容の確認待ち"); }
        finally { setupBusy=false; Render(); }
    }
    public async Task ApplyImportAsync()
    {
        Render(); if(!CanApplyImport) return;
        var preview=importPreview!; importPreview=null; setupBusy=true; Render();
        using var status = BeginStatus("設定を復元中");
        try
        {
            var result=await client.ApplyImportAsync(preview); ReportResult(result.Message, result.Outcome);
            if(result.Outcome==Outcome.Confirmed)
            {
                // Appearance belongs to this GUI user, not to the service's machine-wide settings.
                ui=preview.Edit.Configuration.Preferences.Ui; theme=ui.Theme; Notify(nameof(Theme)); ThemeManager.Apply(theme); QueueUiSave(); await FlushUiAsync();
                ClearImportDraft();
            }
        }
        finally { setupBusy=false; Render(); }
    }
}
