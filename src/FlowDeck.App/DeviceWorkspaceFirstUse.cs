using FlowDeck.Core;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    public bool AutomaticMonitoring => client.Snapshot?.Configuration.Configuration.Dashboard.Items.Length == 0;
    public string FirstUseStatus => "検出した温度・回転数を表示しています。冷却は機器の設定を維持し、表示や制御はあとから変更できます。";

    public async Task PrepareFirstUseAsync()
    {
        if (closing || setupBusy || !client.Connected) return;
        setupBusy = true; Render();
        try
        {
            var result = await client.PrepareFirstUseAsync();
            if (result.Deferred is not null) Notice = "照明の自動準備を保留しました。監視はそのまま使えます。 " + result.Deferred;
            else if (result.PreparedLighting > 0) Notice = $"照明{result.PreparedLighting}端子を自動認識しました。色・効果を選んで適用できます。";
        }
        finally { setupBusy = false; Render(); }
    }
}
