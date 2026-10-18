using FlowDeck.Core;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    public bool AutomaticMonitoring => client.Snapshot?.Configuration.Configuration.Dashboard.Items.Length == 0;

    public async Task PrepareFirstUseAsync()
    {
        if (closing || setupBusy || !client.Connected) return;
        setupBusy = true; Render();
        using var status = BeginStatus("照明を自動認識中");
        try
        {
            var result = await client.PrepareFirstUseAsync();
            if (result.Deferred is not null) Notice = "照明の自動準備を保留: " + result.Deferred;
            else if (result.PreparedLighting > 0) ReportStatus($"照明 {result.PreparedLighting}端子を自動認識済み", hint: true);
        }
        finally { setupBusy = false; Render(); }
    }
}
