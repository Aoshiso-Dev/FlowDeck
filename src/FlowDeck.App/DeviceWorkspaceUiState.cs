using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private DeviceWorkspaceUiState workspaceUi = DeviceWorkspaceUiState.Normalize(null);
    private bool workspaceUiLoaded, restoringWorkspaceUi, restoreLightingSelection;

    private void LoadWorkspaceUi()
    {
        workspaceUi = uiStore?.Workspace ?? DeviceWorkspaceUiState.Normalize(null);
        workspaceUiLoaded = true;
        restoreLightingSelection = uiStore is not null;
        // Navigation is local UI state, not an instruction to restore control or an unsent draft.
        Set(ref selectedPage, workspaceUi.SelectedPage, nameof(SelectedPage));
    }

    private void RememberWorkspacePage()
    {
        if (!workspaceUiLoaded) return;
        workspaceUi = workspaceUi with { SelectedPage = selectedPage };
        QueueUiSave();
    }

    private void RememberLightingSelection()
    {
        if (!workspaceUiLoaded || restoringWorkspaceUi) return;
        restoreLightingSelection = false;
        var targets = LightingDraft.Targets;
        if (workspaceUi.LightingTargets!.SequenceEqual(targets)) return;
        workspaceUi = workspaceUi with { LightingTargets = targets };
        QueueUiSave();
    }

    private void RestoreWorkspaceUi(DeviceWorkspaceSnapshot snapshot)
    {
        if (!restoreLightingSelection || !snapshot.Connected || snapshot.State.Releasing || snapshot.State.ConfigurationChanging) return;
        var available = snapshot.State.Inventory.Devices.Where(d => d.Connected)
            .SelectMany(d => d.Device.Endpoints.Where(e => e.Kind == EndpointKind.Lighting)
                .Select(e => new EndpointReference(d.Device.Id, e.Id))).ToArray();
        // A connected service can briefly have no device inventory during discovery or reconnect.
        // Keep the pending UI selection until lighting is observed, unless the user selects first.
        if (available.Length == 0) return;
        restoreLightingSelection = false;
        var targets = workspaceUi.AvailableLightingTargets(available);
        restoringWorkspaceUi = true;
        try
        {
            SelectLights(targets);
            workspaceUi = workspaceUi with { LightingTargets = targets };
        }
        finally { restoringWorkspaceUi = false; }
    }
}
