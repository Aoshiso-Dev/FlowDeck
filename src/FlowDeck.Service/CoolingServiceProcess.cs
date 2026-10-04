using System.Security.Principal;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Service;

public sealed class CoolingServiceProcess : IAsyncDisposable, IWindowsServiceProcess
{
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly ServiceHardwareLease? hardwareLease;
    private readonly IEventLog log;
    private bool stopping;
    public CoolingServiceRuntime Runtime { get; }
    public Task Completion { get; }
    private CoolingServiceProcess(CoolingServiceRuntime runtime, string[] allowedUsers, string pipe, IEventLog log, ServiceHardwareLease? lease = null, string? setupData = null)
    {
        Runtime = runtime; this.log = log; hardwareLease = lease;
        Completion = ServeAsync(runtime, allowedUsers, pipe, log, setupData);
    }
    private async Task ServeAsync(CoolingServiceRuntime runtime, string[] users, string pipe, IEventLog eventLog, string? setupData)
    {
        try
        {
            while (setupData is not null && !ServiceDeployment.AllowsClients(setupData)) await Task.Delay(200, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            await new CoolingServicePipeServer(runtime, users, eventLog, pipe).RunAsync(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public static async Task<CoolingServiceProcess> StartRealAsync()
    {
        var installation = ServiceEnvironment.ValidateInstallation(AppContext.BaseDirectory);
        var lease = await ServiceHardwareLease.AcquireAsync();
        var directory = ServiceEnvironment.DataDirectory; var log = new RollingEventLog(Path.Combine(directory, "logs"));
        HardwareTemperatures? temperatures = null; RecoverableHubAdapter? hub = null; CoolingServiceRuntime? runtime = null;
        try
        {
            var store = new ServiceSettingsStore(directory); var settings = store.Load();
            var boot = ServiceEnvironment.BootIdentity();
            temperatures = new(new FanRecoveryJournal(directory, boot));
            hub = new(new HubWorkerClient(Path.Combine(ServiceEnvironment.InstallationDirectory, "runtime", "python", "python.exe"),
                Path.Combine(ServiceEnvironment.InstallationDirectory, "worker", "hub_worker.py"), log, isolated: true),
                new HubFanRecoveryJournal(directory), () => runtime?.Coordinator.Settings ?? settings);
            var errors = new List<Exception>();
            try { await temperatures.RecoverInterruptedFansAsync(); } catch (Exception e) { errors.Add(e); }
            try { await hub.RecoverAsync(); } catch (Exception e) { errors.Add(e); }
            if (errors.Count > 0) throw new AggregateException("前回のファン制御を復旧できません", errors);
            runtime = new(hub, temperatures, temperatures, p => temperatures.FanDriver(p.Id), settings, installation.AllowedUsers, log, persistSettings: store.Save, memoryRgb: new EneMemoryRgbControl(recoveryDirectory: directory));
            await runtime.InitializeAsync();
            ServiceEnvironment.Write(Path.Combine(directory, "service-ready.json"), new ServiceReadiness(1, Environment.ProcessId,
                runtime.Epoch, ServiceDeployment.ReadPending(directory)?.Id, DateTimeOffset.UtcNow));
            log.Write("serviceStart", new { processId = Environment.ProcessId, runtime.Epoch, boot });
            return new(runtime, installation.AllowedUsers, CoolingServiceProtocol.PipeName, log, lease, directory);
        }
        catch (Exception e)
        {
            log.Write("serviceStartFailure", new { error = e.ToString() });
            try
            {
                if (runtime is not null) await runtime.DisposeAsync();
                else { try { if (hub is not null) await hub.DisposeAsync(); } finally { if (temperatures is not null) await temperatures.DisposeAsync(); } }
            }
            finally { await lease.DisposeAsync(); }
            throw;
        }
    }
    public static async Task<CoolingServiceProcess> StartDemoAsync(string directory, string pipe)
    {
        RequireDemoPipe(pipe); ServiceEnvironment.NoLinks(directory); Directory.CreateDirectory(directory);
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User?.Value ?? throw new IOException("Windowsユーザーを取得できません");
        var log = new RollingEventLog(directory); var store = new ServiceSettingsStore(directory);
        var settings = File.Exists(Path.Combine(directory, "cooling-settings.json")) ? store.Load() : DemoHub.CreateSettings() with
        { CpuFan = new(true, 20), ChassisFan = new(true, 30), BoardFans = MotherboardFans.Ports.Skip(2).ToDictionary(p => p.Id, _ => new CpuFanSettings(true, 30)) };
        CoolingServiceRuntime? runtime = null;
        var demo = await PersistentDemoHub.CreateAsync(directory);
        var hub = new RecoverableHubAdapter(demo, new(directory), () => runtime?.Coordinator.Settings ?? settings);
        try
        {
            await hub.RecoverAsync();
            var drivers = MotherboardFans.Ports.ToDictionary(p => p.Id, _ => new DemoCpuFan());
            runtime = new(hub, new DemoTemperatures(), new DemoMotherboard(), p => drivers[p.Id], settings, [sid], log, persistSettings: store.Save);
            await runtime.InitializeAsync();
            ServiceEnvironment.Write(Path.Combine(directory, "service-ready.json"), new ServiceReadiness(1, Environment.ProcessId,
                runtime.Epoch, ServiceDeployment.ReadPending(directory)?.Id, DateTimeOffset.UtcNow));
            return new(runtime, [sid], pipe, log, setupData: directory);
        }
        catch { if (runtime is not null) await runtime.DisposeAsync(); else await hub.DisposeAsync(); throw; }
    }
    public static void RequireDemoPipe(string pipe)
    {
        const string prefix = "FlowDeck.Cooling.Test.";
        if (!pipe.StartsWith(prefix, StringComparison.Ordinal) || !CoolingServiceProtocol.Id(pipe[prefix.Length..]))
            throw new ArgumentException("デモ通信には専用のランダムなパイプ名が必要です");
    }
    public async Task PowerAsync(bool resume)
    {
        await lifecycle.WaitAsync();
        try
        {
            if (stopping) return;
            if (resume) await Runtime.ResumeForPowerAsync(); else await Runtime.SuspendForPowerAsync();
            log.Write("servicePower", new { resume, Runtime.LifecycleError });
        }
        catch (Exception e) { log.Write("servicePowerFailure", new { resume, error = e.ToString() }); throw; }
        finally { lifecycle.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (stopping) return; stopping = true;
            await stop.CancelAsync();
            try { await Completion; }
            finally
            {
                try { await Runtime.DisposeAsync(); }
                finally { if (hardwareLease is not null) await hardwareLease.DisposeAsync(); }
            }
            log.Write("serviceStop", new { Runtime.LifecycleError });
            if (Runtime.LifecycleError is not null) throw new IOException(Runtime.LifecycleError);
        }
        finally { lifecycle.Release(); }
    }
}

/// <summary>Only used by --demo-host; persists simulated Hub PWM across process crashes.</summary>
internal sealed class PersistentDemoHub(string path, DemoHub inner) : IHubAdapter
{
    private long generation;
    public static async Task<PersistentDemoHub> CreateAsync(string directory)
    {
        var path = Path.Combine(directory, "demo-hub.json"); var inner = new DemoHub { DelayMs = 0 };
        if (File.Exists(path))
        {
            var duties = ServiceEnvironment.Read<Dictionary<string, int>>(path);
            if (duties.Count != 5 || duties.Any(p => p.Key is not ("fan1" or "fan2" or "fan3" or "fan4" or "fan5") || p.Value is < 0 or > 100))
                throw new IOException("模擬Hubの状態が不正です");
            foreach (var p in duties) await inner.SetFanGroupAsync([p.Key], p.Value);
        }
        return new(path, inner);
    }
    public bool IsDemo => true;
    public string Version => inner.Version;
    public Task<HubIdentity[]> ProbeAsync(CancellationToken ct = default) => inner.ProbeAsync(ct);
    public Task<HubStatus> OpenAsync(HubIdentity identity, long nextGeneration, CancellationToken ct = default)
    {
        if (nextGeneration <= generation) throw new IOException("模擬Worker: 古いデバイス世代です");
        generation = nextGeneration; return inner.OpenAsync(identity, nextGeneration, ct);
    }
    public Task<HubStatus> ReadAsync(CancellationToken ct = default) => inner.ReadAsync(ct);
    public async Task<HubResult> SetFanGroupAsync(string[] channels, int duty, CancellationToken ct = default)
    {
        var result = await inner.SetFanGroupAsync(channels, duty, ct);
        ServiceEnvironment.Write(path, result.Status!.Channels.ToDictionary(c => c.Id, c => c.Duty!.Value)); return result;
    }
    public Task<HubResult> SetLightingAsync(string[] channels, string preset, CancellationToken ct = default) => inner.SetLightingAsync(channels, preset, ct);
    public Task<HubResult> SetLightingEffectAsync(string[] channels, HubLightSetting effect, CancellationToken ct = default) => inner.SetLightingEffectAsync(channels, effect, ct);
    public Task CloseAsync(CancellationToken ct = default) => inner.CloseAsync(ct);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
