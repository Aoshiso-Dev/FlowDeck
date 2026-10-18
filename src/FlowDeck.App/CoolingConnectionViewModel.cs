using System.Windows.Input;

namespace FlowDeck.App;

public sealed record CoolingConnectionChoice(string Id, string Name);
public sealed partial class FlowDeckViewModel
{
    public CoolingConnectionChoice[] CoolingConnections { get; } = [new("auto", "自動"), new("service", "Windowsサービス"), new("direct", "直接接続")];
    public string SelectedCoolingConnection { get; set; } = "auto";
    public string CoolingConnectionStatus => host.CoolingConnectionStatus;
    public bool UsesService => host.UsesService;
    public bool UsesDirectCooling => !host.UsesService;
    public ICommand ReconnectCooling { get; private set; } = null!;
    public ICommand SaveCoolingConnection { get; private set; } = null!;
    private bool reconnectingCooling;
    private void InitializeCoolingConnection()
    {
        SelectedCoolingConnection = host.CoolingConnectionPreference;
        ReconnectCooling = Command(async _ =>
        {
            reconnectingCooling = true;
            try { await host.ReconnectCoolingAsync(); LoadSettings(); Notice = "サービス接続済み"; }
            finally { reconnectingCooling = false; Refresh(); }
        }, _ => UsesService && !reconnectingCooling);
        SaveCoolingConnection = Command(_ =>
        { host.CoolingConnectionPreference = SelectedCoolingConnection; Notice = "保存済み・次回起動時に反映"; return Task.CompletedTask; });
    }
}
