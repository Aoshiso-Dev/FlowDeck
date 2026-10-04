using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed record SensorOption(string Id, string Name, string Kind);
public sealed record ProfileOption(string Id, string Name, long Revision, bool Ready);
public sealed class LedRow(string id, string accessory, int? count) : Observable
{
    public string Id { get; } = id;
    public string Accessory { get; } = accessory;
    public string Count { get; set; } = count?.ToString() ?? "";
}

public sealed partial class FlowDeckViewModel : Observable, IDisposable
{
    private readonly RuntimeHost host;
    private IControlCoordinator core;
    private ControlSnapshot state;
    private readonly System.Windows.Threading.Dispatcher dispatcher;
    private long fanEditRevision, lightingEditRevision, curveEditRevision, profileEditRevision;
    private bool editingFan, editingLighting;
    private int page;
    private string notice = "", manualText = "", draftMode = "Auto", selectedLight = "blue";
    private ProfileOption? selectedProfile, curveProfile;
    private bool previewOpen;
    private M5ProtocolSession? virtualProtocol;
    private JsonElement virtualState;
    private string virtualPage = "home", virtualMessage = "", virtualSession = "";
    private long virtualEditRevision, virtualSequence, virtualProfileRevision;
    private int virtualDuty, virtualSelection;
    private bool virtualConnected = true;
    private readonly System.Windows.Threading.DispatcherTimer timer;
    private string diagnosticFilter = "全件";
    public FlowDeckViewModel(RuntimeHost host)
    {
        this.host = host; core = host.Coordinator; state = core.Snapshot; dispatcher = Application.Current.Dispatcher;
        InitializeMotherboard(); InitializeBoardLighting(); InitializeHubLighting(); InitializeMemoryLighting();
        InitializeLightingScenes(); InitializeLightingBrightness(); InitializeLightingGroups(); InitializeLightingIdentification(); InitializeNotifications();
        InitializeCpuFan(); InitializeSettingsTransfer(); InitializeCoolingConnection();
        InitializeDashboard();
        host.Reinitialized += Reinitialize;
        Navigate = Command(p => { SelectedPage = int.Parse(p!.ToString()!); return Task.CompletedTask; });
        ChooseMode = Command(p => { BeginFanEdit(); DraftMode = p!.ToString()!; return Task.CompletedTask; });
        ChangeDuty = Command(p => { ManualDuty = Math.Clamp(ManualDuty + int.Parse(p!.ToString()!), MinimumDuty, 100); return Task.CompletedTask; });
        ApplyFan = Command(_ => ApplyFanAsync(), _ => CanApplyFan);
        RevertFan = Command(_ => { ResetFanDraft(); return Task.CompletedTask; });
        Resume = Command(async _ => Show(await core.RequestAsync(Request("ResumeControl", state.ControlRevision))), _ => state.CanResume);
        StopControl = Command(async _ => Show(await core.StopControlAsync()), _ => !state.FanBusy);
        TogglePreview = Command(_ => { PreviewOpen = !PreviewOpen; return Task.CompletedTask; });
        SelectLight = Command(p => { if (!editingLighting) { lightingEditRevision = state.LightingRevision; editingLighting = true; } SelectedLight = p!.ToString()!; return Task.CompletedTask; });
        ApplyLight = Command(async _ => { var result = await core.RequestAsync(Request("SetLighting", lightingEditRevision) with { Lighting = SelectedLight }); Show(result); if (result.Outcome == Outcome.WrittenUnverified) { editingLighting = false; lightingEditRevision = core.Snapshot.LightingRevision; } }, _ => CanApplyLight);
        RevertLight = Command(_ => { editingLighting = false; lightingEditRevision = state.LightingRevision; SelectedLight = state.Lighting.LastSent ?? "blue"; return Task.CompletedTask; });
        Probe = Command(async _ => { Replace(HubChoices, await core.ProbeAsync()); Notice = HubChoices.Count == 0 ? "対象のControl Hubが見つかりません" : "接続先を選択してください"; });
        ConnectHub = Command(async _ => { await core.OpenAsync(SelectedHub!); LoadSettings(); Notice = "接続しました。実配線と制御条件を確認してください"; }, _ => SelectedHub is not null && !state.Managing);
        SaveSettings = Command(_ => SaveConnectionSettingsAsync(), _ => !state.Managing && !state.FanBusy && !state.LightingBusy);
        RefreshSettings = Command(_ => { LoadSettings(); return Task.CompletedTask; });
        RefreshPorts = Command(_ => { Replace(Ports, M5SerialSession.Ports); return Task.CompletedTask; }, _ => !Demo);
        ConnectM5 = Command(async _ => { await ApplyM5ConnectionAsync(); Notice = "接続方式を保存しました。M5GOの接続を待っています"; }, _ => !Demo && !pairing && (SelectedPort is not null || M5Transport != "usb"));
        DisconnectM5 = Command(async _ => { await host.DisconnectM5Async(); Refresh(); }, _ => host.M5 is not null && !pairing);
        AllowWifi = Command(_ => { M5Firewall.Allow(Environment.ProcessPath!); Notice = "このアプリのWi-Fi通信をプライベートLAN内に許可しました"; return Task.CompletedTask; }, _ => !Demo);
        SwitchMode = Command(async _ => { if (!await host.SwitchAsync(!host.Demo)) Notice = "終了時出力を確認できないためモードを切り替えられません"; }, _ => !cpuFanBusy && !boardLightBusy);
        SaveCurve = Command(p => SaveCurveAsync(p?.ToString() == "apply"));
        ReloadCurve = Command(_ => { LoadCurve(); return Task.CompletedTask; });
        AddPoint = Command(p => { var cpu = p?.ToString() == "cpu"; AddCurvePoint(cpu ? CpuPoints : GpuPoints, HubCurveMinimum, HubCurveAllowStop, row => { if (cpu) SelectedCpuPoint = row; else SelectedGpuPoint = row; }); return Task.CompletedTask; });
        RemovePoint = Command(p => { if (p?.ToString() == "cpu" && SelectedCpuPoint is not null) CpuPoints.Remove(SelectedCpuPoint); else if (p?.ToString() == "gpu" && SelectedGpuPoint is not null) GpuPoints.Remove(SelectedGpuPoint); return Task.CompletedTask; });
        CalculatePreview = Command(_ => { CalculateCurvePreview(); return Task.CompletedTask; });
        ExportLog = Command(_ => { var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "診断アーカイブ (*.zip)|*.zip", FileName = $"FlowDeck-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.zip" }; if (dialog.ShowDialog() == true) { host.Log.Export(dialog.FileName, new { snapshot = state, settings = core.Settings, motherboard = host.Motherboard.Board, rgb = host.Rgb, worker = host.Hub.Version }); Notice = "診断ログを保存しました"; } return Task.CompletedTask; });
        VirtualButton = Command(p => { VirtualPress(p!.ToString()!); return Task.CompletedTask; }, _ => Demo && virtualConnected);
        DemoFault = Command(p => { if (host.Hub is not DemoHub hub) return Task.CompletedTask; switch (p?.ToString())
            { case "partial": hub.FailNextFanWrite = true; Notice = "次の出力変更でFAN3の不一致を再現します"; break;
              case "hub": hub.Disconnected = !hub.Disconnected; Notice = hub.Disconnected ? "デモHubを切断しました" : "デモHubを復帰しました"; break;
              case "sensor": host.DemoTemperature!.Missing = !host.DemoTemperature.Missing; break;
              case "m5": virtualConnected = !virtualConnected; virtualProtocol!.Disconnect(); virtualPage = "home"; if (virtualConnected) HelloVirtual(); break; }
            Refresh(); return Task.CompletedTask; }, _ => Demo);
        timer = new(TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
        { virtualProtocol?.Tick(); OnState(core.Snapshot); CommandManager.InvalidateRequerySuggested(); }, dispatcher);
        Attach(); timer.Start();
    }
    public ICommand Navigate { get; } public ICommand ChooseMode { get; } public ICommand ChangeDuty { get; } public ICommand ApplyFan { get; }
    public ICommand RevertFan { get; } public ICommand Resume { get; } public ICommand StopControl { get; } public ICommand TogglePreview { get; }
    public ICommand SelectLight { get; } public ICommand ApplyLight { get; } public ICommand RevertLight { get; } public ICommand Probe { get; }
    public ICommand ConnectHub { get; } public ICommand SaveSettings { get; } public ICommand RefreshSettings { get; } public ICommand RefreshPorts { get; }
    public ICommand ConnectM5 { get; } public ICommand DisconnectM5 { get; } public ICommand SwitchMode { get; } public ICommand SaveCurve { get; }
    public ICommand ReloadCurve { get; } public ICommand AddPoint { get; } public ICommand RemovePoint { get; } public ICommand CalculatePreview { get; }
    public ICommand ExportLog { get; } public ICommand VirtualButton { get; } public ICommand DemoFault { get; } public ICommand AllowWifi { get; }
    public ObservableCollection<ProfileOption> Profiles { get; } = [];
    public ObservableCollection<HubIdentity> HubChoices { get; } = [];
    public ObservableCollection<SensorOption> CpuSensors { get; } = [];
    public ObservableCollection<SensorOption> GpuSensors { get; } = [];
    public ObservableCollection<LedRow> LedDetails { get; } = [];
    public ObservableCollection<string> Ports { get; } = [];
    public ObservableCollection<CurvePointRow> CpuPoints { get; } = [];
    public ObservableCollection<CurvePointRow> GpuPoints { get; } = [];
    public ObservableCollection<OperationResult> History { get; } = [];
    public IEnumerable<OperationResult> FilteredHistory => History.Where(r => DiagnosticFilter switch { "エラー" => r.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified), "PC操作" => r.Source == "PC", "M5GO操作" => r.Source == "M5GO", _ => true });
    public string[] DiagnosticFilters { get; } = ["全件", "エラー", "PC操作", "M5GO操作"];
    public string DiagnosticFilter { get => diagnosticFilter; set { Set(ref diagnosticFilter, value); Notify(nameof(FilteredHistory)); } }
    public OperationResult? SelectedOperation { get => selectedOperation; set { Set(ref selectedOperation, value); Notify(nameof(OperationDetail)); } }
    private OperationResult? selectedOperation;
    public string OperationDetail => SelectedOperation is null ? "履歴を選択すると要求ID・チャンネル別結果を表示します" : JsonSerializer.Serialize(SelectedOperation, new JsonSerializerOptions(Contract.Json) { WriteIndented = true });
    public int SelectedPage { get => page; set { Set(ref page, value); Notify(nameof(PageTitle)); } }
    public string PageTitle => new[] { "モニター", "NZXT 温度カーブ", "照明", "接続・設定", "診断", "マザーボード", "表示項目", "冷却", "冷却", "通知", "設定の書き出し・復元" }[page];
    public bool Demo => host.Demo;
    public string ModeLabel => Demo ? "DEMO・実機未接続" : "実機モード";
    public string SwitchLabel => Demo ? "実機モードへ" : "デモを開く";
    public string HubLabel => state.HubReady ? "● Hub 接続" : "○ Hub 未接続";
    public string M5Label => (Demo ? virtualConnected : host.M5?.Protocol.Connected == true) ? "● M5GO 接続" : "○ M5GO 未接続";
    public string M5Sync => Demo ? virtualConnected ? virtualProtocol?.Synchronized == true ? "同期済み（デモ）" : "同期中（デモ）" : "未接続・PC制御は継続" :
        host.M5?.Protocol.Connected == true ? host.M5.Protocol.Synchronized ? "同期済み" : "同期中・状態採用待ち" : "未接続・PC制御は継続";
    public string DutyDisplay => state.ConfirmedDuty?.ToString() ?? "--";
    public string CurrentMode => state.Mode switch { FanMode.Auto => "自動 / " + state.LastConfirmed?.Profile?.Name, FanMode.Manual => "手動", FanMode.Monitor => "監視のみ", FanMode.Fallback => "異常時制御", _ => "状態未確認" };
    public string Confirmation => state.FanBusy ? $"適用中・目標 {state.TargetDuty}%" : state.Faulted ? state.Fault ?? "異常時制御" :
        state.ConfirmedDuty is not null ? "✓ 全対象の出力報告を確認" : "出力未確認";
    public string CpuTemperature => Temperature(core.Settings.CpuSensorId);
    public string GpuTemperature => Temperature(core.Settings.GpuSensorId);
    public string CpuName => state.Sensors.SingleOrDefault(s => s.Id == core.Settings.CpuSensorId)?.Name ?? "CPUセンサー未選択";
    public string GpuName => state.Sensors.SingleOrDefault(s => s.Id == core.Settings.GpuSensorId)?.Name ?? "GPUセンサー未選択";
    public string TemperatureHint => Demo ? "温度・回転数はデモ値" : "温度とRPMの有効期限：3秒";
    public string Banner => state.Faulted ? state.Fault! : "";
    public bool HasBanner => Banner.Length > 0;
    public bool CanResume => state.CanResume;
    public bool CanApplyFan => state.HubReady && !state.FanBusy && !state.Faulted && state.Readiness == "制御可能" &&
        (DraftMode == "Auto" ? SelectedProfile?.Ready == true && core.ProfileReadiness(SelectedProfile.Id) is null : int.TryParse(ManualText, out var d) && FanPolicyEngine.IsDutyAllowed(d, core.Settings.Limits));
    public bool CanApplyLight => state.HubReady && !state.LightingBusy && core.Settings.Hardware.Leds is { Length: > 0 };
    public string DraftMode { get => draftMode; set { if (draftMode == value) return; BeginFanEdit(); Set(ref draftMode, value); Refresh(); } }
    public int MinimumDuty => core.Settings.Limits.AllowStop ? 0 : core.Settings.Limits.Minimum ?? 1;
    public string ManualText { get => manualText; set { BeginFanEdit(); Set(ref manualText, value); Notify(nameof(ManualDuty)); Notify(nameof(FanDraftLabel)); CommandManager.InvalidateRequerySuggested(); } }
    public int ManualDuty { get => int.TryParse(manualText, out var d) ? d : MinimumDuty; set => ManualText = value.ToString(); }
    public string FanDraftLabel => state.FanBusy ? "適用中" : editingFan ? fanEditRevision != state.ControlRevision ? "別の操作で変更あり・最新状態を確認" : "未適用の変更案" : "現在の設定";
    public ProfileOption? SelectedProfile { get => selectedProfile; set { if (Set(ref selectedProfile, value)) BeginFanEdit(); } }
    public bool PreviewOpen { get => previewOpen; set { Set(ref previewOpen, value); Notify(nameof(PreviewLabel)); } }
    public string PreviewLabel => PreviewOpen ? "端末を閉じる" : "端末を表示";
    public string SelectedLight { get => selectedLight; set { Set(ref selectedLight, value); Notify(nameof(SelectedLightName)); Notify(nameof(LightDraftLabel)); } }
    public string SelectedLightName => LightName(SelectedLight);
    public string LightingSummary => state.Lighting.LastSent is null ? "未送信" : LightName(state.Lighting.LastSent) + "・" + (state.Lighting.Generation == state.DeviceGeneration ? "送信済み" : "前回接続時") +
        (state.Lighting.Channels is { Length: > 0 } channels ? " · " + string.Join(" / ", channels).ToUpperInvariant() : "");
    public string LightingTimestamp => state.Lighting.SentAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") ?? "—";
    public string LightDraftLabel => editingLighting && lightingEditRevision != state.LightingRevision ? "別の操作で照明が変更されました" : editingLighting ? "未送信の変更案" : "候補を選んで送信";
    public string Notice { get => notice; set => Set(ref notice, value); }
    public string Footer => state.Operation is { } op ? $"{op.Time.ToLocalTime():HH:mm:ss}  {op.Source}  {op.Message}" : "監視中・出力を変更するには設定を完了してください";
    public string VersionInfo => $"FlowDeck 1.9.0 / .NET {Environment.Version}\n{host.Hub.Version}\nM5GO: {host.M5?.Protocol.FirmwareVersion ?? (Demo ? "仮想端末" : "未接続")} / protocol 1" + (Demo ? "" : "\n" + HardwareTemperatures.AccessStatus);
    public HubIdentity? SelectedHub { get; set; }
    public SensorOption? SelectedCpu { get; set; }
    public SensorOption? SelectedGpu { get; set; }
    public string? SelectedPort { get; set; }
    public string SelectedLayout { get; set; } = "A";
    public string M5Transport { get; set; } = "usb";
    public sealed record TransportChoice(string Id, string Name);
    public TransportChoice[] M5Transports { get; } = [new("usb", "USB"), new("wifi", "Wi-Fi"), new("auto", "自動（USB優先）")];
    public ObservableCollection<M5LanAddress> LanAddresses { get; } = [];
    public M5LanAddress? SelectedLan { get; set; }
    public string WifiSsid { get; set; } = "";
    private bool pairing;
    public bool CanPairWifi => !Demo && !pairing && host.Serial?.UsbDeviceId is not null;
    public string WifiConnectionDetail
    {
        get
        {
            var m = host.M5;
            if (m is null) return "M5GO通信は停止中です";
            var seen = m.LastStatusAt is { } t ? $" / 最終受信 {(DateTimeOffset.UtcNow - t).TotalSeconds:F0}秒前" : "";
            var error = m.Error ?? m.Wifi?.Error ?? (m.ActiveTransport == "Wi-Fi" ? null : m.Serial?.Error);
            return $"使用中：{m.ActiveTransport} / USB：{m.Serial?.UsbDeviceId ?? "未確認"}\n" +
                $"Wi-Fi：{m.WifiStatus ?? (m.Identity is null ? "未登録" : "端末の応答待ち")} / {m.Ssid ?? "—"}\n" +
                $"端末IP：{m.Ip ?? "—"} / 電波：{m.Rssi?.ToString() ?? "—"} dBm{seen}\n" +
                (m.Wifi?.Authenticated == true ? "TLS認証済み" : m.Wifi?.Listening == true ? "PCはWi-Fi接続を待受中" : "PCのWi-Fi待受は停止中") +
                (error is not null ? "\n" + error : "");
        }
    }
    private M5Settings M5Draft() => core.Settings.M5 with
    { Port = SelectedPort, DeviceId = host.Serial?.UsbDeviceId ?? host.M5?.Protocol.DeviceId ?? core.Settings.M5.DeviceId,
      Layout = SelectedLayout, Transport = M5Transport, WifiAddress = SelectedLan?.Address, WifiInterfaceId = SelectedLan?.InterfaceId };
    private async Task ApplyM5ConnectionAsync()
    {
        var config = M5Draft();
        if (config.Transport != "usb" && SelectedLan is null) throw new InvalidOperationException("PCのLANアドレスを選択してください");
        await host.DisconnectM5Async(); await core.UpdateSettingsAsync(core.Settings with { M5 = config }, false);
        await host.FlushSettingsAsync(); host.ConnectM5(config); Refresh();
    }
    public async Task PairWifiAsync(string password)
    {
        if (!CanPairWifi || host.M5 is null) return;
        pairing = true; Refresh();
        try
        {
            var lan = SelectedLan ?? throw new InvalidOperationException("PCのLANアドレスを選択してください");
            var config = M5Draft();
            await host.M5.PairAsync(WifiSsid, password, lan, M5Transport);
            await core.UpdateSettingsAsync(core.Settings with { M5 = config }, false); await host.FlushSettingsAsync();
            await host.DisconnectM5Async(); host.ConnectM5(config);
            Notice = "Wi-Fi設定をM5GOに登録しました。接続方式がUSBの場合は、自動またはWi-Fiを選んで接続してください";
        }
        catch (Exception e) { Notice = "Wi-Fi登録: " + e.Message; }
        finally { pairing = false; Refresh(); }
    }
    public string[] Layouts { get; } = ["A", "B"];
    public bool WiringChecked { get; set; }
    public string WiringDescription => string.Join("\n", core.LastStatus.Channels.Select(c => $"{c.Id.ToUpperInvariant()}  {c.ControlMode ?? "接続未確認"}  {c.Rpm?.ToString() ?? "—"} rpm"));
    public string MinimumText { get; set; } = "";
    public string FallbackText { get; set; } = "";
    public string ExitText { get; set; } = "";
    public bool AllowStop { get; set; }
    public string StopCpuText { get; set; } = "";
    public string StopGpuText { get; set; } = "";
    public string RestartDutyText { get; set; } = "";
    public string RestartSecondsText { get; set; } = "";
    public bool ProtectManualCpu { get; set; }
    public bool ProtectManualGpu { get; set; }
    public string ManualCpuOverheat { get; set; } = "";
    public string ManualCpuResume { get; set; } = "";
    public string ManualGpuOverheat { get; set; } = "";
    public string ManualGpuResume { get; set; } = "";
    public bool ResumeStartup { get; set; }
    public bool LoginStartup { get; set; }
    public string PythonPath { get; set; } = "";
    public string Theme { get; set; } = "system";
    public string[] Themes { get; } = ["system", "light", "dark"];
    public ProfileOption? CurveProfile { get => curveProfile; set { if (Set(ref curveProfile, value)) LoadCurve(); } }
    public bool UseCpu { get; set; }
    public bool UseGpu { get; set; }
    public string CpuOverheat { get; set; } = "";
    public string CpuResume { get; set; } = "";
    public string GpuOverheat { get; set; } = "";
    public string GpuResume { get; set; } = "";
    public string DescentDelay { get; set; } = "";
    public string DescentRate { get; set; } = "";
    public string PreviewCpu { get; set; } = "50";
    public string PreviewGpu { get; set; } = "50";
    public string CurvePreview { get; private set; } = "計算のみ・実機には送信しません";
    public string CurveStatus => state.LastConfirmed?.Profile is { } current && core.Settings.Profiles.Any(p => p.Id == current.Id && p.Revision != current.Revision)
        ? "保存した変更は現在の制御に未適用です" : "保存だけでは実行中の制御を変更しません";
    public string VirtualDisplay => !(Demo ? virtualConnected : host.M5?.Protocol.Connected == true) ? "M5GOは未接続です\nPC側の制御は継続できます" : virtualPage switch
    {
        "lightMenu" or "lightGroups" or "lightScenes" or "lightBrightness" => VirtualLightingDisplay,
        "manual" => $"全ファン・手動出力\n現在 {CurrentMode} / {DutyDisplay}%\n\n{virtualDuty} %  未適用\n{VirtualConflict}\nA −5    B 一括適用    C ＋5",
        "auto" => $"全ファン・自動モード\n\n{Profiles.ElementAtOrDefault(virtualSelection)?.Name}\n{VirtualConflict}\nA 前へ    B 適用    C 次へ",
        "lighting" => $"RGB・全チャンネル\n\n{LightName(new[] { "blue", "white", "purple", "off" }[virtualSelection])}\n{VirtualConflict}\nA 前へ    B 送信    C 次へ",
        "pending" => "適用中\n\n処理結果を待っています",
        "result" => $"適用結果\n\n{virtualMessage}\nB: {(virtualLightingResult ? "照明へ" : state.CanResume ? "制御を再開" : "閉じる")}",
        "motherboard" => VirtualMotherboard,
        _ => $"FlowDeck       {(state.HubReady ? "接続済み" : "Hub切断")}\n{CurrentMode}   {DutyDisplay}%\nCPU {CpuTemperature}  GPU {GpuTemperature}\n" +
            string.Join("  ", Enumerable.Range(1, 5).Select(i => $"FAN {i}:{state.Channels.FirstOrDefault(c => c.Id == "fan" + i)?.Rpm?.ToString("N0") ?? "—"}")) + $"\n{(state.Faulted ? "異常あり・Bで詳細" : "A 自動    B 手動    C 照明")}" 
    };
    private string VirtualConflict => virtualEditRevision != (virtualPage == "lighting" ? state.LightingRevision : state.ControlRevision) ? "PC側で変更あり" : "B長押しで取り消し";
    private AsyncCommand Command(Func<object?, Task> run, Func<object?, bool>? can = null) => new(run, can, e => { Notice = e.Message; host.Log.Write("ui", new { error = e.ToString() }); });
    private ControlRequest Request(string name, long revision) => new(Guid.NewGuid().ToString("N"), "PC", core.Epoch, revision, name);
    private string Temperature(string? id) => state.Sensors.SingleOrDefault(s => s.Id == id)?.Celsius is { } t ? $"{t:F0} °C" : "— °C";
    private static string LightName(string? id) => id switch { "blue" => "ブルー", "white" => "ホワイト", "purple" => "パープル", "off" => "消灯", _ => HubLighting.Name(id) };
    private void BeginFanEdit() { if (!editingFan) { fanEditRevision = state.ControlRevision; editingFan = true; } Notify(nameof(FanDraftLabel)); }
    private void ResetFanDraft()
    {
        fanEditRevision = state.ControlRevision; editingFan = false;
        draftMode = state.LastConfirmed?.Mode == FanMode.Manual ? "Manual" : "Auto";
        manualText = (state.ConfirmedDuty ?? core.Settings.Limits.Minimum)?.ToString() ?? "";
        selectedProfile = Profiles.FirstOrDefault(p => p.Id == state.LastConfirmed?.Profile?.Id) ?? Profiles.FirstOrDefault(); Refresh();
    }
    private async Task ApplyFanAsync()
    {
        var request = DraftMode == "Manual" ? Request("SetManualDuty", fanEditRevision) with { Duty = int.Parse(ManualText) }
            : Request("SelectAutoProfile", fanEditRevision) with { ProfileId = SelectedProfile!.Id, ProfileRevision = SelectedProfile.Revision };
        var result = await core.RequestAsync(request); Show(result); if (result.Outcome == Outcome.Confirmed) ResetFanDraft();
    }
    private void Show(OperationResult result) { Notice = result.Message; state = core.Snapshot; Refresh(); }
    private void Attach()
    {
        LightPalette = new ColorPalette(host.Store.DirectoryPath);
        LoadNotificationSettings();
        identificationTarget = null; identificationSignature = ""; identificationResult = ""; lightAlias = ""; IdentificationTargets.Clear();
        deletedLightingScene = null; selectedLightingScene = null; lightingSceneResult = ""; LightingSceneName = "";
        SceneIncludeHub = core.Settings.Hardware.Hub is not null; SceneIncludeBoard = host.Rgb.Devices.Any(d => d.ControlIdentity is not null);
        RefreshLightingScenes();
        deletedLightingGroup = null; selectedLightingGroup = null; lightingGroupResult = ""; RefreshLightingGroups();
        MasterBrightness = host.LightingAdjustment.Brightness; brightnessBusy = false;
        brightnessResult = host.LightingAdjustment.BaseScene is not null ? "復帰する設定があります" : "";
        core.Changed += OnState; core.OperationCompleted += OnOperation;
        state = core.Snapshot; LoadSettings(); UpdateProfiles(); ResetFanDraft(); lightingEditRevision = state.LightingRevision;
        LoadHubLighting(); lastLightInventory = null; lastLightSettings = null; boardLightTarget = null; LoadMotherboardSettings();
        fanDrafts.Clear(); BoardFanRows.Clear(); BoardFanTemperatures.Clear(); LoadCpuFanSettings();
        dashboardHistory = new(); dashboardLogged = false; LoadDashboard();
        if (Demo) { virtualProtocol = new(core, motherboard: host.BoardPanel, lighting: host); virtualProtocol.Outgoing += VirtualReceive; HelloVirtual(); }
        OnState(state); Notice = host.Warning ?? (host.SettingsRestoreMessage.Length > 0 ? host.SettingsRestoreMessage : Demo ? "デモ操作は実機に送信されません" : "初回は監視のみ。接続・設定から開始してください");
    }
    private void Reinitialize()
    {
        settingsRestorePreview = null; settingsTransferFile = settingsTransferResult = ""; SettingsDifferences.Clear(); SettingsDeviceChecks.Clear();
        core.Changed -= OnState; core.OperationCompleted -= OnOperation;
        if (virtualProtocol is not null) { virtualProtocol.Outgoing -= VirtualReceive; virtualProtocol = null; }
        core = host.Coordinator; editingFan = editingLighting = false; virtualPage = "home"; virtualConnected = true;
        previewOpen = false; History.Clear(); Attach(); ThemeManager.Apply(core.Settings.Ui.Theme); Refresh();
    }
    private void OnState(ControlSnapshot snapshot) => dispatcher.BeginInvoke(() =>
    {
        state = snapshot;
        UpdateSensors();
        RefreshMotherboard(); RefreshLightingGroupTargets(); RefreshHubLighting(); RefreshRemoteBrightness(); RefreshIdentificationTargets();
        RefreshCpuFan();
        RefreshDashboard(); RefreshNotifications(); RefreshDeviceCatalog();
        if (!editingFan && !snapshot.FanBusy) ResetFanDraft();
        if (!editingLighting) { lightingEditRevision = snapshot.LightingRevision; selectedLight = snapshot.Lighting.LastSent ?? "blue"; }
        virtualProtocol?.SendState(); Refresh(); CommandManager.InvalidateRequerySuggested();
    });
    private void OnOperation(OperationResult result) => dispatcher.BeginInvoke(() =>
    { History.Insert(0, result); while (History.Count > 256) History.RemoveAt(History.Count - 1); Notify(nameof(FilteredHistory)); });
    private void UpdateProfiles()
    {
        var selectedId = selectedProfile?.Id; var curveId = curveProfile?.Id;
        Replace(Profiles, core.Settings.Profiles.Select(p => new ProfileOption(p.Id, p.Name + (FanPolicyEngine.ValidateProfile(p, core.Settings.Limits) is null ? "" : "（未設定）"),
            p.Revision, FanPolicyEngine.ValidateProfile(p, core.Settings.Limits) is null)));
        selectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles.FirstOrDefault();
        curveProfile = Profiles.FirstOrDefault(p => p.Id == curveId) ?? Profiles.FirstOrDefault();
    }
    private void UpdateSensors()
    {
        var cpuId = SelectedCpu?.Id ?? core.Settings.CpuSensorId; var gpuId = SelectedGpu?.Id ?? core.Settings.GpuSensorId;
        var options = state.Sensors.Select(s => new SensorOption(s.Id, $"{s.Name} / {(s.Celsius is { } t ? t.ToString("F1") + " °C" : "取得不可")}", s.Kind)).ToArray();
        Replace(CpuSensors, options.Where(s => s.Kind == "cpu")); Replace(GpuSensors, options.Where(s => s.Kind == "gpu"));
        if (cpuId is not null && CpuSensors.All(s => s.Id != cpuId)) CpuSensors.Add(new(cpuId, cpuId + " / 取得不可", "cpu"));
        if (gpuId is not null && GpuSensors.All(s => s.Id != gpuId)) GpuSensors.Add(new(gpuId, gpuId + " / 取得不可", "gpu"));
        SelectedCpu = CpuSensors.FirstOrDefault(s => s.Id == cpuId); SelectedGpu = GpuSensors.FirstOrDefault(s => s.Id == gpuId);
    }
    private void LoadSettings()
    {
        var s = core.Settings; var l = s.Limits;
        MinimumText = Format(l.Minimum); FallbackText = Format(l.Fallback); ExitText = Format(l.Exit); AllowStop = l.AllowStop;
        StopCpuText = Format(l.Stop?.CpuBelow); StopGpuText = Format(l.Stop?.GpuBelow); RestartDutyText = Format(l.Stop?.RestartDuty); RestartSecondsText = Format(l.Stop?.RestartSeconds);
        ProtectManualCpu = l.ProtectManualCpu; ProtectManualGpu = l.ProtectManualGpu;
        ManualCpuOverheat = Format(l.CpuOverheat); ManualCpuResume = Format(l.CpuResumeBelow); ManualGpuOverheat = Format(l.GpuOverheat); ManualGpuResume = Format(l.GpuResumeBelow);
        ResumeStartup = s.Startup.ResumeControl; LoginStartup = s.Startup.Login; PythonPath = s.PythonPath ?? Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
        Theme = s.Ui.Theme; SelectedPort = s.M5.Port; SelectedLayout = s.M5.Layout; WiringChecked = s.Hardware.WiringConfirmed;
        M5Transport = s.M5.Transport;
        Replace(LanAddresses, Demo ? [] : M5Network.Addresses());
        SelectedLan = LanAddresses.FirstOrDefault(a => a.InterfaceId == s.M5.WifiInterfaceId) ?? LanAddresses.FirstOrDefault(a => a.Address == s.M5.WifiAddress) ?? LanAddresses.FirstOrDefault();
        if (s.Hardware.Hub is { } h && HubChoices.All(c => c.Id != h.Id)) HubChoices.Add(h);
        SelectedHub = HubChoices.FirstOrDefault(c => c.Id == s.Hardware.Hub?.Id);
        Replace(LedDetails, core.LastStatus.Lighting.Select(l => new LedRow(l.Id, l.Accessory, s.Hardware.LedDetails?.SingleOrDefault(d => d.Id == l.Id)?.LedCount)));
        if (!Demo) Replace(Ports, M5SerialSession.Ports);
        UpdateSensors(); UpdateProfiles(); LoadCurve(); Refresh();
    }
    private async Task SaveConnectionSettingsAsync()
    {
        var old = core.Settings;
        var limits = new FanLimits(ParseInt(MinimumText), ParseInt(FallbackText), ParseInt(ExitText), AllowStop,
            AllowStop ? new(RequiredDouble(StopCpuText), RequiredDouble(StopGpuText), RequiredInt(RestartDutyText), RequiredDouble(RestartSecondsText)) : null,
            ProtectManualCpu, ProtectManualGpu, ParseDouble(ManualCpuOverheat), ParseDouble(ManualCpuResume), ParseDouble(ManualGpuOverheat), ParseDouble(ManualGpuResume));
        if (limits.Minimum is not null || limits.Fallback is not null || limits.Exit is not null)
            if (FanPolicyEngine.ValidateLimits(limits) is { } error) throw new InvalidOperationException(error);
        if (WiringChecked && !state.HubReady) throw new InvalidOperationException("Hubに接続して検出構成を確認してください");
        var data = core.LastStatus;
        var allFans = data.Channels.Where(c => c.ControlMode is "DC" or "PWM").Select(c => c.Id).ToArray();
        if (WiringChecked && allFans.Length == 0) throw new InvalidOperationException("ファンの接続方式を確認できません");
        var ledDetails = LedDetails.Select(l => new LedChannel(l.Id, l.Accessory, ParseInt(l.Count))).ToArray();
        if (ledDetails.Any(l => l.LedCount is <= 0)) throw new InvalidOperationException("LED数は正の整数で設定してください");
        if (ledDetails.Any(l => l.LedCount is not null) && ledDetails.Any(l => l.LedCount is null))
            throw new InvalidOperationException("照明を有効にする場合は接続した全対象のLED数を確認してください");
        var next = old with
        {
            Hardware = new(old.Hardware.Hub, allFans, WiringChecked ? ledDetails.Where(l => l.LedCount is not null).Select(l => l.Id).ToArray() : [],
                WiringChecked ? ControlCoordinator.WiringSignature(data) : null, WiringChecked, ledDetails),
            Limits = limits, CpuSensorId = SelectedCpu?.Id, GpuSensorId = SelectedGpu?.Id,
            M5 = M5Draft(),
            Startup = new(LoginStartup, ResumeStartup), PythonPath = string.IsNullOrWhiteSpace(PythonPath) ? null : Path.GetFullPath(PythonPath),
            Ui = old.Ui with { Theme = Theme }
        };
        var changedControls = JsonSerializer.Serialize(new { old.Hardware, old.Limits, old.CpuSensorId, old.GpuSensorId }, Contract.Json)
            != JsonSerializer.Serialize(new { next.Hardware, next.Limits, next.CpuSensorId, next.GpuSensorId }, Contract.Json);
        await core.UpdateSettingsAsync(next with { LastConfirmedPolicy = changedControls ? null : old.LastConfirmedPolicy }, changedControls);
        await host.FlushSettingsAsync();
        if (!Demo) RuntimeHost.ConfigureLogin(LoginStartup);
        ThemeManager.Apply(Theme); UpdateProfiles(); ResetFanDraft(); Notice = UsesService ? "設定を保存しました" : "設定を保存しました。Python実行環境の変更は再起動後に有効です"; Refresh();
    }
    private void LoadCurve()
    {
        var p = core.Settings.Profiles.SingleOrDefault(p => p.Id == CurveProfile?.Id);
        if (p is null) return;
        profileEditRevision = p.Revision; curveEditRevision = state.ControlRevision;
        UseCpu = p.Cpu is not null; UseGpu = p.Gpu is not null;
        Replace(CpuPoints, (p.Cpu?.Points ?? []).Select(p => new CurvePointRow(p.Temperature, p.Duty)));
        Replace(GpuPoints, (p.Gpu?.Points ?? []).Select(p => new CurvePointRow(p.Temperature, p.Duty)));
        CpuOverheat = Format(p.Cpu?.Overheat); CpuResume = Format(p.Cpu?.ResumeBelow); GpuOverheat = Format(p.Gpu?.Overheat); GpuResume = Format(p.Gpu?.ResumeBelow);
        DescentDelay = Format(p.DescentDelaySeconds); DescentRate = Format(p.DescentPerSecond); Refresh();
    }
    private AutoProfile BuildCurve()
    {
        var old = core.Settings.Profiles.Single(p => p.Id == CurveProfile!.Id);
        return old with { Cpu = UseCpu ? new(core.Settings.CpuSensorId ?? "", CpuPoints.Select(p => new CurvePoint(p.Temperature, p.Duty)).ToArray(), RequiredDouble(CpuOverheat), RequiredDouble(CpuResume)) : null,
            Gpu = UseGpu ? new(core.Settings.GpuSensorId ?? "", GpuPoints.Select(p => new CurvePoint(p.Temperature, p.Duty)).ToArray(), RequiredDouble(GpuOverheat), RequiredDouble(GpuResume)) : null,
            DescentDelaySeconds = RequiredDouble(DescentDelay), DescentPerSecond = RequiredDouble(DescentRate) };
    }
    private async Task SaveCurveAsync(bool apply)
    {
        var saved = await core.SaveProfileAsync(BuildCurve(), profileEditRevision); profileEditRevision = saved.Revision; UpdateProfiles();
        await host.FlushSettingsAsync();
        Notice = "プリセットを保存しました。現在の制御への適用は別操作です";
        if (apply)
        {
            var result = await core.RequestAsync(Request("SelectAutoProfile", curveEditRevision) with { ProfileId = saved.Id, ProfileRevision = saved.Revision });
            Notice = result.Outcome == Outcome.Confirmed ? "保存済み・適用確認済み" : "保存済み / " + result.Message;
            if (result.Outcome == Outcome.Confirmed) curveEditRevision = core.Snapshot.ControlRevision;
        }
        Refresh();
    }
    private void CalculateCurvePreview()
    {
        var p = BuildCurve(); if (FanPolicyEngine.ValidateProfile(p, core.Settings.Limits) is { } error) throw new InvalidOperationException(error);
        var temperatures = new Dictionary<string, double>();
        var cpu = p.Cpu is null ? (double?)null : FanPolicyEngine.Interpolate(p.Cpu.Points, RequiredDouble(PreviewCpu));
        var gpu = p.Gpu is null ? (double?)null : FanPolicyEngine.Interpolate(p.Gpu.Points, RequiredDouble(PreviewGpu));
        if (p.Cpu is not null) temperatures[p.Cpu.SensorId] = RequiredDouble(PreviewCpu);
        if (p.Gpu is not null) temperatures[p.Gpu.SensorId] = RequiredDouble(PreviewGpu);
        CurvePreview = $"CPU {cpu?.ToString("F1") ?? "未使用"}% / GPU {gpu?.ToString("F1") ?? "未使用"}% → 共通 {FanPolicyEngine.Calculate(p, core.Settings.Limits, temperatures)}%\n計算のみ・実機には送信しません"; Notify(nameof(CurvePreview));
    }
    private void HelloVirtual() => virtualProtocol!.Receive(JsonSerializer.Serialize(new { v = 1, type = "hello", deviceType = "M5GO", deviceId = "virtual-m5", bootId = Guid.NewGuid().ToString("N"), firmwareVersion = "DEMO-1.3", capabilities = new[] { "motherboard-v1", "lighting-v1" } }));
    private void VirtualReceive(string line)
    {
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(() => VirtualReceive(line)); return; }
        if (!virtualConnected) return;
        using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
        var type = r.GetProperty("type").GetString();
        if (type == "helloRequest") { HelloVirtual(); return; }
        if (type == "welcome") { virtualSession = r.GetProperty("sessionId").GetString()!; virtualSequence = 0; virtualPage = "home"; virtualLightScope = "all"; virtualLightState = default; virtualLightingResult = false; }
        if (type is "lightingState" or "lightingCatalog") ReceiveVirtualLighting(r);
        if (type == "heartbeat") SendVirtual("heartbeat", new Dictionary<string, object?>());
        if (type == "state")
        {
            virtualState = r.Clone();
            SendVirtual("stateApplied", new() { ["snapshotSequence"] = r.GetProperty("snapshotSequence").GetInt64(),
                ["controlRevision"] = r.GetProperty("controlRevision").GetInt64(), ["lightingRevision"] = r.GetProperty("lightingRevision").GetInt64() });
        }
        if (type == "result")
        {
            var outcome = r.GetProperty("outcome").GetString(); virtualMessage = r.GetProperty("message").GetString()!;
            virtualPage = outcome is "confirmed" or "writtenUnverified" ? "home" : "result";
        }
        dispatcher.BeginInvoke(() => { Notify(nameof(VirtualDisplay)); Notify(nameof(M5Sync)); });
    }
    private void SendVirtual(string type, Dictionary<string, object?> data)
    { data["v"] = 1; data["type"] = type; data["sessionId"] = virtualSession; data["controllerEpoch"] = core.Epoch; virtualProtocol!.Receive(JsonSerializer.Serialize(data)); }
    private void VirtualPress(string button)
    {
        if (!Demo || !virtualConnected || virtualPage == "pending") return;
        if (button == "long") { virtualPage = virtualPage == "home" ? "motherboard" : "home"; Notify(nameof(VirtualDisplay)); return; }
        if (VirtualLightingPress(button)) { Notify(nameof(VirtualDisplay)); return; }
        if (virtualPage == "motherboard") { if (button == "B") virtualPage = "home"; else boardPreviewPage = 1 - boardPreviewPage; Notify(nameof(VirtualDisplay)); return; }
        if (virtualPage == "home")
        {
            if (!state.HubReady) return;
            virtualPage = button == "A" ? "auto" : button == "C" ? "lighting" : state.Faulted ? "result" : "manual";
            if (state.Faulted && button == "A") virtualPage = "result";
            virtualMessage = state.Fault ?? ""; virtualDuty = state.ConfirmedDuty ?? MinimumDuty; virtualSelection = 0;
            virtualEditRevision = button == "C" ? state.LightingRevision : state.ControlRevision;
            virtualProfileRevision = Profiles.FirstOrDefault()?.Revision ?? 0;
        }
        else if (button == "B")
        {
            if (virtualPage == "result" && !state.CanResume) virtualPage = "home";
            else
            {
                var command = virtualPage switch { "manual" => "SetManualDuty", "auto" => "SelectAutoProfile", "lighting" => "SetLighting", _ => "ResumeControl" };
                virtualLightingResult = command == "SetLighting";
                var payload = virtualPage switch
                {
                    "manual" => new Dictionary<string, object?> { ["duty"] = virtualDuty },
                    "auto" => new() { ["profileId"] = Profiles[virtualSelection].Id, ["profileRevision"] = virtualProfileRevision },
                    "lighting" => new() { ["preset"] = new[] { "blue", "white", "purple", "off" }[virtualSelection] }, _ => new()
                };
                var revision = virtualPage == "result" ? state.ControlRevision : virtualEditRevision;
                virtualPage = "pending";
                SendVirtual("command", new() { ["id"] = "virtual-" + ++virtualSequence, ["commandSequence"] = virtualSequence, ["expectedRevision"] = revision, ["command"] = command, ["payload"] = payload });
            }
        }
        else
        {
            var delta = button == "A" ? -1 : 1;
            if (virtualPage == "manual") virtualDuty = Math.Clamp(virtualDuty + delta * 5, MinimumDuty, 100);
            else if (virtualPage is "auto" or "lighting") { var count = virtualPage == "auto" ? 3 : 4; virtualSelection = (virtualSelection + delta + count) % count; virtualProfileRevision = Profiles.ElementAtOrDefault(virtualSelection)?.Revision ?? 0; }
        }
        Notify(nameof(VirtualDisplay));
    }
    private static string Format(object? value) => value is IFormattable format ? format.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
    private static int? ParseInt(string text) => string.IsNullOrWhiteSpace(text) ? null : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new InvalidOperationException("整数を入力してください: " + text);
    private static int RequiredInt(string text) => ParseInt(text) ?? throw new InvalidOperationException("必須の整数が未入力です");
    private static double? ParseDouble(string text) => string.IsNullOrWhiteSpace(text) ? null : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : throw new InvalidOperationException("数値を入力してください: " + text);
    private static double RequiredDouble(string text) => ParseDouble(text) ?? throw new InvalidOperationException("必須の数値が未入力です");
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values) { var copy = values.ToArray(); collection.Clear(); foreach (var item in copy) collection.Add(item); }
    public void Dispose() { timer.Stop(); core.Changed -= OnState; core.OperationCompleted -= OnOperation; host.Reinitialized -= Reinitialize; if (virtualProtocol is not null) virtualProtocol.Outgoing -= VirtualReceive; }
}
