using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class DeviceWorkspaceViewModel
{
    private DeviceDialSettingsStore? dialStore;
    private DeviceDialSettings dialSettings = new();
    private readonly DeviceDialStartup dialStartup = new();
    private readonly DeviceDialReconnect dialReconnect = new();
    private DialSerialTransport? dialTransport;
    private DialProtocolSession? dialProtocol;
    private DispatcherTimer? dialTimer;
    private DispatcherTimer? dialRetryTimer;
    private Task? dialInput;
    private Task? dialDisconnection;
    private string dialPort = "COM6", dialMessage = "";
    private bool dialClosing, dialPreview, dialConnecting, dialRetryPolling, dialRegisteredObserved;
    private long dialPublished, dialConnectionRevision;
    public ObservableCollection<string> DialPorts { get; } = ["COM6"];
    public bool DialSupported => dialPreview || dialStore is not null && !client.IsDemo;
    private bool DialLinkAvailable => DialSupported && !client.IsDemo && !closing && !dialClosing && (dialTransport is null || dialTransport.Finished);
    public bool CanEditDial => DialLinkAvailable && !dialConnecting;
    public bool CanConnectDial => CanEditDial && DeviceDialSettingsStore.ValidPort(DialPort);
    public bool CanDisconnectDial => !closing && (dialTransport is not null || dialReconnect.Enabled || dialConnecting);
    public bool CanRegisterDial => !closing && !dialClosing && dialTransport?.IsOpen == true && dialProtocol is { DeviceId: not null, Busy: false, Registered: false };
    public string DialPort { get => dialPort; set { if (!CanEditDial || value is null) return; if (Set(ref dialPort, value.Trim().ToUpperInvariant())) { dialStartup.Suppress(); StopDialReconnect(); PublishDial(); } } }
    public string DialStatus => dialClosing ? "切断中" : dialTransport?.Finished == true ? dialReconnect.Enabled ? "再接続待ち" : "接続終了" : dialProtocol?.SessionId is not null ?
        dialProtocol.Registered ? "接続中・登録済み" : "接続中・登録待ち" : dialTransport?.IsOpen == true ? "応答待ち" : dialTransport is not null ? "接続中" : dialReconnect.Enabled ? "再接続待ち" : "未接続";
    public string DialIdentity => dialProtocol?.DeviceId is { } observed ? $"機器ID: {observed} · Firmware: {dialProtocol.Firmware}" :
        dialSettings.DeviceId is { } saved ? $"登録済みID: {saved}" : "機器ID未登録";
    public string DialMessage => dialMessage;
    public ICommand RefreshDialPorts => new AsyncCommand(_ => { ReadDialPorts(); return Task.CompletedTask; }, _ => CanEditDial, e => { dialMessage = e.Message; PublishDial(); });
    public ICommand ConnectDial => new AsyncCommand(_ => ConnectDialAsync(), _ => CanConnectDial, e => { dialMessage = e.Message; PublishDial(); });
    public ICommand DisconnectDial => new AsyncCommand(_ => DisconnectDialAsync(), _ => CanDisconnectDial, e => { dialMessage = e.Message; PublishDial(); });
    public ICommand RegisterDial => new AsyncCommand(_ => {
        if (!CanRegisterDial) return Task.CompletedTask;
        var saved = dialSettings with { DeviceId = dialProtocol!.DeviceId };
        dialStore!.Save(saved); dialSettings = saved; dialProtocol.RegisterObservedDevice(); EnableDialReconnect(saved);
        dialReconnect.Connected(Environment.TickCount64); dialMessage = "登録済み"; PublishDial();
        return Task.CompletedTask;
    }, _ => CanRegisterDial, e => { dialMessage = e.Message; PublishDial(); });
    public void ConfigureDial(string directory)
    {
        if (dialStore is not null || client.IsDemo) return;
        dialStore = new(Path.Combine(directory, "dial-v1.json")); dialSettings = dialStore.Load(); dialPort = dialSettings.Port;
        dialMessage = dialStore.Warning ?? "";
        try { ReadDialPorts(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        { dialMessage = "USBポート取得失敗: " + e.Message; }
        PublishDial();
    }
    internal void EnableDialPreview()
    {
        if (!client.IsDemo) throw new InvalidOperationException("プレビューにはデモ接続が必要");
        dialPreview = true; dialMessage = "DEMO · USB接続・機器操作は無効"; PublishDial();
    }
    private void ReadDialPorts()
    {
        if (client.IsDemo) return;
        Reconcile(DialPorts, DialSerialTransport.Ports().Append(dialPort).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }
    public async Task ConnectDialAsync()
    {
        dialStartup.Suppress();
        if (!CanConnectDial) return;
        StopDialReconnect();
        var revision = dialConnectionRevision;
        var selected = dialSettings with { Port = DialPort };
        // A manual retry of the saved dial also resumes automatic reconnection.
        if (selected == dialSettings && dialStore is { Warning: null }) EnableDialReconnect(selected);
        await ConnectDialCoreAsync(selected, persistSettings: true, () => revision == dialConnectionRevision);
    }
    public async Task StartDialAsync()
    {
        try
        {
            await dialStartup.StartAsync(dialSettings, client.IsDemo,
                () => dialStore is { Warning: null } && dialTransport is null && CanConnectDial && client.Snapshot?.ExitAttempted != true,
                async (saved, current) => { if (current()) { EnableDialReconnect(saved); await PollDialReconnectAsync(); } });
        }
        catch (Exception e) { dialMessage = "自動接続失敗: " + e.Message; PublishDial(); }
    }
    private void EnableDialReconnect(DeviceDialSettings settings)
    {
        dialReconnect.Enable(settings, client.IsDemo, Environment.TickCount64);
        if (!dialReconnect.Enabled) return;
        dialRetryTimer ??= new(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            async (_, _) => await PollDialReconnectAsync(), Dispatcher.CurrentDispatcher);
        dialRetryTimer.Start();
    }
    private void StopDialReconnect()
    {
        dialConnectionRevision++; dialReconnect.Suppress(); dialRetryTimer?.Stop(); dialRetryTimer = null;
    }
    private bool DialReconnectAllowed(DeviceDialSettings saved) => !closing && !client.IsDemo && client.Snapshot?.ExitAttempted != true &&
        dialStore is { Warning: null } && dialSettings == saved && DialPort == saved.Port;
    private async Task PollDialReconnectAsync()
    {
        if (dialRetryPolling || !dialReconnect.Enabled || closing || client.IsDemo || dialConnecting || dialClosing ||
            dialTransport is { Finished: false }) return;
        dialRetryPolling = true;
        try
        {
            var saved = dialReconnect.Target!;
            // Only enumerate while waiting. Never open or identify a different port.
            var present = DialSerialTransport.Ports().Contains(saved.Port, StringComparer.OrdinalIgnoreCase);
            var attempted = await dialReconnect.TryAsync(Environment.TickCount64, present, DialReconnectAllowed,
                (target, current) => ConnectDialCoreAsync(target, persistSettings: false, current));
            if (!attempted && dialReconnect.Enabled && (dialTransport is null || dialTransport.Finished))
                dialMessage = !present ? "USB接続待ち" : "再接続待ち";
        }
        catch (Exception e) { if (dialReconnect.Enabled) dialMessage = "再接続待ち: " + e.Message; }
        finally { dialRetryPolling = false; PublishDial(); }
    }
    private async Task ConnectDialCoreAsync(DeviceDialSettings settings, bool persistSettings, Func<bool>? current = null)
    {
        if (!CanConnectDial || current?.Invoke() == false) return;
        dialConnecting = true; PublishDial();
        try
        {
        var port = settings.Port;
        await DisconnectDialCoreAsync();
        // Closing the previous transport may await an accepted operation; recheck the manual action.
        if (!DialLinkAvailable || client.Snapshot?.ExitAttempted == true || current?.Invoke() == false) return;
        if (persistSettings) { dialStore!.Save(settings); dialSettings = settings; EnableDialReconnect(settings); }
        var backend = new DeviceDialBackend(client,
            target => Cooling.Any(r => r.Draft.Target == target && (r.Busy || r.Draft.Dirty || r.Draft.Conflict || r.HasSettingsEditor)),
            _ => LightingDraft.Dirty || LightLevelDraft.Dirty || !LightingIdle,
            () => closing || dialClosing || !SetupIdle);
        dialProtocol = new(backend, settings.DeviceId);
        dialTransport = new(port, client.IsDemo); dialProtocol.Outgoing += dialTransport.Send;
        dialTransport.Start(); dialMessage = "機器ID確認中"; dialPublished = 0; dialRegisteredObserved = false;
        dialTimer = new(TimeSpan.FromMilliseconds(15), DispatcherPriority.Background, (_, _) => PollDial(), Dispatcher.CurrentDispatcher);
        dialTimer.Start(); PublishDial();
        }
        finally { dialConnecting = false; PublishDial(); }
    }
    private void PollDial()
    {
        try
        {
            if (dialClosing || dialTransport is null || dialProtocol is null) return;
            var changed = false;
            if (dialTransport.Finished)
            {
                dialMessage = dialTransport.Error ?? (dialReconnect.Enabled ? "USB切断 · 再接続待ち" : "USB切断 · 要再接続");
                dialProtocol.Disconnect(); dialTimer?.Stop(); PublishDial(); return;
            }
            if (dialInput?.IsCompleted == true)
            {
                dialInput.GetAwaiter().GetResult(); dialInput = null; Render(); changed = true;
            }
            if (dialInput is null)
            {
                var budget = 16;
                while (budget-- > 0 && dialTransport.TryRead(out var frame))
                {
                    changed = true;
                    dialInput = dialProtocol.ReceiveAsync(frame);
                    if (!dialInput.IsCompleted) break;
                    dialInput.GetAwaiter().GetResult(); dialInput = null;
                }
            }
            // Keep sending the confirmed state while a service operation is awaiting its result.
            if (Environment.TickCount64 - dialPublished >= 500)
            { dialProtocol.Publish(); dialPublished = Environment.TickCount64; }
            if (!dialRegisteredObserved && dialProtocol.Registered)
            {
                dialRegisteredObserved = true; dialReconnect.Connected(Environment.TickCount64);
                dialMessage = "接続中・登録済み"; changed = true;
            }
            if (changed) PublishDial();
        }
        catch (Exception e) { dialMessage = e.Message; dialInput = null; PublishDial(); }
    }
    public async Task DisconnectDialAsync()
    {
        dialStartup.Suppress();
        StopDialReconnect();
        await DisconnectDialCoreAsync();
    }
    private Task DisconnectDialCoreAsync()
    {
        // Exit and a manual disconnect must join any retry teardown already waiting for an accepted operation.
        if (dialDisconnection is { IsCompleted: false }) return dialDisconnection;
        return dialDisconnection = DisconnectDialTransportAsync();
    }
    private async Task DisconnectDialTransportAsync()
    {
        var input = dialInput; var protocol = dialProtocol; var transport = dialTransport;
        dialClosing = true; dialTimer?.Stop(); dialTimer = null; protocol?.Disconnect(); PublishDial();
        try
        {
            // Finish an already accepted service operation. Serial disconnect must not revoke PC cooling.
            try { if (input is not null) await input; }
            finally { if (transport is not null) await transport.DisposeAsync(); }
        }
        finally
        {
            if (ReferenceEquals(dialTransport, transport) && ReferenceEquals(dialProtocol, protocol))
            { dialInput = null; dialProtocol = null; dialTransport = null; }
            dialClosing = false; dialMessage = "切断済み"; PublishDial();
        }
    }
    private void PublishDial()
    {
        foreach (var name in new[] { nameof(DialSupported), nameof(CanEditDial), nameof(CanConnectDial), nameof(CanDisconnectDial), nameof(CanRegisterDial),
            nameof(DialPort), nameof(DialStatus), nameof(DialIdentity), nameof(DialMessage) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
}
