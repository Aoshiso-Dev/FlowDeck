using Microsoft.Win32;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.App;

public sealed partial class RuntimeHost(string dataRoot, bool demoOnly = false, CoolingConnectionOptions? connection = null,
    CoolingServicePipeClient? initialServiceClient = null) : IAsyncDisposable
{
    private readonly bool authenticatedStartup = initialServiceClient is not null;
    private CoolingServicePipeClient CreateServiceClient() => Interlocked.Exchange(ref initialServiceClient, null) ?? connection!.CreateClient();
    public bool UsesService => Coordinator is RemoteControlCoordinator;
    public string CoolingConnectionStatus => Coordinator is RemoteControlCoordinator remote ? remote.ConfigurationPending ? "サービス：復元設定の反映待ち" :
        remote.Connected ? "サービス：接続済み" : "サービス：" + remote.ConnectionError : Demo ? "デモ" : "直接接続";
    public string CoolingConnectionPreference { get => new CoolingConnectionPreferenceStore(dataRoot).Load(); set => new CoolingConnectionPreferenceStore(dataRoot).Save(value); }
    public async Task ReconnectCoolingAsync()
    {
        if (Coordinator is not RemoteControlCoordinator remote) return;
        await remote.ConnectAsync(); await remote.ApplyPendingConfigurationAsync(); await FlushSettingsAsync(); Warning = null;
    }
    private Task saveTask = Task.CompletedTask;
    private readonly object saveSync = new();
    private readonly object inventorySync = new();
    private string inventorySignature = "";
    public IControlCoordinator Coordinator { get; private set; } = null!;
    public JsonSettingsStore Store { get; private set; } = null!;
    public RollingEventLog Log { get; private set; } = null!;
    public IHubAdapter Hub { get; private set; } = null!;
    public DemoTemperatures? DemoTemperature { get; private set; }
    public IMotherboardSource Motherboard { get; private set; } = new DemoMotherboard();
    public IBoardFanControllers BoardFans { get; private set; } = null!;
    public IBoardFanController CpuFan => BoardFans[MotherboardFans.Cpu.Id];
    public RgbInventory Rgb { get; private set; } = new([], Error: "OpenRGBは未接続");
    private readonly SemaphoreSlim rgbGate = new(1);
    public async Task UseAsrockRgbAsync()
    {
        await Coordinator.UpdateSettingsAsync(Coordinator.Settings with { Motherboard = Coordinator.Settings.Motherboard with { OpenRgbEnabled = false } }, false);
        await FlushSettingsAsync(); await RefreshRgbAsync();
    }
    public async Task RefreshRgbAsync()
    {
        using var rgbEdit = await LockRgbEditAsync();
        {
            if (Demo) { if (Rgb.Devices.Length == 0) Rgb = DemoMotherboard.Rgb; return; }
            var owner = Coordinator; var settings = owner.Settings.Motherboard;
            RgbInventory result;
            try
            {
                if (!settings.OpenRgbEnabled && await AsrockRgbRecovery.RecoverAsync(Store.DirectoryPath) is { } recovered)
                { Log.Write("boardLightingRecovery", new { message = recovered }); lightingRecoveryError = null; }
                result = await ReadNativeRgbAsync();
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException or
                UnauthorizedAccessException or TimeoutException or InvalidOperationException)
            { result = new([], Error: "RGB一覧を取得できません。" + e.Message); }
            var current = Coordinator.Settings.Motherboard;
            if (ReferenceEquals(owner, Coordinator) && settings.OpenRgbEnabled == current.OpenRgbEnabled && settings.OpenRgbPort == current.OpenRgbPort)
            { Rgb = result; Log.Write("rgbInventory", result); }
        }
    }
    public async Task<BoardLightApplyResult> ApplyBoardLightingAsync(string identity, BoardLightChange[] changes, LightingGroup? group = null)
    {
        using var rgbEdit = await LockRgbEditAsync(() =>
        {
            group = ResolveLightingGroup(group); CheckGroupSyncScope(group);
            if (LightingSynchronized) throw new InvalidOperationException("同期停止後にマザーの個別設定を変更してください");
            if (group is not null && changes.Any(c => !group.Board.Contains(new(identity, c.Zone)))) throw new IOException("グループ外の照明は変更できません");
        });
        await InvalidateAdjustmentBaselineAsync(new(Guid.NewGuid().ToString("N"), "マザー照明", [], changes.Select(c => new SceneBoardLight(identity, c.Zone, c.Desired)).ToArray()));
        return await ApplyBoardLightingCoreAsync(identity, changes);
    }
    private async Task<BoardLightApplyResult> ApplyBoardLightingCoreAsync(string identity, BoardLightChange[] changes)
    {
            var owner = Coordinator;
            if (owner.Settings.Motherboard.OpenRgbEnabled && !Demo && !MemoryLighting.IsIdentity(identity))
                throw new InvalidOperationException("マザー照明はUSB直接接続で利用できます。設定でOpenRGB連携をオフにしてください");
            BoardLightApplyResult result;
            if (Demo)
            {
                var device = Rgb.Devices.Single(d => d.ControlIdentity == identity);
                if (changes.Any(c => device.Zones.Single(z => z.HardwareZone == c.Zone).Configuration != c.Expected))
                    throw new IOException("照明設定が変更されました。再取得してください");
                var updated = device with { Zones = device.Zones.Select(z => changes.FirstOrDefault(c => c.Zone == z.HardwareZone) is { } c
                    ? z with { Configuration = c.Desired, ReportedMode = BoardLighting.Name(c.Desired.Mode) } : z).ToArray() };
                Rgb = Rgb with { Devices = Rgb.Devices.Select(d => d.ControlIdentity == identity ? updated : d).ToArray(), UpdatedAt = DateTimeOffset.UtcNow };
                result = new(changes.Select(c => new BoardLightResult(c.Zone, "verified", c.Desired)).ToArray());
            }
            else
            {
                result = MemoryLighting.IsIdentity(identity)
                    ? Coordinator is RemoteControlCoordinator remote ? await remote.ApplyMemoryRgbAsync(identity, changes) : await new EneMemoryRgbControl(recoveryDirectory: Store.DirectoryPath).ApplyAsync(identity, changes)
                    : await new AsrockRgbControl().ApplyAsync(identity, changes);
                try { Rgb = await ReadNativeRgbAsync(); }
                catch (Exception e) { Rgb = new([], Error: e.Message); }
            }
            Log.Write("boardLighting", new { demo = Demo, changes, result });
            if (ReferenceEquals(owner, Coordinator))
            {
                var saved = (owner.Settings.Motherboard.RgbZones ?? []).ToDictionary(z => z.Id);
                foreach (var device in Rgb.Devices.Where(d => d.ControlIdentity == identity))
                for (var i = 0; i < device.Zones.Length; i++)
                {
                    var zone = device.Zones[i];
                    if (result.Zones.FirstOrDefault(r => r.Zone == zone.HardwareZone) is not { Status: "verified", Reported: { } value }) continue;
                    var id = device.ZoneRegistrationId(zone.Name, i);
                    saved[id] = (saved.GetValueOrDefault(id) ?? new(id)) with { LastLighting = value };
                }
                await owner.UpdateSettingsAsync(owner.Settings with { Motherboard = owner.Settings.Motherboard with { RgbZones = saved.Values.ToArray() } }, false);
                await FlushSettingsAsync();
            }
            return result;
    }
    public string? Warning { get; private set; }
    public bool Demo { get; private set; }
    public event Action? Reinitialized;
    public async Task InitializeAsync(bool demo)
    {
        if (demoOnly && !demo) throw new InvalidOperationException("画面試験中は実機モードに切り替えられません");
        var service = connection?.UseService == true && (!demo || connection.DemoPipe is not null);
        Demo = demo;
        var directory = Path.Combine(dataRoot, demo ? "demo" : "hardware");
        Store = new(directory); Log = new(Path.Combine(directory, "logs"));
        InitializeDeviceCatalog(directory);
        var restored = new SettingsTransferStore(directory).ApplyAtStartup(service);
        SettingsRestoreMessage = restored.Message ?? "";
        if (restored.Attempted) Log.Write("settingsRestore", restored);
        ActiveLightingSync = null; await LoadScenesAsync(); await LoadLightingAdjustmentAsync(); await LoadLightingGroupsCoreAsync(); await LoadLightNamesAsync();
        var settings = await Store.LoadAsync(); Warning = Store.LoadWarning;
        if (!service && settings.ServiceConfigurationPending) throw new IOException("復元設定のサービスへの反映が残っています。サービス接続で起動してください");
        if (restored.Attempted) settings = settings with { Startup = settings.Startup with { ResumeControl = false }, LastConfirmedPolicy = null };
        if (!Demo && Warning is null && ApplicationDataMigration.RelocateBundledPython(settings.PythonPath, AppContext.BaseDirectory) is { } relocated && relocated != settings.PythonPath)
        {
            settings = settings with { PythonPath = relocated };
            await Store.SaveAsync(settings);
        }
        if (demo && !File.Exists(Path.Combine(directory, "settings.json"))) settings = DemoHub.CreateSettings();
        if (service)
        {
            var remote = new RemoteControlCoordinator(settings, CreateServiceClient, Log, demo);
            Coordinator = remote; Hub = remote.Hub; Motherboard = remote; BoardFans = remote.BoardFans; DemoTemperature = null;
        }
        else
        {
        DemoTemperature = demo ? new DemoTemperatures() : null;
        var defaultPython = Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
        Hub = demo ? new DemoHub() : new HubWorkerClient(settings.PythonPath ?? defaultPython,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "worker", "hub_worker.py")), Log);
        var temperatures = DemoTemperature ?? (ITemperatureSource)new HardwareTemperatures();
        Motherboard = demo ? new DemoMotherboard() : (IMotherboardSource)temperatures;
        Coordinator = new ControlCoordinator(Hub, temperatures, settings, Log);
        BoardFans = new MotherboardFanControllers(p => demo ? new DemoCpuFan() : ((HardwareTemperatures)temperatures).FanDriver(p.Id), () =>
        {
            var board = Motherboard.Board;
            return Coordinator.Snapshot.Sensors.Concat(board.Sensors.Where(s => s.Kind == "temperature")
                .Select(s => new SensorReading(s.Id, s.SourceName, "motherboard", board.Error is null && s.Problem is null ? s.Value : null, board.Timestamp ?? 0))).ToArray();
        }, Log);
        }
        Rgb = demo ? DemoMotherboard.Rgb : new([], Error: "OpenRGBは未取得");
        inventorySignature = ""; Coordinator.Changed += TrackInventory;
        Coordinator.SettingsChanged += Save;
        if (service)
        {
            try { await ReconnectCoolingAsync(); if (!restored.Attempted) await Coordinator.RestoreStartupAsync(); }
            catch (Exception e) { Warning = e.Message; Log.Write("serviceConnection", new { error = e.Message }); if (authenticatedStartup) throw; }
        }
        else
        {
        if (settings.Hardware.Hub is { } selected)
        {
            try { await Coordinator.OpenAsync(selected); }
            catch (Exception e) { Warning = e.Message; Log.Write("startup", new { error = e.ToString() }); }
        }
        await Coordinator.TickAsync();
        if (!demo) await Coordinator.RestoreStartupAsync();
        if (demo && !restored.Attempted && Coordinator.Snapshot.HubReady)
        {
            var profile = Coordinator.Settings.Profiles.Single(p => p.Id == "balanced");
            await Coordinator.RequestAsync(new(Guid.NewGuid().ToString("N"), "デモ", Coordinator.Epoch,
                Coordinator.Snapshot.ControlRevision, "SelectAutoProfile", ProfileId: profile.Id, ProfileRevision: profile.Revision));
        }
        }
        settings = Coordinator.Settings;
        Coordinator.Start(); BoardFans.Start(); await RefreshRgbAsync(); await StartNotificationsAsync(); Reinitialized?.Invoke();
    }
    public async Task<bool> SwitchAsync(bool demo)
    {
        if (rgbGate.CurrentCount == 0) throw new InvalidOperationException("RGB操作の完了を待ってください");
        await BoardFans.RestoreAsync();
        if ((await Coordinator.StopControlAsync()).Outcome != Outcome.Confirmed) return false;
        await DisposeCurrentAsync(); await InitializeAsync(demo); return true;
    }
    private void Save(AppSettings settings)
    {
        var store = Store;
        lock (saveSync) saveTask = saveTask.ContinueWith(async previous =>
        {
            _ = previous.Exception;
            try { await store.SaveAsync(settings); }
            catch (Exception e) { Warning = "設定を保存できません: " + e.Message; Log.Write("settings", new { error = e.ToString() }); throw; }
        }, TaskScheduler.Default).Unwrap();
    }
    private void TrackInventory(ControlSnapshot snapshot)
    {
        var board = Motherboard.Board;
        if (Demo || board.Sensors.Length == 0) return;
        var settings = Coordinator.Settings;
        var cpu = snapshot.Sensors.FirstOrDefault(s => s.Id == settings.CpuSensorId);
        var gpu = snapshot.Sensors.FirstOrDefault(s => s.Id == settings.GpuSensorId);
        var signature = string.Join('|', board.Sensors.Select(s => s.Id)) + $"/{cpu?.Celsius is not null}/{gpu?.Celsius is not null}";
        lock (inventorySync)
        {
            if (signature == inventorySignature) return;
            inventorySignature = signature; Log.Write("motherboardInventory", new { board, cpu, gpu });
        }
    }
    public async Task FlushSettingsAsync() { Task task; lock (saveSync) task = saveTask; await task; }
    public static void ConfigureLogin(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("FlowDeck", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("FlowDeck", false);
        key.DeleteValue(ApplicationDataMigration.LegacyDirectory, false);
    }
    private async Task DisposeCurrentAsync()
    {
        await StopNotificationsAsync();
        await StopLightingSyncAsync();
        try { await BoardFans.DisposeAsync(); }
        finally
        {
            Coordinator.SettingsChanged -= Save; Coordinator.Changed -= TrackInventory;
            try { await Coordinator.DisposeAsync(); }
            finally { try { await FlushSettingsAsync(); } catch (Exception e) { Log.Write("settingsFlush", new { error = e.Message }); } }
        }
    }
    public async ValueTask DisposeAsync() { if (Coordinator is not null) await DisposeCurrentAsync(); }
}
