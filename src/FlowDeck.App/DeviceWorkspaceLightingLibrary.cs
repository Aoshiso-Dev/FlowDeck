using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    public DeviceLightingLibraryEditor? LightLibraryEditor { get; private set; }
    public bool HasLightLibraryEditor => LightLibraryEditor is not null;
    public bool CanSaveLightLibraryEditor => LightingIdle && LightLibraryEditor?.Draft is { Dirty: true, Problem: null };
    public bool CanDeleteLightLibraryEditor => LightingIdle && LightLibraryEditor?.Draft.CanDelete == true;
    public ICommand NewLightLibrary => new AsyncCommand(p => { OpenLightLibrary(p?.ToString() == "scene" ? LightingPresetKind.Scene : LightingPresetKind.Group); return Task.CompletedTask; }, _ => LightingIdle, e => Notice = e.Message);
    public ICommand EditLightGroup => new AsyncCommand(_ => { OpenLightLibrary(LightingPresetKind.Group, SelectedLightGroup!.Id); return Task.CompletedTask; },
        _ => LightingIdle && SelectedLightGroup is not null && LightGroups.Contains(SelectedLightGroup), e => Notice = e.Message);
    public ICommand EditLightScene => new AsyncCommand(_ => { OpenLightLibrary(LightingPresetKind.Scene, SelectedLightScene!.Id); return Task.CompletedTask; },
        _ => LightingIdle && SelectedLightScene is not null && LightScenes.Contains(SelectedLightScene), e => Notice = e.Message);
    public ICommand SaveLightLibraryEditor => new AsyncCommand(_ => SaveLightLibraryEditorAsync(), _ => CanSaveLightLibraryEditor, e => Notice = e.Message);
    public ICommand DeleteLightLibraryEditor => new AsyncCommand(_ => SaveLightLibraryEditorAsync(true), _ => CanDeleteLightLibraryEditor, e => Notice = e.Message);
    public ICommand ReloadLightLibraryEditor => new AsyncCommand(_ => ReloadLightLibraryEditorAsync(), _ => LightingIdle && HasLightLibraryEditor, e => Notice = e.Message);
    public ICommand CloseLightLibraryEditor => new AsyncCommand(_ => { LightLibraryEditor = null; PublishLightLibrary(); return Task.CompletedTask; }, _ => LightingIdle);
    public ICommand AddLightScenePart => new AsyncCommand(_ => { LightLibraryEditor!.AddPart(); return Task.CompletedTask; }, _ => LightingIdle && LightLibraryEditor?.CanAdd == true, e => Notice = e.Message);
    public ICommand RemoveLightScenePart => new AsyncCommand(_ => { LightLibraryEditor!.RemovePart(); return Task.CompletedTask; }, _ => LightingIdle && LightLibraryEditor?.HasPart == true, e => Notice = e.Message);
    public void OpenLightLibrary(LightingPresetKind kind, string? id = null)
    {
        if (!LightingIdle) return;
        var draft = new DeviceLightingLibraryDraft(client.Snapshot!, kind, id);
        if (id is null && kind == LightingPresetKind.Scene && draft.Candidates.Length > 0) draft.AddPart();
        Notice = "";
        LightLibraryEditor = new(draft, PublishLightLibrary, LightSavedColors); PublishLightLibrary();
    }
    private void PublishLightLibrary()
    {
        foreach (var name in new[] { nameof(LightLibraryEditor), nameof(HasLightLibraryEditor), nameof(CanSaveLightLibraryEditor), nameof(CanDeleteLightLibraryEditor) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
    public async Task ReloadLightLibraryEditorAsync()
    {
        if (LightLibraryEditor is not { } editor || !LightingIdle) return;
        await client.RefreshAsync(true); Render();
        var lighting = client.Snapshot!.Configuration.Configuration.Lighting;
        if (editor.Draft.Existing && !(editor.IsScene ? lighting.Scenes.Any(s => s.Id == editor.Draft.Id) : lighting.Groups.Any(g => g.Id == editor.Draft.Id)))
        { LightLibraryEditor = null; Notice = "保存した項目は削除されています"; PublishLightLibrary(); return; }
        OpenLightLibrary(editor.Draft.Kind, editor.Draft.Existing ? editor.Draft.Id : null);
    }
    public async Task SaveLightLibraryEditorAsync(bool delete = false)
    {
        Render(); if (delete ? !CanDeleteLightLibraryEditor : !CanSaveLightLibraryEditor) return;
        var editor = LightLibraryEditor!; var edit = editor.Draft.Capture(delete);
        lightingBusy = true; PublishLightingEditor(); PublishLightLibrary();
        try
        {
            var result = await client.SaveLightingEditAsync(edit); Notice = result.Message;
            LightLibraryEditor = delete ? null : new(new(client.Snapshot!, editor.Draft.Kind, editor.Draft.Id), PublishLightLibrary, LightSavedColors);
            Render();
            if (!delete && editor.Draft.Kind == LightingPresetKind.Scene) SelectedLightScene = LightScenes.Single(s => s.Id == editor.Draft.Id);
            if (!delete && editor.Draft.Kind == LightingPresetKind.Group) SelectedLightGroup = LightGroups.Single(g => g.Id == editor.Draft.Id);
        }
        finally { lightingBusy = false; Render(); PublishLightLibrary(); }
    }
}
