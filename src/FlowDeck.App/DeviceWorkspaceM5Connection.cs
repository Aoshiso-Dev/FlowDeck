using System.Windows.Input;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private string m5Ssid = "";
    private bool m5Connecting;
    public bool M5ConnectionSupported => m5Session is not null;
    public bool M5ConnectionIdle => SetupIdle && !m5Connecting;
    public bool CanConnectM5 => M5ConnectionIdle && M5ConnectionSupported && !M5Draft.Dirty;
    public bool CanDisconnectM5 => !closing && m5Session?.State is { Running: true } or { Wanted: true };
    public bool CanRegisterM5 => CanConnectM5 && m5Session!.State.CanRegister;
    public bool CanPairM5 => CanConnectM5 && m5Session!.State.CanPair && System.Text.Encoding.UTF8.GetByteCount(M5Ssid) is >= 1 and <= 32;
    public string M5Ssid { get => m5Ssid; set { if (Set(ref m5Ssid, value)) PublishM5Connection(); } }
    public string M5LinkStatus => m5Session?.State is not { } s ? "" : s.Busy || m5Connecting ? "処理中" :
        s.NeedsRegistration && s.Link.UsbOpen ? "USB · 登録待ち" : s.Link.Connected ? s.Link.Route + (s.Link.Synchronized ? " · 同期済み" : " · 状態同期中") :
        s.Link.WifiAuthenticated ? "Wi-Fi · TLS認証済み" : s.Running ? "接続待ち" : "未接続";
    public string M5LinkDetail => m5Session?.State is not { } s ? "" : s.NotificationError ?? s.Message ?? s.Link.Error ??
        (s.Running && !s.NeedsRegistration && !s.Link.WifiPaired && M5Draft.Settings.Connection.Transport != "usb" ? "Wi-Fi未登録" :
        s.Link.Firmware is { } version ? "M5GO · " + version : "");
    public ICommand ConnectM5 => new AsyncCommand(_ => ConnectM5Async(), _ => CanConnectM5, M5ConnectionFailed);
    public ICommand DisconnectM5 => new AsyncCommand(_ => DisconnectM5Async(), _ => CanDisconnectM5, M5ConnectionFailed);
    public ICommand RegisterM5 => new AsyncCommand(_ => RegisterM5Async(), _ => CanRegisterM5, M5ConnectionFailed);
    private void M5ConnectionFailed(Exception e) { m5Message = e is OperationCanceledException ? "接続操作を中断しました" : e.Message; PublishM5(); }
    private void PublishM5Connection()
    {
        foreach (var name in new[] { nameof(M5ConnectionSupported), nameof(M5ConnectionIdle), nameof(CanConnectM5), nameof(CanDisconnectM5),
            nameof(CanRegisterM5), nameof(CanPairM5), nameof(M5LinkStatus), nameof(M5LinkDetail) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
    public async Task StartM5Async()
    { if (m5Session is not null) await m5Session.StartupAsync(); PublishM5Connection(); }
    public async Task ConnectM5Async()
    {
        if (!CanConnectM5) return; m5Connecting = true; PublishM5Connection();
        try { await m5Session!.ConnectAsync(); }
        finally { m5Connecting = false; Render(); }
    }
    public async Task DisconnectM5Async()
    {
        if (m5Session is null) return;
        await m5Session.DisconnectAsync(); Render();
    }
    public async Task RegisterM5Async()
    {
        if (!CanRegisterM5) return; m5Connecting = setupBusy = true; ClearConfigurationPreviews(); Render();
        try { await m5Session!.RegisterAsync(); M5Draft.Reset(client.Snapshot!); }
        finally { m5Connecting = setupBusy = false; Render(); }
    }
    public async Task PairM5Async(string password)
    {
        if (!CanPairM5) return; m5Connecting = setupBusy = true; Render();
        try { await m5Session!.PairAsync(M5Ssid, password); }
        catch (Exception e) { M5ConnectionFailed(e); }
        finally { m5Connecting = setupBusy = false; Render(); }
    }
}
