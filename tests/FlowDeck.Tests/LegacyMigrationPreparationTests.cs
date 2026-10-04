using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Infrastructure;

namespace FlowDeck.Tests;

public class LegacyMigrationPreparationTests
{
    private const string Installation = "0123456789abcdef0123456789abcdef";
    private sealed class Backend : IHardwareSensorBackend
    {
        public HardwareSensorDevice[] Values = [];
        public Task<HardwareSensorDevice[]> ReadDevicesAsync(CancellationToken ct) => Task.FromResult(Values);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceTestDirectory directory = new();
        public DemoHub Hub = null!;
        public HubDeviceDriver Driver = null!;
        public readonly Backend Backend = new();
        public HardwareSensorProvider Sensors = null!;
        public IDeviceControlDriver[] Drivers => [Driver, .. Sensors.CachedDrivers];
        public static async Task<Rig> Create(bool serial = true)
        {
            var r = new Rig(); r.Hub = new(DemoHub.Identity with { Serial = serial ? DemoHub.Identity.Serial : null }) { DelayMs = 0 };
            r.Driver = new(Installation, DemoHub.Identity.Id, r.Hub, r.directory.Path); await r.Driver.InitializeAsync();
            HardwareSensorDevice Temperature(string native, string id, DeviceKind kind, string serialId) => new(native, native, kind, new('a', 64), serialId,
                Guid.NewGuid().ToString("N"), null, [new(id, "温度", native, DeviceFeature.Temperature, 50, TimeProvider.System.GetTimestamp())]);
            var cpuPort = MotherboardFans.Cpu;
            r.Backend.Values = [Temperature("/cpu/0", "demo/cpu", DeviceKind.Processor, "cpu-serial"), Temperature("/gpu/0", "demo/gpu", DeviceKind.Graphics, "gpu-serial"),
                new("/motherboard", "ASRock X870E Taichi White", DeviceKind.Motherboard, new('b', 64), "board-serial", Guid.NewGuid().ToString("N"),
                    "asrock-x870e-taichi-white-v1", [new(cpuPort.SensorId, "CPU_FAN1", cpuPort.HardwareId, DeviceFeature.Rpm, 1000, TimeProvider.System.GetTimestamp())])];
            r.Sensors = new(Installation, r.Backend); await r.Sensors.PollAsync(CancellationToken.None); return r;
        }
        public AppSettings Settings(FanMode mode = FanMode.Manual)
        {
            var value = DemoHub.CreateSettings();
            return value with { Startup = new(true, true), LastConfirmedPolicy = mode == FanMode.Manual ? new(mode, 60, Limits: value.Limits) :
                new(mode, Profile: value.Profiles[1], Limits: value.Limits),
                Dashboard = new([new("cpu", "selected", "temperature", "CPU温度"), new("nzxt", "fan1", "fan", "前面")]) };
        }
        public async ValueTask DisposeAsync()
        {
            await Driver.DisposeAsync(); foreach (var d in Sensors.CachedDrivers) await d.DisposeAsync(); await Sensors.DisposeAsync(); directory.Dispose();
        }
    }
    private static Dictionary<string, byte[]> Files(AppSettings value) => new() { ["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(value, Contract.Json) };
    [Fact]
    public async Task MatchingHubSerialAndWiringRetainCalibrationWhileNamesAndReferencesUseTheCompiledDrivers()
    {
        await using var r = await Rig.Create(); var prepared = LegacyMigrationPreparationBuilder.Prepare(Installation, Files(r.Settings()), r.Drivers, true);
        Assert.Empty(prepared.Unresolved); Assert.Equal(4, prepared.Configuration.Bindings.Devices.Length);
        Assert.All(prepared.Configuration.Cooling.Endpoints, c => { Assert.True(c.WiringConfirmed); Assert.True(c.ResumeOnStart); Assert.Equal(25, c.Safety.Minimum); Assert.Equal(60, c.SavedPolicy!.Duty); });
        var hub = prepared.Configuration.Devices.Devices.Single(d => d.Id == r.Driver.DeviceId);
        Assert.Equal(new EndpointSetup("DEMO RGB", 24, true), hub.Endpoints.Single(e => e.Id == "led1").Setup);
        Assert.Equal("前面", prepared.Configuration.Dashboard.Items[1].Name);
        Assert.Equal(r.Driver.DeviceId, prepared.Configuration.Dashboard.Items[1].Target.DeviceId); Assert.Equal(0, r.Hub.Writes);
    }
    [Fact]
    public async Task HubSetupEvidenceMustBelongToTheSamePhysicalSnapshotAsTheMigrationBinding()
    {
        await using var r = await Rig.Create(); var binding = r.Driver.BindingCandidate!;
        Assert.NotNull(r.Driver.LegacySetupEvidence(r.Settings(), binding));
        Assert.Null(r.Driver.LegacySetupEvidence(r.Settings(), binding with { Fingerprint = new('f', 64) }));
        Assert.Equal(0, r.Hub.Writes);
    }
    [Fact]
    public async Task ACurrentSensorIdMatchDoesNotProveTheHistoricalSensorForAutomaticResume()
    {
        await using var r = await Rig.Create(); var prepared = LegacyMigrationPreparationBuilder.Prepare(Installation, Files(r.Settings(FanMode.Auto)), r.Drivers, true);
        Assert.Empty(prepared.Unresolved); Assert.All(prepared.Configuration.Cooling.Endpoints, c => { Assert.True(c.WiringConfirmed); Assert.False(c.ResumeOnStart); Assert.Equal(FanMode.Auto, c.SavedPolicy!.Mode); });
        Assert.Contains(prepared.Configuration.Migration.Notices, n => n.Contains("温度源の個体照合"));
        Assert.Equal(3, prepared.Configuration.Cooling.ProfileTemplates!.Length);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task WeakHubIdentityOrChangedWiringPreserveValuesWithoutCarryingConfirmation(bool serial, bool wiringChanged)
    {
        await using var r = await Rig.Create(serial); var settings = r.Settings();
        if (wiringChanged) settings = settings with { Hardware = settings.Hardware with { WiringSignature = "different-wiring" } };
        var prepared = LegacyMigrationPreparationBuilder.Prepare(Installation, Files(settings), r.Drivers, true);
        Assert.Empty(prepared.Unresolved); Assert.All(prepared.Configuration.Cooling.Endpoints, c => { Assert.False(c.WiringConfirmed); Assert.False(c.ResumeOnStart); Assert.Equal(25, c.Safety.Minimum); });
    }
    [Fact]
    public async Task MotherboardNamesRpmAndProfilesMapButAnOldBooleanCannotProveTheBoardWiring()
    {
        await using var r = await Rig.Create(); var settings = r.Settings() with { CpuFan = new(true, 40),
            Motherboard = new([new(MotherboardFans.Cpu.SensorId, "ラジエーターポンプ", true)]),
            Dashboard = new([new("motherboard", MotherboardFans.Cpu.SensorId, "fan", "ポンプ", Role: "pump")]) };
        var prepared = LegacyMigrationPreparationBuilder.Prepare(Installation, Files(settings), r.Drivers, true);
        var board = prepared.Configuration.Devices.Devices.Single(d => d.Kind == DeviceKind.Motherboard);
        var fan = prepared.Configuration.Cooling.Endpoints.Single(c => c.Target.DeviceId == board.Id);
        Assert.False(fan.WiringConfirmed); Assert.Equal(40, fan.Safety.Minimum); Assert.Equal(CoolingExitAction.ReleaseToDevice, fan.Safety.ExitAction);
        Assert.Equal(MotherboardFans.Cpu.Id, fan.Target.EndpointId);
        var endpoint = board.Endpoints.Single(e => e.Id == fan.Target.EndpointId); Assert.Equal("ラジエーターポンプ", endpoint.Name); Assert.True(endpoint.Setup!.ShowOnM5);
        Assert.Equal(fan.Target, prepared.Configuration.Dashboard.Items[0].Target); Assert.Equal("pump", prepared.Configuration.Dashboard.Items[0].Role);
    }
    [Fact]
    public async Task MissingDevicesKeepAllSettingsAsUnboundDraftsIncludingScenesAndUnidentifiedZoneEffects()
    {
        await using var r = await Rig.Create(); var settings = r.Settings() with {
            Motherboard = new(RgbZones: [new("old-zone-registration", "ケース", 24, new(14, 11, 22, 33))]) };
        var files = Files(settings);
        var scene = new LightingScene(Guid.NewGuid().ToString("N"), "保存色", [new(DemoHub.Identity.Id, "led1", HubLighting.Preset("blue"))],
            [new(MemoryLighting.Identity(0x71), 0, new(1, 20, 30, 40))]);
        files["lighting-scenes.json"] = JsonSerializer.SerializeToUtf8Bytes(new LightingSceneCollection(Scenes: [scene]), Contract.Json);
        var prepared = LegacyMigrationPreparationBuilder.Prepare(Installation, files, [], true);
        Assert.NotEmpty(prepared.Unresolved); Assert.Empty(prepared.Configuration.Bindings.Devices);
        Assert.All(prepared.Configuration.Devices.Devices, d => Assert.Equal("legacy-unbound", d.Provider));
        Assert.All(prepared.Configuration.Cooling.Endpoints, c => { Assert.False(c.WiringConfirmed); Assert.False(c.ResumeOnStart); Assert.Equal(60, c.SavedPolicy!.Duty); });
        Assert.Equal("保存色", prepared.Configuration.Lighting.Scenes[0].Name); Assert.Equal(2, prepared.Configuration.Lighting.Scenes[0].Individual.Length);
        var unknown = Assert.Single(prepared.Configuration.Lighting.Preferences!); Assert.Equal("legacy/unresolved/14", unknown.Program.EffectId);
        Assert.Equal(new RgbColor(11, 22, 33), Assert.Single(unknown.Program.Colors));
        Assert.Equal(SettingsTransfer.Hash(files), prepared.Configuration.Migration.SourceHash);
    }
    [Fact]
    public async Task AmbiguousRawSensorAddressesAreNeverResolvedByEnumerationOrder()
    {
        await using var r = await Rig.Create();
        r.Backend.Values = [.. r.Backend.Values, r.Backend.Values[0] with { SerialIdentity = "other-cpu", ConnectionId = Guid.NewGuid().ToString("N") }];
        await r.Sensors.PollAsync(CancellationToken.None);
        var prepared = LegacyMigrationPreparationBuilder.Prepare(Installation, Files(r.Settings()), r.Drivers.Reverse(), true);
        var missing = Assert.Single(prepared.Unresolved, r => r.Source == "temperature" && r.LegacyEndpointId == "demo/cpu");
        Assert.Equal("legacy-unbound", prepared.Configuration.Devices.Devices.Single(d => d.Id == missing.Target.DeviceId).Provider);
        Assert.Equal(missing.Target, prepared.Configuration.Dashboard.Items[0].Target);
    }
    [Fact]
    public async Task AForeignInstallationKeepsPreferencesButNoBindingsOrResumePermission()
    {
        await using var r = await Rig.Create(); var prepared = LegacyMigrationPreparationBuilder.Prepare(Installation, Files(r.Settings()), r.Drivers, false);
        Assert.Empty(prepared.Configuration.Bindings.Devices); Assert.Null(prepared.Configuration.Bindings.InstallationId);
        Assert.All(prepared.Configuration.Cooling.Endpoints, c => { Assert.False(c.WiringConfirmed); Assert.Null(c.SavedPolicy); });
        Assert.Equal("前面", prepared.Configuration.Dashboard.Items[1].Name);
    }
    [Fact]
    public void UnselectedDefaultSensorsRemainExplicitlyUnboundInsteadOfChoosingTheFirstCpu()
    {
        var settings = new AppSettings(); var defaults = new DashboardItem[] { new("cpu", "selected", "temperature", "CPU"), new("gpu", "selected", "temperature", "GPU") };
        var prepared = LegacyMigrationPreparationBuilder.Prepare(Installation, Files(settings), [], true, defaults);
        Assert.Equal(2, prepared.Unresolved.Length); Assert.Equal(2, prepared.Configuration.Dashboard.Items.Length);
        Assert.Empty(prepared.Configuration.Bindings.Devices); Assert.NotEqual(prepared.Configuration.Dashboard.Items[0].Target, prepared.Configuration.Dashboard.Items[1].Target);
    }
}
