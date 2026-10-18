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
        SelectLight = Command(p => { if (!editingLighting) { lightingEditRevision = state.LightingRevision; editingLighting = true; } SelectedLight = p!.ToString()!; return Task.CompletedTask; });
        ApplyLight = Command(async _ => { var result = await core.RequestAsync(Request("SetLighting", lightingEditRevision) with { Lighting = SelectedLight }); Show(result); if (result.Outcome == Outcome.WrittenUnverified) { editingLighting = false; lightingEditRevision = core.Snapshot.LightingRevision; } }, _ => CanApplyLight);
        RevertLight = Command(_ => { editingLighting = false; lightingEditRevision = state.LightingRevision; SelectedLight = state.Lighting.LastSent ?? "blue"; return Task.CompletedTask; });
        Probe = Command(async _ => { Replace(HubChoices, await core.ProbeAsync()); Notice = HubChoices.Count == 0 ? "Control Hub未検出" : "接続先未選択"; });
        ConnectHub = Command(async _ => { await core.OpenAsync(SelectedHub!); LoadSettings(); Notice = "接続済み・実配線と制御条件は要確認"; }, _ => SelectedHub is not null && !state.Managing);
        SaveSettings = Command(_ => SaveConnectionSettingsAsync(), _ => !state.Managing && !state.FanBusy && !state.LightingBusy);
        RefreshSettings = Command(_ => { LoadSettings(); return Task.CompletedTask; });
        SwitchMode = Command(async _ => { if (!await host.SwitchAsync(!host.Demo)) Notice = "切替不可・終了時出力が未確認"; }, _ => !cpuFanBusy && !boardLightBusy);
        SaveCurve = Command(p => SaveCurveAsync(p?.ToString() == "apply"));
        ReloadCurve = Command(_ => { LoadCurve(); return Task.CompletedTask; });
        AddPoint = Command(p => { var cpu = p?.ToString() == "cpu"; AddCurvePoint(cpu ? CpuPoints : GpuPoints, HubCurveMinimum, HubCurveAllowStop, row => { if (cpu) SelectedCpuPoint = row; else SelectedGpuPoint = row; }); return Task.CompletedTask; });
        RemovePoint = Command(p => { if (p?.ToString() == "cpu" && SelectedCpuPoint is not null) CpuPoints.Remove(SelectedCpuPoint); else if (p?.ToString() == "gpu" && SelectedGpuPoint is not null) GpuPoints.Remove(SelectedGpuPoint); return Task.CompletedTask; });
        CalculatePreview = Command(_ => { CalculateCurvePreview(); return Task.CompletedTask; });
        ExportLog = Command(_ => { var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "診断アーカイブ (*.zip)|*.zip", FileName = $"FlowDeck-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.zip" }; if (dialog.ShowDialog() == true) { host.Log.Export(dialog.FileName, new { snapshot = state, settings = core.Settings, motherboard = host.Motherboard.Board, rgb = host.Rgb, worker = host.Hub.Version }); Notice = "ログ保存済み"; } return Task.CompletedTask; });
        DemoFault = Command(p => { if (host.Hub is not DemoHub hub) return Task.CompletedTask; switch (p?.ToString())
            { case "partial": hub.FailNextFanWrite = true; Notice = "次回出力変更：FAN3不一致"; break;
              case "hub": hub.Disconnected = !hub.Disconnected; Notice = hub.Disconnected ? "デモHub切断" : "デモHub復帰"; break;
              case "sensor": host.DemoTemperature!.Missing = !host.DemoTemperature.Missing; break; }
            Refresh(); return Task.CompletedTask; }, _ => Demo);
        timer = new(TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
        { OnState(core.Snapshot); CommandManager.InvalidateRequerySuggested(); }, dispatcher);
        Attach(); timer.Start();
    }
    public ICommand Navigate { get; } public ICommand ChooseMode { get; } public ICommand ChangeDuty { get; } public ICommand ApplyFan { get; }
    public ICommand RevertFan { get; } public ICommand Resume { get; } public ICommand StopControl { get; }
    public ICommand SelectLight { get; } public ICommand ApplyLight { get; } public ICommand RevertLight { get; } public ICommand Probe { get; }
    public ICommand ConnectHub { get; } public ICommand SaveSettings { get; } public ICommand RefreshSettings { get; }
    public ICommand SwitchMode { get; } public ICommand SaveCurve { get; }
    public ICommand ReloadCurve { get; } public ICommand AddPoint { get; } public ICommand RemovePoint { get; } public ICommand CalculatePreview { get; }
    public ICommand ExportLog { get; } public ICommand DemoFault { get; }
    public ObservableCollection<ProfileOption> Profiles { get; } = [];
    public ObservableCollection<HubIdentity> HubChoices { get; } = [];
    public ObservableCollection<SensorOption> CpuSensors { get; } = [];
    public ObservableCollection<SensorOption> GpuSensors { get; } = [];
    public ObservableCollection<LedRow> LedDetails { get; } = [];
    public ObservableCollection<CurvePointRow> CpuPoints { get; } = [];
    public ObservableCollection<CurvePointRow> GpuPoints { get; } = [];
    public ObservableCollection<OperationResult> History { get; } = [];
    public IEnumerable<OperationResult> FilteredHistory => History.Where(r => DiagnosticFilter switch { "エラー" => r.Outcome is not (Outcome.Confirmed or Outcome.WrittenUnverified), "PC操作" => r.Source == "PC", _ => true });
    public string[] DiagnosticFilters { get; } = ["全件", "エラー", "PC操作"];
    public string DiagnosticFilter { get => diagnosticFilter; set { Set(ref diagnosticFilter, value); Notify(nameof(FilteredHistory)); } }
    public OperationResult? SelectedOperation { get => selectedOperation; set { Set(ref selectedOperation, value); Notify(nameof(OperationDetail)); } }
    private OperationResult? selectedOperation;
    public string OperationDetail => SelectedOperation is null ? "履歴未選択" : JsonSerializer.Serialize(SelectedOperation, new JsonSerializerOptions(Contract.Json) { WriteIndented = true });
    public int SelectedPage { get => page; set { Set(ref page, value); Notify(nameof(PageTitle)); } }
    public string PageTitle => new[] { "モニター", "NZXT 温度カーブ", "照明", "設定", "診断", "マザーボード", "表示項目", "冷却", "冷却", "通知", "書き出し・復元" }[page];
    public bool Demo => host.Demo;
    public string ModeLabel => Demo ? "DEMO・実機未接続" : "実機";
    public string SwitchLabel => Demo ? "実機へ" : "デモ";
    public string HubLabel => state.HubReady ? "● Hub 接続" : "○ Hub 未接続";
    public string DutyDisplay => state.ConfirmedDuty?.ToString() ?? "--";
    public string CurrentMode => state.Mode switch { FanMode.Auto => "自動 / " + state.LastConfirmed?.Profile?.Name, FanMode.Manual => "手動", FanMode.Monitor => "監視のみ", FanMode.Fallback => "異常時制御", _ => "状態未確認" };
    public string Confirmation => state.FanBusy ? $"適用中・目標 {state.TargetDuty}%" : state.Faulted ? state.Fault ?? "異常時制御" :
        state.ConfirmedDuty is not null ? "✓ 全対象の出力報告確認済み" : "出力未確認";
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
    public string FanDraftLabel => state.FanBusy ? "適用中" : editingFan ? fanEditRevision != state.ControlRevision ? "別の操作で変更あり" : "未適用" : "現在の設定";
    public ProfileOption? SelectedProfile { get => selectedProfile; set { if (Set(ref selectedProfile, value)) BeginFanEdit(); } }
    public string SelectedLight { get => selectedLight; set { Set(ref selectedLight, value); Notify(nameof(SelectedLightName)); Notify(nameof(LightDraftLabel)); } }
    public string SelectedLightName => LightName(SelectedLight);
    public string LightingSummary => state.Lighting.LastSent is null ? "未送信" : LightName(state.Lighting.LastSent) + "・" + (state.Lighting.Generation == state.DeviceGeneration ? "送信済み" : "前回接続時") +
        (state.Lighting.Channels is { Length: > 0 } channels ? " · " + string.Join(" / ", channels).ToUpperInvariant() : "");
    public string LightingTimestamp => state.Lighting.SentAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") ?? "—";
    public string LightDraftLabel => editingLighting && lightingEditRevision != state.LightingRevision ? "別の操作で変更あり" : editingLighting ? "未送信" : "";
    public string Notice { get => notice; set => Set(ref notice, value); }
    public string Footer => state.Operation is { } op ? $"{op.Time.ToLocalTime():HH:mm:ss}  {op.Source}  {op.Message}" : "監視中";
    public string VersionInfo => $"FlowDeck 1.9.0 / .NET {Environment.Version}\n{host.Hub.Version}" + (Demo ? "" : "\n" + HardwareTemperatures.AccessStatus);
    public HubIdentity? SelectedHub { get; set; }
    public SensorOption? SelectedCpu { get; set; }
    public SensorOption? SelectedGpu { get; set; }
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
    public string CurvePreview { get; private set; } = "計算のみ・実機へ未送信";
    public string CurveStatus => state.LastConfirmed?.Profile is { } current && core.Settings.Profiles.Any(p => p.Id == current.Id && p.Revision != current.Revision)
        ? "保存済み・未適用" : "保存のみ・制御変更なし";
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
        brightnessResult = host.LightingAdjustment.BaseScene is not null ? "復元可能" : "";
        core.Changed += OnState; core.OperationCompleted += OnOperation;
        state = core.Snapshot; LoadSettings(); UpdateProfiles(); ResetFanDraft(); lightingEditRevision = state.LightingRevision;
        LoadHubLighting(); lastLightInventory = null; lastLightSettings = null; boardLightTarget = null; LoadMotherboardSettings();
        fanDrafts.Clear(); BoardFanRows.Clear(); BoardFanTemperatures.Clear(); LoadCpuFanSettings();
        dashboardHistory = new(); dashboardLogged = false; LoadDashboard();
        OnState(state); Notice = host.Warning ?? (host.SettingsRestoreMessage.Length > 0 ? host.SettingsRestoreMessage : Demo ? "デモ・実機へ未送信" : "監視中・制御未設定");
    }
    private void Reinitialize()
    {
        settingsRestorePreview = null; settingsTransferFile = settingsTransferResult = ""; SettingsDifferences.Clear(); SettingsDeviceChecks.Clear();
        core.Changed -= OnState; core.OperationCompleted -= OnOperation;
        core = host.Coordinator; editingFan = editingLighting = false;
        History.Clear(); Attach(); ThemeManager.Apply(core.Settings.Ui.Theme); Refresh();
    }
    private void OnState(ControlSnapshot snapshot) => dispatcher.BeginInvoke(() =>
    {
        state = snapshot;
        UpdateSensors();
        RefreshMotherboard(); RefreshLightingGroupTargets(); RefreshHubLighting(); RefreshIdentificationTargets();
        RefreshCpuFan();
        RefreshDashboard(); RefreshNotifications(); RefreshDeviceCatalog();
        if (!editingFan && !snapshot.FanBusy) ResetFanDraft();
        if (!editingLighting) { lightingEditRevision = snapshot.LightingRevision; selectedLight = snapshot.Lighting.LastSent ?? "blue"; }
        Refresh(); CommandManager.InvalidateRequerySuggested();
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
        Theme = s.Ui.Theme; WiringChecked = s.Hardware.WiringConfirmed;
        if (s.Hardware.Hub is { } h && HubChoices.All(c => c.Id != h.Id)) HubChoices.Add(h);
        SelectedHub = HubChoices.FirstOrDefault(c => c.Id == s.Hardware.Hub?.Id);
        Replace(LedDetails, core.LastStatus.Lighting.Select(l => new LedRow(l.Id, l.Accessory, s.Hardware.LedDetails?.SingleOrDefault(d => d.Id == l.Id)?.LedCount)));
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
            Startup = new(LoginStartup, ResumeStartup), PythonPath = string.IsNullOrWhiteSpace(PythonPath) ? null : Path.GetFullPath(PythonPath),
            Ui = old.Ui with { Theme = Theme }
        };
        var changedControls = JsonSerializer.Serialize(new { old.Hardware, old.Limits, old.CpuSensorId, old.GpuSensorId }, Contract.Json)
            != JsonSerializer.Serialize(new { next.Hardware, next.Limits, next.CpuSensorId, next.GpuSensorId }, Contract.Json);
        await core.UpdateSettingsAsync(next with { LastConfirmedPolicy = changedControls ? null : old.LastConfirmedPolicy }, changedControls);
        await host.FlushSettingsAsync();
        if (!Demo) RuntimeHost.ConfigureLogin(LoginStartup);
        ThemeManager.Apply(Theme); UpdateProfiles(); ResetFanDraft(); Notice = UsesService ? "保存済み" : "保存済み・Python変更は再起動後に反映"; Refresh();
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
        Notice = "保存済み・未適用";
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
        CurvePreview = $"CPU {cpu?.ToString("F1") ?? "未使用"}% / GPU {gpu?.ToString("F1") ?? "未使用"}% → 共通 {FanPolicyEngine.Calculate(p, core.Settings.Limits, temperatures)}%\n計算のみ・実機へ未送信"; Notify(nameof(CurvePreview));
    }
    private static string Format(object? value) => value is IFormattable format ? format.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
    private static int? ParseInt(string text) => string.IsNullOrWhiteSpace(text) ? null : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new InvalidOperationException("整数を入力してください: " + text);
    private static int RequiredInt(string text) => ParseInt(text) ?? throw new InvalidOperationException("必須の整数が未入力です");
    private static double? ParseDouble(string text) => string.IsNullOrWhiteSpace(text) ? null : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : throw new InvalidOperationException("数値を入力してください: " + text);
    private static double RequiredDouble(string text) => ParseDouble(text) ?? throw new InvalidOperationException("必須の数値が未入力です");
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values) { var copy = values.ToArray(); collection.Clear(); foreach (var item in copy) collection.Add(item); }
    public void Dispose() { timer.Stop(); core.Changed -= OnState; core.OperationCompleted -= OnOperation; host.Reinitialized -= Reinitialize; }
}
