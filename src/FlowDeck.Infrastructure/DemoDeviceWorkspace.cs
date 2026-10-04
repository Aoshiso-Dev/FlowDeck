using System.Security.Principal;
using System.Text.Json;
using FlowDeck.Core;

namespace FlowDeck.Infrastructure;

/// <summary>Explicit demo fixture: real v2 IPC, configuration and Hub drivers, exclusively memory-backed hardware.</summary>
public sealed class DemoDeviceWorkspace : IAsyncDisposable
{
    private sealed class Log : IEventLog { public void Write(string category, object data) { } }
    private sealed class Temperatures : IHardwareSensorBackend
    {
        public bool GpuConnected = true;
        public double Cpu { get; set; } = 52;
        public DeviceMeasurementIssue CpuIssue { get; set; }
        public double CpuAge { get; set; }
        public int AdditionalSensors { get; set; }
        private HardwareSensorValue[] Additional(string device) => Enumerable.Range(1, AdditionalSensors).Select(i =>
            new HardwareSensorValue($"/demo/{device}/temperature/{i}", "補助温度", $"/demo/{device}", DeviceFeature.Temperature, 42, TimeProvider.System.GetTimestamp())).ToArray();
        private readonly string cpuConnection = Guid.NewGuid().ToString("N"), gpuConnection = Guid.NewGuid().ToString("N");
        public Task<HardwareSensorDevice[]> ReadDevicesAsync(CancellationToken ct) => Task.FromResult(new HardwareSensorDevice[] {
            new("/demo/cpu", "CPU", DeviceKind.Processor, new('a', 64), "flowdeck-demo-cpu", cpuConnection, null,
                [new("/demo/cpu/temperature/0", "CPU Package", "/demo/cpu", DeviceFeature.Temperature, CpuIssue == DeviceMeasurementIssue.None ? Cpu : null,
                    TimeProvider.System.GetTimestamp() - (long)(CpuAge * TimeProvider.System.TimestampFrequency), Issue: CpuIssue), .. Additional("cpu")]),
            new("/demo/gpu", "GPU", DeviceKind.Graphics, new('b', 64), "flowdeck-demo-gpu", gpuConnection, null,
                [new("/demo/gpu/temperature/0", "GPU Core", "/demo/gpu", DeviceFeature.Temperature, 43, TimeProvider.System.GetTimestamp()), .. Additional("gpu")]) }.Where(d => GpuConnected || d.Kind != DeviceKind.Graphics).ToArray());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private readonly DeviceServiceHost host;
    private readonly DemoWorkspaceData? data;
    private readonly Temperatures temperatures;
    public bool GpuConnected { get => temperatures.GpuConnected; set => temperatures.GpuConnected = value; }
    public double CpuTemperature { get => temperatures.Cpu; set { if (!double.IsFinite(value) || value is <= 0 or >= 150) throw new ArgumentOutOfRangeException(nameof(value)); temperatures.Cpu = value; } }
    public DeviceMeasurementIssue CpuMeasurementIssue { get => temperatures.CpuIssue; set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); temperatures.CpuIssue = value; } }
    public double CpuMeasurementAge { get => temperatures.CpuAge; set { if (!double.IsFinite(value) || value is < 0 or > 60) throw new ArgumentOutOfRangeException(nameof(value)); temperatures.CpuAge = value; } }
    public int AdditionalSensorsPerDevice { get => temperatures.AdditionalSensors; set { if (value is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(value)); temperatures.AdditionalSensors = value; } }
    public DemoHub[] Hubs { get; }
    public DemoOpenRgbInspector OpenRgb { get; }
    public DemoOpenRgbRuntimeInspector OpenRgbRuntime { get; }
    public DemoProviderRouting? Routing { get; private init; }
    public string[] DeviceIds { get; }
    public string PipeName { get; }
    public DeviceControlRuntime Runtime => host.Runtime;
    public Task Completion => host.Completion;
    private DemoDeviceWorkspace(DeviceServiceHost host, string pipeName, DemoHub[] hubs, string[] ids, DemoWorkspaceData? data, Temperatures temperatures,
        DemoOpenRgbInspector openRgb, DemoOpenRgbRuntimeInspector openRgbRuntime)
    {
        this.host = host; this.data = data; this.temperatures = temperatures; Hubs = hubs; DeviceIds = ids; PipeName = pipeName;
        OpenRgb = openRgb;
        OpenRgbRuntime = openRgbRuntime;
    }
    public DeviceWorkspaceClient CreateClient() => new(DeviceServicePipeClient.ForDemo(PipeName, Environment.ProcessId));
    public static async Task<DemoDeviceWorkspace> CreateAsync(string directory, string? pipeName = null, bool persistent = false, bool monitoringOnly = false,
        bool openRgbInspection = true, bool providerRouting = false, bool openRgbRuntimeInspection = true, bool manySetupCandidates = false, int lightingPorts = 1)
    {
        pipeName ??= "FlowDeck.Devices.Demo." + Guid.NewGuid().ToString("N");
        if (!DeviceWorkspaceOptions.DemoPipe(pipeName))
            throw new ArgumentException("専用のデモパイプ名が必要です");
        Directory.CreateDirectory(directory);
        if (monitoringOnly && persistent) throw new ArgumentException("初回監視のデモには使い捨ての専用フォルダーが必要です");
        if (providerRouting && (persistent || monitoringOnly)) throw new ArgumentException("経路切替デモには使い捨ての専用構成が必要です");
        var data = persistent ? DemoWorkspaceData.Open(directory) : null;
        try { return await CreateCoreAsync(directory, pipeName, data, monitoringOnly, openRgbInspection, providerRouting, openRgbRuntimeInspection, manySetupCandidates, lightingPorts).ConfigureAwait(false); }
        catch { data?.Dispose(); throw; }
    }
    private static async Task<DemoDeviceWorkspace> CreateCoreAsync(string directory, string pipeName, DemoWorkspaceData? data, bool monitoringOnly,
        bool openRgbInspection, bool providerRouting, bool openRgbRuntimeInspection, bool manySetupCandidates, int lightingPorts)
    {
        using var user = WindowsIdentity.GetCurrent(); var sid = user.User?.Value ?? throw new IOException("Windowsユーザーを確認できません");
        var installation = data?.Installation ?? Guid.NewGuid().ToString("N");
        HubIdentity[] identities = [new("demo-hub-a", "Control Hub", "A", "demo:a"), new("demo-hub-b", "Control Hub", "B", "demo:b")];
        var hubs = identities.Select(i => new DemoHub(i) { DelayMs = 0, LightingPortCount = lightingPorts }).ToArray();
        var drivers = identities.Select((i, n) => new HubDeviceDriver(installation, i.Id, hubs[n], directory)).ToArray();
        var temperatures = new Temperatures();
        var sensors = new HardwareSensorProvider(installation, temperatures);
        DeviceRuntimeStartup? startup = null;
        try
        {
            var store = DeviceConfigurationCodec.Store(Path.Combine(directory, "configuration"));
            if (monitoringOnly && store.ReadActive() is not null) throw new IOException("初回監視デモの設定がすでにあります");
            if (monitoringOnly) store.Activate(store.Stage(DeviceConfigurationCodec.Write(ServiceRuntimeBootstrap.MonitoringConfiguration(installation))));
            if (store.ReadActive() is null)
            {
            foreach (var driver in drivers) await driver.InitializeAsync().ConfigureAwait(false);
            await sensors.PollAsync(CancellationToken.None).ConfigureAwait(false);
            var registered = drivers.Cast<IPhysicalDeviceDriver>().Concat(sensors.CachedDrivers.Cast<IPhysicalDeviceDriver>()).ToArray();
            var status = drivers[0].CachedStatus!;
            var settings = new AppSettings { Hardware = new(identities[0], status.Channels.Select(c => c.Id).ToArray(), [],
                ControlCoordinator.WiringSignature(status), true, status.Lighting), Limits = new(30, 100, 70),
                CpuSensorId = "/demo/cpu/temperature/0", GpuSensorId = "/demo/gpu/temperature/0",
                Dashboard = new([new("cpu", "selected", "temperature", "CPU"), new("gpu", "selected", "temperature", "GPU")]) };
            var legacy = new Dictionary<string, byte[]> { ["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(settings, Contract.Json) };
            var config = LegacyMigrationPreparationBuilder.Prepare(installation, legacy, registered, true).Configuration;
            var cpu = config.Dashboard.Items.Single(i => i.Name == "CPU").Target;
            var fanTargets = drivers.SelectMany(d => d.Observe()!.Endpoints.Where(e => e.Kind == EndpointKind.Cooling).Select(e => new EndpointReference(d.DeviceId, e.Id))).ToArray();
            var safety = new CoolingSafety(30, 100, 70, CoolingExitAction.FixedDuty, [new(cpu, 95, 85)]);
            CoolingTemperatureProfile[] profiles = [new("balanced", "標準", 1, [new(cpu, [new(30, 30), new(60, 50), new(90, 100)], 95, 85)], 3, 2)];
            config = config with
            {
                Bindings = new(2, installation, registered.Select(d => d.BindingCandidate!).ToArray()),
                Devices = config.Devices with { Devices = config.Devices.Devices.Select(d => d.Id == drivers[0].DeviceId ? d with { Name = "ケースファン" } :
                    d.Id == drivers[1].DeviceId ? d with { Name = "ラジエーター" } : d).ToArray() },
                Cooling = config.Cooling with { Endpoints = fanTargets.Select(t => new CoolingEndpointConfiguration(t, true, safety, profiles)).ToArray() },
                Lighting = config.Lighting with { Endpoints = drivers.Select(d => new LightingEndpointConfiguration(new(d.DeviceId, "led1"), true, new(LightingExitAction.Off))).ToArray() },
                Dashboard = new(2, [.. config.Dashboard.Items, .. fanTargets.Select(t => new DeviceDashboardItem(t, DeviceFeature.Rpm,
                    t.EndpointId.ToUpperInvariant(), t == fanTargets[^1] ? "pump" : "fan"))])
            };
            config = config with { Devices = config.Devices with { Devices = config.Devices.Devices.Select(d => d with {
                Endpoints = d.Endpoints.Select(e => e.Kind == EndpointKind.Lighting ? e with { Setup = new("DEMO RGB", 24, true) } : e).ToArray() }).ToArray() } };
            store.Activate(store.Stage(DeviceConfigurationCodec.Write(config), legacy));
            }
            var inspector = new DemoOpenRgbInspector();
            var runtimeInspector = new DemoOpenRgbRuntimeInspector();
            var routing = providerRouting ? new DemoProviderRouting() : null;
            startup = await DeviceRuntimeStartup.CreateAsync(store, installation, () => new(drivers, [sensors,
                .. manySetupCandidates ? new IDeviceDriverProvider[] { new DemoSetupProvider() } : [],
                .. routing?.Create(DeviceConfigurationCodec.Read(store.ReadActive()!.Files), ["demo-direct"]) ?? []]), [sid], new Log(), demo: true,
                openRgbInspector: openRgbInspection ? inspector : null, providerRouting: routing,
                openRgbRuntimeInspector: openRgbRuntimeInspection ? runtimeInspector : null).ConfigureAwait(false);
            var host = await DeviceServiceHost.StartAsync(startup, [sid], new Log(), pipeName).ConfigureAwait(false);
            return new(host, pipeName, hubs, drivers.Select(d => d.DeviceId).ToArray(), data, temperatures, inspector, runtimeInspector) { Routing = routing };
        }
        catch
        {
            if (startup is not null) await startup.DisposeAsync().ConfigureAwait(false);
            else { foreach (var driver in drivers) await driver.DisposeAsync().ConfigureAwait(false); await sensors.DisposeAsync().ConfigureAwait(false); }
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        try { await host.DisposeAsync().ConfigureAwait(false); }
        finally { data?.Dispose(); }
    }
}
