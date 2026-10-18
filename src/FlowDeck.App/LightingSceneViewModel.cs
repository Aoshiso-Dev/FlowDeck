using System.Collections.ObjectModel;
using System.Windows.Input;
using FlowDeck.Core;

namespace FlowDeck.App;

public sealed partial class FlowDeckViewModel
{
    public ObservableCollection<LightingScene> LightingSceneChoices { get; } = [];
    public ObservableCollection<SceneApplyRow> LightingSceneResults { get; } = [];
    private LightingScene? selectedLightingScene, deletedLightingScene;
    private bool sceneBusy, sceneIncludeHub = true, sceneIncludeBoard = true, sceneManagementOpen;
    private string lightingSceneName = "", lightingSceneResult = "";
    public bool LightingScenesIdle => !sceneBusy && !brightnessBusy && !groupBusy && !hubLightBusy && !boardLightBusy && !state.LightingBusy && !host.SceneApplying && !host.LightingAdjustmentBusy && !IdentificationBusy;
    public bool SceneIncludeHub { get => sceneIncludeHub; set => Set(ref sceneIncludeHub, value); }
    public bool SceneIncludeBoard { get => sceneIncludeBoard; set => Set(ref sceneIncludeBoard, value); }
    public string LightingSceneName { get => lightingSceneName; set => Set(ref lightingSceneName, value); }
    public string LightingSceneResult { get => host.SceneLibraryError ?? lightingSceneResult; private set => Set(ref lightingSceneResult, value); }
    public bool SceneManagementOpen { get => sceneManagementOpen; set => Set(ref sceneManagementOpen, value); }
    public bool HasLightingSceneResult => !string.IsNullOrWhiteSpace(LightingSceneResult);
    public bool HasLightingSceneResults => LightingSceneResults.Count > 0;
    public bool HasLightingSceneSummary => SelectedLightingScene is not null;
    public LightingScene? SelectedLightingScene
    {
        get => selectedLightingScene;
        set { if (Set(ref selectedLightingScene, value)) { if (value is not null) LightingSceneName = value.Name; Notify(nameof(LightingSceneSummary)); Notify(nameof(HasLightingSceneSummary)); } }
    }
    public string LightingSceneSummary
    {
        get
        {
            if (SelectedLightingScene is not { } source) return "";
            try { var s = LightingGroups.Select(source, CurrentLightingGroup); return $"NZXT {s.Hub.Length + (s.Sync?.Channels.Length ?? 0)} · マザー {s.Board.Length + (s.Sync?.Zones.Length ?? 0)}" + (s.Sync is { } sync ? " · 同期 " + HubLighting.Name(sync.Mode) : ""); }
            catch (Exception e) { return e.Message; }
        }
    }
    public ICommand SaveLightingScene { get; private set; } = null!;
    public ICommand OverwriteLightingScene { get; private set; } = null!;
    public ICommand RenameLightingScene { get; private set; } = null!;
    public ICommand ApplyLightingScene { get; private set; } = null!;
    public ICommand DeleteLightingScene { get; private set; } = null!;
    public ICommand UndoDeleteLightingScene { get; private set; } = null!;
    public ICommand ReloadLightingScenes { get; private set; } = null!;
    public ICommand ToggleSceneManagement { get; private set; } = null!;
    private void InitializeLightingScenes()
    {
        ToggleSceneManagement = Command(_ => { SceneManagementOpen = !SceneManagementOpen; return Task.CompletedTask; }, _ => LightingScenesIdle);
        async Task Edit(Func<Task> action)
        {
            sceneBusy = true; Refresh();
            try { await action(); }
            catch (Exception e) { LightingSceneResult = e.Message; throw; }
            finally { sceneBusy = false; Refresh(); }
        }
        async Task Capture(bool overwrite)
        {
            var id = overwrite ? SelectedLightingScene!.Id : Guid.NewGuid().ToString("N");
            var name = LightingSceneName.Trim();
            if (name.Length is < 1 or > 40) throw new InvalidOperationException("シーン名を1〜40文字で入力してください");
            var scene = await host.CaptureLightingSceneAsync(id, name, SceneIncludeHub, SceneIncludeBoard, CurrentLightingGroup);
            await host.SaveSceneAsync(scene); RefreshLightingScenes(id); LightingSceneResult = "保存済み · " + name;
        }
        SaveLightingScene = Command(_ => Edit(() => Capture(false)), _ => LightingScenesIdle && CanUseLightingScope && !string.IsNullOrWhiteSpace(LightingSceneName));
        OverwriteLightingScene = Command(_ => Edit(() => Capture(true)), _ => LightingScenesIdle && CanUseLightingScope && SelectedLightingScene is not null && !string.IsNullOrWhiteSpace(LightingSceneName));
        RenameLightingScene = Command(_ => Edit(async () =>
        {
            var scene = SelectedLightingScene! with { Name = LightingSceneName.Trim() };
            await host.SaveSceneAsync(scene); RefreshLightingScenes(scene.Id); LightingSceneResult = "名前変更済み";
        }), _ => LightingScenesIdle && SelectedLightingScene is not null && !string.IsNullOrWhiteSpace(LightingSceneName));
        DeleteLightingScene = Command(_ => Edit(async () =>
        {
            var scene = SelectedLightingScene!; await host.DeleteSceneAsync(scene.Id); deletedLightingScene = scene;
            RefreshLightingScenes(); LightingSceneName = ""; LightingSceneResult = "削除済み";
        }), _ => LightingScenesIdle && SelectedLightingScene is not null);
        UndoDeleteLightingScene = Command(_ => Edit(async () =>
        {
            var scene = deletedLightingScene!; await host.SaveSceneAsync(scene); deletedLightingScene = null;
            RefreshLightingScenes(scene.Id); LightingSceneResult = "削除取消済み";
        }), _ => LightingScenesIdle && deletedLightingScene is not null);
        ReloadLightingScenes = Command(_ => Edit(async () => { await host.LoadScenesAsync(); RefreshLightingScenes(); LightingSceneResult = "再読込済み"; }), _ => LightingScenesIdle);
        ApplyLightingScene = Command(_ => Edit(async () =>
        {
            var scene = SelectedLightingScene!;
            LightingSceneResult = "適用中…"; LightingSceneResults.Clear();
            var result = await host.ApplyLightingSceneAsync(scene, CurrentLightingGroup);
            foreach (var row in result.Rows) LightingSceneResults.Add(row);
            LightingSceneResult = result.Succeeded ? "適用済み · " + scene.Name : result.Error ?? "適用未確認";
            if (result.Succeeded)
            {
                Notice = ""; ResetBrightnessDisplay();
                if (scene.Hub.Length > 0 || scene.Sync is not null) LoadHubLighting();
                RefreshMotherboard(); if (scene.Board.Length > 0 || scene.Sync is not null) LoadBoardLightDraft();
            }
        }), _ => LightingScenesIdle && CanUseLightingScope && SelectedLightingScene is not null);
    }
    private void RefreshLightingScenes(string? select = null)
    {
        var id = select ?? SelectedLightingScene?.Id;
        LightingSceneChoices.Clear(); foreach (var scene in host.SceneLibrary.Scenes ?? []) LightingSceneChoices.Add(scene);
        SelectedLightingScene = LightingSceneChoices.FirstOrDefault(s => s.Id == id);
        Notify(nameof(LightingSceneResult));
    }
}
