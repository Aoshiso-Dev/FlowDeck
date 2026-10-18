using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private bool providerRoutesInitialized, useOpenRgb;
    private DeviceProviderRoutePreview? providerRoutePreview;
    public DeviceExitChoice<bool>[] ProviderRouteChoices { get; } = [new(false, "FlowDeck"), new(true, "OpenRGB")];
    public bool UseOpenRgb
    {
        get => useOpenRgb;
        set { if (Set(ref useOpenRgb, value)) { providerRoutePreview = null; PublishProviderRoutes(); } }
    }
    public bool ProviderRouteAvailable => client.SupportsProviderRouting;
    public string CurrentProviderRoute => client.Snapshot?.Configuration.Configuration.Preferences.Providers?.OpenRgbEnabled == true ? "OpenRGB" : "FlowDeck";
    public bool ProviderRouteIdle => SetupIdle && !openRgbBusy && !openRgbRuntimeBusy && client.Snapshot is { State: { Releasing: false, ConfigurationChanging: false } };
    public bool CanPreviewProviderRoute => ProviderRouteAvailable && ProviderRouteIdle && ReadOpenRgbPort(out var port) &&
        !(client.Snapshot!.Configuration.Configuration.Preferences.Providers ?? new()).EquivalentTo(DraftProviderSettings(port));
    public bool HasProviderRoutePreview => providerRoutePreview is not null;
    public string ProviderRouteChanges => DeviceProviderRoutePresentation.Describe(providerRoutePreview?.Transfer.ProviderRoute, client.Snapshot);
    public bool CanApplyProviderRoute => CanPreviewProviderRoute && providerRoutePreview is not null && providerRoutePreview.Edit.Matches(client.Snapshot);
    public string ProviderRouteStatus => ProviderRouteAvailable ? "現在：" + CurrentProviderRoute : "サービスは切替未対応";
    public ICommand PreviewProviderRoute => new AsyncCommand(_ => PreviewProviderRouteAsync(), _ => CanPreviewProviderRoute, e => Notice = e.Message);
    public ICommand ApplyProviderRoute => new AsyncCommand(_ => ApplyProviderRouteAsync(), _ => CanApplyProviderRoute, e => Notice = e.Message);
    private void PublishProviderRoutes()
    {
        if (client.Snapshot is { } snapshot) RenderProviderSelections(snapshot);
        foreach (var property in new[] { nameof(ProviderRouteAvailable), nameof(CurrentProviderRoute), nameof(ProviderRouteIdle), nameof(CanPreviewProviderRoute),
            nameof(HasProviderRoutePreview), nameof(ProviderRouteChanges), nameof(CanApplyProviderRoute), nameof(ProviderRouteStatus) }) Notify(property);
        CommandManager.InvalidateRequerySuggested();
    }
    private void RenderProviderRoutes(DeviceWorkspaceSnapshot snapshot)
    {
        if (!providerRoutesInitialized)
        {
            providerRoutesInitialized = true; useOpenRgb = snapshot.Configuration.Configuration.Preferences.Providers?.OpenRgbEnabled == true;
            providerSelectedDevices.UnionWith(snapshot.Configuration.Configuration.Preferences.Providers?.OpenRgbDeviceIds ?? []); Notify(nameof(UseOpenRgb));
        }
        if (providerRoutePreview is not null && !providerRoutePreview.Edit.Matches(snapshot)) providerRoutePreview = null;
        PublishProviderRoutes();
    }
    public async Task PreviewProviderRouteAsync()
    {
        Render(); if (!CanPreviewProviderRoute || !ReadOpenRgbPort(out var port)) return;
        var edit = DeviceProviderRouteEdit.Capture(client.Snapshot!, useOpenRgb, port, providerSelectedDevices);
        setupBusy = true; ClearConfigurationPreviews(); Render();
        using var status = BeginStatus("接続先の切替内容を確認中");
        try
        {
            var preview = await client.PreviewProviderRouteAsync(edit);
            if (edit.Matches(client.Snapshot) && ReadOpenRgbPort(out var current) && DraftProviderSettings(current).EquivalentTo(edit.Settings))
            { providerRoutePreview = preview; ReportStatus("切替内容の確認待ち"); }
        }
        finally { setupBusy = false; Render(); }
    }
    public async Task ApplyProviderRouteAsync()
    {
        Render(); if (!CanApplyProviderRoute) return;
        var preview = providerRoutePreview!; providerRoutePreview = null; setupBusy = true; Render();
        using var status = BeginStatus("接続先を切替中");
        try
        {
            var result = await client.ApplyProviderRouteAsync(preview); ReportResult(result.Message, result.Outcome);
            if (result.Outcome == Outcome.Confirmed)
            {
                providerSelectedDevices.Clear();
                providerSelectedDevices.UnionWith(client.Snapshot?.Configuration.Configuration.Preferences.Providers?.OpenRgbDeviceIds ?? []);
            }
        }
        finally { setupBusy = false; Render(); }
    }
}
